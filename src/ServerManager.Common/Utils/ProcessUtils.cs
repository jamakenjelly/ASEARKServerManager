using NLog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;
using System.Threading.Tasks;

namespace ServerManagerTool.Common.Utils
{
    public static class ProcessUtils
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        // Delegate type to be used as the Handler Routine for SCCH
        delegate bool ConsoleCtrlDelegate(CtrlTypes CtrlType);

        // Enumerated type for the control messages sent to the handler routine
        enum CtrlTypes : uint
        {
            CTRL_C_EVENT = 0,
            CTRL_BREAK_EVENT,
            CTRL_CLOSE_EVENT,
            CTRL_LOGOFF_EVENT = 5,
            CTRL_SHUTDOWN_EVENT
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        private static extern bool FreeConsole();

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GenerateConsoleCtrlEvent(CtrlTypes dwCtrlEvent, uint dwProcessGroupId);

        [DllImport("kernel32.dll")]
        static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate HandlerRoutine, bool Add);

        [DllImport("user32.dll")]
        private static extern int ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern int SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int IsIconic(IntPtr hWnd);

        public static string FIELD_COMMANDLINE = "CommandLine";
        public static string FIELD_EXECUTABLEPATH = "ExecutablePath";
        public static string FIELD_PROCESSID = "ProcessId";

        private const int SW_RESTORE = 9;

        private static Mutex _mutex;

        public static string GetCommandLineForProcess(int processId)
        {
            var wmiQueryString = $"SELECT {FIELD_COMMANDLINE} FROM Win32_Process WHERE {FIELD_PROCESSID} = {processId}";

            using (var searcher = new ManagementObjectSearcher(wmiQueryString))
            {
                using (var results = searcher.Get())
                {
                    ManagementObject mo = results.Cast<ManagementObject>().FirstOrDefault();
                    if (mo != null)
                        return (string)mo[FIELD_COMMANDLINE];
                }
            }

            return null;
        }

        // Bulk equivalent of GetCommandLineForProcess - one WMI query for every
        // process with the given name instead of one query per process. With
        // several dozen server processes sharing the same name (e.g.
        // ShooterGameServer.exe across every ARK profile) and a status check
        // that needs each one's command line to figure out which process
        // belongs to which profile, calling GetCommandLineForProcess in a
        // per-process, per-profile loop multiplies out badly - N profiles each
        // re-querying all N processes individually is N² WMI round-trips per
        // status tick, which is exactly the kind of load that overwhelms
        // WmiPrvSE.exe (WMI Provider Host) and starts producing WBEM_E_
        // CALL_CANCELLED / client-failure entries in the WMI-Activity event
        // log. Callers should fetch this once per status tick and look up PIDs
        // in the returned dictionary instead of querying WMI per process.
        public static Dictionary<int, string> GetCommandLinesForProcessName(string processName)
        {
            var result = new Dictionary<int, string>();

            var wmiQueryString = $"SELECT {FIELD_PROCESSID}, {FIELD_COMMANDLINE} FROM Win32_Process WHERE Name = '{processName}'";

            try
            {
                using (var searcher = new ManagementObjectSearcher(wmiQueryString))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject mo in results)
                    {
                        try
                        {
                            var processId = (int)(uint)mo[FIELD_PROCESSID];
                            result[processId] = (string)mo[FIELD_COMMANDLINE];
                        }
                        catch
                        {
                            // Skip a row that didn't have the expected fields
                            // rather than losing the whole batch over it.
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"{nameof(GetCommandLinesForProcessName)} failed for '{processName}'. {ex.Message}");
            }

            return result;
        }

        public static string GetMainModuleFilepath(int processId)
        {
            var wmiQueryString = $"SELECT {FIELD_EXECUTABLEPATH} FROM Win32_Process WHERE {FIELD_PROCESSID} = {processId}";

            using (var searcher = new ManagementObjectSearcher(wmiQueryString))
            {
                using (var results = searcher.Get())
                {
                    ManagementObject mo = results.Cast<ManagementObject>().FirstOrDefault();
                    if (mo != null)
                        return (string)mo[FIELD_EXECUTABLEPATH];
                }
            }

            return null;
        }

        // Only one console can be attached at a time, so stops are done one after the other.
        private static readonly SemaphoreSlim StopLock = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Asks a server's console program to stop (Ctrl-C) and waits for it to exit, killing it after 10 seconds. The waiting is done on a background
        /// thread: this used to wait on the caller's thread, so a Stop click froze the manager until the server had closed.
        /// </summary>
        public static async Task SendStopAsync(Process process)
        {
            if (process == null)
                return;

            await StopLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await Task.Run(() => SendStopCore(process)).ConfigureAwait(false);
            }
            finally
            {
                StopLock.Release();
            }
        }

        private static void SendStopCore(Process process)
        {
            var ts = new TaskCompletionSource<bool>();
            EventHandler handler = (s, e) => ts.TrySetResult(true);

            try
            {
                process.Exited += handler;

                //This does not require the console window to be visible.
                var result = AttachConsole((uint)process.Id);
                if (result)
                {
                    // Disable Ctrl-C handling for our program
                    result = SetConsoleCtrlHandler(null, true);
                    if (result)
                    {
                        result = GenerateConsoleCtrlEvent(CtrlTypes.CTRL_C_EVENT, 0);
                        if (result)
                        {
                            FreeConsole();

                            // Must wait here. If we don't and re-enable Ctrl-C
                            // handling below too fast, we might terminate ourselves.
                            try
                            {
                                ts.Task.Wait(10000);
                                result = true;
                            }
                            catch (Exception)
                            {
                                result = false;
                            }

                            //Re-enable Ctrl-C handling or any subsequently started
                            //programs will inherit the disabled state.
                            SetConsoleCtrlHandler(null, false);
                        }
                        else
                        {
                            //Re-enable Ctrl-C handling or any subsequently started
                            //programs will inherit the disabled state.
                            SetConsoleCtrlHandler(null, false);

                            FreeConsole();
                        }
                    }
                    else
                    {
                        FreeConsole();
                    }
                }
                
                if (!result && !process.HasExited)
                {
                    process.Kill();
                }
            }
            finally
            {
                process.Exited -= handler;
            }
        }

        public static Task<bool> RunProcessAsync(string file, string arguments, string verb, string workingDirectory, string username, SecureString password, List<int> SteamCmdIgnoreExitStatusCodes, DataReceivedEventHandler outputHandler, CancellationToken cancellationToken, ProcessWindowStyle windowStyle = ProcessWindowStyle.Normal)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
                    throw new FileNotFoundException("The specified file does not exist or could not be found.", file);

                var fileName = Path.GetFileName(file);

                var startInfo = new ProcessStartInfo()
                {
                    FileName = file,
                    Arguments = arguments,
                    Verb = verb,
                    UseShellExecute = outputHandler == null && windowStyle == ProcessWindowStyle.Minimized && string.IsNullOrWhiteSpace(username),
                    RedirectStandardOutput = outputHandler != null,
                    CreateNoWindow = outputHandler != null || windowStyle == ProcessWindowStyle.Hidden,
                    WindowStyle = windowStyle,
                    UserName = string.IsNullOrWhiteSpace(username) ? null : username,
                    Password = string.IsNullOrWhiteSpace(username) ? null : password,
                    WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory) ? Path.GetDirectoryName(file) : workingDirectory,
                };

                var process = Process.Start(startInfo);
                process.EnableRaisingEvents = true;
                if (startInfo.RedirectStandardOutput && outputHandler != null)
                {
                    process.OutputDataReceived += outputHandler;
                    process.BeginOutputReadLine();
                }

                var tcs = new TaskCompletionSource<bool>();
                using (var cancelRegistration = cancellationToken.Register(() =>
                    {
                        try
                        {
                            process.Kill();
                        }
                        finally
                        {
                            tcs.TrySetCanceled();
                        }
                    }))
                {
                    process.Exited += (s, e) =>
                    {
                        var exitCode = process.ExitCode;

                        _logger.Debug($"{nameof(RunProcessAsync)}: filename {fileName}; exitcode = {exitCode}");

                        if (exitCode != 0)
                        {
                            _logger.Error($"{nameof(RunProcessAsync)}: filename {fileName}; exitcode = {exitCode}");

                            if (SteamCmdIgnoreExitStatusCodes.Contains(exitCode))
                            {
                                exitCode = 0;
                            }
                        }

                        tcs.TrySetResult(exitCode == 0);
                        process.Close();
                    };
                    return tcs.Task;
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"{nameof(RunProcessAsync)}. {ex.Message}\r\n{ex.StackTrace}");
                throw;
            }
        }

        private static IntPtr GetCurrentInstanceWindowHandle()
        {
            var hWnd = IntPtr.Zero;
            var currentProcess = Process.GetCurrentProcess();

            var processes = Process.GetProcessesByName(currentProcess.ProcessName);
            foreach (var process in processes)
            {
                // Get the first instance that is not this instance, has the same process name and was started from the same file name
                // and location. Also check that the process has a valid window handle in this session to filter out other user's processes.
                if (process.Id != currentProcess.Id && process.MainModule.FileName == currentProcess.MainModule.FileName && process.MainWindowHandle != IntPtr.Zero)
                {
                    hWnd = process.MainWindowHandle;
                    break;
                }
            }

            return hWnd;
        }

        public static bool IsAlreadyRunning()
        {
            var assemblyLocation = Assembly.GetEntryAssembly().Location;
            var name = $"Global::{Path.GetFileName(assemblyLocation)}";

            _mutex = new Mutex(true, name, out bool createdNew);
            if (createdNew)
                _mutex.ReleaseMutex();

            return !createdNew;
        }

        public static bool SwitchToCurrentInstance()
        {
            var hWnd = GetCurrentInstanceWindowHandle();
            if (hWnd == IntPtr.Zero)
                return false;

            // Restore window if minimised. Do not restore if already in normal or maximised window state, since we don't want to
            // change the current state of the window.
            if (IsIconic(hWnd) != 0)
                ShowWindow(hWnd, SW_RESTORE);

            // Set foreground window.
            SetForegroundWindow(hWnd);

            return true;
        }

        public static int ProcessorCount
        {
            get
            {
                var processorCount = Environment.ProcessorCount;
//#if DEBUG
//                processorCount = 40;
//#endif
                return processorCount;
            }
        }

        public static IEnumerable<BigInteger> GetProcessorAffinityList()
        {
            var processorCount = ProcessorCount;
            var results = new List<BigInteger>(processorCount + 1);
            results.Add(BigInteger.Zero); // default - all processors

            for (int index = 0; index < processorCount; index++)
            {
                results.Add((BigInteger)Math.Pow(2, index));
            }
            return results;
        }

        // Returns, for each physical CPU package (socket), how many logical
        // processors (threads) belong to it, in the order Windows numbers them -
        // e.g. a single quad-core/8-thread CPU returns [8]; a dual-socket board
        // with two 8-thread CPUs returns [8, 8]. Logical processor numbers are
        // assigned sequentially per package, so the Nth package "owns" a
        // contiguous block of thread indexes. Falls back to a single group
        // covering all threads if the WMI query is unavailable for any reason
        // (e.g. restricted permissions), since that's still a correct - just
        // less organized - affinity list.
        // Cached after the first successful detection so opening the Processor
        // Affinity window repeatedly (or on multiple profiles) doesn't re-query
        // WMI every time - CPU topology doesn't change while ASM is running.
        private static IList<int> _cachedProcessorPackageThreadCounts = null;
        private static readonly object _processorPackageThreadCountsLock = new object();
        private static bool _processorPackageThreadCountsQueryStarted = false;

        // Called once at application startup (see App.xaml.cs) so the CPU topology
        // is already known by the time anyone opens the Processor Affinity window -
        // no WMI query delay on first use. Safe to call more than once; only the
        // first call actually does the detection.
        public static void WarmProcessorPackageThreadCountsCache()
        {
            StartBackgroundQueryIfNeeded();
        }

        // Never blocks the calling thread on the WMI query itself, even if the
        // cache hasn't been warmed yet - WMI queries are known to occasionally
        // stall for several seconds (repository issues, AV/security software,
        // etc.), and this can get called from the UI thread when someone opens
        // the Processor Affinity window. If the real result isn't ready yet, this
        // returns an immediate single-group fallback (still correct, just less
        // organized) while the real query keeps running in the background for
        // next time.
        public static IList<int> GetProcessorPackageThreadCounts()
        {
            if (_cachedProcessorPackageThreadCounts != null)
                return _cachedProcessorPackageThreadCounts;

            StartBackgroundQueryIfNeeded();

            return new List<int> { ProcessorCount };
        }

        private static void StartBackgroundQueryIfNeeded()
        {
            lock (_processorPackageThreadCountsLock)
            {
                if (_processorPackageThreadCountsQueryStarted || _cachedProcessorPackageThreadCounts != null)
                    return;

                _processorPackageThreadCountsQueryStarted = true;
            }

            System.Threading.Tasks.Task.Run(() =>
            {
                var counts = new List<int>();

                try
                {
                    using (var searcher = new ManagementObjectSearcher("SELECT NumberOfLogicalProcessors FROM Win32_Processor"))
                    {
                        foreach (var item in searcher.Get())
                        {
                            var value = item["NumberOfLogicalProcessors"];
                            if (value != null && int.TryParse(value.ToString(), out var logicalCount) && logicalCount > 0)
                            {
                                counts.Add(logicalCount);
                            }
                        }
                    }
                }
                catch
                {
                    counts.Clear();
                }

                var totalFromWmi = counts.Sum();
                if (counts.Count == 0 || totalFromWmi != ProcessorCount)
                {
                    // WMI didn't return something that adds up to what .NET sees -
                    // safest fallback is one group with everything in it. This also
                    // covers everything from a single-CPU/few-thread machine to an
                    // 8+ thread one, or multiple physical CPUs - whatever the
                    // hardware actually reports.
                    counts.Clear();
                    counts.Add(ProcessorCount);
                }

                _cachedProcessorPackageThreadCounts = counts;
            });
        }

        public static string[] GetProcessPriorityList()
        {
            return new string[] { "low", "belownormal", "normal", "abovenormal", "high" };
        }

        // Converts a profile's ProcessPriority string ("low", "belownormal", etc.)
        // into the real System.Diagnostics.ProcessPriorityClass value needed to
        // actually apply it to a running process.
        public static System.Diagnostics.ProcessPriorityClass GetProcessPriorityClass(string priorityValue)
        {
            switch ((priorityValue ?? string.Empty).ToLowerInvariant())
            {
                case "low": return System.Diagnostics.ProcessPriorityClass.Idle;
                case "belownormal": return System.Diagnostics.ProcessPriorityClass.BelowNormal;
                case "abovenormal": return System.Diagnostics.ProcessPriorityClass.AboveNormal;
                case "high": return System.Diagnostics.ProcessPriorityClass.High;
                default: return System.Diagnostics.ProcessPriorityClass.Normal;
            }
        }

        public static bool IsProcessorAffinityValid(BigInteger affinityValue)
        {
            if (affinityValue == BigInteger.Zero)
                return true;

            var maxaffinity = (BigInteger)Math.Pow(2, ProcessorCount);
            if (affinityValue < BigInteger.Zero || affinityValue > maxaffinity)
                return false;

            return true;
        }

        public static bool IsProcessPriorityValid(string priorityValue)
        {
            if (string.IsNullOrWhiteSpace(priorityValue))
                return false;

            var priorityList = GetProcessPriorityList();
            if (!priorityList.Contains(priorityValue))
                return false;

            return true;
        }
    }
}

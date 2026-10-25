using System;
using System.ComponentModel;
using System.Configuration;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ArkData;
using NLog;
using NLog.Config;
using NLog.Targets;
using ServerManagerTool.Common;
using ServerManagerTool.Common.Utils;
using ServerManagerTool.Enums;
using ServerManagerTool.Lib;
using ServerManagerTool.Plugin.Common;
using ServerManagerTool.Utils;
using ServerManagerTool.Windows;
using WPFSharp.Globalizer;

namespace ServerManagerTool
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : GlobalizedApplication, INotifyPropertyChanged
    {
        public new static App Instance
        {
            get;
            private set;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private GlobalizedApplication _globalizer;
        private bool _applicationStarted;
        private string _args;
        private bool _betaVersion;
        private string _title;
        private string _version;

        public App()
        {
            Instance = this;
            ApplicationStarted = false;
            Args = string.Empty;
            BetaVersion = false;
            Title = string.Empty;
            Version = AppUtils.GetDeployedVersion(Assembly.GetEntryAssembly());
        }

        public bool ApplicationStarted
        {
            get
            {
                return _applicationStarted;
            }
            set
            {
                if (!Equals(value, _applicationStarted))
                {
                    _applicationStarted = value;
                    OnPropertyChanged();
                }
            }
        }

        public string Args
        {
            get
            {
                return _args;
            }
            set
            {
                if (!Equals(value, _args))
                {
                    _args = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool BetaVersion
        {
            get
            {
                return _betaVersion;
            }
            set
            {
                if (!Equals(value, _betaVersion))
                {
                    _betaVersion = value;
                    OnPropertyChanged();
                }
            }
        }

        public string Title
        {
            get
            {
                return _title;
            }
            set
            {
                if (!Equals(value, _title))
                {
                    _title = value;
                    OnPropertyChanged();
                }
            }
        }

        public string Version
        {
            get
            {
                return _version;
            }
            set
            {
                if (!Equals(value, _version))
                {
                    _version = value;
                    OnPropertyChanged();
                }
            }
        }

        private async Task CallHomeAsync()
        {
            try
            {
                var publicIP = await NetworkUtils.DiscoverPublicIPAsync();
                var url = new Uri(string.Format(Config.Default.ServerCallUrlFormat, Config.Default.ServerManagerCode, publicIP));
                await NetworkUtils.PerformCallToAPIAsync(url);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed calling home to API.\r\n{ex.Message}");
            }
        }

        private void CheckForValidSettings()
        {
            try
            {
                // test one property from both setting files
                var upgradeConfigTest = Config.Default.UpgradeConfig;
                upgradeConfigTest = CommonConfig.Default.UpgradeConfig;

                // no issues fetching the setting values, must have valid files.
                return;
            }
            catch (ConfigurationErrorsException ex)
            {
                // one or more issues occurred when fetching the setting values.
                // we need to delete the setting files.

                ConfigurationErrorsException exception = ex;
                while (exception != null)
                {
                    if (!string.IsNullOrWhiteSpace(exception.Filename) && exception.Filename.EndsWith("user.config"))
                    {
                        File.Delete(exception.Filename);
                    }

                    exception = exception.InnerException as ConfigurationErrorsException;
                }

                throw;
            }
        }

        public static void DiscoverMachinePublicIP(bool forceOverride)
        {
            if (forceOverride || string.IsNullOrWhiteSpace(Config.Default.MachinePublicIP))
            {
                var publicIP = NetworkUtils.DiscoverPublicIP();
                if (string.IsNullOrWhiteSpace(publicIP))
                    return;

                if (!Config.Default.MachinePublicIP.Equals(publicIP, StringComparison.OrdinalIgnoreCase))
                {
                    Config.Default.MachinePublicIP = publicIP;
                }
            }
        }

        public static async Task DiscoverMachinePublicIPAsync(bool forceOverride)
        {
            if (forceOverride || string.IsNullOrWhiteSpace(Config.Default.MachinePublicIP))
            {
                var publicIP = await NetworkUtils.DiscoverPublicIPAsync();
                if (string.IsNullOrWhiteSpace(publicIP))
                    return;

                if (!Config.Default.MachinePublicIP.Equals(publicIP, StringComparison.OrdinalIgnoreCase))
                {
                    await App.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        Config.Default.MachinePublicIP = publicIP;
                    }));
                }
            }
        }

        public static string GetLogFolder() => IOUtils.NormalizePath(Path.Combine(Config.Default.DataDir, Config.Default.LogsDir));

        public static string GetProfileLogFolder(string profileId) => IOUtils.NormalizePath(Path.Combine(Config.Default.DataDir, Config.Default.LogsDir, profileId.ToLower()));

        public static Logger GetProfileLogger(string profileId, string logName, LogLevel minLevel, LogLevel maxLevel)
        {
            if (string.IsNullOrWhiteSpace(profileId) || string.IsNullOrWhiteSpace(logName))
                return null;

            var loggerName = $"{profileId.ToLower()}_{logName}".Replace(" ", "_");

            if (LogManager.Configuration.FindTargetByName(loggerName) == null)
            {
                var logFilePath = GetProfileLogFolder(profileId);

                var logFile = new FileTarget(loggerName)
                {
                    FileName = Path.Combine(logFilePath, $"{logName}.log"),
                    Layout = "${time} [${level:uppercase=true}] ${message}",
                    ArchiveFileName = Path.Combine(logFilePath, $"{logName}.{{#}}.log"),
                    ArchiveNumbering = ArchiveNumberingMode.DateAndSequence,
                    ArchiveEvery = FileArchivePeriod.Day,
                    ArchiveDateFormat = "yyyyMMdd",
                    ArchiveOldFileOnStartup = true,
                    MaxArchiveFiles = Config.Default.LoggingMaxArchiveFiles,
                    MaxArchiveDays = Config.Default.LoggingMaxArchiveDays,
                    CreateDirs = true,
                };
                LogManager.Configuration.AddTarget(loggerName, logFile);

                var rule = new LoggingRule(loggerName, minLevel, maxLevel, logFile);
                LogManager.Configuration.LoggingRules.Add(rule);
                LogManager.ReconfigExistingLoggers();
            }

            return LogManager.GetLogger(loggerName);
        }

        private static void MigrateSettings()
        {
            var installFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            //
            // Migrate settings when we update.
            //
            // IMPORTANT: Upgrade() copies values from the PREVIOUS version's saved
            // settings into memory, but does not persist them. A Reload() called
            // right after it re-reads from the NEW version's on-disk store - which,
            // being brand new, has nothing saved yet - and wipes the just-migrated
            // values back to hard-coded defaults before they're ever used. That's
            // why settings (e.g. Global Settings checkboxes) appeared to reset on
            // every update. Save() must happen before any Reload().
            if (CommonConfig.Default.UpgradeConfig)
            {
                var settingsFile = IOUtils.NormalizePath(Path.Combine(installFolder, "commonconfig.json"));

                CommonConfig.Default.Upgrade();
                SettingsUtils.MigrateSettings(CommonConfig.Default, settingsFile);
                CommonConfig.Default.UpgradeConfig = false;
                CommonConfig.Default.Save();
                CommonConfig.Default.Reload();
            }
            if (Config.Default.UpgradeConfig)
            {
                var settingsFile = IOUtils.NormalizePath(Path.Combine(installFolder, "userconfig.json"));

                Config.Default.Upgrade();
                SettingsUtils.MigrateSettings(Config.Default, settingsFile);
                Config.Default.UpgradeConfig = false;
                Config.Default.Save();
                Config.Default.Reload();
            }

            Config.Default.SteamCmdRedirectOutput = false;
        }

        private void CheckForVisualCppRedistributable()
        {
            // ASM itself is plain .NET Framework/WPF and needs nothing beyond
            // that to run (if this method is executing, .NET Framework is
            // already fine). This is specifically a safety net for optional
            // third-party native components (some NuGet packages ship native
            // DLLs compiled with MSVC) that could otherwise fail in a confusing,
            // hard-to-diagnose way later rather than up front. No Node.js is
            // needed anywhere in ASM or its Discord bot - that's a JavaScript/
            // discord.js thing; ours is plain C#/.NET (Discord.Net), so there's
            // nothing to check or install for that.
            try
            {
                if (IsVisualCppRedistributableInstalled())
                    return;

                var result = MessageBox.Show(
                    "The Microsoft Visual C++ Redistributable (x64) doesn't appear to be installed. Most of ARK Server Manager will work fine without it, but some features that use third-party components may fail in confusing ways if it's missing.\r\n\r\nWould you like to open the official Microsoft download page now? (ASM will not download or install anything on its own.)",
                    "Optional Dependency Check",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (result == MessageBoxResult.Yes)
                {
                    Process.Start(new ProcessStartInfo("https://aka.ms/vs/17/release/vc_redist.x64.exe") { UseShellExecute = true });
                }
            }
            catch
            {
                // Never let this optional check block startup.
            }
        }

        private bool IsVisualCppRedistributableInstalled()
        {
            // Standard, well-documented registry locations Microsoft's own VC++
            // Redistributable installers write to - checking these directly
            // avoids needing to actually load any native DLL just to find out.
            string[] keyPaths =
            {
                @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64",
                @"SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\X64",
                @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X86",
                @"SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\X86",
            };

            foreach (var keyPath in keyPaths)
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(keyPath))
                {
                    if (key?.GetValue("Installed") is int installed && installed == 1)
                        return true;
                }
            }

            return false;
        }

        // The ARK server binary links against the legacy DirectX End-User
        // Runtime (the June 2010 redist) - XAPOFX1_5.dll and friends. Those
        // are NOT part of the DirectX that ships with Windows 10/11 and are
        // NOT installed by any Visual C++ redistributable, so a freshly built
        // server box that has never had a DX9-era game on it doesn't have
        // them. The server then dies at launch with a bare Windows dialog
        // ("The code execution cannot proceed because XAPOFX1_5.dll was not
        // found"), which looks like a broken ARK install rather than a missing
        // OS component. Checked once at startup so it's dealt with before the
        // first Start rather than after it fails.
        private const string DirectXWebSetupUrl = "https://download.microsoft.com/download/1/7/1/1718CCC4-6315-4D8E-9543-8E28A4E18C4C/dxwebsetup.exe";

        private void CheckForDirectXRuntime()
        {
            try
            {
                if (IsDirectXRuntimeInstalled())
                    return;

                var result = MessageBox.Show(
                    "The DirectX End-User Runtime doesn't appear to be installed. ARK servers need it (XAPOFX1_5.dll) and will fail to start without it - Windows and the Visual C++ redistributables do not include it.\r\n\r\n" +
                    "Would you like the Server Manager to download Microsoft's official DirectX installer and open it now? The installer's own window will appear - accept its licence terms and click Install to continue.\r\n\r\n" +
                    "Choose No to skip - you can install it yourself from Microsoft at any time.",
                    "DirectX Runtime Required",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes)
                    return;

                InstallDirectXRuntime();
            }
            catch (Exception ex)
            {
                // Never let this check block startup - a failure here just
                // means the user has to install DirectX themselves, which is
                // exactly where they'd have been without it.
                LogManager.GetCurrentClassLogger().Error($"{nameof(CheckForDirectXRuntime)} - {ex.Message}");
            }
        }

        private bool IsDirectXRuntimeInstalled()
        {
            // There is no reliable registry marker for the legacy runtime
            // (the June 2010 redist writes per-component version data that has
            // moved around over the years), so the file itself is the honest
            // check. The ARK server is 64-bit and needs the 64-bit copy from
            // the real System32 - which, if this process happens to be running
            // 32-bit, is reachable only via the Sysnative alias since
            // System32 gets redirected to SysWOW64 underneath us.
            var windowsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var systemFolder = Path.Combine(windowsFolder, Environment.Is64BitProcess ? "System32" : "Sysnative");

            return File.Exists(Path.Combine(systemFolder, "XAPOFX1_5.dll"));
        }

        private void InstallDirectXRuntime()
        {
            var installerPath = Path.Combine(Path.GetTempPath(), "dxwebsetup.exe");

            try
            {
                using (var client = new WebClient())
                {
                    // .NET Framework's default protocol selection predates
                    // Microsoft's download endpoints dropping the older TLS
                    // versions, so it has to be asked for explicitly here.
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                    client.DownloadFile(DirectXWebSetupUrl, installerPath);
                }
            }
            catch (Exception ex)
            {
                LogManager.GetCurrentClassLogger().Error($"{nameof(InstallDirectXRuntime)} - download failed. {ex.Message}");
                MessageBox.Show(
                    $"Could not download the DirectX installer.\r\n\r\n{ex.Message}\r\n\r\nYou can install it manually from Microsoft, then restart the Server Manager.",
                    "DirectX Runtime Required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            // This is about to be run elevated, so confirm it really is
            // Microsoft's binary and not something a hijacked DNS entry or a
            // proxy handed back instead. Cheap to check, and the alternative
            // is executing whatever arrived with full privileges.
            try
            {
                var certificate = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(installerPath);
                if (certificate.Subject.IndexOf("O=Microsoft Corporation", StringComparison.OrdinalIgnoreCase) < 0)
                    throw new System.Security.SecurityException($"unexpected publisher: {certificate.Subject}");
            }
            catch (Exception ex)
            {
                LogManager.GetCurrentClassLogger().Error($"{nameof(InstallDirectXRuntime)} - signature check failed. {ex.Message}");
                try { File.Delete(installerPath); } catch { }

                MessageBox.Show(
                    $"The downloaded DirectX installer could not be verified as Microsoft's and has not been run.\r\n\r\n{ex.Message}\r\n\r\nPlease install the DirectX End-User Runtime manually from Microsoft.",
                    "DirectX Runtime Required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            try
            {
                // Run with Microsoft's own installer UI rather than silently,
                // so the licence terms are presented and accepted by the user
                // rather than on their behalf, and so the component download
                // (which can take a minute or two on a slow connection) shows
                // its own progress instead of looking like a hang.
                var startInfo = new ProcessStartInfo(installerPath)
                {
                    UseShellExecute = true,
                    Verb = SecurityUtils.IsAdministrator() ? string.Empty : "runas",
                };

                using (var process = Process.Start(startInfo))
                {
                    process?.WaitForExit();
                }
            }
            catch (Exception ex)
            {
                LogManager.GetCurrentClassLogger().Error($"{nameof(InstallDirectXRuntime)} - installer failed. {ex.Message}");
                MessageBox.Show(
                    $"The DirectX installer could not be run.\r\n\r\n{ex.Message}",
                    "DirectX Runtime Required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }
            finally
            {
                try { File.Delete(installerPath); } catch { }
            }

            if (IsDirectXRuntimeInstalled())
            {
                MessageBox.Show(
                    "The DirectX End-User Runtime was installed successfully.",
                    "DirectX Runtime Required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(
                    "XAPOFX1_5.dll still isn't present - the installer was either cancelled or did not complete. Your servers will not start until the DirectX End-User Runtime is installed. The Server Manager will offer this again next time it starts.",
                    "DirectX Runtime Required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            CheckForVisualCppRedistributable();
            CheckForDirectXRuntime();

            try
            {
                CheckForValidSettings();
            }
            catch (Exception ex)
            {
                var message = $"{ex.Message}\r\n\r\nTry restarting the server manager, if this keeps happening please report this crash to the Server Manager discord.";
                var result = MessageBox.Show(message, "Server Manager crashed", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                Environment.Exit(1);
            }

            MigrateSettings();

            // Collects CPU package/thread topology once here (via WMI, off the UI
            // thread since it can take a moment) so the Processor Affinity window
            // has it ready instantly later, whether this machine has 1 CPU with a
            // handful of threads or several CPUs with many more.
            System.Threading.Tasks.Task.Run(() => ServerManagerTool.Common.Utils.ProcessUtils.WarmProcessorPackageThreadCountsCache());

            if (string.IsNullOrWhiteSpace(Config.Default.ServerManagerUniqueKey))
            {
                Config.Default.ServerManagerUniqueKey = Guid.NewGuid().ToString();
            }

            if (!string.IsNullOrWhiteSpace(Config.Default.DataDir))
            {
                Config.Default.DataDir = IOUtils.NormalizeFolder(Config.Default.DataDir);
            }

            if (!string.IsNullOrWhiteSpace(Config.Default.ConfigDirectory))
            {
                Config.Default.ConfigDirectory = IOUtils.NormalizeFolder(Config.Default.ConfigDirectory);
            }

            if (!string.IsNullOrWhiteSpace(Config.Default.BackupPath))
            {
                Config.Default.BackupPath = IOUtils.NormalizeFolder(Config.Default.BackupPath);
            }

            if (!string.IsNullOrWhiteSpace(Config.Default.AutoUpdate_CacheDir))
            {
                Config.Default.AutoUpdate_CacheDir = IOUtils.NormalizeFolder(Config.Default.AutoUpdate_CacheDir);
            }

            ServicePointManager.SecurityProtocol = SecurityUtils.GetSecurityProtocol(Config.Default.ServicePointManager_SecurityProtocol);

            AppDomain.CurrentDomain.UnhandledException += ErrorHandling.CurrentDomain_UnhandledException;

            ReconfigureLogging();

            _globalizer = GlobalizedApplication.Instance;
            try
            {
                var langCode = GlobalizationManager.FallBackLanguage;

                if (string.IsNullOrWhiteSpace(Config.Default.DataDir))
                    langCode = CultureInfo.CurrentCulture.Name;
                else if (!string.IsNullOrWhiteSpace(Config.Default.CultureName))
                    langCode = Config.Default.CultureName;

                _globalizer.GlobalizationManager.SwitchLanguage(langCode, true);
            }
            catch (Exception ex)
            {
                // just output the exception message, it should default back to the fallback language.
                Debug.WriteLine(ex.Message);
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(Config.Default.StyleName))
                    _globalizer.StyleManager.SwitchStyle(Config.Default.StyleName, true);
            }
            catch (Exception ex)
            {
                // just output the exception message, it should default back to the fallback style.
                Debug.WriteLine(ex.Message);
            }

            TaskSchedulerUtils.TaskFolder = Config.Default.ScheduledTaskFolder;

            Args = string.Join(" ", e.Args);

            // check if we are starting server manager in BETA/TEST mode
            if (e.Args.Any(a => a.Equals(Constants.ARG_BETA, StringComparison.OrdinalIgnoreCase) || a.Equals(Constants.ARG_TEST, StringComparison.OrdinalIgnoreCase)))
            {
                BetaVersion = true;
            }

            // check if we need to set the title
            if (e.Args.Any(a => a.Equals(Constants.ARG_TITLE, StringComparison.OrdinalIgnoreCase)))
            {
                for (int i = 0; i < e.Args.Length - 1; i++)
                {
                    if (e.Args[i].Equals(Constants.ARG_TITLE, StringComparison.OrdinalIgnoreCase) && i < e.Args.Length - 1 && !e.Args[i + 1].StartsWith("-"))
                    {
                        Title = e.Args[i + 1].Trim();
                    }
                }
            }

            // check and update the public IP address
            DiscoverMachinePublicIP(Config.Default.ManagePublicIPAutomatically);

            if (string.IsNullOrWhiteSpace(Config.Default.RCON_BackupMessageCommand))
            {
                Config.Default.RCON_BackupMessageCommand = Config.Default.RCON_MessageCommand;
            }

            var installPath = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
            PluginHelper.Instance.BetaEnabled = this.BetaVersion;
            PluginHelper.Instance.LoadPlugins(installPath, true);
            PluginHelper.Instance.SetFetchProfileCallback(DiscordPluginHelper.FetchProfiles);
            OnResourceDictionaryChanged(Thread.CurrentThread.CurrentCulture.Name);

            // check if we are starting server manager for the old server restart - no longer supported
            if (e.Args.Any(a => a.StartsWith(Constants.ARG_AUTORESTART, StringComparison.OrdinalIgnoreCase)))
            {
                // just exit
                Environment.Exit(0);
            }

            // check if we are starting server manager for server shutdown
            if (e.Args.Any(a => a.StartsWith(Constants.ARG_AUTOSHUTDOWN1, StringComparison.OrdinalIgnoreCase)))
            {
                var arg = e.Args.FirstOrDefault(a => a.StartsWith(Constants.ARG_AUTOSHUTDOWN1, StringComparison.OrdinalIgnoreCase));
                var exitCode = ServerApp.PerformAutoShutdown(arg, ServerProcessType.AutoShutdown1);

                // once we are finished, just exit
                Environment.Exit(exitCode);
            }

            // check if we are starting server manager for server shutdown
            if (e.Args.Any(a => a.StartsWith(Constants.ARG_AUTOSHUTDOWN2, StringComparison.OrdinalIgnoreCase)))
            {
                var arg = e.Args.FirstOrDefault(a => a.StartsWith(Constants.ARG_AUTOSHUTDOWN2, StringComparison.OrdinalIgnoreCase));
                var exitCode = ServerApp.PerformAutoShutdown(arg, ServerProcessType.AutoShutdown2);

                // once we are finished, just exit
                Environment.Exit(exitCode);
            }

            // check if we are starting server manager for server updating
            if (e.Args.Any(a => a.Equals(Constants.ARG_AUTOUPDATE, StringComparison.OrdinalIgnoreCase)))
            {
                // PVE/PVP are now two separate scheduled tasks, each passing
                // its own ruleset as the argument right after ARG_AUTOUPDATE
                // (see TaskSchedulerUtils.ScheduleAutoUpdate) - no suffix at
                // all means process both (manual/legacy invocation).
                bool? onlyPVP = null;
                if (e.Args.Any(a => a.Equals("PVE", StringComparison.OrdinalIgnoreCase)))
                    onlyPVP = false;
                else if (e.Args.Any(a => a.Equals("PVP", StringComparison.OrdinalIgnoreCase)))
                    onlyPVP = true;

                var exitCode = ServerApp.PerformAutoUpdate(onlyPVP);

                // once we are finished, just exit
                Environment.Exit(exitCode);
            }

            // check if we are starting server manager for server backups
            if (e.Args.Any(a => a.Equals(Constants.ARG_AUTOBACKUP, StringComparison.OrdinalIgnoreCase)))
            {
                // PVE/PVP are now two separate scheduled tasks, each passing
                // its own ruleset as the argument right after ARG_AUTOBACKUP
                // (see TaskSchedulerUtils.ScheduleAutoBackup) - no suffix at
                // all means process both (manual/legacy invocation).
                bool? onlyPVP = null;
                if (e.Args.Any(a => a.Equals("PVE", StringComparison.OrdinalIgnoreCase)))
                    onlyPVP = false;
                else if (e.Args.Any(a => a.Equals("PVP", StringComparison.OrdinalIgnoreCase)))
                    onlyPVP = true;

                // A forced run (manual "Run" button) is signaled via a
                // marker file rather than a command-line flag - see
                // AutoBackupScheduleUtils.SetForceNextRun/ConsumeForceNextRun
                // and MainWindow.AutoBackupTaskRun_Click for why.
                var exitCode = ServerApp.PerformAutoBackup(onlyPVP);

                // once we are finished, just exit
                Environment.Exit(exitCode);
            }

            if (Config.Default.RunAsAdministratorPrompt && !SecurityUtils.IsAdministrator())
            {
                var result = MessageBox.Show(_globalizer.GetResourceString("Application_RunAsAdministratorLabel"), _globalizer.GetResourceString("Application_RunAsAdministratorTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    var processInfo = new ProcessStartInfo(Assembly.GetExecutingAssembly().CodeBase)
                    {

                        // The following properties run the new process as administrator
                        UseShellExecute = true,
                        Verb = "runas",
                        Arguments = string.Join(" ", e.Args)
                    };

                    // Start the new process
                    try
                    {
                        Process.Start(processInfo);

                        // Shut down the current process
                        Application.Current.Shutdown(0);

                        return;
                    }
                    catch (Exception)
                    {
                        // The user did not allow the application to run as administrator
                        MessageBox.Show(_globalizer.GetResourceString("Application_RunAsAdministrator_FailedLabel"), _globalizer.GetResourceString("Application_RunAsAdministrator_FailedTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }

            // check if application is already running
            if (Config.Default.CheckIfServerManagerRunningOnStartup && ProcessUtils.IsAlreadyRunning())
            {
                var result = MessageBox.Show(_globalizer.GetResourceString("Application_SingleInstanceLabel"), _globalizer.GetResourceString("Application_SingleInstanceTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    if (ProcessUtils.SwitchToCurrentInstance())
                    {
                        // Shut down the current process
                        Application.Current.Shutdown(0);

                        return;
                    }

                    MessageBox.Show(_globalizer.GetResourceString("Application_SingleInstance_FailedLabel"), _globalizer.GetResourceString("Application_SingleInstance_FailedTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }

            ApplicationStarted = true;

            var restartRequired = false;
            if (string.IsNullOrWhiteSpace(Config.Default.DataDir))
            {
                var dataDirectoryWindow = new DataDirectoryWindow();
                dataDirectoryWindow.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                var result = dataDirectoryWindow.ShowDialog();

                if (!result.HasValue || !result.Value)
                {
                    Environment.Exit(0);
                }

                restartRequired = true;
            }

            Config.Default.ConfigDirectory = Path.Combine(Config.Default.DataDir, Config.Default.ProfilesDir);            
            System.IO.Directory.CreateDirectory(Config.Default.ConfigDirectory);
            SaveConfigFiles();

            if (restartRequired)
            {
                Environment.Exit(0);
            }

            DataFileDetails.PlayerFileExtension = Config.Default.PlayerFileExtension;
            DataFileDetails.TribeFileExtension = Config.Default.TribeFileExtension;

            if (e.Args.Any(a => a.StartsWith(Constants.ARG_SERVERMONITOR, StringComparison.OrdinalIgnoreCase)))
            {
                ServerRuntime.EnableUpdateModStatus = false;
                ServerProfile.EnableServerFilesWatcher = false;

                StartupUri = new Uri("Windows/ServerMonitorWindow.xaml", UriKind.RelativeOrAbsolute);
            }
            else
            {
                // initialize all the game data
                GameData.Initialize();

                // player profile backups (watches the running servers over RCON)
                PlayerProfileBackup.Start();

                StartupUri = new Uri("Windows/AutoUpdateWindow.xaml", UriKind.RelativeOrAbsolute);
            }


            if (Config.Default.WebDashboardEnabled)
            {
                WebDashboard.Start();
            }

            if (Config.Default.ServerCallUrlLast.AddHours(Config.Default.ServerCallUrlDelay) < DateTime.Now)
            {
                CallHomeAsync().DoNotWait();

                Config.Default.ServerCallUrlLast = DateTime.Now;
                Config.Default.Save();
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            ShutDownApplication();

            base.OnExit(e);

            // ShutDownApplication() signals background work (scheduled-task loops, RCON connections) to stop, but several of
            // those signals aren't awaited before we get here, since waiting on
            // them here would delay the whole app closing. Any of them running
            // long (e.g. a slow RCON timeout) can keep the process alive in Task
            // Manager well after the window has closed. Everything that matters
            // has already been saved above, so it's safe to force the process to
            // actually end here rather than leaving that to chance.
            Environment.Exit(0);
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public void OnResourceDictionaryChanged(string languageCode)
        {
            PluginHelper.Instance.OnResourceDictionaryChanged(languageCode);
        }

        public static void ReconfigureLogging()
        {
            if (string.IsNullOrWhiteSpace(Config.Default.DataDir))
                return;

            UpdateLoggingStatus();

            var logDir = Path.Combine(Config.Default.DataDir, Config.Default.LogsDir);
            if (!System.IO.Directory.Exists(logDir))
                System.IO.Directory.CreateDirectory(logDir);

            LogManager.Configuration.Variables["logDir"] = logDir;

            foreach (var fileTarget in LogManager.Configuration.AllTargets.OfType<FileTarget>())
            {
                var fileName = Path.GetFileNameWithoutExtension(fileTarget.FileName.ToString());
                fileTarget.FileName = Path.Combine(logDir, $"{fileName}.log");
                fileTarget.ArchiveFileName = Path.Combine(logDir, $"{fileName}.{{#}}.log");
                fileTarget.MaxArchiveFiles = Config.Default.LoggingMaxArchiveFiles;
                fileTarget.MaxArchiveDays = Config.Default.LoggingMaxArchiveDays;
                fileTarget.CreateDirs = true;
            }

            LogManager.ReconfigExistingLoggers();
        }

        public static void SaveConfigFiles(bool includeBackup = true)
        {
            Config.Default.Save();
            CommonConfig.Default.Save();

            Config.Default.Reload();
            CommonConfig.Default.Reload();

            var installFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var backupFolder = string.IsNullOrWhiteSpace(Config.Default.BackupPath)
                        ? Path.Combine(Config.Default.DataDir, Config.Default.BackupDir)
                        : Path.Combine(Config.Default.BackupPath);

            SettingsUtils.BackupUserConfigSettings(Config.Default, "userconfig.json", installFolder, includeBackup ? backupFolder : null);
            SettingsUtils.BackupUserConfigSettings(CommonConfig.Default, "commonconfig.json", installFolder, includeBackup ? backupFolder : null);

            SettingsUtils.DeleteBackupUserConfigFiles("userconfig.json", backupFolder, Config.Default.AutoBackup_DeleteInterval);
            SettingsUtils.DeleteBackupUserConfigFiles("commonconfig.json", backupFolder, Config.Default.AutoBackup_DeleteInterval);
        }

        private void ShutDownApplication()
        {
            WebDashboard.Stop();

            // Auto-Backup/Auto-Update run as their own separate headless
            // processes launched by their scheduled tasks. If either happens
            // to be actively running right as the app closes, stop that
            // instance too rather than leaving it as an orphaned process the
            // user has no way to see or control once the GUI is gone. This
            // only stops a run already in progress - it does not disable or
            // otherwise change the schedules themselves, so future runs
            // still happen normally.
            try
            {
                var taskKey = TaskSchedulerUtils.ComputeKey(Config.Default.DataDir);
                TaskSchedulerUtils.StopAutoBackup(taskKey, "PVE");
                TaskSchedulerUtils.StopAutoBackup(taskKey, "PVP");
                TaskSchedulerUtils.StopAutoUpdate(taskKey, "PVE");
                TaskSchedulerUtils.StopAutoUpdate(taskKey, "PVP");
            }
            catch (Exception ex)
            {
                LogManager.GetCurrentClassLogger().Error($"{nameof(ShutDownApplication)} - Unable to stop in-progress scheduled tasks. {ex.Message}\r\n{ex.StackTrace}");
            }

            if (ApplicationStarted)
            {
                foreach (var server in ServerManager.Instance.Servers)
                {
                    try
                    {
                        server.Profile.Save(false, false, null);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(String.Format(_globalizer.GetResourceString("Application_Profile_SaveFailedLabel"), server.Profile.ProfileName, ex.Message, ex.StackTrace), _globalizer.GetResourceString("Application_Profile_SaveFailedTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                SaveConfigFiles();
            }

            PluginHelper.Instance?.Dispose();
            LogManager.Flush();
            LogManager.Shutdown();

            ApplicationStarted = false;
        }

        public static void UpdateLoggingStatus()
        {
            if (Config.Default.LoggingEnabled)
            {
                while (!LogManager.IsLoggingEnabled())
                    LogManager.ResumeLogging();
            }
            else
            {
                while (LogManager.IsLoggingEnabled())
                    LogManager.SuspendLogging();
            }

            Debug.WriteLine($"Logging Enabled: {LogManager.IsLoggingEnabled()}");
        }
    }
}

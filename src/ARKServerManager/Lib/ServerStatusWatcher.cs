using Newtonsoft.Json.Linq;
using NLog;
using QueryMaster;
using ServerManagerTool.Common.Enums;
using ServerManagerTool.Common.Utils;
using ServerManagerTool.Enums;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace ServerManagerTool.Lib
{
    public class ServerStatusWatcher
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private readonly List<ServerStatusUpdateRegistration> _serverRegistrations = new List<ServerStatusUpdateRegistration>();
        private readonly ActionBlock<Func<Task>> _eventQueue;
        private readonly Dictionary<string, DateTime> _nextExternalStatusQuery = new Dictionary<string, DateTime>();

        // The RCON-based player count (see GetRconPlayerCount) opens a fresh
        // RCON connection every time it's called. Doing that on the same 5
        // second cadence as the Source-query status check, for every
        // registered server, turned out to be enough RCON connection churn
        // on a large fleet to make ARK's own RCON handling flaky - which in
        // turn made the Source query itself start failing too, showing as
        // servers stuck "Unknown" (blue) even though they were running fine.
        // Throttled to its own, much slower interval instead, reusing the
        // last known count in between rather than re-querying every tick.
        private static readonly TimeSpan RconPlayerCountInterval = TimeSpan.FromSeconds(30);
        private readonly Dictionary<string, DateTime> _nextRconPlayerCountQuery = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, int> _lastRconPlayerCount = new Dictionary<string, int>();

        static ServerStatusWatcher()
        {
            Instance = new ServerStatusWatcher();
        }

        private ServerStatusWatcher()
        {
            _eventQueue = new ActionBlock<Func<Task>>(async f => await f.Invoke(), new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = 1 });
            _eventQueue.Post(DoUpdateAsync);
        }

        public static ServerStatusWatcher Instance
        {
            get;
            private set;
        }

        public IAsyncDisposable RegisterForUpdates(string installDirectory, string profileId, IPEndPoint localEndpoint, IPEndPoint steamEndpoint, Action<IAsyncDisposable, ServerStatusUpdate> updateCallback, bool rconEnabled = false, int rconPort = 0, string rconPassword = null)
        {
            var registration = new ServerStatusUpdateRegistration
            {
                InstallDirectory = installDirectory,
                ProfileId = profileId,
                LocalEndpoint = localEndpoint,
                PublicEndpoint = steamEndpoint,
                UpdateCallback = updateCallback,
                RCONEnabled = rconEnabled,
                RCONPort = rconPort,
                RCONPassword = rconPassword,
            };

            registration.UnregisterAction = async () => 
                {
                    var tcs = new TaskCompletionSource<bool>();
                    _eventQueue.Post(() => 
                    {
                        if(_serverRegistrations.Contains(registration))
                        {
                            Logger.Debug($"{nameof(RegisterForUpdates)} Removing registration for L:{registration.LocalEndpoint} P:{registration.PublicEndpoint}");
                            _serverRegistrations.Remove(registration);
                        }
                        tcs.TrySetResult(true);
                        return Task.FromResult(true);
                    });

                    await tcs.Task;
                };

            _eventQueue.Post(() =>
                {
                    if (!_serverRegistrations.Contains(registration))
                    {
                        Logger.Debug($"{nameof(RegisterForUpdates)} Adding registration for L:{registration.LocalEndpoint} P:{registration.PublicEndpoint}");
                        _serverRegistrations.Add(registration);

                        var registrationKey = registration.PublicEndpoint.ToString();
                        _nextExternalStatusQuery[registrationKey] = DateTime.MinValue;
                    }
                    return Task.FromResult(true);
                }
            );

            return registration;
        }

        private async Task DoUpdateAsync()
        {
            try
            {
                // One WMI query covering every ShooterGameServer process,
                // shared across every profile's check this tick - instead of
                // each of the (potentially 20+) registered profiles running
                // its own per-process WMI lookup over that same process list.
                // That per-profile, per-process pattern was O(registrations x
                // processes) WMI round-trips every tick, which is what was
                // overloading WmiPrvSE.exe (WMI Provider Host) and producing
                // WBEM_E_CALL_CANCELLED failures in the WMI-Activity log on a
                // large fleet. This is O(1) per tick instead.
                var processCommandLines = ProcessUtils.GetCommandLinesForProcessName(Config.Default.ServerProcessName + ".exe");

                // Check every registered server concurrently rather than one
                // at a time. Each check is an independent socket call with up
                // to an 8 second timeout (see ServerStatusWatcher_QueryTimeout)
                // - sequentially, a handful of slow/unresponsive servers on a
                // large fleet could back up the whole queue and delay the
                // status update for every other (perfectly healthy) server
                // behind them, showing widely as stuck "Unknown" (blue). This
                // event queue only ever runs one action at a time anyway (see
                // MaxDegreeOfParallelism = 1 above), so _serverRegistrations
                // can't be mutated while this is running - safe to fan out.
                var updateTasks = _serverRegistrations.Select(async registration =>
                {
                    var statusUpdate = new ServerStatusUpdate();

                    try
                    {
                        Logger.Debug($"{nameof(DoUpdateAsync)} Start: {registration.LocalEndpoint}, {registration.PublicEndpoint}");
                        statusUpdate = await DoServerStatusUpdateAsync(registration, processCommandLines);

                        PostServerStatusUpdate(registration, statusUpdate);
                    }
                    catch (Exception ex)
                    {
                        // We don't want to stop other registration queries or break the ActionBlock.
                        // Debugger.Break() here (as this used to do) is fine with a debugger attached,
                        // but in a normal running build it can trigger a "debug this program?" prompt
                        // or hang this background thread waiting for one - with no visible UI on this
                        // thread, that silently freezes status updates for every server, recoverable
                        // only by killing and restarting the whole app. Only actually break when a
                        // debugger is already attached, i.e. only during development.
                        Logger.Error($"{nameof(DoUpdateAsync)} - Exception in local update. {ex.Message}\r\n{ex.StackTrace}");
                        if (Debugger.IsAttached)
                            Debugger.Break();
                    }
                    finally
                    {
                        Logger.Debug($"{nameof(DoUpdateAsync)} End: {registration.LocalEndpoint}, {registration.PublicEndpoint}, Status: {statusUpdate.Status}");
                    }
                }).ToList();

                await Task.WhenAll(updateTasks);
            }
            finally
            {
                Task.Delay(Config.Default.ServerStatusWatcher_LocalStatusQueryDelay)
                    .ContinueWith(_ => _eventQueue.Post(DoUpdateAsync))
                    .DoNotWait();
            }
        }

        private async Task<ServerStatusUpdate> DoServerStatusUpdateAsync(ServerStatusUpdateRegistration registration, IReadOnlyDictionary<int, string> processCommandLines)
        {
            var registrationKey = registration.PublicEndpoint.ToString();

            //
            // First check the process status
            //
            var processStatus = GetServerProcessStatus(registration, processCommandLines, out Process process);
            switch (processStatus)
            {
                case ServerProcessStatus.NotInstalled:
                    return new ServerStatusUpdate { Status = WatcherServerStatus.NotInstalled };

                case ServerProcessStatus.Stopped:
                    return new ServerStatusUpdate { Status = WatcherServerStatus.Stopped };

                case ServerProcessStatus.Unknown:
                    return new ServerStatusUpdate { Status = WatcherServerStatus.Unknown };

                case ServerProcessStatus.Running:
                    break;

                default:
                    // Same reasoning as the catch block below - only actually
                    // break with a debugger already attached, not in a
                    // normal running build.
                    if (Debugger.IsAttached)
                        Debugger.Break();
                    break;
            }

            var currentStatus = WatcherServerStatus.Initializing;

            Logger.Debug($"{nameof(DoServerStatusUpdateAsync)} Checking server local (directly) status at {registration.LocalEndpoint}");

            // get the server information direct from the server using local endpoint.
            var serverStatus = GetLocalNetworkStatus(registration.LocalEndpoint, out ServerInfo localInfo, out int onlinePlayerCount);

            if (serverStatus && registration.RCONEnabled && registration.RCONPort > 0)
            {
                // The Steam Source Query player list only ever reports
                // Steam-connected players - a cross-platform server with
                // Epic Games players would always undercount without this.
                // RCON's listplayers is a game-level command and reports
                // every connected player regardless of platform, so it
                // replaces the Source query's count whenever RCON is
                // available (falling back to the Source query's count if
                // this fails for any reason, e.g. a transient RCON hiccup).
                //
                // Only actually queried on RconPlayerCountInterval, not every
                // tick - see the comment on that field for why.
                var nextQuery = _nextRconPlayerCountQuery.TryGetValue(registrationKey, out var next) ? next : DateTime.MinValue;
                if (DateTime.UtcNow >= nextQuery)
                {
                    var rconPlayerCount = GetRconPlayerCount(registration.LocalEndpoint.Address, registration.RCONPort, registration.RCONPassword);
                    _nextRconPlayerCountQuery[registrationKey] = DateTime.UtcNow + RconPlayerCountInterval;

                    if (rconPlayerCount.HasValue)
                    {
                        _lastRconPlayerCount[registrationKey] = rconPlayerCount.Value;
                        onlinePlayerCount = rconPlayerCount.Value;
                    }
                    else if (_lastRconPlayerCount.TryGetValue(registrationKey, out var lastKnown))
                    {
                        onlinePlayerCount = lastKnown;
                    }
                }
                else if (_lastRconPlayerCount.TryGetValue(registrationKey, out var lastKnown))
                {
                    onlinePlayerCount = lastKnown;
                }
            }

            if (serverStatus)
            {
                currentStatus = WatcherServerStatus.LocalSuccess;

                Logger.Debug($"{nameof(DoServerStatusUpdateAsync)} Checking server public (directly) status at {registration.PublicEndpoint}");

                // get the server information direct from the server using public endpoint.
                serverStatus = GetPublicNetworkStatusDirectly(registration.PublicEndpoint);

                if (serverStatus)
                {
                    _nextExternalStatusQuery[registrationKey] = DateTime.MinValue;

                    currentStatus = WatcherServerStatus.Published;
                }
                else if (!string.IsNullOrWhiteSpace(Config.Default.ServerStatusUrlFormat))
                {
                    var nextExternalStatusQuery = _nextExternalStatusQuery.ContainsKey(registrationKey) ? _nextExternalStatusQuery[registrationKey] : DateTime.MinValue;
                    if (DateTime.Now >= nextExternalStatusQuery)
                    {
                        Logger.Debug($"{nameof(DoServerStatusUpdateAsync)} Checking server public (externally) status at {registration.PublicEndpoint}");

                        // get the server information direct from the server using external endpoint.
                        var url = string.Format(Config.Default.ServerStatusUrlFormat, Config.Default.ServerManagerCode, App.Instance.Version, registration.PublicEndpoint.Address, registration.PublicEndpoint.Port);
                        var uri = new Uri(url);
                        serverStatus = await GetPublicNetworkStatusViaAPIAsync(uri, registration.PublicEndpoint);

                        var remoteStatusQueryDelay = serverStatus 
                            ? Config.Default.ServerStatusWatcher_RemoteStatusQueryDelay 
                            : 15000;
                        _nextExternalStatusQuery[registrationKey] = DateTime.Now.AddMilliseconds(remoteStatusQueryDelay);

                        if (serverStatus)
                        {
                            currentStatus = WatcherServerStatus.ExternalSuccess;
                        }
                    }
                    else
                    {
                        currentStatus = WatcherServerStatus.ExternalSkipped;
                    }
                }
            }

            var statusUpdate = new ServerStatusUpdate
            {
                Process = process,
                Status = currentStatus,
                ServerInfo = localInfo,
                OnlinePlayerCount = onlinePlayerCount,
            };

            return await Task.FromResult(statusUpdate);
        }

        private static ServerProcessStatus GetServerProcessStatus(ServerStatusUpdateRegistration updateContext, IReadOnlyDictionary<int, string> processCommandLines, out Process serverProcess)
        {
            serverProcess = null;
            if (string.IsNullOrWhiteSpace(updateContext.InstallDirectory))
            {
                return ServerProcessStatus.NotInstalled;
            }

            var serverExePath = Path.Combine(updateContext.InstallDirectory, Config.Default.ServerBinaryRelativePath, Config.Default.ServerExe);
            if(!File.Exists(serverExePath))
            {
                return ServerProcessStatus.NotInstalled;
            }

            //
            // The server appears to be installed, now determine if it is running or stopped.
            //
            try
            {
                foreach (var process in Process.GetProcessesByName(Config.Default.ServerProcessName))
                {
                    // processCommandLines is fetched once per status tick (one
                    // WMI query covering every ShooterGameServer process) and
                    // reused across every profile's check here, instead of
                    // this per-process, per-profile loop each doing its own
                    // GetCommandLineForProcess WMI round-trip - see
                    // ProcessUtils.GetCommandLinesForProcessName for why that
                    // matters at 20+ servers.
                    var commandLine = (processCommandLines.TryGetValue(process.Id, out var cl) ? cl : null)?.ToLower();

                    if (commandLine != null &&
                        (commandLine.StartsWith(serverExePath, StringComparison.OrdinalIgnoreCase) || commandLine.StartsWith($"\"{serverExePath}\"", StringComparison.OrdinalIgnoreCase)))
                    {
                        // Does this match our server exe and port?
                        var serverArgMatch = string.Format(Config.Default.ServerCommandLineArgsMatchFormat, updateContext.LocalEndpoint.Port).ToLower();
                        if (commandLine.Contains(serverArgMatch))
                        {
                            // Was an IP set on it?
                            var anyIpArgMatch = string.Format(Config.Default.ServerCommandLineArgsIPMatchFormat, string.Empty).ToLower();
                            if (commandLine.Contains(anyIpArgMatch))
                            {
                                // If we have a specific IP, check for it.
                                var ipArgMatch = string.Format(Config.Default.ServerCommandLineArgsIPMatchFormat, updateContext.LocalEndpoint.Address.ToString()).ToLower();
                                if (!commandLine.Contains(ipArgMatch))
                                {
                                    // Specific IP set didn't match
                                    continue;
                                }

                                // Specific IP matched
                            }

                            // Either specific IP matched or no specific IP was set and we will claim this is ours.

                            process.EnableRaisingEvents = true;
                            if (process.HasExited)
                            {
                                return ServerProcessStatus.Stopped;
                            }

                            serverProcess = process;
                            return ServerProcessStatus.Running;
                        }
                    }
                }
            }
            catch(Exception ex)
            {
                Logger.Error($"{nameof(GetServerProcessStatus)}. {ex.Message}\r\n{ex.StackTrace}");
            }

            return ServerProcessStatus.Stopped;
        }

        private static bool GetLocalNetworkStatus(IPEndPoint endpoint, out ServerInfo serverInfo, out int onlinePlayerCount)
        {
            serverInfo = null;
            onlinePlayerCount = 0;

            try
            {
                using (var server = ServerQuery.GetServerInstance(EngineType.Source, endpoint, false, Config.Default.ServerStatusWatcher_QueryTimeout, Config.Default.ServerStatusWatcher_QueryTimeout))
                {
                    try
                    {
                        serverInfo = server?.GetInfo();
                    }
                    catch (Exception)
                    {
                        serverInfo = null;
                    }

                    try
                    {
                        var playerInfo = server?.GetPlayers()?.Where(p => !string.IsNullOrWhiteSpace(p.Name?.Trim()));
                        onlinePlayerCount = playerInfo?.Count() ?? 0;
                    }
                    catch (Exception)
                    {
                        onlinePlayerCount = 0;
                    }
                }

                return serverInfo != null;
            }
            catch (SocketException ex)
            {
                // Common when the server is unreachable.  Ignore it.
                Logger.Debug($"{nameof(GetLocalNetworkStatus)} - Failed checking local status for: {endpoint.Address}:{endpoint.Port}. {ex.Message}");
            }
            catch (Exception ex)
            {
                Logger.Debug($"{nameof(GetLocalNetworkStatus)} - Failed checking local status for: {endpoint.Address}:{endpoint.Port}. {ex.Message}");
            }

            return false;
        }

        // Same "index. name, id" line format ArkApi's RCON console already
        // parses (see ServerRCON.HandleListPlayersCommand) - it matches both
        // Steam64 ids and Epic's longer numeric EOS ids identically, so no
        // platform-specific handling is needed here, just a count of
        // matching lines. Returns null (rather than 0) on any failure to
        // query RCON, so the caller can fall back to the Source query's
        // count instead of reporting a false "0 players".
        private static readonly Regex ListPlayersLineRegex = new Regex(@"^\d*\. .*, \d*\s*$");

        private static int? GetRconPlayerCount(IPAddress address, int rconPort, string rconPassword)
        {
            try
            {
                var endpoint = new IPEndPoint(address, rconPort);
                using (var server = ServerQuery.GetServerInstance(EngineType.Source, endpoint, sendTimeOut: Config.Default.ServerStatusWatcher_QueryTimeout, receiveTimeOut: Config.Default.ServerStatusWatcher_QueryTimeout))
                {
                    var rcon = server?.GetControl(rconPassword ?? string.Empty);
                    if (rcon == null)
                        return null;

                    var response = rcon.SendCommand("listplayers");
                    if (string.IsNullOrWhiteSpace(response))
                        return 0;

                    var lines = response.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                    return lines.Count(line => ListPlayersLineRegex.IsMatch(line.Trim()));
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"{nameof(GetRconPlayerCount)} - Failed querying RCON player count for {address}:{rconPort}. {ex.Message}");
                return null;
            }
        }

        // On-demand RCON access for the web dashboard. Both return null on any failure
        // (RCON off, wrong password, server not answering) rather than throwing.
        public static IList<string> QueryRconPlayerNames(IPAddress address, int rconPort, string rconPassword)
        {
            var response = SendRconCommand(address, rconPort, rconPassword, "listplayers");
            if (response == null)
                return null;

            var names = new List<string>();
            foreach (var line in response.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (!ListPlayersLineRegex.IsMatch(trimmed))
                    continue;

                var start = trimmed.IndexOf(". ", StringComparison.Ordinal);
                var end = trimmed.LastIndexOf(", ", StringComparison.Ordinal);
                names.Add(start >= 0 && end > start ? trimmed.Substring(start + 2, end - start - 2) : trimmed);
            }
            return names;
        }

        public static string SendRconCommand(IPAddress address, int rconPort, string rconPassword, string command)
        {
            try
            {
                var endpoint = new IPEndPoint(address, rconPort);
                using (var server = ServerQuery.GetServerInstance(EngineType.Source, endpoint, sendTimeOut: Config.Default.ServerStatusWatcher_QueryTimeout, receiveTimeOut: Config.Default.ServerStatusWatcher_QueryTimeout))
                {
                    var rcon = server?.GetControl(rconPassword ?? string.Empty);
                    if (rcon == null)
                        return null;

                    return rcon.SendCommand(command) ?? string.Empty;
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"{nameof(SendRconCommand)} - Failed for {address}:{rconPort}. {ex.Message}");
                return null;
            }
        }

        private static bool GetPublicNetworkStatusDirectly(IPEndPoint endpoint)
        {
            ServerInfo serverInfo;

            try
            {
                using (var server = ServerQuery.GetServerInstance(EngineType.Source, endpoint, false, Config.Default.ServerStatusWatcher_QueryTimeout, Config.Default.ServerStatusWatcher_QueryTimeout))
                {
                    serverInfo = server.GetInfo();
                }

                return serverInfo != null;
            }
            catch (Exception ex)
            {
                Logger.Debug($"{nameof(GetPublicNetworkStatusDirectly)} - Failed checking public status for: {endpoint.Address}:{endpoint.Port}. {ex.Message}");
            }

            return false;
        }

        public static async Task<bool> GetPublicNetworkStatusViaAPIAsync(Uri uri, IPEndPoint endpoint)
        {
            try
            {
                string jsonString;
                using (var client = new WebClient())
                {
                    jsonString = await client.DownloadStringTaskAsync(uri);
                }

                if (jsonString == null)
                {
                    Logger.Debug($"Server info request returned null string for {endpoint.Address}:{endpoint.Port}");
                    return false;
                }

                JObject query = JObject.Parse(jsonString);
                if (query == null)
                {
                    Logger.Debug($"Server info request failed to parse for {endpoint.Address}:{endpoint.Port} - '{jsonString}'");
                    return false;
                }

                var available = query.SelectToken("available");
                if (available == null)
                {
                    Logger.Debug($"Server at {endpoint.Address}:{endpoint.Port} returned no availability.");
                    return false;
                }

                return (bool)available;
            }
            catch (Exception ex)
            {
                Logger.Debug($"{nameof(GetPublicNetworkStatusViaAPIAsync)} - Failed checking public status for: {endpoint.Address}:{endpoint.Port}. {ex.Message}");
            }

            return false;
        }

        private void PostServerStatusUpdate(ServerStatusUpdateRegistration registration, ServerStatusUpdate statusUpdate)
        {
            _eventQueue.Post(() =>
            {
                if (_serverRegistrations.Contains(registration))
                {
                    try
                    {
                        registration.UpdateCallback(registration, statusUpdate);
                    }
                    catch (Exception ex)
                    {
                        DebugUtils.WriteFormatThreadSafeAsync("Exception during server status update callback: {0}\n{1}", ex.Message, ex.StackTrace).DoNotWait();
                    }
                }
                return TaskUtils.FinishedTask;
            });
        }
    }
}

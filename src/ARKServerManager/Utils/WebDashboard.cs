using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using ServerManagerTool.Common.Enums;
using ServerManagerTool.Common.Utils;
using ServerManagerTool.Enums;
using ServerManagerTool.Lib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ServerManagerTool.Utils
{
    /// <summary>
    /// Built-in web dashboard: log in with a per-admin username/password, see
    /// each server's status and player count, and Start / Stop / Restart / Update.
    /// </summary>
    internal static class WebDashboard
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
        private static readonly object _lock = new object();

        private static HttpListener _listener;
        private static CancellationTokenSource _cts;

        private class Session { public string Username; public DateTime Expires; }
        private static readonly ConcurrentDictionary<string, Session> _sessions = new ConcurrentDictionary<string, Session>();
        private static readonly ConcurrentDictionary<string, (int Fails, DateTime Until)> _loginFails = new ConcurrentDictionary<string, (int, DateTime)>();
        private class PendingDiscord { public string Username; public DateTime Expires; }
        private static readonly ConcurrentDictionary<string, PendingDiscord> _pendingDiscord = new ConcurrentDictionary<string, PendingDiscord>();
        private static readonly HttpClient _http = new HttpClient();
        // CPU% needs two samples of a process's total CPU time; keep the previous one per server.
        private static readonly ConcurrentDictionary<int, (DateTime Time, TimeSpan Cpu)> _cpuSamples = new ConcurrentDictionary<int, (DateTime, TimeSpan)>();
        private static readonly ConcurrentDictionary<string, string> _busy = new ConcurrentDictionary<string, string>();

        // Deliberately short and cookie-only (no Max-Age): closing the browser ends the session,
        // and an idle session expires - so every visit needs the full password + Discord login again.
        private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(30);
        private const int MaxLoginFails = 5;
        private static readonly TimeSpan LockoutTime = TimeSpan.FromMinutes(5);

        public static bool IsRunning => _listener != null && _listener.IsListening;

        public static void Start()
        {
            lock (_lock)
            {
                Stop();

                // Discord's API needs TLS 1.2, which older .NET Framework defaults may not enable.
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

                var port = Config.Default.WebDashboardPort;
                _cts = new CancellationTokenSource();

                HttpListener listener = null;
                foreach (var host in new[] { "+", "localhost" })
                {
                    try
                    {
                        listener = new HttpListener();
                        listener.Prefixes.Add($"http://{host}:{port}/");
                        listener.Start();
                        _logger.Info($"Web dashboard listening on http://{host}:{port}/");
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn($"Web dashboard could not listen on '{host}:{port}': {ex.Message}");
                        try { listener?.Close(); } catch { }
                        listener = null;
                    }
                }

                if (listener == null)
                    return;

                _listener = listener;
                var token = _cts.Token;
                Task.Run(() => AcceptLoopAsync(listener, token));
            }
        }

        public static void Stop()
        {
            lock (_lock)
            {
                try { _cts?.Cancel(); } catch { }
                try { _listener?.Close(); } catch { }
                _listener = null;
                _cts = null;
                _sessions.Clear();
            }
        }

        private static async Task AcceptLoopAsync(HttpListener listener, CancellationToken token)
        {
            while (!token.IsCancellationRequested && listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); }
                catch { break; }

                Task.Run(() => HandleAsync(context, token)).DoNotWait();
            }
        }

        private static async Task HandleAsync(HttpListenerContext context, CancellationToken token)
        {
            try
            {
                var request = context.Request;
                var path = request.Url.AbsolutePath;
                var response = context.Response;
                response.Headers["X-Content-Type-Options"] = "nosniff";
                response.Headers["Cache-Control"] = "no-store";

                if (request.HttpMethod == "GET" && (path == "/" || path == "/index.html"))
                {
                    response.Headers["X-Frame-Options"] = "DENY";
                    Write(response, 200, "text/html; charset=utf-8", PageHtml);
                    return;
                }

                if (request.HttpMethod == "POST" && path == "/api/login")
                {
                    HandleLogin(context);
                    return;
                }

                if (request.HttpMethod == "GET" && path == "/auth/discord/callback")
                {
                    await HandleDiscordCallbackAsync(context);
                    return;
                }

                // Everything else needs a valid session, and state-changing calls
                // additionally need the custom header (blocks cross-site form posts).
                var session = GetSession(request);
                if (session == null)
                {
                    Write(response, 401, "application/json", "{\"error\":\"login\"}");
                    return;
                }

                if (request.HttpMethod == "POST" && request.Headers["X-Requested-With"] != "asm")
                {
                    Write(response, 403, "application/json", "{\"error\":\"forbidden\"}");
                    return;
                }

                if (request.HttpMethod == "GET" && path == "/api/status")
                {
                    var servers = await GetStatusAsync();
                    Write(response, 200, "application/json", JsonConvert.SerializeObject(new { user = session.Username, servers }));
                    return;
                }

                if (request.HttpMethod == "GET" && path == "/api/players")
                {
                    var result = await GetPlayersAsync(request.QueryString["id"]);
                    Write(response, 200, "application/json", JsonConvert.SerializeObject(result));
                    return;
                }

                if (request.HttpMethod == "POST" && path == "/api/action")
                {
                    var body = ReadBody(request);
                    var json = JObject.Parse(body);
                    var message = await RunActionAsync((string)json["action"], (string)json["id"], session.Username, token, (string)json["text"]);
                    Write(response, 200, "application/json", JsonConvert.SerializeObject(new { message }));
                    return;
                }

                if (request.HttpMethod == "POST" && path == "/api/logout")
                {
                    var cookie = GetCookie(request);
                    if (cookie != null)
                        _sessions.TryRemove(cookie, out _);
                    response.Headers.Add("Set-Cookie", "asmweb=; Path=/; Max-Age=0; HttpOnly; SameSite=Strict");
                    Write(response, 200, "application/json", "{}");
                    return;
                }

                Write(response, 404, "text/plain", "Not found");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Web dashboard request failed");
                try { Write(context.Response, 500, "application/json", "{\"error\":\"server error\"}"); } catch { }
            }
        }

        private static void HandleLogin(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            var ip = request.RemoteEndPoint?.Address.ToString() ?? "unknown";

            if (_loginFails.TryGetValue(ip, out var state) && state.Fails >= MaxLoginFails && state.Until > DateTime.UtcNow)
            {
                Write(response, 429, "application/json", "{\"error\":\"Too many failed attempts. Try again in a few minutes.\"}");
                return;
            }

            var json = JObject.Parse(ReadBody(request));
            var username = WebDashboardUsers.Verify((string)json["username"], (string)json["password"]);

            if (username == null)
            {
                var fails = (state.Until > DateTime.UtcNow || state.Fails < MaxLoginFails ? state.Fails : 0) + 1;
                _loginFails[ip] = (fails, DateTime.UtcNow + LockoutTime);
                _logger.Warn($"Web dashboard failed login for '{(string)json["username"]}' from {ip}");
                Thread.Sleep(500);
                Write(response, 401, "application/json", "{\"error\":\"Wrong username or password.\"}");
                return;
            }

            if (Config.Default.WebDashboardRequireDiscord)
            {
                if (!ulong.TryParse(WebDashboardUsers.GetDiscordUserId(username), out var discordUserId) || discordUserId == 0)
                {
                    Write(response, 403, "application/json", "{\"error\":\"Discord login is required but this user has no Discord ID set. Ask the manager admin.\"}");
                    return;
                }

                if (string.IsNullOrWhiteSpace(Config.Default.WebDashboardDiscordClientId) || string.IsNullOrWhiteSpace(Config.Default.WebDashboardDiscordClientSecret))
                {
                    _logger.Warn("Web dashboard: Discord login is required but the Discord Client ID / Secret are not set.");
                    Write(response, 503, "application/json", "{\"error\":\"Discord login is not configured on the manager.\"}");
                    return;
                }

                foreach (var old in _pendingDiscord.Where(p => p.Value.Expires < DateTime.UtcNow).Select(p => p.Key).ToList())
                    _pendingDiscord.TryRemove(old, out _);

                var oauthState = NewToken();
                _pendingDiscord[oauthState] = new PendingDiscord { Username = username, Expires = DateTime.UtcNow + TimeSpan.FromMinutes(10) };

                // Lax (not Strict) so the browser still sends it when Discord redirects back.
                response.Headers.Add("Set-Cookie", $"asmstate={oauthState}; Path=/; HttpOnly; SameSite=Lax; Max-Age=600");

                var url = "https://discord.com/oauth2/authorize"
                    + $"?client_id={Uri.EscapeDataString(Config.Default.WebDashboardDiscordClientId.Trim())}"
                    + $"&redirect_uri={Uri.EscapeDataString(GetRedirectUri(request))}"
                    + "&response_type=code&scope=identify&prompt=consent"
                    + $"&state={oauthState}";

                _logger.Info($"Web dashboard: password accepted for '{username}' from {ip}, redirecting to Discord");
                Write(response, 200, "application/json", JsonConvert.SerializeObject(new { redirect = url }));
                return;
            }

            _loginFails.TryRemove(ip, out _);
            FinishLogin(response, username, ip);
        }

        private static string GetRedirectUri(HttpListenerRequest request)
        {
            var scheme = string.Equals(request.Headers["X-Forwarded-Proto"], "https", StringComparison.OrdinalIgnoreCase) ? "https" : request.Url.Scheme;
            return $"{scheme}://{request.Headers["Host"]}/auth/discord/callback";
        }

        private static void WriteLoginError(HttpListenerResponse response, string message)
        {
            Write(response, 403, "text/html; charset=utf-8", "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Login failed</title></head><body style=\"font-family:system-ui;background:#12161c;color:#e6edf3;text-align:center;padding-top:15vh\"><h2>Login failed</h2><p>"
                + WebUtility.HtmlEncode(message) + "</p><p><a style=\"color:#2f81f7\" href=\"/\">Back to login</a></p></body></html>");
        }

        private static async Task HandleDiscordCallbackAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            var ip = request.RemoteEndPoint?.Address.ToString() ?? "unknown";

            var state = request.QueryString["state"];
            var code = request.QueryString["code"];
            var cookieState = request.Cookies["asmstate"]?.Value;

            // The state must match both a pending login here and the cookie set in this browser.
            if (string.IsNullOrEmpty(state) || string.IsNullOrEmpty(code) || state != cookieState
                || !_pendingDiscord.TryRemove(state, out var pending) || pending.Expires < DateTime.UtcNow)
            {
                WriteLoginError(response, "The Discord login expired or was cancelled. Please log in again.");
                return;
            }

            response.Headers.Add("Set-Cookie", "asmstate=; Path=/; Max-Age=0; HttpOnly; SameSite=Lax");

            string discordId;
            try
            {
                var form = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = Config.Default.WebDashboardDiscordClientId.Trim(),
                    ["client_secret"] = Config.Default.WebDashboardDiscordClientSecret,
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                    ["redirect_uri"] = GetRedirectUri(request),
                });

                var tokenResponse = await _http.PostAsync("https://discord.com/api/oauth2/token", form);
                var token = JObject.Parse(await tokenResponse.Content.ReadAsStringAsync());
                var accessToken = (string)token["access_token"];
                if (string.IsNullOrEmpty(accessToken))
                {
                    _logger.Warn($"Web dashboard: Discord token exchange failed: {(string)token["error_description"] ?? (string)token["error"]}");
                    WriteLoginError(response, "Discord did not accept the login. Check the Client ID, Client Secret and Redirect URL.");
                    return;
                }

                var userRequest = new HttpRequestMessage(HttpMethod.Get, "https://discord.com/api/users/@me");
                userRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                var userResponse = await _http.SendAsync(userRequest);
                discordId = (string)JObject.Parse(await userResponse.Content.ReadAsStringAsync())["id"];
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Web dashboard Discord login failed");
                WriteLoginError(response, "Could not reach Discord. Please try again.");
                return;
            }

            var expected = WebDashboardUsers.GetDiscordUserId(pending.Username);
            if (string.IsNullOrEmpty(discordId) || !string.Equals(discordId, expected, StringComparison.Ordinal))
            {
                _logger.Warn($"Web dashboard: Discord account {discordId} does not match the Discord ID saved for '{pending.Username}' (from {ip})");
                WriteLoginError(response, "This Discord account is not the one registered for that user.");
                return;
            }

            _loginFails.TryRemove(ip, out _);
            FinishLogin(response, pending.Username, ip, redirectHome: true);
        }

        private static string NewToken()
        {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            return BitConverter.ToString(bytes).Replace("-", string.Empty);
        }

        private static void FinishLogin(HttpListenerResponse response, string username, string ip, bool redirectHome = false)
        {
            var token = NewToken();
            _sessions[token] = new Session { Username = username, Expires = DateTime.UtcNow + SessionLifetime };

            _logger.Info($"Web dashboard login: '{username}' from {ip}");
            response.Headers.Add("Set-Cookie", $"asmweb={token}; Path=/; HttpOnly; SameSite=Strict");
            if (redirectHome)
            {
                response.StatusCode = 302;
                response.RedirectLocation = "/";
                response.Close();
                return;
            }
            Write(response, 200, "application/json", "{}");
        }

        private static string GetCookie(HttpListenerRequest request) => request.Cookies["asmweb"]?.Value;

        private static Session GetSession(HttpListenerRequest request)
        {
            var token = GetCookie(request);
            if (token == null || !_sessions.TryGetValue(token, out var session))
                return null;
            if (session.Expires < DateTime.UtcNow)
            {
                _sessions.TryRemove(token, out _);
                return null;
            }
            session.Expires = DateTime.UtcNow + SessionLifetime;
            return session;
        }

        private static string ReadBody(HttpListenerRequest request)
        {
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
                return reader.ReadToEnd();
        }

        private static void Write(HttpListenerResponse response, int status, string contentType, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            response.StatusCode = status;
            response.ContentType = contentType;
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
            response.Close();
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private class MemoryStatusEx
        {
            public uint dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(MemoryStatusEx));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto, SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([System.Runtime.InteropServices.In, System.Runtime.InteropServices.Out] MemoryStatusEx buffer);

        private static ulong GetTotalPhysicalMemory()
        {
            var status = new MemoryStatusEx();
            return GlobalMemoryStatusEx(status) ? status.ullTotalPhys : 0;
        }

        private static (double Cpu, long RamMb, double RamPct, string Uptime)? GetUsage(int? pid)
        {
            if (pid == null)
                return null;

            try
            {
                using (var process = Process.GetProcessById(pid.Value))
                {
                    var now = DateTime.UtcNow;
                    var cpuTime = process.TotalProcessorTime;
                    var cpu = 0.0;
                    if (_cpuSamples.TryGetValue(pid.Value, out var prev) && now > prev.Time)
                        cpu = (cpuTime - prev.Cpu).TotalMilliseconds / ((now - prev.Time).TotalMilliseconds * Environment.ProcessorCount) * 100.0;
                    _cpuSamples[pid.Value] = (now, cpuTime);

                    var ramBytes = process.WorkingSet64;
                    var totalRam = (double)GetTotalPhysicalMemory();
                    var up = DateTime.Now - process.StartTime;
                    var uptime = up.TotalDays >= 1 ? $"{(int)up.TotalDays}d {up.Hours}h" : $"{(int)up.TotalHours}h {up.Minutes}m";

                    return (Math.Max(0, Math.Min(100, cpu)), ramBytes / (1024 * 1024), totalRam > 0 ? ramBytes * 100.0 / totalRam : 0, uptime);
                }
            }
            catch
            {
                return null;
            }
        }

        private static async Task<object> GetStatusAsync()
        {
            return await TaskUtils.RunOnUIThreadAsync(() =>
            {
                return (object)ServerManager.Instance.Servers.Select(s =>
                {
                    var usage = GetUsage(s.Runtime.ServerProcessId);
                    return new
                {
                    id = s.Profile.ProfileID,
                    name = s.Profile.ProfileName,
                    mode = s.Profile.ManagerEnablePVP ? "PVP" : "PVE",
                    status = s.Runtime.Status.ToString(),
                    statusText = s.Runtime.StatusString,
                    players = s.Runtime.Players,
                    maxPlayers = s.Runtime.MaxPlayers,
                    version = s.Runtime.Version?.ToString(),
                    rcon = s.Profile.RCONEnabled,
                    cpu = usage?.Cpu,
                    ramMb = usage?.RamMb,
                    ramPct = usage?.RamPct,
                    uptime = usage?.Uptime,
                    busy = _busy.TryGetValue(s.Profile.ProfileID, out var b) ? b : null,
                    };
                }).ToList();
            });
        }

        private class RconTarget { public IPAddress Address; public int Port; public string Password; public string Name; public ServerStatus Status; }

        private static async Task<RconTarget> GetRconTargetAsync(string id)
        {
            return await TaskUtils.RunOnUIThreadAsync(() =>
            {
                var server = ServerManager.Instance.Servers.FirstOrDefault(s => string.Equals(s.Profile.ProfileID, id, StringComparison.OrdinalIgnoreCase));
                if (server == null)
                    return null;

                var snapshot = ServerProfileSnapshot.Create(server.Profile);
                return new RconTarget
                {
                    Address = snapshot.ServerIPAddress,
                    Port = snapshot.RCONEnabled ? snapshot.RCONPort : 0,
                    Password = snapshot.RCONPassword,
                    Name = snapshot.ProfileName,
                    Status = server.Runtime.Status,
                };
            });
        }

        private static async Task<object> GetPlayersAsync(string id)
        {
            var target = await GetRconTargetAsync(id);
            if (target == null || target.Port <= 0)
                return new { ok = false, message = "RCON is not enabled for this server." };
            if (target.Status != ServerStatus.Running)
                return new { ok = false, message = "Server is not running." };

            var names = await Task.Run(() => ServerStatusWatcher.QueryRconPlayerNames(target.Address, target.Port, target.Password));
            return names == null
                ? (object)new { ok = false, message = "Could not reach the server over RCON." }
                : new { ok = true, players = names };
        }

        private static async Task<string> RunRconActionAsync(string action, string id, string text, string username)
        {
            var target = await GetRconTargetAsync(id);
            if (target == null)
                return "Server not found.";
            if (target.Port <= 0)
                return $"{target.Name}: RCON is not enabled for this server.";
            if (target.Status != ServerStatus.Running)
                return $"{target.Name}: server is not running.";

            string command;
            if (action == "save")
            {
                command = "saveworld";
            }
            else
            {
                text = (text ?? string.Empty).Trim();
                if (text.Length == 0)
                    return "Enter a message first.";
                if (text.Length > 400)
                    text = text.Substring(0, 400);
                command = "serverchat " + text.Replace("\r", " ").Replace("\n", " ");
            }

            _logger.Info($"Web dashboard: '{username}' sent '{(action == "save" ? "saveworld" : "serverchat")}' to '{target.Name}'");
            var reply = await Task.Run(() => ServerStatusWatcher.SendRconCommand(target.Address, target.Port, target.Password, command));
            if (reply == null)
                return $"{target.Name}: could not reach the server over RCON.";

            return action == "save" ? $"{target.Name}: world save requested." : $"{target.Name}: message sent.";
        }

        private static async Task<string> RunActionAsync(string action, string id, string username, CancellationToken token, string text = null)
        {
            action = (action ?? string.Empty).ToLowerInvariant();
            if (action == "save" || action == "broadcast")
                return await RunRconActionAsync(action, id, text, username);

            if (action != "start" && action != "stop" && action != "restart" && action != "update")
                return "Unknown action.";

            ServerProfileSnapshot profile = null;
            string error = null;

            await TaskUtils.RunOnUIThreadAsync(() =>
            {
                var server = ServerManager.Instance.Servers.FirstOrDefault(s => string.Equals(s.Profile.ProfileID, id, StringComparison.OrdinalIgnoreCase));
                if (server == null) { error = "Server not found."; return 0; }

                if (_busy.TryGetValue(server.Profile.ProfileID, out var running)) { error = $"{server.Profile.ProfileName}: already busy ({running})."; return 0; }

                var status = server.Runtime.Status;
                if (status == ServerStatus.Updating) { error = $"{server.Profile.ProfileName}: currently updating."; return 0; }
                if (status == ServerStatus.Initializing || status == ServerStatus.Stopping || status == ServerStatus.Unknown || status == ServerStatus.Uninstalled)
                { error = $"{server.Profile.ProfileName}: can't {action} while {server.Runtime.StatusString}."; return 0; }
                if (action == "start" && status == ServerStatus.Running) { error = $"{server.Profile.ProfileName}: already running."; return 0; }
                if (action == "stop" && status == ServerStatus.Stopped) { error = $"{server.Profile.ProfileName}: already stopped."; return 0; }
                if (action == "update" && status != ServerStatus.Running && status != ServerStatus.Stopped) { error = $"{server.Profile.ProfileName}: can't update right now."; return 0; }

                if (!_busy.TryAdd(server.Profile.ProfileID, action)) { error = $"{server.Profile.ProfileName}: already busy."; return 0; }
                profile = ServerProfileSnapshot.Create(server.Profile);
                server.DisarmWatchdogs();
                return 0;
            });

            if (error != null)
                return error;

            _logger.Info($"Web dashboard: '{username}' requested {action} on '{profile.ProfileName}'");

            var app = new ServerApp(true)
            {
                OutputLogs = false,
                SendAlerts = true,
                SendEmails = false,
                ServerStatusChangeCallback = (ServerStatus serverStatus) =>
                {
                    TaskUtils.RunOnUIThreadAsync(() =>
                    {
                        var server = ServerManager.Instance.Servers.FirstOrDefault(s => string.Equals(profile.ProfileId, s.Profile.ProfileID, StringComparison.OrdinalIgnoreCase));
                        server?.Runtime.UpdateServerStatus(serverStatus, serverStatus != ServerStatus.Unknown);
                    }).Wait(token);
                }
            };

            switch (action)
            {
                case "start":
                case "restart":
                    app.ServerProcess = ServerProcessType.Restart;
                    app.RestartIfShutdown = true;
                    break;
                case "stop":
                    app.BackupWorldFile = Config.Default.BackupWorldFile;
                    app.PerformWorldSave = true;
                    app.ServerProcess = ServerProcessType.Stop;
                    app.RestartIfShutdown = false;
                    app.ShutdownInterval = 0;
                    break;
                case "update":
                    app.ServerProcess = ServerProcessType.Update;
                    app.RestartIfShutdown = false;
                    break;
            }

            var snapshot = profile;
            Task.Run(() =>
            {
                try
                {
                    if (action == "stop")
                        app.PerformProfileShutdown(snapshot, false, ServerUpdateType.None, false, false, token);
                    else if (action == "update")
                        app.PerformProfileShutdown(snapshot, true, ServerUpdateType.ServerAndMods, false, false, token);
                    else
                        app.PerformProfileShutdown(snapshot, true, ServerUpdateType.None, false, false, token);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, $"Web dashboard {action} failed for '{snapshot.ProfileName}'");
                }
                finally
                {
                    _busy.TryRemove(snapshot.ProfileId, out _);
                }
            });

            switch (action)
            {
                case "start": return $"Start requested for {profile.ServerName}.";
                case "stop": return $"Stop requested for {profile.ServerName} (saving the world first).";
                case "restart": return $"Restart requested for {profile.ServerName}.";
                default: return $"Update requested for {profile.ServerName}.";
            }
        }

        private const string PageHtml = @"<!DOCTYPE html>
<html lang=""en""><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>Server Dashboard</title>
<style>
:root{--bg:#0e1116;--card:#171c24;--card2:#1e2530;--line:#2a3441;--fg:#e6edf3;--mut:#8b98a8;--ok:#3fb950;--warn:#d29922;--bad:#f85149;--acc:#2f81f7}
*{box-sizing:border-box}body{margin:0;font-family:system-ui,Segoe UI,sans-serif;background:var(--bg);color:var(--fg)}
header{display:flex;justify-content:space-between;align-items:center;padding:12px 22px;border-bottom:1px solid var(--line);background:var(--card);position:sticky;top:0;z-index:5}
h1{font-size:17px;margin:0;letter-spacing:.3px}main{padding:20px 22px;max-width:1400px;margin:0 auto}
button{background:var(--line);color:var(--fg);border:0;border-radius:6px;padding:7px 12px;cursor:pointer;font-size:13px}
button:hover:not(:disabled){filter:brightness(1.25)}button:disabled{opacity:.35;cursor:default}
button.go{background:#238636}button.stop{background:#b62324}button.upd{background:#1f6feb}button.ghost{background:transparent;border:1px solid var(--line)}
#login{max-width:320px;margin:15vh auto;background:var(--card);padding:24px;border-radius:10px;border:1px solid var(--line)}
#login input{width:100%;padding:9px;margin:6px 0 12px;border-radius:6px;border:1px solid var(--line);background:var(--bg);color:var(--fg)}
#login button{width:100%;background:var(--acc)}.err{color:var(--bad);font-size:13px;min-height:18px}
.stats{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:12px;margin-bottom:18px}
.stat{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:12px 14px}.stat b{display:block;font-size:24px;margin-top:2px}.stat span{color:var(--mut);font-size:12px;text-transform:uppercase;letter-spacing:.6px}
.tabs{display:flex;gap:8px;margin-bottom:16px}.tabs button{padding:8px 18px}.tabs button.on{background:var(--acc)}
#grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(300px,1fr));gap:14px;align-items:start}
.card{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:14px;border-top:3px solid var(--mut)}
.card.Running{border-top-color:var(--ok)}.card.Stopped,.card.Uninstalled{border-top-color:var(--bad)}.card.Initializing,.card.Updating,.card.Stopping{border-top-color:var(--warn)}
.top{display:flex;justify-content:space-between;align-items:center;margin-bottom:8px}.name{font-weight:600}
.tag{font-size:11px;padding:2px 7px;border-radius:10px;background:var(--line);color:var(--mut)}
.st{display:flex;align-items:center;gap:7px;font-size:14px;margin:4px 0}.dot{width:10px;height:10px;border-radius:50%;background:var(--mut)}
.Running .dot{background:var(--ok)}.Stopped .dot,.Uninstalled .dot{background:var(--bad)}.Initializing .dot,.Updating .dot,.Stopping .dot{background:var(--warn)}
.bar{height:6px;background:var(--line);border-radius:4px;overflow:hidden;margin:5px 0 3px}.bar i{display:block;height:100%;background:var(--acc)}.bar i.hot{background:var(--bad)}.bar i.mid{background:var(--warn)}
.pl{color:var(--mut);font-size:12px;display:flex;justify-content:space-between}
.btns{display:flex;gap:6px;flex-wrap:wrap;margin-top:10px}
.det{margin-top:10px;background:var(--card2);border-radius:8px;padding:10px;font-size:13px;display:none}.det.open{display:block}
.det ul{margin:6px 0 0;padding-left:18px;max-height:220px;overflow:auto}.det .m{color:var(--mut)}
#msg{position:fixed;bottom:16px;left:50%;transform:translateX(-50%);background:var(--card);border:1px solid var(--line);padding:10px 16px;border-radius:8px;display:none;z-index:9}
</style></head><body>
<div id=""login"" style=""display:none""><h1 style=""margin-bottom:14px"">Server Dashboard</h1>
<label>Username</label><input id=""u"" autocomplete=""username""><label>Password</label><input id=""p"" type=""password"" autocomplete=""current-password"">
<div class=""err"" id=""lerr""></div><button onclick=""login()"">Log in</button>
</div>
<div id=""app"" style=""display:none""><header><h1>Server Dashboard</h1><div><span id=""who"" style=""color:var(--mut);margin-right:10px""></span><button class=""ghost"" onclick=""logout()"">Log out</button></div></header>
<main><div class=""stats"" id=""stats""></div>
<div class=""tabs"" id=""tabs""><button data-t=""ALL"">All</button><button data-t=""PVE"">PVE</button><button data-t=""PVP"">PVP</button></div><div id=""grid""></div></main></div><div id=""msg""></div>
<script>
const H={'X-Requested-With':'asm','Content-Type':'application/json'};let timer=null,tab=localStorage.getItem('tab')||'ALL',last=null;const open={};
function esc(s){return String(s??'').replace(/[&<>""']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','""':'&quot;',""'"":'&#39;'}[c]))}
function toast(t){const m=document.getElementById('msg');m.textContent=t;m.style.display='block';clearTimeout(m._t);m._t=setTimeout(()=>m.style.display='none',5000)}
async function login(){const r=await fetch('/api/login',{method:'POST',headers:H,body:JSON.stringify({username:u.value,password:p.value})});
const d=await r.json().catch(()=>({}));
if(!r.ok){lerr.textContent=d.error||'Login failed';return}
p.value='';lerr.textContent='';
if(d.redirect){location.href=d.redirect;return}
refresh()}
async function logout(){await fetch('/api/logout',{method:'POST',headers:H});show(false)}
function show(on){login_.style.display=on?'none':'block';app_.style.display=on?'block':'none';clearInterval(timer);if(on)timer=setInterval(refresh,5000)}
const login_=document.getElementById('login'),app_=document.getElementById('app');
async function refresh(){let r;try{r=await fetch('/api/status')}catch(e){return}
if(r.status===401){show(false);return}last=await r.json();show(true);render()}
function gb(mb){return mb>=1024?(mb/1024).toFixed(1)+' GB':mb+' MB'}
function usage(label,pct,text){const c=pct>=90?'hot':pct>=70?'mid':'';return `<div class=""pl""><span>${label}</span><span>${text}</span></div><div class=""bar""><i class=""${c}"" style=""width:${Math.min(100,pct)}%""></i></div>`}
function render(){const d=last;who.textContent=d.user;const S=d.servers;
const on=S.filter(s=>s.status==='Running'),pl=on.reduce((a,s)=>a+s.players,0),cap=on.reduce((a,s)=>a+s.maxPlayers,0);
stats.innerHTML=[['Servers',S.length],['Online',on.length],['Stopped',S.filter(s=>s.status==='Stopped').length],['Busy',S.filter(s=>['Updating','Initializing','Stopping'].includes(s.status)||s.busy).length],['Players',pl+' / '+cap],['RAM in use',gb(on.reduce((a,s)=>a+(s.ramMb||0),0))]].map(x=>`<div class=""stat""><span>${x[0]}</span><b>${x[1]}</b></div>`).join('');
for(const b of document.querySelectorAll('#tabs button')){const n=S.filter(s=>b.dataset.t==='ALL'||s.mode===b.dataset.t).length;b.textContent=(b.dataset.t==='ALL'?'All':b.dataset.t)+' ('+n+')';b.className=b.dataset.t===tab?'on':''}
const keep={};for(const el of document.querySelectorAll('.det.open')){keep[el.dataset.id]=el.innerHTML}
grid.innerHTML=S.filter(s=>tab==='ALL'||s.mode===tab).map(s=>{const b=s.busy,st=s.status,run=st==='Running',id=esc(s.id),nm=esc(s.name),hasU=run&&s.ramMb!=null;
return `<div class=""card ${esc(st)}""><div class=""top""><span class=""name"">${nm}</span><span class=""tag"">${esc(s.mode)}</span></div>
<div class=""st ${esc(st)}""><span class=""dot""></span>${esc(s.statusText)}${b?' ('+esc(b)+'…)':''}</div>
${usage('Players',s.maxPlayers?s.players*100/s.maxPlayers:0,s.players+' / '+s.maxPlayers)}
${hasU?usage('CPU',s.cpu,s.cpu.toFixed(0)+'%')+usage('RAM',s.ramPct,gb(s.ramMb)):''}
<div class=""pl""><span>${s.version&&s.version!=='0.0'?'v'+esc(s.version):''}</span><span>${hasU&&s.uptime?'Up '+esc(s.uptime):''}</span></div>
<div class=""btns"">
<button class=""go"" ${b||run?'disabled':''} onclick=""act('start','${id}','${nm}')"">Start</button>
<button class=""stop"" ${b||!run?'disabled':''} onclick=""act('stop','${id}','${nm}')"">Stop</button>
<button ${b||!run?'disabled':''} onclick=""act('restart','${id}','${nm}')"">Restart</button>
<button class=""upd"" ${b||(!run&&st!=='Stopped')?'disabled':''} onclick=""act('update','${id}','${nm}')"">Update</button>
<button class=""ghost"" ${b||!run||!s.rcon?'disabled':''} onclick=""act('save','${id}','${nm}')"">Save World</button>
<button class=""ghost"" ${b||!run||!s.rcon?'disabled':''} onclick=""say('${id}','${nm}')"">Message</button>
<button class=""ghost"" ${!run||!s.rcon?'disabled':''} onclick=""players('${id}')"">Players</button></div>
<div class=""det ${open[s.id]?'open':''}"" data-id=""${id}"">${open[s.id]&&keep[s.id]?keep[s.id]:''}</div></div>`}).join('')}
async function players(id){const el=document.querySelector('.det[data-id=""'+id+'""]');if(!el)return;
if(open[id]&&el.classList.contains('open')){open[id]=false;el.classList.remove('open');return}
open[id]=true;el.classList.add('open');el.innerHTML='<span class=""m"">Loading players…</span>';
const r=await fetch('/api/players?id='+encodeURIComponent(id));if(r.status===401){show(false);return}const d=await r.json();
el.innerHTML=d.ok?(d.players.length?'<b>'+d.players.length+' online</b><ul>'+d.players.map(n=>'<li>'+esc(n)+'</li>').join('')+'</ul>':'<span class=""m"">No players online.</span>'):'<span class=""m"">'+esc(d.message)+'</span>'}
async function send(a,id,extra){const r=await fetch('/api/action',{method:'POST',headers:H,body:JSON.stringify(Object.assign({action:a,id:id},extra||{}))});
if(r.status===401){show(false);return}toast((await r.json()).message||'Done');refresh()}
async function act(a,id,name){if(a!=='start'&&!confirm(a[0].toUpperCase()+a.slice(1)+' '+name+'?'))return;send(a,id)}
function say(id,name){const t=prompt('Message to all players on '+name+':');if(t&&t.trim())send('broadcast',id,{text:t})}
document.getElementById('tabs').addEventListener('click',e=>{if(e.target.dataset.t){tab=e.target.dataset.t;localStorage.setItem('tab',tab);if(last)render()}});
document.getElementById('p').addEventListener('keydown',e=>{if(e.key==='Enter')login()});
refresh();
</script></body></html>";
    }
}

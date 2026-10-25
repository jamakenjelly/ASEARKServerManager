using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using ServerManagerTool.Common.Enums;

namespace ServerManagerTool.Lib
{
    /// <summary>
    /// Player profile backups: while a server runs, the players on it are watched over RCON (listplayers). Three seconds after a player
    /// connects, and again after they disconnect, their own profile file (<id>.arkprofile) is copied into the server's backup folder
    /// (PlayerProfile). The newest N copies are kept per player. A copy can be put back while the server is stopped.
    /// </summary>
    public static class PlayerProfileBackup
    {
        private static readonly Regex PlayerLine = new Regex(@"^\s*\d+\.\s+(?<name>.*),\s*(?<id>[0-9A-Fa-f]+)\s*$", RegexOptions.Compiled);
        private static readonly Regex BackupName = new Regex(@"^(?<time>\d{8}-\d{6})_(?<status>connect|disconnect)_(?<id>[0-9A-Fa-f]+)_(?<name>.*)\.arkprofile$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Dictionary<string, Dictionary<string, string>> Known = new Dictionary<string, Dictionary<string, string>>();
        private static readonly HashSet<string> Busy = new HashSet<string>();
        private static DispatcherTimer _timer;

        public class Request
        {
            public string ProfileId;
            public IPAddress Address;
            public int RconPort;
            public string RconPassword;
            public string SaveFolder;
            public string BackupFolder;
            public int Keep;
        }

        public class Entry
        {
            public string File { get; set; }
            public DateTime Time { get; set; }
            public string Name { get; set; }
            public string Id { get; set; }
            public string Status { get; set; }
            public string StatusText => string.Equals(Status, "connect", StringComparison.OrdinalIgnoreCase) ? "Connected" : "Disconnected";
        }

        /// <summary>Raised (from a background thread) with the profile id after a player backup was written, so a list that shows them can refresh.</summary>
        public static event Action<string> BackupWritten;

        public static void Start()
        {
            if (_timer != null)
                return;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _timer.Tick += (s, e) => Tick();
            _timer.Start();
        }

        public static string GetBackupFolder(ServerProfile profile) => Path.Combine(ServerApp.GetServerBackupFolder(profile), "PlayerProfile");

        private static void Tick()
        {
            try
            {
                foreach (var server in ServerManager.Instance.Servers.ToList())
                {
                    var profile = server.Profile;
                    var id = profile?.ProfileID;
                    if (profile == null || server.Runtime?.Status != ServerStatus.Running || !profile.RCONEnabled || profile.RCONPort <= 0)
                    {
                        // a stopped server forgets who was on it, so the next start begins clean
                        if (id != null)
                            lock (Busy) Known.Remove(id);
                        continue;
                    }

                    if (!profile.PlayerBackupEnabled)
                        continue;

                    lock (Busy)
                    {
                        if (!Busy.Add(id))
                            continue;
                    }

                    var snapshot = server.Runtime.ProfileSnapshot;
                    var request = new Request
                    {
                        ProfileId = id,
                        Address = snapshot?.ServerIPAddress ?? IPAddress.Loopback,
                        RconPort = profile.RCONPort,
                        RconPassword = snapshot?.RCONPassword ?? string.Empty,
                        SaveFolder = ServerProfile.GetProfileSavePath(profile),
                        BackupFolder = GetBackupFolder(profile),
                        Keep = Math.Max(1, profile.PlayerBackupKeep),
                    };

                    Task.Run(() =>
                    {
                        try { Poll(request); }
                        catch { }
                        finally { lock (Busy) Busy.Remove(id); }
                    });
                }
            }
            catch
            {
            }
        }

        private static void Poll(Request request)
        {
            var response = ServerStatusWatcher.SendRconCommand(request.Address, request.RconPort, request.RconPassword, "listplayers");
            if (response == null)
                return; // could not ask - never mistake that for everyone leaving

            var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in response.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var match = PlayerLine.Match(line.Trim());
                if (match.Success)
                    current[match.Groups["id"].Value] = match.Groups["name"].Value.Trim();
            }

            Dictionary<string, string> previous;
            lock (Busy)
            {
                Known.TryGetValue(request.ProfileId, out previous);
                Known[request.ProfileId] = current;
            }

            // the first look at a server only learns who is already on it
            if (previous == null)
                return;

            var joined = current.Where(c => !previous.ContainsKey(c.Key)).ToList();
            var left = previous.Where(p => !current.ContainsKey(p.Key)).ToList();
            if (joined.Count == 0 && left.Count == 0)
                return;

            // the server writes the profile a moment after the event
            Thread.Sleep(3000);
            foreach (var player in joined)
                BackupPlayer(request, player.Key, player.Value, "connect");
            foreach (var player in left)
                BackupPlayer(request, player.Key, player.Value, "disconnect");
        }

        private static string SafeName(string name)
        {
            // Windows file names can hold any Unicode text (other languages, symbols); only the characters a file name cannot contain are replaced
            var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
            var safe = new string((name ?? string.Empty).Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
            if (safe.Length > 40)
            {
                // do not cut a surrogate pair (emoji and some symbols are two chars) in half
                var length = char.IsHighSurrogate(safe[39]) ? 39 : 40;
                safe = safe.Substring(0, length).TrimEnd();
            }
            return safe.Length == 0 ? "player" : safe;
        }

        private static void BackupPlayer(Request request, string id, string name, string status)
        {
            try
            {
                var source = Directory.Exists(request.SaveFolder)
                    ? Directory.GetFiles(request.SaveFolder, id + ".arkprofile", SearchOption.TopDirectoryOnly).FirstOrDefault()
                    : null;
                if (source == null)
                    return;

                Directory.CreateDirectory(request.BackupFolder);
                var target = Path.Combine(request.BackupFolder, $"{DateTime.Now:yyyyMMdd-HHmmss}_{status}_{id}_{SafeName(name)}.arkprofile");
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var output = new FileStream(target, FileMode.Create, FileAccess.Write))
                    input.CopyTo(output);

                // keep only the newest N copies of this player's profile (connect and disconnect copies together); older ones go as new ones arrive
                foreach (var old in List(request.BackupFolder, id).Where(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)).Skip(request.Keep))
                {
                    try { File.Delete(old.File); } catch { }
                }

                BackupWritten?.Invoke(request.ProfileId);
            }
            catch
            {
            }
        }

        /// <summary>The player backups in a folder, newest first, optionally only those whose name or id contains the filter.</summary>
        public static List<Entry> List(string backupFolder, string filter = null)
        {
            var entries = new List<Entry>();
            if (!Directory.Exists(backupFolder))
                return entries;

            foreach (var file in Directory.GetFiles(backupFolder, "*.arkprofile"))
            {
                var match = BackupName.Match(Path.GetFileName(file));
                if (!match.Success)
                    continue;

                if (!DateTime.TryParseExact(match.Groups["time"].Value, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                    time = File.GetLastWriteTime(file);

                entries.Add(new Entry { File = file, Time = time, Name = match.Groups["name"].Value, Id = match.Groups["id"].Value, Status = match.Groups["status"].Value });
            }

            if (!string.IsNullOrWhiteSpace(filter))
            {
                var needle = filter.Trim();
                entries = entries.Where(e => e.Id.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0 || e.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            }

            return entries.OrderByDescending(e => e.Time).ToList();
        }

        /// <summary>Puts a player's backed-up profile back into the save folder (the server must be stopped).</summary>
        public static void Restore(Entry entry, string saveFolder)
        {
            Directory.CreateDirectory(saveFolder);
            var target = Path.Combine(saveFolder, entry.Id + ".arkprofile");
            if (File.Exists(target))
                File.SetAttributes(target, FileAttributes.Normal);
            File.Copy(entry.File, target, true);
        }
    }
}

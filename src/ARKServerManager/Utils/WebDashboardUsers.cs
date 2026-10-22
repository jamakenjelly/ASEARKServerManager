using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ServerManagerTool.Utils
{
    /// <summary>
    /// Web dashboard logins. Passwords are never stored - only a salted PBKDF2 hash,
    /// in WebDashboardUsers.json inside the manager's data folder.
    /// </summary>
    internal static class WebDashboardUsers
    {
        private class UserRecord
        {
            public string Username { get; set; }
            public string Salt { get; set; }
            public string Hash { get; set; }
            public int Iterations { get; set; }
            public string DiscordUserId { get; set; }
        }

        private const int Iterations = 100000;
        private static readonly object _lock = new object();

        private static string FilePath => Path.Combine(Config.Default.DataDir, "WebDashboardUsers.json");

        private static List<UserRecord> Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonConvert.DeserializeObject<List<UserRecord>>(File.ReadAllText(FilePath)) ?? new List<UserRecord>();
            }
            catch
            {
            }
            return new List<UserRecord>();
        }

        private static void Save(List<UserRecord> users)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, JsonConvert.SerializeObject(users, Formatting.Indented));
        }

        private static byte[] Hash(string password, byte[] salt, int iterations)
        {
            using (var kdf = new Rfc2898DeriveBytes(password, salt, iterations))
                return kdf.GetBytes(32);
        }

        public static IList<string> GetUsernames()
        {
            lock (_lock)
                return Load().Select(u => u.Username).OrderBy(u => u, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static string GetDiscordUserId(string username)
        {
            lock (_lock)
                return Load().FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase))?.DiscordUserId;
        }

        public static bool HasUsers()
        {
            lock (_lock)
                return Load().Count > 0;
        }

        // Adds the user, or changes the password if they already exist.
        public static void SetUser(string username, string password, string discordUserId = null)
        {
            username = (username ?? string.Empty).Trim();
            if (username.Length == 0)
                throw new ArgumentException("Username is required.");
            if ((password ?? string.Empty).Length < 8)
                throw new ArgumentException("Password must be at least 8 characters.");

            discordUserId = (discordUserId ?? string.Empty).Trim();
            if (discordUserId.Length > 0 && !ulong.TryParse(discordUserId, out _))
                throw new ArgumentException("Discord User ID must be the numeric ID (Discord > Settings > Advanced > Developer Mode, then right-click yourself > Copy User ID).");

            var salt = new byte[16];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(salt);

            lock (_lock)
            {
                var users = Load();
                users.RemoveAll(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
                users.Add(new UserRecord { Username = username, Salt = Convert.ToBase64String(salt), Hash = Convert.ToBase64String(Hash(password, salt, Iterations)), Iterations = Iterations, DiscordUserId = discordUserId });
                Save(users);
            }
        }

        public static void Remove(string username)
        {
            lock (_lock)
            {
                var users = Load();
                users.RemoveAll(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
                Save(users);
            }
        }

        // Returns the canonical username on success, otherwise null.
        public static string Verify(string username, string password)
        {
            UserRecord user;
            lock (_lock)
                user = Load().FirstOrDefault(u => string.Equals(u.Username, (username ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));

            // Always do the hash work so a wrong username takes as long as a wrong password.
            var salt = user != null ? Convert.FromBase64String(user.Salt) : new byte[16];
            var computed = Hash(password ?? string.Empty, salt, user?.Iterations ?? Iterations);
            if (user == null)
                return null;

            var expected = Convert.FromBase64String(user.Hash);
            var diff = expected.Length ^ computed.Length;
            for (var i = 0; i < expected.Length && i < computed.Length; i++)
                diff |= expected[i] ^ computed[i];

            return diff == 0 ? user.Username : null;
        }
    }
}

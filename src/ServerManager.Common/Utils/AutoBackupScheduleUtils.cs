using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;

namespace ServerManagerTool.Common.Utils
{
    // Auto-Backup runs one shared, frequently-ticking scheduled task (see
    // AutoBackup_BackupPeriod - now just "how often to check", not "how often
    // backups happen"), but each server has its own desired cadence (its own
    // Auto Save Period + 1 minute). This file is the per-profile memory of
    // when each server was last actually backed up, so every tick can decide
    // independently whether a given server is due yet, without needing a
    // separate scheduled task per server.
    public static class AutoBackupScheduleUtils
    {
        private const string FileName = "autobackup_lastrun.json";

        private static readonly object _fileLock = new object();

        private static string GetFilePath(string dataDir)
        {
            return string.IsNullOrWhiteSpace(dataDir) ? null : Path.Combine(dataDir, FileName);
        }

        private static Dictionary<string, DateTime> ReadAll(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                    return new Dictionary<string, DateTime>();

                var json = File.ReadAllText(filePath);
                return JsonConvert.DeserializeObject<Dictionary<string, DateTime>>(json) ?? new Dictionary<string, DateTime>();
            }
            catch
            {
                // Best-effort only - a corrupt or unreadable file just means
                // every profile is treated as "never backed up", which is
                // safe (it just backs up sooner than strictly necessary).
                return new Dictionary<string, DateTime>();
            }
        }

        public static DateTime? GetLastBackupUTC(string dataDir, string profileId)
        {
            var filePath = GetFilePath(dataDir);
            if (filePath == null || string.IsNullOrWhiteSpace(profileId))
                return null;

            lock (_fileLock)
            {
                var all = ReadAll(filePath);
                return all.TryGetValue(profileId, out var lastUTC) ? lastUTC : (DateTime?)null;
            }
        }

        public static void SetLastBackupUTC(string dataDir, string profileId, DateTime utc)
        {
            var filePath = GetFilePath(dataDir);
            if (filePath == null || string.IsNullOrWhiteSpace(profileId))
                return;

            lock (_fileLock)
            {
                try
                {
                    var all = ReadAll(filePath);
                    all[profileId] = utc;
                    File.WriteAllText(filePath, JsonConvert.SerializeObject(all));
                }
                catch
                {
                    // Best-effort only - a failure here should never interrupt
                    // an actual backup run. Worst case, this profile gets
                    // re-checked (and possibly re-backed-up) sooner than its
                    // configured period on the next tick.
                }
            }
        }

        // The "Run" button in the Task Status area still fires the real
        // scheduled task (TaskSchedulerUtils.RunAutoBackup) rather than
        // launching its own separate process - that's what makes the OS
        // task's own state actually flip to Running (and the button show
        // Stop) the same way a normal tick does. Since that reuses the
        // task's own fixed command-line arguments, there's no room to pass
        // a one-off "skip the due-check" flag through the command line -
        // this marker file does the same job instead: set immediately
        // before RunAutoBackup, consumed (read once and deleted) by the
        // very next PerformAutoBackup for that ruleset, whichever process
        // that turns out to be.
        private static string GetForceFilePath(string dataDir, string ruleset)
        {
            if (string.IsNullOrWhiteSpace(dataDir))
                return null;

            var suffix = string.IsNullOrWhiteSpace(ruleset) ? string.Empty : $"_{ruleset.ToLowerInvariant()}";
            return Path.Combine(dataDir, $"autobackup_force{suffix}.flag");
        }

        public static void SetForceNextRun(string dataDir, string ruleset)
        {
            var filePath = GetForceFilePath(dataDir, ruleset);
            if (filePath == null)
                return;

            try
            {
                File.WriteAllText(filePath, DateTime.UtcNow.ToString("o"));
            }
            catch
            {
                // Best-effort only - worst case the manual Run behaves like a
                // normal tick instead of a forced one.
            }
        }

        public static bool ConsumeForceNextRun(string dataDir, string ruleset)
        {
            var filePath = GetForceFilePath(dataDir, ruleset);
            if (filePath == null || !File.Exists(filePath))
                return false;

            try
            {
                File.Delete(filePath);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}

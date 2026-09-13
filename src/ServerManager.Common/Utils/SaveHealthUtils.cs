using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace ServerManagerTool.Common.Utils
{
    // Tracks which profiles had their last RCON "saveworld" fail to actually
    // update the world file (see ServerApp.VerifyWorldSaveCompleted) - drives
    // the per-tab warning icon. Mirrors BackupProgressUtils: a small shared
    // JSON file so both the GUI and the separate headless Auto-Backup/
    // Auto-Shutdown processes (each launched per-profile by Task Scheduler)
    // can read/write the same state, with a cross-process Mutex since two of
    // those processes can write at the same moment.
    public class SaveHealthStatus
    {
        public List<string> WarningProfileIds { get; set; } = new List<string>();
    }

    public static class SaveHealthUtils
    {
        private const string FileName = "save_health.json";
        private static readonly object _lock = new object();

        private static string GetFilePath(string dataDir)
        {
            return string.IsNullOrWhiteSpace(dataDir) ? null : Path.Combine(dataDir, FileName);
        }

        private static Mutex AcquireFileMutex(string filePath)
        {
            var mutex = new Mutex(false, "Global\\ASM_SaveHealth_" + filePath.GetHashCode());
            mutex.WaitOne(TimeSpan.FromSeconds(10));
            return mutex;
        }

        private static SaveHealthStatus ReadRaw(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                    return null;

                return JsonConvert.DeserializeObject<SaveHealthStatus>(File.ReadAllText(filePath));
            }
            catch
            {
                return null;
            }
        }

        public static void SetSaveWarning(string dataDir, string profileId, bool hasWarning)
        {
            var filePath = GetFilePath(dataDir);
            if (filePath == null || string.IsNullOrWhiteSpace(profileId))
                return;

            lock (_lock)
            using (var fileMutex = AcquireFileMutex(filePath))
            {
                try
                {
                    var status = ReadRaw(filePath) ?? new SaveHealthStatus();
                    status.WarningProfileIds = status.WarningProfileIds ?? new List<string>();

                    if (hasWarning)
                    {
                        if (!status.WarningProfileIds.Contains(profileId))
                            status.WarningProfileIds.Add(profileId);
                    }
                    else
                    {
                        status.WarningProfileIds.Remove(profileId);
                    }

                    File.WriteAllText(filePath, JsonConvert.SerializeObject(status));
                }
                catch
                {
                    // Best-effort only - this is a UI indicator, never worth
                    // interrupting an actual save/backup over.
                }
                finally
                {
                    try { fileMutex.ReleaseMutex(); } catch { }
                }
            }
        }

        public static HashSet<string> GetWarningProfileIds(string dataDir)
        {
            var filePath = GetFilePath(dataDir);
            if (filePath == null)
                return new HashSet<string>();

            var status = ReadRaw(filePath);
            if (status?.WarningProfileIds == null)
                return new HashSet<string>();

            return new HashSet<string>(status.WarningProfileIds);
        }
    }
}

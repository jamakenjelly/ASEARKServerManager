using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace ServerManagerTool.Common.Utils
{
    // Scheduled auto-backups run as a separate, headless process (Task Scheduler
    // launches ServerManager.exe /autobackup with no window at all), so it has
    // nowhere of its own to show progress. This writes a small status file that
    // the main GUI - if it happens to be running at the same time - can poll and
    // display (e.g. total/remaining counts in the tray tooltip), without the two
    // processes needing to talk to each other directly.
    public class BackupProgressStatus
    {
        public int Total { get; set; }
        public int Completed { get; set; }
        public int Remaining => Math.Max(0, Total - Completed);
        public DateTime UpdatedUtc { get; set; }
        public bool IsComplete => Total > 0 && Completed >= Total;

        // Which profile(s) are actively being backed up right now, across
        // every source (scheduled Auto-Backup, manual "Backup Now", Discord
        // !backup) - a dictionary of profile id -> when it started, rather
        // than a single value, because a manual click can overlap with an
        // in-progress scheduled run for a different server. Used purely for
        // the GUI's per-tab "backing up" indicator.
        //
        // Keyed per-profile (not just a flat list) specifically so staleness
        // can be judged per entry - see GetActiveProfileIds. With several
        // servers, a shared single "last touched" timestamp for the whole
        // file never goes stale as long as ANY server's backup is active, so
        // if one server's backup process dies (crash, OOM-kill, forced
        // termination) between setting itself active and its finally block
        // clearing it, its icon would otherwise stay lit forever - every
        // other server's normal activity keeps "refreshing" a shared
        // timestamp that was never about that stuck entry in the first place.
        public Dictionary<string, DateTime> ActiveProfiles { get; set; } = new Dictionary<string, DateTime>();
    }

    public static class BackupProgressUtils
    {
        private const string FileName = "backup_progress.json";

        private static string GetFilePath(string dataDir)
        {
            return string.IsNullOrWhiteSpace(dataDir) ? null : Path.Combine(dataDir, FileName);
        }

        // Auto-shutdown/restart for each profile runs as its own separate
        // headless process (Task Scheduler launches one per profile), so
        // when several profiles stop/restart at the same moment, multiple
        // processes can read-modify-write this file concurrently. The
        // in-process _counterLock below only serializes callers within a
        // single process - it does nothing across processes - so without
        // this, one profile's ActiveProfiles update can be silently lost
        // to a race and its "backing up" tab icon never appears. A named
        // system-wide Mutex serializes every process that touches this file.
        private static Mutex AcquireFileMutex(string filePath)
        {
            var mutex = new Mutex(false, "Global\\ASM_BackupProgress_" + filePath.GetHashCode());
            if (!mutex.WaitOne(TimeSpan.FromSeconds(10)))
            {
                // Best-effort only - proceed rather than block a backup/shutdown
                // indefinitely over a stuck UI-indicator lock.
            }
            return mutex;
        }

        public static void WriteProgress(string dataDir, int total, int completed)
        {
            var filePath = GetFilePath(dataDir);
            if (filePath == null)
                return;

            lock (_counterLock)
            using (var fileMutex = AcquireFileMutex(filePath))
            {
                try
                {
                    // Preserve whatever ActiveProfiles is already there - a
                    // concurrent ad-hoc backup (manual "Backup Now" on a
                    // different server) may have set it, and this call
                    // shouldn't wipe that out from under it.
                    var existingActiveProfiles = ReadRaw(filePath)?.ActiveProfiles ?? new Dictionary<string, DateTime>();

                    var status = new BackupProgressStatus
                    {
                        Total = total,
                        Completed = completed,
                        UpdatedUtc = DateTime.UtcNow,
                        ActiveProfiles = existingActiveProfiles,
                    };

                    File.WriteAllText(filePath, JsonConvert.SerializeObject(status));
                }
                catch
                {
                    // Best-effort only - a failure here should never interrupt an
                    // actual backup run.
                }
                finally
                {
                    try { fileMutex.ReleaseMutex(); } catch { }
                }
            }
        }

        private static BackupProgressStatus ReadRaw(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                    return null;

                return JsonConvert.DeserializeObject<BackupProgressStatus>(File.ReadAllText(filePath));
            }
            catch
            {
                return null;
            }
        }

        // Marks a single profile as actively backing up right now, regardless
        // of the Total/Completed counters used elsewhere in this file (those
        // track a different concern - see ProfileSyncWindow). Safe to call
        // even if no batch run is otherwise in progress; creates a minimal
        // status entry just to carry this one profile's active state.
        public static void SetProfileBackingUp(string dataDir, string profileId, bool isBackingUp)
        {
            var filePath = GetFilePath(dataDir);
            if (filePath == null || string.IsNullOrWhiteSpace(profileId))
                return;

            lock (_counterLock)
            using (var fileMutex = AcquireFileMutex(filePath))
            {
                try
                {
                    var status = ReadRaw(filePath) ?? new BackupProgressStatus();
                    status.ActiveProfiles = status.ActiveProfiles ?? new Dictionary<string, DateTime>();

                    if (isBackingUp)
                    {
                        status.ActiveProfiles[profileId] = DateTime.UtcNow;
                    }
                    else
                    {
                        status.ActiveProfiles.Remove(profileId);
                    }

                    status.UpdatedUtc = DateTime.UtcNow;

                    File.WriteAllText(filePath, JsonConvert.SerializeObject(status));
                }
                catch
                {
                    // Best-effort only - this is a UI indicator, never worth
                    // interrupting an actual backup over.
                }
                finally
                {
                    try { fileMutex.ReleaseMutex(); } catch { }
                }
            }
        }

        // The set of profile IDs currently backing up, for the GUI's per-tab
        // indicator. Independent of Total/Completed/IsComplete - a status
        // file can be "complete" for the batch-progress feature while still
        // correctly reporting no active profiles (or vice versa, for an
        // ad-hoc backup that hasn't touched Total/Completed at all).
        //
        // Staleness is judged per profile entry, not against the file's
        // overall UpdatedUtc - with several servers, that shared timestamp
        // gets refreshed by any of them and would never go stale just
        // because ONE specific server's backup died mid-flight.
        public static HashSet<string> GetActiveProfileIds(string dataDir, int staleAfterMinutes = 30)
        {
            var filePath = GetFilePath(dataDir);
            if (filePath == null)
                return new HashSet<string>();

            var status = ReadRaw(filePath);
            if (status?.ActiveProfiles == null)
                return new HashSet<string>();

            var now = DateTime.UtcNow;
            var staleWindow = TimeSpan.FromMinutes(staleAfterMinutes);

            return new HashSet<string>(status.ActiveProfiles
                .Where(kvp => now - kvp.Value <= staleWindow)
                .Select(kvp => kvp.Key));
        }

        public static void ClearProgress(string dataDir)
        {
            var filePath = GetFilePath(dataDir);
            if (filePath == null)
                return;

            try
            {
                if (File.Exists(filePath))
                    File.Delete(filePath);
            }
            catch
            {
                // Best-effort only.
            }
        }

        private static readonly object _counterLock = new object();

        // For ad-hoc backups (shutdown/restart/stop/wipe world backups) rather
        // than the batch /autobackup run. Each backup registers itself when it
        // starts (Total+1) and reports done when it finishes (Completed+1), so
        // the GUI's "running X of Y" label covers every backup, not just the
        // scheduled batch. Joins an in-flight run's counts if one is active;
        // a completed or stale file starts a fresh 0-of-1 count instead of
        // continuing from the old numbers. Once Completed catches up to Total
        // the status reads as complete and ReadProgress hides it again -
        // self-clearing, no explicit Clear needed on this path.
        public static void BackupStarted(string dataDir)
        {
            AdjustCounters(dataDir, totalDelta: 1, completedDelta: 0);
        }

        public static void BackupCompleted(string dataDir)
        {
            AdjustCounters(dataDir, totalDelta: 0, completedDelta: 1);
        }

        private static void AdjustCounters(string dataDir, int totalDelta, int completedDelta)
        {
            var filePath = GetFilePath(dataDir);
            if (filePath == null)
                return;

            lock (_counterLock)
            using (var fileMutex = AcquireFileMutex(filePath))
            {
                try
                {
                    BackupProgressStatus status = null;

                    if (File.Exists(filePath))
                    {
                        try
                        {
                            status = JsonConvert.DeserializeObject<BackupProgressStatus>(File.ReadAllText(filePath));
                        }
                        catch
                        {
                            status = null;
                        }
                    }

                    // A finished or abandoned previous run should not carry its
                    // counts into this new one.
                    if (status == null || status.IsComplete || DateTime.UtcNow - status.UpdatedUtc > TimeSpan.FromMinutes(30))
                    {
                        // Preserve any still-active profiles across the reset -
                        // this counter reset is about Total/Completed only, not
                        // about whichever servers happen to be mid-backup.
                        var existingActiveProfiles = status?.ActiveProfiles ?? new Dictionary<string, DateTime>();
                        status = new BackupProgressStatus { ActiveProfiles = existingActiveProfiles };
                    }

                    status.Total = Math.Max(0, status.Total + totalDelta);
                    status.Completed = Math.Max(0, status.Completed + completedDelta);
                    status.UpdatedUtc = DateTime.UtcNow;

                    File.WriteAllText(filePath, JsonConvert.SerializeObject(status));
                }
                catch
                {
                    // Best-effort only - a failure here should never interrupt
                    // an actual backup run.
                }
                finally
                {
                    try { fileMutex.ReleaseMutex(); } catch { }
                }
            }
        }

        // Returns null if there's no active backup, the file is missing/corrupt,
        // the run reported itself complete, or the file is stale (older than
        // staleAfterMinutes - covers a crashed/killed backup process that never
        // got to clean up after itself, so the GUI doesn't show a stuck counter
        // forever).
        public static BackupProgressStatus ReadProgress(string dataDir, int staleAfterMinutes = 30)
        {
            var filePath = GetFilePath(dataDir);
            if (filePath == null || !File.Exists(filePath))
                return null;

            try
            {
                var status = JsonConvert.DeserializeObject<BackupProgressStatus>(File.ReadAllText(filePath));
                if (status == null || status.IsComplete)
                    return null;

                if (DateTime.UtcNow - status.UpdatedUtc > TimeSpan.FromMinutes(staleAfterMinutes))
                    return null;

                return status;
            }
            catch
            {
                return null;
            }
        }
    }
}

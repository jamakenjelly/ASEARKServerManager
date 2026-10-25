using NLog;
using ServerManagerTool.Common.Enums;
using ServerManagerTool.Common.Lib;
using ServerManagerTool.Common.Utils;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace ServerManagerTool.Lib
{
    public class Server : DependencyObject, IDisposable
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
        public static readonly DependencyProperty ProfileProperty = DependencyProperty.Register(nameof(Profile), typeof(ServerProfile), typeof(Server), new PropertyMetadata((ServerProfile)null));
        public static readonly DependencyProperty RuntimeProperty = DependencyProperty.Register(nameof(Runtime), typeof(ServerRuntime), typeof(Server), new PropertyMetadata((ServerRuntime)null));
        public static readonly DependencyProperty SelectedProperty = DependencyProperty.Register(nameof(Selected), typeof(bool), typeof(Server), new PropertyMetadata(false));

        // Kept in sync by MainWindow polling BackupProgressUtils - true while
        // THIS profile is the one actually being backed up right now, whether
        // that's a scheduled Auto-Backup, a manual "Backup Now" click, or a
        // Discord !backup command. Purely a UI indicator, not touched by any
        // backup logic itself.
        public static readonly DependencyProperty IsBackingUpProperty = DependencyProperty.Register(nameof(IsBackingUp), typeof(bool), typeof(Server), new PropertyMetadata(false));
        public bool IsBackingUp
        {
            get { return (bool)GetValue(IsBackingUpProperty); }
            set { SetValue(IsBackingUpProperty, value); }
        }

        // Kept in sync by MainWindow polling SaveHealthUtils - true when the
        // most recent RCON "saveworld" for this profile (from a Stop/
        // Shutdown/Restart or a scheduled/manual backup) sent successfully
        // but the world file's own last-write time never actually moved,
        // meaning the server likely didn't really save. Clears itself the
        // next time a save for this profile is confirmed to have completed.
        public static readonly DependencyProperty HasSaveWarningProperty = DependencyProperty.Register(nameof(HasSaveWarning), typeof(bool), typeof(Server), new PropertyMetadata(false));
        public bool HasSaveWarning
        {
            get { return (bool)GetValue(HasSaveWarningProperty); }
            set { SetValue(HasSaveWarningProperty, value); }
        }

        public ServerProfile Profile
        {
            get { return (ServerProfile)GetValue(ProfileProperty); }
            protected set { SetValue(ProfileProperty, value); }
        }

        public ServerRuntime Runtime
        {
            get { return (ServerRuntime)GetValue(RuntimeProperty); }
            protected set { SetValue(RuntimeProperty, value); }
        }

        public bool Selected
        {
            get { return (bool)GetValue(SelectedProperty); }
            set { SetValue(SelectedProperty, value); }
        }

        private Server(ServerProfile profile)
        {
            InitializeFromProfile(profile);
        }

        public void Dispose()
        {
            this.Profile.DestroyServerFilesWatcher();

            this.Runtime.StatusUpdate -= Runtime_StatusUpdate;
            this.Runtime.Dispose();
        }

        private void Runtime_StatusUpdate(object sender, EventArgs eventArgs)
        {
            this.Profile.LastInstalledVersion = this.Runtime.Version.ToString();

            CheckStatusWatchdog();
            CheckInitializingWatchdog();
        }

        // Once a watchdog's timeout has elapsed, require this many consecutive
        // still-stuck status checks (one per Runtime_StatusUpdate event) before
        // actually restarting, rather than acting on the very first check past
        // the timeout - a single missed/delayed RCON or process poll otherwise
        // looks identical to a genuinely hung server.
        private const int WATCHDOG_CONFIRMATION_CHECKS = 5;

        // Liveness watchdog: if this server was confirmed Running at some point
        // and then stays anything other than Running for longer than the
        // configured timeout, stop and restart it. Deliberately does NOT fire
        // the very first time a profile is loaded (never having been seen
        // Running means it just hasn't been started yet, not that it crashed),
        // and _watchdogArmedUTC is cleared whenever this app explicitly stops
        // the server (see StopAsync) so an intentional stop is never fought.
        private DateTime? _watchdogArmedUTC;
        private int _statusWatchdogStrikes;
        private bool _watchdogRestartInFlight;

        private void CheckStatusWatchdog()
        {
            if (!this.Profile.EnableStatusWatchdog)
            {
                _watchdogArmedUTC = null;
                _statusWatchdogStrikes = 0;
                return;
            }

            var status = this.Runtime.Status;

            if (status == ServerStatus.Running)
            {
                _watchdogArmedUTC = DateTime.UtcNow;
                _statusWatchdogStrikes = 0;
                return;
            }

            // Not armed yet (never seen Running since this was enabled/loaded),
            // or mid-transition (Stopping/Initializing/Updating) - those aren't
            // "unresponsive", they're normal states on the way to Running or a
            // deliberate stop, so don't count time against the timeout for them.
            if (!_watchdogArmedUTC.HasValue || status == ServerStatus.Stopping || status == ServerStatus.Initializing || status == ServerStatus.Updating)
                return;

            if (_watchdogRestartInFlight)
                return;

            var timeout = TimeSpan.FromMinutes(Math.Max(1, this.Profile.StatusWatchdogTimeoutMinutes));
            if (DateTime.UtcNow - _watchdogArmedUTC.Value < timeout)
                return;

            // Timeout has elapsed - confirm it on WATCHDOG_CONFIRMATION_CHECKS
            // consecutive checks before actually restarting.
            _statusWatchdogStrikes++;
            if (_statusWatchdogStrikes < WATCHDOG_CONFIRMATION_CHECKS)
            {
                _logger.Warn($"Status watchdog - '{this.Profile.ProfileName}' has not been Running for {timeout.TotalMinutes:F0} minute(s) (current status: {status}); confirming ({_statusWatchdogStrikes}/{WATCHDOG_CONFIRMATION_CHECKS}) before restarting.");
                return;
            }

            _watchdogRestartInFlight = true;
            _logger.Warn($"Status watchdog - '{this.Profile.ProfileName}' has not been Running for {timeout.TotalMinutes:F0} minute(s) (current status: {status}), confirmed on {_statusWatchdogStrikes} consecutive checks; stopping and restarting it.");

            RunWatchdogRestartAsync().DoNotWait();
        }

        private async Task RunWatchdogRestartAsync()
        {
            try
            {
                // Route through this class's own StopAsync/StartAsync rather than
                // Runtime directly, for the same firewall-rule and re-attach
                // handling every other start/stop path already gets.
                await StopAsync();
                await StartAsync();
            }
            catch (Exception ex)
            {
                _logger.Error($"Status watchdog - Unable to restart '{this.Profile.ProfileName}'. {ex.Message}");
            }
            finally
            {
                // Re-arm from now rather than leaving it unset, so a restart
                // that doesn't actually come back up within the timeout gets
                // caught and retried again, instead of the watchdog going
                // permanently quiet after one failed attempt.
                _watchdogArmedUTC = DateTime.UtcNow;
                _statusWatchdogStrikes = 0;
                _initializingSinceUTC = null;
                _initializingWatchdogStrikes = 0;
                _watchdogRestartInFlight = false;
            }
        }

        // A normal startup legitimately sits in Initializing for a while
        // (loading the world, mods, etc.), which is exactly why the main
        // watchdog above ignores that state entirely - so a startup that hangs
        // and never reaches Running would otherwise never get caught. This is
        // a second, separate timer just for that: how long Initializing itself
        // has been continuously observed, independent of whether it was ever
        // Running before.
        private DateTime? _initializingSinceUTC;
        private int _initializingWatchdogStrikes;

        private void CheckInitializingWatchdog()
        {
            if (!this.Profile.EnableInitializingWatchdog)
            {
                _initializingSinceUTC = null;
                _initializingWatchdogStrikes = 0;
                return;
            }

            var status = this.Runtime.Status;

            if (status != ServerStatus.Initializing)
            {
                _initializingSinceUTC = null;
                _initializingWatchdogStrikes = 0;
                return;
            }

            if (!_initializingSinceUTC.HasValue)
            {
                _initializingSinceUTC = DateTime.UtcNow;
                return;
            }

            if (_watchdogRestartInFlight)
                return;

            var timeout = TimeSpan.FromMinutes(Math.Max(1, this.Profile.InitializingWatchdogTimeoutMinutes));
            if (DateTime.UtcNow - _initializingSinceUTC.Value < timeout)
                return;

            // Timeout has elapsed - confirm it on WATCHDOG_CONFIRMATION_CHECKS
            // consecutive checks before actually restarting.
            _initializingWatchdogStrikes++;
            if (_initializingWatchdogStrikes < WATCHDOG_CONFIRMATION_CHECKS)
            {
                _logger.Warn($"Status watchdog - '{this.Profile.ProfileName}' has been stuck Initializing for {timeout.TotalMinutes:F0} minute(s); confirming ({_initializingWatchdogStrikes}/{WATCHDOG_CONFIRMATION_CHECKS}) before restarting.");
                return;
            }

            _watchdogRestartInFlight = true;
            _logger.Warn($"Status watchdog - '{this.Profile.ProfileName}' has been stuck Initializing for {timeout.TotalMinutes:F0} minute(s), confirmed on {_initializingWatchdogStrikes} consecutive checks; stopping and restarting it.");

            RunWatchdogRestartAsync().DoNotWait();
        }

        public void ImportFromPath(string path, ServerProfile profile = null)
        {
            var loadedProfile = ServerProfile.LoadFrom(path, profile);
            if (loadedProfile != null)
                InitializeFromProfile(loadedProfile);
        }

        private void InitializeFromProfile(ServerProfile profile)
        {
            if (profile == null)
                return;

            this.Profile = profile;
            this.Runtime = new ServerRuntime();
            this.Runtime.AttachToProfile(this.Profile).Wait();

            this.Runtime.StatusUpdate += Runtime_StatusUpdate;
        }

        public static Server FromPath(string path)
        {
            var loadedProfile = ServerProfile.LoadFrom(path, profile: null);
            if (loadedProfile == null)
                return null;
            return new Server(loadedProfile);
        }

        public static Server FromDefaults()
        {
            var loadedProfile = ServerProfile.FromDefaults();
            if (loadedProfile == null)
                return null;
            return new Server(loadedProfile);
        }

        // alreadySaved: the caller has just saved the profile (the Start button does, before it validates it), so it is not saved a second time -
        // each save writes all the server's files on the window's thread, and saving twice on every start made the manager freeze twice as long.
        public async Task StartAsync(bool alreadySaved = false)
        {
            // always start from the saved settings, so a change made since the last save is not lost
            if (!alreadySaved)
            {
                try
                {
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => this.Profile.Save(false, false, null)).Task;
                }
                catch (Exception ex)
                {
                    _logger.Error($"The profile could not be saved before starting: {ex.Message}");
                }
            }

            await this.Runtime.AttachToProfile(this.Profile);
            await this.Runtime.StartAsync();
        }

        public async Task StopAsync()
        {
            DisarmWatchdogs();

            await this.Runtime.StopAsync();
        }

        // Disarms both watchdogs' elapsed-time tracking. If this came from
        // RunWatchdogRestartAsync itself, that method's own finally block
        // re-arms them again once the restart completes - this just
        // guarantees a deliberate stop is never mistaken for continued
        // downtime measured from before the stop happened.
        //
        // Public because not every manual stop/shutdown/restart goes through
        // this class's own StopAsync above - the Shutdown dialog and Server
        // Monitor's bulk Stop/Shutdown/Restart run a separate headless
        // ServerApp and only forward status changes back into Runtime, so
        // they call this directly before doing so. Without it, a server the
        // user deliberately shut down and left stopped would eventually look
        // "unresponsive" to the status watchdog and get force-restarted out
        // from under them.
        public void DisarmWatchdogs()
        {
            _watchdogArmedUTC = null;
            _initializingSinceUTC = null;
        }

        public async Task<bool> UpgradeAsync(CancellationToken cancellationToken, bool updateServer, BranchSnapshot branch, bool validate, bool updateMods, ProgressDelegate progressCallback)
        {
            await this.Runtime.AttachToProfile(this.Profile);
            var success = await this.Runtime.UpgradeAsync(cancellationToken, updateServer, branch, validate, updateMods, progressCallback);
            this.Profile.LastInstalledVersion = this.Runtime.Version.ToString();
            return success;
        }

        public async Task ResetAsync()
        {
            // delete the world, player and tribe files (SavedArks)
            var saveFolder = ServerProfile.GetProfileSavePath(Profile);
            if (Directory.Exists(saveFolder))
            {
                await Task.Run(() =>
                {
                    foreach (var file in Directory.GetFiles(saveFolder, "*.*", SearchOption.TopDirectoryOnly))
                    {
                        File.Delete(file);
                    }
                });
            };

            // delete the mod files (SaveGames)
            var saveGamesFolder = ServerProfile.GetProfileSaveGamesPath(Profile);
            if (Directory.Exists(saveGamesFolder))
            {
                await Task.Run(() =>
                {
                    Directory.Delete(saveGamesFolder, true);
                });
            }

            // delete the log files (Logs)
            var logsFolder = Path.Combine(Profile.InstallDirectory, Config.Default.SavedRelativePath, Config.Default.LogsDir);
            if (Directory.Exists(logsFolder))
            {
                await Task.Run(() =>
                {
                    Directory.Delete(logsFolder, true);
                });
            }
        }
    }
}

using EO.Wpf;
using NLog;
using ServerManagerTool.Common;
using ServerManagerTool.Common.Enums;
using ServerManagerTool.Common.Lib;
using ServerManagerTool.Common.Utils;
using ServerManagerTool.DiscordBot;
using ServerManagerTool.Enums;
using ServerManagerTool.Lib;
using ServerManagerTool.Plugin.Common;
using ServerManagerTool.Utils;
using ServerManagerTool.Windows;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DaysOfTheWeek = Microsoft.Win32.TaskScheduler.DaysOfTheWeek;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using WPFSharp.Globalizer;

namespace ServerManagerTool
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        // Separate named logger for the Discord control panel (Start/Stop/
        // Restart button) posting - see NLog.config. The general "*" rule
        // only writes Error and above, which used to make every reason a
        // panel silently didn't post (bot disabled, bad channel Id, bot not
        // connected, etc) invisible in every log file. This one has its own
        // dedicated Info-and-above file (ServerManager_DiscordControlPanel.log)
        // so those reasons are actually diagnosable.
        private static readonly Logger DiscordPanelLogger = LogManager.GetLogger("ServerManagerTool.DiscordControlPanel");

        private readonly GlobalizedApplication _globalizer = GlobalizedApplication.Instance;
        private readonly ActionQueue versionChecker;
        private readonly ActionQueue scheduledTaskChecker;
        private readonly ActionQueue discordBotChecker;
        private readonly ActionQueue discordPostUpdater;
        private readonly ActionQueue profileAutoSaver;

        // Discord control panels used to be rewritten for every server every
        // minute, even when status and player count had not changed. With a
        // large cluster that creates avoidable network, JSON and logging work.
        // Keep a short-lived signature cache; a ten-minute forced refresh still
        // repairs messages after transient Discord failures or external edits.
        private readonly object discordControlPanelCacheLock = new object();
        private readonly Dictionary<string, string> discordControlPanelSignatures = new Dictionary<string, string>();
        private readonly Dictionary<string, DateTime> discordControlPanelLastUpdates = new Dictionary<string, DateTime>();
        private static readonly TimeSpan DiscordControlPanelForcedRefreshPeriod = TimeSpan.FromMinutes(5);

        // See AutoUpdateDiscordPosts and CheckForScheduledTasks - lets a
        // stalled self-rescheduling Discord-post loop be detected and
        // revived automatically instead of silently never posting again.
        private DateTime discordPostUpdaterLastTickUTC = DateTime.UtcNow;

        private bool discordBotStateClicked = false;

        public static readonly DependencyProperty AppInstanceProperty = DependencyProperty.Register(nameof(AppInstance), typeof(App), typeof(MainWindow), new PropertyMetadata(null));
        public static readonly DependencyProperty ConfigProperty = DependencyProperty.Register(nameof(Config), typeof(Config), typeof(MainWindow), new PropertyMetadata(null));
        public static readonly DependencyProperty CommonConfigProperty = DependencyProperty.Register(nameof(CommonConfig), typeof(CommonConfig), typeof(MainWindow), new PropertyMetadata(null));
        public static readonly DependencyProperty ServerManagerProperty = DependencyProperty.Register(nameof(ServerManager), typeof(ServerManager), typeof(MainWindow), new PropertyMetadata(null));
        public static readonly DependencyProperty AutoBackupStateProperty = DependencyProperty.Register(nameof(AutoBackupState), typeof(Microsoft.Win32.TaskScheduler.TaskState), typeof(MainWindow), new PropertyMetadata(Microsoft.Win32.TaskScheduler.TaskState.Unknown));
        public static readonly DependencyProperty AutoBackupStateStringProperty = DependencyProperty.Register(nameof(AutoBackupStateString), typeof(string), typeof(MainWindow), new PropertyMetadata(string.Empty));
        public static readonly DependencyProperty AutoBackupNextRunTimeProperty = DependencyProperty.Register(nameof(AutoBackupNextRunTime), typeof(string), typeof(MainWindow), new PropertyMetadata(string.Empty));
        public static readonly DependencyProperty AutoBackupStatePVPProperty = DependencyProperty.Register(nameof(AutoBackupStatePVP), typeof(Microsoft.Win32.TaskScheduler.TaskState), typeof(MainWindow), new PropertyMetadata(Microsoft.Win32.TaskScheduler.TaskState.Unknown));
        public static readonly DependencyProperty AutoBackupStateStringPVPProperty = DependencyProperty.Register(nameof(AutoBackupStateStringPVP), typeof(string), typeof(MainWindow), new PropertyMetadata(string.Empty));
        public static readonly DependencyProperty AutoBackupNextRunTimePVPProperty = DependencyProperty.Register(nameof(AutoBackupNextRunTimePVP), typeof(string), typeof(MainWindow), new PropertyMetadata(string.Empty));
        public static readonly DependencyProperty AutoUpdateStateProperty = DependencyProperty.Register(nameof(AutoUpdateState), typeof(Microsoft.Win32.TaskScheduler.TaskState), typeof(MainWindow), new PropertyMetadata(Microsoft.Win32.TaskScheduler.TaskState.Unknown));
        public static readonly DependencyProperty AutoUpdateStateStringProperty = DependencyProperty.Register(nameof(AutoUpdateStateString), typeof(string), typeof(MainWindow), new PropertyMetadata(string.Empty));
        public static readonly DependencyProperty AutoUpdateNextRunTimeProperty = DependencyProperty.Register(nameof(AutoUpdateNextRunTime), typeof(string), typeof(MainWindow), new PropertyMetadata(string.Empty));
        public static readonly DependencyProperty AutoUpdateStatePVPProperty = DependencyProperty.Register(nameof(AutoUpdateStatePVP), typeof(Microsoft.Win32.TaskScheduler.TaskState), typeof(MainWindow), new PropertyMetadata(Microsoft.Win32.TaskScheduler.TaskState.Unknown));
        public static readonly DependencyProperty AutoUpdateStateStringPVPProperty = DependencyProperty.Register(nameof(AutoUpdateStateStringPVP), typeof(string), typeof(MainWindow), new PropertyMetadata(string.Empty));
        public static readonly DependencyProperty AutoUpdateNextRunTimePVPProperty = DependencyProperty.Register(nameof(AutoUpdateNextRunTimePVP), typeof(string), typeof(MainWindow), new PropertyMetadata(string.Empty));
        public static readonly DependencyProperty DiscordBotStateProperty = DependencyProperty.Register(nameof(DiscordBotState), typeof(DiscordBot.Enums.BotState), typeof(MainWindow), new PropertyMetadata(DiscordBot.Enums.BotState.Unknown));
        public static readonly DependencyProperty DiscordBotStateStringProperty = DependencyProperty.Register(nameof(DiscordBotStateString), typeof(string), typeof(MainWindow), new PropertyMetadata(string.Empty));
        public static readonly DependencyProperty IsIpValidProperty = DependencyProperty.Register(nameof(IsIpValid), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));
        public static readonly DependencyProperty LatestServerManagerVersionProperty = DependencyProperty.Register(nameof(LatestServerManagerVersion), typeof(Version), typeof(MainWindow), new PropertyMetadata(new Version()));
        public static readonly DependencyProperty NewServerManagerAvailableProperty = DependencyProperty.Register(nameof(NewServerManagerAvailable), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));
        public static readonly DependencyProperty ServerLoadStatusProperty = DependencyProperty.Register(nameof(ServerLoadStatus), typeof(string), typeof(MainWindow), new PropertyMetadata(string.Empty));
        public static readonly DependencyProperty ServerLoadCurrentProperty = DependencyProperty.Register(nameof(ServerLoadCurrent), typeof(int), typeof(MainWindow), new PropertyMetadata(0));
        public static readonly DependencyProperty ServerLoadTotalProperty = DependencyProperty.Register(nameof(ServerLoadTotal), typeof(int), typeof(MainWindow), new PropertyMetadata(0));
        public static readonly DependencyProperty IsServerLoadingProperty = DependencyProperty.Register(nameof(IsServerLoading), typeof(bool), typeof(MainWindow), new PropertyMetadata(true));

        public MainWindow()
        {
            this.AppInstance = App.Instance;
            this.Config = Config.Default;
            this.CommonConfig = CommonConfig.Default;

            InitializeComponent();
            WindowUtils.RemoveDefaultResourceDictionary(this, Config.Default.DefaultGlobalizationFile);

            this.ServerManager = ServerManager.Instance;

            // Two separate live-filtered views over the same underlying
            // Servers collection - one PVE row, one PVP row, each sorted
            // alphabetically. Each view auto-updates as profiles are added/
            // removed, without altering the order of the underlying
            // collection itself (so nothing else that reads
            // ServerManager.Servers directly is affected).
            //
            // A profile still on its default "Unnamed Profile" name shows in
            // BOTH rows regardless of its Enable PVP setting - a brand new
            // profile hasn't had its mode meaningfully decided yet, so it
            // stays visible in both until the user either renames it or
            // explicitly sets Enable PVP, at which point it settles into
            // just the one matching row (see PVEPVPRelevantPropertyChanged
            // below for how a live change moves it immediately).
            bool IsStillUnnamed(Server s) => s.Profile.ProfileName == Config.Default.DefaultServerProfileName;

            this.AllServers = new ListCollectionView(this.ServerManager.Servers);
            this.AllServers.SortDescriptions.Add(new SortDescription("Profile.ProfileName", ListSortDirection.Ascending));

            this.PVEServers = new ListCollectionView(this.ServerManager.Servers)
            {
                Filter = o => IsStillUnnamed((Server)o) || !((Server)o).Profile.ManagerEnablePVP
            };
            this.PVEServers.SortDescriptions.Add(new SortDescription("Profile.ProfileName", ListSortDirection.Ascending));

            this.PVPServers = new ListCollectionView(this.ServerManager.Servers)
            {
                Filter = o => IsStillUnnamed((Server)o) || ((Server)o).Profile.ManagerEnablePVP
            };
            this.PVPServers.SortDescriptions.Add(new SortDescription("Profile.ProfileName", ListSortDirection.Ascending));

            // ListCollectionView only auto-re-evaluates its filter on the
            // source collection's own add/remove, not when a property on an
            // already-added item changes afterward - without this, toggling
            // Enable PVP (or renaming a profile away from "Unnamed Profile")
            // wouldn't move it to the other tab row until something else
            // happened to force a refresh.
            ServerProfile.PVEPVPRelevantPropertyChanged += (s, e) =>
            {
                this.AllServers?.Refresh();
                this.PVEServers?.Refresh();
                this.PVPServers?.Refresh();
            };

            this.DataContext = this;
            this.versionChecker = new ActionQueue();
            this.scheduledTaskChecker = new ActionQueue();
            this.discordBotChecker = new ActionQueue();
            this.discordPostUpdater = new ActionQueue();
            this.profileAutoSaver = new ActionQueue();

            IsAdministrator = SecurityUtils.IsAdministrator();
            if (!string.IsNullOrWhiteSpace(App.Instance.Title))
            {
                this.Title = App.Instance.Title;
            }
            else
            {
                if (IsAdministrator)
                {
                    this.Title = _globalizer.GetResourceString("MainWindow_TitleWithAdmin");
                }
                else
                {
                    this.Title = _globalizer.GetResourceString("MainWindow_Title");
                }
            }

            this.Left = Config.Default.MainWindow_Left;
            this.Top = Config.Default.MainWindow_Top;
            this.Height = Config.Default.MainWindow_Height;
            this.Width = Config.Default.MainWindow_Width;
            this.WindowState = Config.Default.MainWindow_WindowState;

            // hook into the language change event
            GlobalizedApplication.Instance.GlobalizationManager.ResourceDictionaryChangedEvent += ResourceDictionaryChangedEvent;
        }

        public App AppInstance
        {
            get { return GetValue(AppInstanceProperty) as App; }
            set { SetValue(AppInstanceProperty, value); }
        }

        public Config Config
        {
            get { return GetValue(ConfigProperty) as Config; }
            set { SetValue(ConfigProperty, value); }
        }

        public CommonConfig CommonConfig
        {
            get { return GetValue(CommonConfigProperty) as CommonConfig; }
            set { SetValue(CommonConfigProperty, value); }
        }

        public ServerManager ServerManager
        {
            get { return (ServerManager)GetValue(ServerManagerProperty); }
            set { SetValue(ServerManagerProperty, value); }
        }

        // Three sorted views over the same underlying ServerManager.Servers
        // collection - see the constructor. Only one is ever shown in the
        // single Tabs control at a time; the All/PVE/PVP mode buttons swap
        // Tabs.ItemsSource between them. AllServers has no filter.
        public ListCollectionView AllServers { get; private set; }
        public ListCollectionView PVEServers { get; private set; }
        public ListCollectionView PVPServers { get; private set; }

        public Microsoft.Win32.TaskScheduler.TaskState AutoBackupState
        {
            get { return (Microsoft.Win32.TaskScheduler.TaskState)GetValue(AutoBackupStateProperty); }
            set { SetValue(AutoBackupStateProperty, value); }
        }

        public string AutoBackupStateString
        {
            get { return (string)GetValue(AutoBackupStateStringProperty); }
            set { SetValue(AutoBackupStateStringProperty, value); }
        }

        public string AutoBackupNextRunTime
        {
            get { return (string)GetValue(AutoBackupNextRunTimeProperty); }
            set { SetValue(AutoBackupNextRunTimeProperty, value); }
        }

        // PVE and PVP Auto-Backup are two separate scheduled tasks now - the
        // properties above (no suffix) reflect the PVE task; these reflect
        // the PVP task, shown as its own row in the Task Status area.
        public Microsoft.Win32.TaskScheduler.TaskState AutoBackupStatePVP
        {
            get { return (Microsoft.Win32.TaskScheduler.TaskState)GetValue(AutoBackupStatePVPProperty); }
            set { SetValue(AutoBackupStatePVPProperty, value); }
        }

        public string AutoBackupStateStringPVP
        {
            get { return (string)GetValue(AutoBackupStateStringPVPProperty); }
            set { SetValue(AutoBackupStateStringPVPProperty, value); }
        }

        public string AutoBackupNextRunTimePVP
        {
            get { return (string)GetValue(AutoBackupNextRunTimePVPProperty); }
            set { SetValue(AutoBackupNextRunTimePVPProperty, value); }
        }

        public Microsoft.Win32.TaskScheduler.TaskState AutoUpdateState
        {
            get { return (Microsoft.Win32.TaskScheduler.TaskState)GetValue(AutoUpdateStateProperty); }
            set { SetValue(AutoUpdateStateProperty, value); }
        }

        public string AutoUpdateStateString
        {
            get { return (string)GetValue(AutoUpdateStateStringProperty); }
            set { SetValue(AutoUpdateStateStringProperty, value); }
        }

        public string AutoUpdateNextRunTime
        {
            get { return (string)GetValue(AutoUpdateNextRunTimeProperty); }
            set { SetValue(AutoUpdateNextRunTimeProperty, value); }
        }

        // PVE and PVP Auto-Update are two separate scheduled tasks now - the
        // properties above (no suffix) reflect the PVE task; these reflect
        // the PVP task, shown as its own row in the Task Status area.
        public Microsoft.Win32.TaskScheduler.TaskState AutoUpdateStatePVP
        {
            get { return (Microsoft.Win32.TaskScheduler.TaskState)GetValue(AutoUpdateStatePVPProperty); }
            set { SetValue(AutoUpdateStatePVPProperty, value); }
        }

        public string AutoUpdateStateStringPVP
        {
            get { return (string)GetValue(AutoUpdateStateStringPVPProperty); }
            set { SetValue(AutoUpdateStateStringPVPProperty, value); }
        }

        public string AutoUpdateNextRunTimePVP
        {
            get { return (string)GetValue(AutoUpdateNextRunTimePVPProperty); }
            set { SetValue(AutoUpdateNextRunTimePVPProperty, value); }
        }

        public DiscordBot.Enums.BotState DiscordBotState
        {
            get { return (DiscordBot.Enums.BotState)GetValue(DiscordBotStateProperty); }
            set { SetValue(DiscordBotStateProperty, value); }
        }

        public string DiscordBotStateString
        {
            get { return (string)GetValue(DiscordBotStateStringProperty); }
            set { SetValue(DiscordBotStateStringProperty, value); }
        }

        public bool IsAdministrator
        {
            get;
            set;
        }

        public bool IsIpValid
        {
            get { return (bool)GetValue(IsIpValidProperty); }
            set { SetValue(IsIpValidProperty, value); }
        }

        public Version LatestServerManagerVersion
        {
            get { return (Version)GetValue(LatestServerManagerVersionProperty); }
            set { SetValue(LatestServerManagerVersionProperty, value); }
        }

        public bool NewServerManagerAvailable
        {
            get { return (bool)GetValue(NewServerManagerAvailableProperty); }
            set { SetValue(NewServerManagerAvailableProperty, value); }
        }

        public string ServerLoadStatus
        {
            get { return (string)GetValue(ServerLoadStatusProperty); }
            set { SetValue(ServerLoadStatusProperty, value); }
        }

        public int ServerLoadCurrent
        {
            get { return (int)GetValue(ServerLoadCurrentProperty); }
            set { SetValue(ServerLoadCurrentProperty, value); }
        }

        public int ServerLoadTotal
        {
            get { return (int)GetValue(ServerLoadTotalProperty); }
            set { SetValue(ServerLoadTotalProperty, value); }
        }

        // While true, the server-load progress bar next to ServerLoadStatus is
        // shown. Once loading finishes, this is set false so the bar
        // disappears and only the "All X servers loaded" text remains -
        // otherwise a full/completed progress bar sits there permanently next
        // to text that already says loading is done, which reads as clutter.
        public bool IsServerLoading
        {
            get { return (bool)GetValue(IsServerLoadingProperty); }
            set { SetValue(IsServerLoadingProperty, value); }
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            //
            // Kick off the initialization.
            //

            // Let the manager shell finish its first layout/render pass before
            // profile deserialization begins. Starting profile work directly in
            // Loaded could block the very first paint and make Windows label the
            // application "Not Responding" even though it was still starting.
            ServerLoadStatus = _globalizer.GetResourceString("MainWindow_ServerLoadPreparing");
            ServerLoadCurrent = 0;
            ServerLoadTotal = 1;
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            await Task.Delay(1000);
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            var profileFiles = Directory.EnumerateFiles(Config.Default.ConfigDirectory, "*" + Config.Default.ProfileExtension).ToList();
            var processedProfiles = 0;
            var loadedProfiles = 0;
            var failedProfiles = 0;
            var failedProfileMessages = new List<string>();

            ServerLoadTotal = profileFiles.Count;
            ServerLoadCurrent = 0;
            ServerLoadStatus = profileFiles.Count == 0
                ? _globalizer.GetResourceString("MainWindow_ServerLoadNone")
                : string.Format(_globalizer.GetResourceString("MainWindow_ServerLoadProgressFormat"), 0, profileFiles.Count, profileFiles.Count);

            foreach (var profile in profileFiles)
            {
                try
                {
                    var previousCount = ServerManager.Instance.Servers.Count;
                    ServerManager.Instance.AddFromPath(profile);
                    if (ServerManager.Instance.Servers.Count > previousCount)
                        loadedProfiles++;
                    else
                    {
                        failedProfiles++;
                        failedProfileMessages.Add(string.Format(
                            _globalizer.GetResourceString("MainWindow_ProfileLoad_FailureEntryFormat"),
                            Path.GetFileName(profile),
                            profile,
                            _globalizer.GetResourceString("MainWindow_ProfileLoad_UnreadableReason")));
                        Logger.Error($"MainWindow_Loaded - Profile returned no server and was not loaded: {profile}");
                    }
                }
                catch (Exception ex)
                {
                    failedProfiles++;
                    failedProfileMessages.Add(string.Format(
                        _globalizer.GetResourceString("MainWindow_ProfileLoad_FailureEntryFormat"),
                        Path.GetFileName(profile),
                        profile,
                        ex.Message));
                    Logger.Error($"MainWindow_Loaded - Failed to load profile '{profile}'. {ex}");
                }

                processedProfiles++;
                ServerLoadCurrent = processedProfiles;
                ServerLoadStatus = string.Format(
                    _globalizer.GetResourceString("MainWindow_ServerLoadProgressFormat"),
                    loadedProfiles,
                    profileFiles.Count,
                    profileFiles.Count - processedProfiles);
            }

            ServerManager.Instance.SortServers();
            ServerManager.Instance.CheckProfiles();

            if (profileFiles.Count > 0)
            {
                ServerLoadStatus = failedProfiles == 0
                    ? string.Format(_globalizer.GetResourceString("MainWindow_ServerLoadCompleteFormat"), loadedProfiles)
                    : string.Format(_globalizer.GetResourceString("MainWindow_ServerLoadCompleteWithFailuresFormat"), loadedProfiles, failedProfiles);
            }

            IsServerLoading = false;

            if (failedProfileMessages.Count > 0)
            {
                MessageBox.Show(
                    string.Format(
                        _globalizer.GetResourceString("MainWindow_ProfileLoad_FailureSummaryFormat"),
                        failedProfileMessages.Count,
                        string.Join(Environment.NewLine + Environment.NewLine, failedProfileMessages)),
                    _globalizer.GetResourceString("MainWindow_ProfileLoad_FailureSummaryTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Exclamation);
            }

            if (Tabs.Items.Count > 0)
                Tabs.SelectedIndex = 0;

            this.versionChecker.PostAction(CheckForUpdates).DoNotWait();
            this.scheduledTaskChecker.PostAction(CheckForScheduledTasks).DoNotWait();
            this.discordBotChecker.PostAction(CheckForDiscordBot).DoNotWait();
            this.discordPostUpdater.PostAction(AutoUpdateDiscordPosts).DoNotWait();
            this.profileAutoSaver.PostAction(AutoSaveProfiles).DoNotWait();
        }

        private void MainWindow_LocationChanged(object sender, EventArgs e)
        {
            if (this.WindowState == WindowState.Normal)
            {
                Config.Default.MainWindow_Left = Math.Max(0D, this.Left);
                Config.Default.MainWindow_Top = Math.Max(0D, this.Top);
            }
        }

        private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (this.WindowState == WindowState.Normal)
            {
                Config.Default.MainWindow_Height = e.NewSize.Height;
                Config.Default.MainWindow_Width = e.NewSize.Width;
            }
        }

        private void MainWindow_StateChanged(object sender, EventArgs e)
        {
            if (Config.Default.MainWindow_MinimizeToTray && this.WindowState == WindowState.Minimized)
            {
                this.Hide();
            }
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            if (sender is Window window)
                window.Closed -= Window_Closed;

            this.Activate();
        }

        protected override void OnClosed(EventArgs e)
        {
            GlobalizedApplication.Instance.GlobalizationManager.ResourceDictionaryChangedEvent -= ResourceDictionaryChangedEvent;

            base.OnClosed(e);
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (DiscordBotHelper.HasRunningCommands)
            {
                var result = MessageBox.Show(_globalizer.GetResourceString("MainWindow_DiscordBot_RunningCommandsLabel"), _globalizer.GetResourceString("MainWindow_DiscordBot_RunningCommandsTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.No)
                {
                    e.Cancel = true;
                    return;
                }
            }

            // Closing mid-backup can leave a truncated/partial archive behind,
            // or (for the scheduled Auto-Backup headless process specifically)
            // interrupt a save that's still in flight on a server. Same shared
            // ActiveProfileIds signal that drives the per-tab 💾 icon, so this
            // catches every source - scheduled Auto-Backup, manual "Backup
            // Now", a Stop/Shutdown/Restart's own backup, Discord !backup.
            // Rather than a blocking "close anyway?" prompt, show which
            // server(s) are still in progress and wait - the window polls the
            // same status and closes itself (triggering the real close below)
            // the moment nothing is backing up anymore.
            if (BackupProgressUtils.GetActiveProfileIds(Config.Default.DataDir).Count > 0)
            {
                var waitWindow = new ClosingWaitWindow { Owner = this };
                waitWindow.ShowDialog();

                if (waitWindow.UserCancelled)
                {
                    e.Cancel = true;
                    return;
                }
                // Either ForceClose was clicked, or the window's own polling
                // found every backup had finished - either way, fall through
                // and actually close.
            }

            base.OnClosing(e);
            RCONWindow.CloseAllWindows();
            PlayerListWindow.CloseAllWindows();
            ServerMonitorWindow.CloseAllWindows();

            // The tray icon (Hardcodet TaskbarIcon) creates its own native window/
            // icon handle that isn't tied to the main window's lifetime. If it's
            // never explicitly disposed, that handle can keep the process alive
            // in Task Manager well after the visible window has closed.
            TrayIcon?.Dispose();

            // App.OnExit performs the final profile/config safety save. Saving all
            // profiles here as well made shutdown perform the same expensive work
            // twice, which was particularly noticeable with large clusters.
            this.versionChecker.DisposeAsync().DoNotWait();
            this.scheduledTaskChecker.DisposeAsync().DoNotWait();
            this.discordBotChecker.DisposeAsync().DoNotWait();
            this.discordPostUpdater.DisposeAsync().DoNotWait();
            this.profileAutoSaver.DisposeAsync().DoNotWait();
        }

        private void ResourceDictionaryChangedEvent(object source, ResourceDictionaryChangedEventArgs e)
        {
            if (IsAdministrator)
                this.Title = _globalizer.GetResourceString("MainWindow_TitleWithAdmin");
            else
                this.Title = _globalizer.GetResourceString("MainWindow_Title");

            this.versionChecker.PostAction(CheckForUpdates).DoNotWait();
            this.scheduledTaskChecker.PostAction(CheckForScheduledTasks).DoNotWait();
            this.discordBotChecker.PostAction(CheckForDiscordBot).DoNotWait();
        }

        private void PatchNotes_Click(object sender, RoutedEventArgs e)
        {
            var url = string.Empty;
            if (AppInstance.BetaVersion)
                url = Config.Default.ServerManagerVersionBetaFeedUrl;
            else
                url = Config.Default.ServerManagerVersionFeedUrl;

            if (!string.IsNullOrWhiteSpace(url))
            {
                var window = new VersionFeedWindow(url);
                window.Closed += Window_Closed;
                window.Owner = this;
                window.ShowDialog();
            }
            else
            {
                if (AppInstance.BetaVersion)
                    url = Config.Default.LatestASMBetaPatchNotesUrl;
                else
                    url = Config.Default.LatestASMPatchNotesUrl;

                if (string.IsNullOrWhiteSpace(url))
                    return;

                Process.Start(url);
            }
        }

        private void Donate_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(Config.Default.DonationUrl))
                return;

            var result = MessageBox.Show(_globalizer.GetResourceString("MainWindow_Donate_Label"), _globalizer.GetResourceString("MainWindow_Donate_Title"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                Process.Start(Config.Default.DonationUrl);
            }
        }

        private void Help_Click(object sender, RoutedEventArgs args)
        {
            if (string.IsNullOrWhiteSpace(Config.Default.HelpUrl))
                return;

            Process.Start(Config.Default.HelpUrl);
        }

        private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
        {
            var logFolder = App.GetLogFolder();
            if (!Directory.Exists(logFolder))
                logFolder = Config.Default.DataDir;
            Process.Start("explorer.exe", logFolder);
        }

        private void OriginalProjectInfo_Click(object sender, RoutedEventArgs e)
        {
            // This build is a personal fork/standalone build of the
            // original open-source ARK Server Manager project - credit
            // where it's due, and a click here takes you straight to it.
            Process.Start(new ProcessStartInfo("https://github.com/Bletch1971/ServerManagers") { UseShellExecute = true });
        }

        private void RCON_Click(object sender, RoutedEventArgs e)
        {
            var window = new OpenRCONWindow();
            window.Closed += Window_Closed;
            window.Owner = this;
            window.ShowDialog();
        }

        private async void RefreshPublicIP_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await App.DiscoverMachinePublicIPAsync(true);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Refresh Public IP Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Settings_Click(object sender, RoutedEventArgs args)
        {
            var window = new SettingsWindow();
            window.Closed += Window_Closed;
            window.Owner = this;
            window.ShowDialog();
        }

        private void GameData_Click(object sender, RoutedEventArgs e)
        {
            var window = new GameDataWindow();
            window.Closed += Window_Closed;
            window.Owner = this;
            window.ShowDialog();
        }

        private void Plugins_Click(object sender, RoutedEventArgs e)
        {
            var window = new PluginsWindow();
            window.Closed += Window_Closed;
            window.Owner = this;
            window.ShowDialog();
        }

        private void ServerMonitor_Click(object sender, RoutedEventArgs e)
        {
            var window = ServerMonitorWindow.GetWindow(ServerManager);
            window.Closed += Window_Closed;
            window.Show();
            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }
            window.Focus();
        }

        private async void SteamCMD_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(_globalizer.GetResourceString("MainWindow_SteamCmd_Label"), _globalizer.GetResourceString("MainWindow_SteamCmd_Title"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                ProgressWindow window = null;

                try
                {
                    var updater = new SteamCmdUpdater();
                    var cancelSource = new CancellationTokenSource();

                    window = new ProgressWindow(_globalizer.GetResourceString("Progress_ReinstallSteamCmd_WindowTitle"));
                    window.Closed += Window_Closed;
                    window.Owner = this;
                    window.Show();

                    await Task.Delay(1000);
                    await updater.ReinstallSteamCmdAsync(Config.Default.DataDir, new Progress<SteamCmdUpdater.Update>(u =>
                    {
                        var resourceString = string.IsNullOrWhiteSpace(u.StatusKey) ? null : _globalizer.GetResourceString(u.StatusKey);
                        var message = resourceString != null ? $"{SteamCmdUpdater.OUTPUT_PREFIX} {resourceString}" : u.StatusKey;
                        window?.AddMessage(message);

                        if (u.FailureText != null)
                        {
                            message = string.Format(_globalizer.GetResourceString("MainWindow_SteamCmd_FailedLabel"), u.FailureText);
                            window?.AddMessage(message);
                            MessageBox.Show(message, _globalizer.GetResourceString("MainWindow_SteamCmd_FailedTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    }), cancelSource.Token);
                }
                catch (Exception ex)
                {
                    var message = string.Format(_globalizer.GetResourceString("MainWindow_SteamCmd_FailedLabel"), ex.Message);
                    window?.AddMessage(message);
                    MessageBox.Show(message, _globalizer.GetResourceString("MainWindow_SteamCmd_FailedTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    if (window != null)
                        window.CloseWindow();
                }
            }
        }

        private async void Upgrade_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(String.Format(_globalizer.GetResourceString("MainWindow_Upgrade_Label"), this.LatestServerManagerVersion), _globalizer.GetResourceString("MainWindow_Upgrade_Title"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if(result == MessageBoxResult.Yes)
            {
                try
                {
                    OverlayMessage.Content = _globalizer.GetResourceString("MainWindow_OverlayMessage_UpgradeLabel");
                    OverlayGrid.Visibility = Visibility.Visible;

                    await Task.Delay(500);

                    var process = Process.GetCurrentProcess();
                    if (process == null || process.HasExited)
                        throw new Exception("Application process could not be found or does not exist.");

                    var assemblyLocation = Assembly.GetEntryAssembly().Location;
                    var updaterFile = Path.Combine(Path.GetDirectoryName(assemblyLocation), Config.Default.UpdaterFile);
                    var newUpdaterFile = Path.Combine(Path.GetDirectoryName(assemblyLocation), $"New{Config.Default.UpdaterFile}");

                    // check if there is a new version of the updater file
                    if (File.Exists(newUpdaterFile))
                    {
                        // file exists, overwrite the file, so that we use the new updater instead
                        try
                        {
                            File.Copy(newUpdaterFile, updaterFile, true);
                            await Task.Delay(1000);
                            File.Delete(newUpdaterFile);
                        }
                        catch (Exception ex)
                        {
                            // if error, then do nothing
                            Logger.Debug($"An error occurred trying to update the server manager updater. {ex.Message}");
                        }
                    }

                    if (!File.Exists(updaterFile))
                        throw new FileNotFoundException("The updater application could not be found or does not exist.");

                    var arguments = new string[]
                        {
                            process.Id.ToString().AsQuoted(),
                            App.Instance.BetaVersion ? Config.Default.LatestASMBetaDownloadUrl.AsQuoted() : Config.Default.LatestASMDownloadUrl.AsQuoted(),
                            Config.Default.UpdaterPrefix.AsQuoted(),
                        };

                    ProcessStartInfo info = new ProcessStartInfo()
                    {
                        FileName = updaterFile.AsQuoted(),
                        Arguments = string.Join(" ", arguments),
                        UseShellExecute = true,
                        CreateNoWindow = true,
                    };

                    var updaterProcess = Process.Start(info);
                    if (updaterProcess == null)
                        throw new Exception("Could not restart application.");
                }
                catch (Exception ex)
                {
                    MessageBox.Show(String.Format(_globalizer.GetResourceString("MainWindow_Upgrade_FailedLabel"), ex.Message, ex.StackTrace), _globalizer.GetResourceString("MainWindow_Upgrade_FailedTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    OverlayGrid.Visibility = Visibility.Collapsed;
                }
            }
        }

        private void Discord_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(Config.Default.DiscordUrl))
                return;

            Process.Start(Config.Default.DiscordUrl);
        }

        // Switches which filtered set the single Tabs control shows. Only
        // one mode visible at a time - matches the original single-row tab
        // layout exactly, just swapping ItemsSource instead of always
        // showing the same flat list.
        private void AllModeButton_Click(object sender, RoutedEventArgs e)
        {
            SetTabsMode(TabsDisplayMode.All);
        }

        private void PVEModeButton_Click(object sender, RoutedEventArgs e)
        {
            SetTabsMode(TabsDisplayMode.PVE);
        }

        private void PVPModeButton_Click(object sender, RoutedEventArgs e)
        {
            SetTabsMode(TabsDisplayMode.PVP);
        }

        private enum TabsDisplayMode
        {
            All,
            PVE,
            PVP
        }

        private void SetTabsMode(TabsDisplayMode mode)
        {
            // Swapping Tabs.ItemsSource below rebuilds the tab containers -
            // the currently active tab's ServerSettingsControl just gets
            // discarded rather than having its Server property change to
            // something else, which is the only thing that normally
            // triggers a save-on-switch-away (see ServerPropertyChanged).
            // Without this, any edit made just before switching the All/
            // PVE/PVP filter (e.g. picking a Local IP) was silently lost -
            // never actually written to disk at all.
            (Tabs.SelectedItem as Server)?.Profile.Save(false, false, null);

            AllModeButton.IsChecked = mode == TabsDisplayMode.All;
            PVEModeButton.IsChecked = mode == TabsDisplayMode.PVE;
            PVPModeButton.IsChecked = mode == TabsDisplayMode.PVP;

            switch (mode)
            {
                case TabsDisplayMode.PVE:
                    Tabs.ItemsSource = PVEServers;
                    break;
                case TabsDisplayMode.PVP:
                    Tabs.ItemsSource = PVPServers;
                    break;
                default:
                    Tabs.ItemsSource = AllServers;
                    break;
            }

            if (Tabs.Items.Count > 0)
                Tabs.SelectedIndex = 0;
        }

        public void Servers_AddNew(object sender, NewItemRequestedEventArgs e)
        {
            var index = this.ServerManager.AddNew();
            var newServer = this.ServerManager.Servers[index];

            // A brand-new profile keeps its default "Unnamed Profile" name,
            // which the PVEServers/PVPServers filters treat as "show in
            // both" - so it's already visible in whichever row you're on
            // (and the other one too) without needing to force its
            // Enable PVP setting one way or the other.
            ((EO.Wpf.TabControl)e.Source).SelectedItem = newServer;
        }

        public void Servers_Remove(object sender, TabItemCloseEventArgs args)
        {
            args.Canceled = true;

            // args.ItemIndex is relative to whichever filtered set (PVE/PVP)
            // is currently bound as ItemsSource, not the full Servers
            // collection - look the item up through Tabs' own Items rather
            // than indexing into ServerManager.Instance.Servers directly.
            var server = (Server)Tabs.Items[args.ItemIndex];

            var result = MessageBox.Show(_globalizer.GetResourceString("MainWindow_ProfileDelete_Label"), String.Format(_globalizer.GetResourceString("MainWindow_ProfileDelete_Title"), server.Profile.ProfileName), MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
            {
                // Leave args.Canceled = true: we remove the server from the
                // bound collection ourselves here, so the underlying
                // PVEServers/PVPServers views already update and close the
                // tab. Setting Canceled = false would additionally let
                // TabControl try its own default removal against a now-stale
                // index, which crashes (UnsetValue can't be removed from a
                // filtered ItemsSource that isn't a plain IList).
                ServerManager.Instance.Remove(server, deleteProfile: true);
            }
        }

        // PVE and PVP Auto-Backup are two entirely separate scheduled tasks
        // (see TaskSchedulerUtils - taskSuffix distinguishes them) - these
        // three handlers are shared between both rows in the Task Status
        // area, reading which ruleset via the clicked button's own Tag
        // ("PVE" or "PVP", set in XAML) rather than duplicating each method.
        private void AutoBackupTaskRun_Click(object sender, RoutedEventArgs e)
        {
            var taskSuffix = (sender as FrameworkElement)?.Tag as string;
            var taskKey = TaskSchedulerUtils.ComputeKey(Config.Default.DataDir);

            try
            {
                // Set the one-time force marker (see AutoBackupScheduleUtils)
                // before actually firing the real scheduled task - RunAutoBackup
                // is what makes the OS task's own state flip to Running (and
                // this button show Stop), the same as a normal tick; a
                // command-line flag can't be threaded through it since it
                // just re-fires the task's own fixed, already-registered
                // arguments.
                AutoBackupScheduleUtils.SetForceNextRun(Config.Default.DataDir, taskSuffix);
                TaskSchedulerUtils.RunAutoBackup(taskKey, taskSuffix);

                // Show Stop immediately rather than waiting for the next
                // 5-second status poll (see ScheduledTasksCheckTime) - a
                // quick run (few/no servers actually due, or this ruleset
                // has few servers) can finish before that poll ever catches
                // it as Running, so Stop would otherwise never appear even
                // though the run genuinely happened. The next real poll
                // (CheckForScheduledTasks) corrects this back to the task's
                // actual state once it completes.
                if (string.Equals(taskSuffix, "PVP", StringComparison.OrdinalIgnoreCase))
                    this.AutoBackupStatePVP = Microsoft.Win32.TaskScheduler.TaskState.Running;
                else
                    this.AutoBackupState = Microsoft.Win32.TaskScheduler.TaskState.Running;
            }
            catch (Exception ex)
            {
                Logger.Error($"{nameof(AutoBackupTaskRun_Click)} - Unable to start a manual Auto-Backup run. {ex.Message}");
            }
        }

        private void AutoBackupTaskStop_Click(object sender, RoutedEventArgs e)
        {
            if (!IsAdministrator)
            {
                MessageBox.Show(_globalizer.GetResourceString("MainWindow_TaskAdminErrorLabel"), _globalizer.GetResourceString("MainWindow_TaskAdminErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var taskSuffix = (sender as FrameworkElement)?.Tag as string;
            var taskKey = TaskSchedulerUtils.ComputeKey(Config.Default.DataDir);

            try
            {
                TaskSchedulerUtils.StopAutoBackup(taskKey, taskSuffix);
            }
            catch (Exception)
            {
                // Ignore.
            }
        }

        private void AutoBackupTaskState_Click(object sender, RoutedEventArgs e)
        {
            if (!IsAdministrator)
            {
                MessageBox.Show(_globalizer.GetResourceString("MainWindow_TaskAdminErrorLabel"), _globalizer.GetResourceString("MainWindow_TaskAdminErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var taskSuffix = (sender as FrameworkElement)?.Tag as string;
            var taskKey = TaskSchedulerUtils.ComputeKey(Config.Default.DataDir);

            try
            {
                TaskSchedulerUtils.SetAutoBackupState(taskKey, taskSuffix, null);
            }
            catch (Exception)
            {
                // Ignore.
            }
        }

        // PVE and PVP Auto-Update are two entirely separate scheduled tasks
        // (see TaskSchedulerUtils - taskSuffix distinguishes them) - these
        // three handlers are shared between both rows in the Task Status
        // area, reading which ruleset via the clicked button's own Tag
        // ("PVE" or "PVP", set in XAML) rather than duplicating each method.
        private void AutoUpdateTaskRun_Click(object sender, RoutedEventArgs e)
        {
            var taskSuffix = (sender as FrameworkElement)?.Tag as string;
            var taskKey = TaskSchedulerUtils.ComputeKey(Config.Default.DataDir);

            try
            {
                TaskSchedulerUtils.RunAutoUpdate(taskKey, taskSuffix);

                // Show Stop immediately rather than waiting for the next
                // scheduled-task status poll - see AutoBackupTaskRun_Click
                // for why.
                if (string.Equals(taskSuffix, "PVP", StringComparison.OrdinalIgnoreCase))
                    this.AutoUpdateStatePVP = Microsoft.Win32.TaskScheduler.TaskState.Running;
                else
                    this.AutoUpdateState = Microsoft.Win32.TaskScheduler.TaskState.Running;
            }
            catch (Exception)
            {
                // Ignore.
            }
        }

        private void AutoUpdateTaskStop_Click(object sender, RoutedEventArgs e)
        {
            if (!IsAdministrator)
            {
                MessageBox.Show(_globalizer.GetResourceString("MainWindow_TaskAdminErrorLabel"), _globalizer.GetResourceString("MainWindow_TaskAdminErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var taskSuffix = (sender as FrameworkElement)?.Tag as string;
            var taskKey = TaskSchedulerUtils.ComputeKey(Config.Default.DataDir);

            try
            {
                TaskSchedulerUtils.StopAutoUpdate(taskKey, taskSuffix);
            }
            catch (Exception)
            {
                // Ignore.
            }
        }

        private void AutoUpdateTaskState_Click(object sender, RoutedEventArgs e)
        {
            if (!IsAdministrator)
            {
                MessageBox.Show(_globalizer.GetResourceString("MainWindow_TaskAdminErrorLabel"), _globalizer.GetResourceString("MainWindow_TaskAdminErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var taskSuffix = (sender as FrameworkElement)?.Tag as string;
            var taskKey = TaskSchedulerUtils.ComputeKey(Config.Default.DataDir);

            try
            {
                TaskSchedulerUtils.SetAutoUpdateState(taskKey, taskSuffix, null);
            }
            catch (Exception)
            {
                // Ignore.
            }
        }

        private async void DiscordBotTaskState_Click(object sender, RoutedEventArgs e)
        {
            if (discordBotStateClicked)
                return;
            discordBotStateClicked = true;

            try
            {
                switch (DiscordBotState)
                {
                    case DiscordBot.Enums.BotState.Stopped:
                        AppInstance.StartDiscordBot();
                        break;
                    case DiscordBot.Enums.BotState.Connecting:
                    case DiscordBot.Enums.BotState.Running:
                        AppInstance.StopDiscordBot();
                        break;
                }

                await Task.Delay(5000);
            }
            catch (Exception)
            {
                // Ignore.
            }
            finally
            {
                discordBotStateClicked = false;
            }
        }

        public ICommand ShowWindowCommand
        {
            get
            {
                return new RelayCommand<object>(
                    execute: (parameter) =>
                    {
                        this.Show();
                        this.WindowState = WindowState.Normal;
                        this.Activate();
                    },
                    canExecute: (parameter) => 
                    {
                        return true;
                    }
                );
            }
        }

        public ICommand StatusButtonCommand
        {
            get
            {
                return new RelayCommand<Server>(
                    execute: (server) =>
                    {
                        Debug.WriteLine($"{server.Profile.ProfileName}: {server.Runtime.Status}");
                        if (!Config.Default.ServerStatus_EnableActions)
                            return;

                        switch (server.Runtime.Status)
                        {
                            case ServerStatus.Stopped:
                                if (Config.Default.ServerStatus_ShowActionConfirmation && MessageBox.Show(_globalizer.GetResourceString("MainWindow_ServerStatus_StartServerActionLabel"), _globalizer.GetResourceString("MainWindow_ServerStatus_StartServerActionTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                                    return;
                                StartServerAsync(server).DoNotWait();
                                break;

                            case ServerStatus.Running:
                                if (Config.Default.ServerStatus_ShowActionConfirmation && MessageBox.Show(_globalizer.GetResourceString("MainWindow_ServerStatus_ShutdownServerActionLabel"), _globalizer.GetResourceString("MainWindow_ServerStatus_ShutdownServerActionTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                                    return;
                                ShutdownServerAsync(server).DoNotWait();
                                break;
                        }
                    },
                    canExecute: (server) => server != null && server.Profile != null && server.Runtime != null
                );
            }
        }

        private async Task CheckForUpdates()
        {
            string url = App.Instance.BetaVersion ? Config.Default.LatestASMBetaVersionUrl : Config.Default.LatestASMVersionUrl;
            var newVersion = await NetworkUtils.GetLatestServerManagerVersion(url);

            TaskUtils.RunOnUIThreadAsync(() =>
            {
                try
                {
                    var appVersion = new Version();
                    Version.TryParse(App.Instance.Version, out appVersion);

                    this.LatestServerManagerVersion = newVersion;
                    this.NewServerManagerAvailable = appVersion < newVersion;

                    Logger.Info($"{nameof(CheckForUpdates)} performed");
                }
                catch (Exception)
                {
                    // Ignore.
                }
            }).DoNotWait();

            await Task.Delay(Config.Default.UpdateCheckTime * 60 * 1000);
            this.versionChecker.PostAction(CheckForUpdates).DoNotWait();
        }

        private async Task CheckForScheduledTasks()
        {
            var taskKey = TaskSchedulerUtils.ComputeKey(Config.Default.DataDir);

            try
            {
                // Windows Task Scheduler access can occasionally block while its
                // service is busy. This method already runs on a dedicated action
                // queue, so perform the COM/task queries here and marshal only the
                // four inexpensive display assignments to the window thread.
                var backupState = TaskSchedulerUtils.TaskStateAutoBackup(taskKey, "PVE", out DateTime backupnextRunTime);
                var backupStatePVP = TaskSchedulerUtils.TaskStateAutoBackup(taskKey, "PVP", out DateTime backupnextRunTimePVP);
                var updateState = TaskSchedulerUtils.TaskStateAutoUpdate(taskKey, "PVE", out DateTime updatenextRunTime);
                var updateStatePVP = TaskSchedulerUtils.TaskStateAutoUpdate(taskKey, "PVP", out DateTime updatenextRunTimePVP);

                // Which profile(s) are actively being backed up right now, across
                // every source (scheduled Auto-Backup running as a separate
                // headless process, a manual "Backup Now" click, Discord
                // !backup) - drives the per-tab "backing up" indicator.
                var activeBackupProfileIds = BackupProgressUtils.GetActiveProfileIds(Config.Default.DataDir);

                // Which profile(s) had their last save fail to actually take
                // (RCON accepted but the world file never updated) - drives
                // the per-tab save-warning indicator.
                var saveWarningProfileIds = SaveHealthUtils.GetWarningProfileIds(Config.Default.DataDir);

                await TaskUtils.RunOnUIThreadAsync(() =>
                {
                    this.AutoBackupState = backupState;
                    this.AutoBackupStatePVP = backupStatePVP;
                    this.AutoUpdateState = updateState;
                    this.AutoUpdateStatePVP = updateStatePVP;

                    this.AutoBackupStateString = GetTaskSchedulerStateString(backupState);
                    this.AutoBackupStateStringPVP = GetTaskSchedulerStateString(backupStatePVP);
                    this.AutoUpdateStateString = GetTaskSchedulerStateString(updateState);
                    this.AutoUpdateStateStringPVP = GetTaskSchedulerStateString(updateStatePVP);

                    // A "X of Y server(s)" count here would be misleading now that
                    // each server backs up on its own independent schedule - a
                    // given tick only ever processes whichever servers happen to
                    // be due at that moment, not "1 of 17" in any meaningful
                    // batch sense. The state string alone (Running/Ready/etc.) is
                    // enough; see BackupProgressUtils for anyone who wants the
                    // raw per-tick counts from the status file directly.

                    this.AutoBackupNextRunTime = backupnextRunTime == DateTime.MinValue ? string.Empty : $"{_globalizer.GetResourceString("MainWindow_TaskRunTimeLabel")} {backupnextRunTime:G}";
                    this.AutoBackupNextRunTimePVP = backupnextRunTimePVP == DateTime.MinValue ? string.Empty : $"{_globalizer.GetResourceString("MainWindow_TaskRunTimeLabel")} {backupnextRunTimePVP:G}";
                    this.AutoUpdateNextRunTime = updatenextRunTime == DateTime.MinValue ? string.Empty : $"{_globalizer.GetResourceString("MainWindow_TaskRunTimeLabel")} {updatenextRunTime:G}";
                    this.AutoUpdateNextRunTimePVP = updatenextRunTimePVP == DateTime.MinValue ? string.Empty : $"{_globalizer.GetResourceString("MainWindow_TaskRunTimeLabel")} {updatenextRunTimePVP:G}";

                    foreach (var server in ServerManager.Instance.Servers)
                    {
                        var profileId = server?.Profile?.ProfileID;
                        server.IsBackingUp = profileId != null && activeBackupProfileIds.Contains(profileId);
                        server.HasSaveWarning = profileId != null && saveWarningProfileIds.Contains(profileId);
                    }
                });

                // Watchdog for the self-rescheduling Discord-post loop (see
                // AutoUpdateDiscordPosts) - it's supposed to tick roughly
                // every 5 minutes on its own. If it's gone quiet for well
                // longer than that while the Discord bot is enabled, kick it
                // again rather than leaving restart-timer embeds (and every
                // other Discord post) silently frozen until the app restarts.
                if (Config.Default.DiscordBotEnabled && DateTime.UtcNow - discordPostUpdaterLastTickUTC > TimeSpan.FromMinutes(12))
                {
                    Logger.Warn($"{nameof(CheckForScheduledTasks)} - Discord post loop hasn't ticked in over 12 minutes, restarting it.");
                    discordPostUpdaterLastTickUTC = DateTime.UtcNow;
                    this.discordPostUpdater.PostAction(AutoUpdateDiscordPosts).DoNotWait();
                }

                Logger.Debug($"{nameof(CheckForScheduledTasks)} performed");
            }
            catch (Exception ex)
            {
                Logger.Debug($"{nameof(CheckForScheduledTasks)} was unable to read task state. {ex.Message}");
            }

            await Task.Delay(Config.Default.ScheduledTasksCheckTime * 1 * 1000);
            this.scheduledTaskChecker.PostAction(CheckForScheduledTasks).DoNotWait();
        }

        private async Task AutoSaveProfiles()
        {
            // Do not start another full-cluster operation immediately after the
            // startup loader finishes. Wait for the configured autosave interval
            // first, then stagger the individual profile saves below.
            await Task.Delay(Math.Max(1, Config.Default.ProfileAutoSavePeriod) * 60 * 1000);

            // Periodically saves every open profile tab so unsaved changes made in
            // the UI aren't lost if the person forgets to click Save (or the app
            // closes unexpectedly). Runs unconditionally on a timer rather than
            // tracking per-field dirty state, similar to autosave in Office/etc.
            if (Config.Default.ProfileAutoSavePeriod > 0)
            {
                var servers = new List<Server>();
                await TaskUtils.RunOnUIThreadAsync(() =>
                {
                    servers = ServerManager.Instance.Servers.ToList();
                });

                // Saving every profile inside one dispatcher callback blocked the
                // window for the whole cluster. Save one profile per callback and
                // pause briefly between them so input, painting and status updates
                // continue to be processed.
                foreach (var server in servers)
                {
                    await TaskUtils.RunOnUIThreadAsync(() =>
                    {
                        try
                        {
                            if (server?.Profile == null || server.Profile.SOTF_Enabled)
                                return;

                            server.Profile.Save(false, false, null);
                        }
                        catch (Exception ex)
                        {
                            Logger.Debug($"{nameof(AutoSaveProfiles)} - Unable to auto-save profile '{server?.Profile?.ProfileName}'. {ex.Message}");
                        }
                    });

                    await Task.Delay(75);
                }

                Logger.Info($"{nameof(AutoSaveProfiles)} performed");
            }

            this.profileAutoSaver.PostAction(AutoSaveProfiles).DoNotWait();
        }

        // Discord message refreshes run independently from other scheduled work.
        // RCON timeouts or other slow operations can take long enough to
        // delay the old shared loop, making the schedule appear to stop updating.
        private async Task AutoUpdateDiscordPosts()
        {
            // Touched on every tick attempt, success or failure - lets
            // CheckForScheduledTasks's watchdog below notice if this
            // self-rescheduling chain ever goes quiet (e.g. an unobserved
            // exception somewhere managed to fault the queue despite the
            // try/catch below) and revive it, rather than silently never
            // posting Discord updates again until the app restarts.
            discordPostUpdaterLastTickUTC = DateTime.UtcNow;

            try
            {
                await UpdateDiscordControlPanels();
                DiscordPanelLogger.Info($"{nameof(AutoUpdateDiscordPosts)} performed");
            }
            catch (Exception ex)
            {
                DiscordPanelLogger.Error($"{nameof(AutoUpdateDiscordPosts)} performed with errors. {ex.Message}");
            }

            // This method is posted to the queue via the Action-based overload,
            // which discards the Task this async method returns - nothing awaits
            // it. If anything below throws unhandled, that becomes an unobserved
            // task exception that silently kills this self-rescheduling chain
            // forever, with no error logged anywhere - it would just quietly stop
            // refreshing. Wrapping the delay + reschedule in their own try/catch
            // guarantees the next cycle always gets queued regardless.
            try
            {
                // Refresh on a low-frequency cadence to avoid unnecessary Discord
                // API traffic. Discord renders the relative timestamps live on
                // the client, so the embed does not need minute-by-minute edits.
                await Task.Delay(5 * 60 * 1000);
            }
            catch (Exception ex)
            {
                DiscordPanelLogger.Error($"{nameof(AutoUpdateDiscordPosts)} delay failed. {ex.Message}");
            }
            finally
            {
                try
                {
                    this.discordPostUpdater.PostAction(AutoUpdateDiscordPosts).DoNotWait();
                }
                catch (Exception ex)
                {
                    DiscordPanelLogger.Error($"{nameof(AutoUpdateDiscordPosts)} failed to reschedule itself. {ex.Message}");
                }
            }
        }

        private async Task UpdateDiscordControlPanels(bool force = false)
        {
            // These two checks used to return silently - with no log entry at
            // all, "nothing posts and there's no clue why" was the only
            // symptom a user could ever see. Log once per call so a missing/
            // wrong Channel 1 Id (the admin buttons channel, distinct from
            // whatever channel chat relay or alerts use) shows up in the logs
            // instead of looking identical to the bot silently doing nothing.
            if (!Config.Default.DiscordBotEnabled)
            {
                DiscordPanelLogger.Info($"{nameof(UpdateDiscordControlPanels)} - skipped, Discord bot is not enabled.");
                return;
            }

            if (string.IsNullOrWhiteSpace(Config.Default.DiscordChannel1Id))
            {
                DiscordPanelLogger.Warn($"{nameof(UpdateDiscordControlPanels)} - skipped, Channel 1 Id (Discord Bot Details - Channel 1 - Admin & Buttons) is not set.");
                return;
            }

            if (!ulong.TryParse(Config.Default.DiscordChannel1Id.Trim(), out var channelId))
            {
                DiscordPanelLogger.Warn($"{nameof(UpdateDiscordControlPanels)} - skipped, Channel 1 Id '{Config.Default.DiscordChannel1Id}' is not a valid Discord channel Id.");
                return;
            }

            // Server/ServerProfile/ServerRuntime are all WPF DependencyObjects,
            // which throw "the calling thread cannot access this object
            // because a different thread owns it" the moment any of their
            // properties are read or written from a thread other than the one
            // that created them. This method runs on the discordPostUpdater
            // background ActionQueue, not the UI thread - it used to touch
            // those objects directly and would throw on the very first
            // property read (ServerManager.Instance.Servers itself), aborting
            // the whole method every single time with nothing ever posted.
            // Build a plain-data snapshot of what needs posting on the UI
            // thread first, then do the actual (network) posting off it, then
            // marshal only the resulting write-back onto the UI thread too.
            var now = DateTime.UtcNow;
            var workItems = await TaskUtils.RunOnUIThreadAsync(() =>
            {
                var items = new List<(Server Server, ServerProfile Profile, string Title, string Description, string Status, int PlayerCount, string CacheKey, string Signature, ulong? ExistingMessageId)>();

                foreach (var server in ServerManager.Instance.Servers.ToList())
                {
                    var profile = server?.Profile;
                    if (profile == null || !profile.EnableRemoteAdminButtons)
                        continue;

                    var mapName = GameData.FriendlyMapNameForClass(profile.ServerMap, true);
                    var title = string.IsNullOrWhiteSpace(mapName) ? profile.ProfileName : mapName;

                    var status = server.Runtime?.Status.ToString() ?? "Unknown";
                    var playerCount = server.Runtime?.Players ?? 0;
                    var description = string.Format(_globalizer.GetResourceString("MainWindow_ControlPanelDescriptionFormat"), status, playerCount);

                    var cacheKey = string.IsNullOrWhiteSpace(profile.ProfileID) ? profile.ProfileName : profile.ProfileID;
                    var signature = $"{channelId}|{profile.ProfileName}|{title}|{status}|{playerCount}|{profile.DiscordControlMessageId}";

                    var skipUnchangedPanel = false;
                    if (!force)
                    {
                        lock (discordControlPanelCacheLock)
                        {
                            skipUnchangedPanel = discordControlPanelSignatures.TryGetValue(cacheKey, out var previousSignature)
                                && string.Equals(previousSignature, signature, StringComparison.Ordinal)
                                && discordControlPanelLastUpdates.TryGetValue(cacheKey, out var previousUpdate)
                                && now - previousUpdate < DiscordControlPanelForcedRefreshPeriod;
                        }
                    }

                    if (skipUnchangedPanel)
                        continue;

                    ulong? existingMessageId = null;
                    if (ulong.TryParse(profile.DiscordControlMessageId, out var parsedMessageId))
                        existingMessageId = parsedMessageId;

                    items.Add((server, profile, title, description, status, playerCount, cacheKey, signature, existingMessageId));
                }

                return items;
            });

            // Logged even on a totally uneventful run (nothing due to post) -
            // an empty list here usually means either no profile has "Enable
            // Remote Admin Discord Buttons" checked, or every panel already
            // matches what was last posted (see DiscordControlPanelForcedRefreshPeriod)
            // and is being skipped as unchanged.
            DiscordPanelLogger.Info($"{nameof(UpdateDiscordControlPanels)} - {workItems.Count} server(s) due for a control panel post/update (force={force}).");

            var bot = ServerManagerBotFactory.GetServerManagerBot();

            foreach (var item in workItems)
            {
                var profileName = item.Profile.ProfileName;

                try
                {
                    var resultMessageId = await bot.PostOrUpdateControlPanelAsync(channelId, item.ExistingMessageId, profileName, item.Title, item.Description);
                    if (!resultMessageId.HasValue)
                    {
                        DiscordPanelLogger.Warn($"{nameof(UpdateDiscordControlPanels)} - '{profileName}' returned no message Id (bot may not be connected yet, or the post silently failed).");
                        continue;
                    }

                    DiscordPanelLogger.Info($"{nameof(UpdateDiscordControlPanels)} - '{profileName}' control panel posted/updated in channel {channelId}, message Id {resultMessageId}.");

                    var signature = item.Signature;

                    await TaskUtils.RunOnUIThreadAsync(() =>
                    {
                        if (resultMessageId.ToString() != item.Profile.DiscordControlMessageId)
                        {
                            item.Profile.DiscordControlMessageId = resultMessageId.Value.ToString();
                            item.Profile.Save(false, false, null);

                            // The saved message id is part of the signature.
                            signature = $"{channelId}|{profileName}|{item.Title}|{item.Status}|{item.PlayerCount}|{item.Profile.DiscordControlMessageId}";
                        }
                    });

                    lock (discordControlPanelCacheLock)
                    {
                        discordControlPanelSignatures[item.CacheKey] = signature;
                        discordControlPanelLastUpdates[item.CacheKey] = now;
                    }
                }
                catch (Exception ex)
                {
                    DiscordPanelLogger.Error($"{nameof(UpdateDiscordControlPanels)} - Unable to update control panel for '{profileName}'. {ex.Message}");
                }

                // A cold run against a large cluster can mean dozens of profiles each
                // making 1-2 Discord REST calls back-to-back with zero pacing.
                // Discord.Net's default RetryMode already absorbs an actual 429 by
                // sleeping and resending rather than throwing, so this isn't required
                // for correctness - but a small gap between profiles is a better
                // citizen of Discord's rate limits and keeps one big cluster's refresh
                // from bursting the channel's message-create bucket all at once.
                await Task.Delay(250);
            }
        }

        private async Task CheckForDiscordBot()
        {
            TaskUtils.RunOnUIThreadAsync(() =>
            {
                try
                {
                    var botState = DiscordBot.Enums.BotState.Unknown;
                    if (AppInstance.DiscordBotStarted)
                    {
                        // DiscordBotStarted only means "the background run-loop is
                        // alive" (see App.xaml.cs) - it goes true the instant the loop
                        // starts, well before the gateway handshake actually completes,
                        // and stays true through any later reconnect. Check the bot's
                        // own IsConnected too so this panel doesn't keep claiming
                        // "Running" while the gateway is actually down and every
                        // Discord post/button-panel update is failing.
                        botState = ServerManagerBotFactory.GetServerManagerBot().IsConnected
                            ? DiscordBot.Enums.BotState.Running
                            : DiscordBot.Enums.BotState.Connecting;
                    }
                    else
                    {
                        if (Config.DiscordBotEnabled)
                        {
                            botState = DiscordBot.Enums.BotState.Stopped;
                        }
                        else
                        {
                            botState = DiscordBot.Enums.BotState.Disabled;
                        }
                    }

                    this.DiscordBotState = botState;

                    this.DiscordBotStateString = GetDiscordBotStateString(botState);

                    Logger.Info($"{nameof(CheckForDiscordBot)} performed");
                }
                catch (Exception)
                {
                    // Ignore.
                }
            }).DoNotWait();

            await Task.Delay(Config.Default.DiscordBotStatusCheckTime * 1 * 1000);
            this.discordBotChecker.PostAction(CheckForDiscordBot).DoNotWait();
        }

        private string GetTaskSchedulerStateString(Microsoft.Win32.TaskScheduler.TaskState taskState)
        {
            switch (taskState)
            {
                case Microsoft.Win32.TaskScheduler.TaskState.Disabled:
                    return _globalizer.GetResourceString("MainWindow_TaskStateDisabledLabel");
                case Microsoft.Win32.TaskScheduler.TaskState.Queued:
                    return _globalizer.GetResourceString("MainWindow_TaskStateQueuedLabel");
                case Microsoft.Win32.TaskScheduler.TaskState.Ready:
                    return _globalizer.GetResourceString("MainWindow_TaskStateReadyLabel");
                case Microsoft.Win32.TaskScheduler.TaskState.Running:
                    return _globalizer.GetResourceString("MainWindow_TaskStateRunningLabel");
                case Microsoft.Win32.TaskScheduler.TaskState.Unknown:
                    return _globalizer.GetResourceString("MainWindow_TaskStateUnknownLabel");
                default:
                    return _globalizer.GetResourceString("MainWindow_TaskStateUnknownLabel");
            }
        }

        private string GetDiscordBotStateString(DiscordBot.Enums.BotState botState)
        {
            switch (botState)
            {
                case DiscordBot.Enums.BotState.Disabled:
                    return _globalizer.GetResourceString("MainWindow_TaskStateDisabledLabel");
                case DiscordBot.Enums.BotState.Running:
                    return _globalizer.GetResourceString("MainWindow_TaskStateRunningLabel");
                case DiscordBot.Enums.BotState.Connecting:
                    return _globalizer.GetResourceString("MainWindow_DiscordBotStateConnectingLabel");
                case DiscordBot.Enums.BotState.Stopped:
                    return _globalizer.GetResourceString("MainWindow_TaskStateStoppedLabel");
                default:
                    return _globalizer.GetResourceString("MainWindow_TaskStateUnknownLabel");
            }
        }

        private async Task StartServerAsync(Server server)
        {
            if (server == null || server.Profile == null || server.Runtime == null || server.Runtime.Status != ServerStatus.Stopped)
                return;

            Mutex mutex = null;
            bool createdNew = false;

            try
            {
                // try to establish a mutex for the profile.
                mutex = new Mutex(true, ServerApp.GetMutexName(server.Profile.InstallDirectory), out createdNew);

                // check if the mutex was established
                if (createdNew)
                {
                    server.Profile.Save(false, false, null);

                    if (!server.Profile.Validate(false, out string validateMessage))
                    {
                        var outputMessage = _globalizer.GetResourceString("ProfileValidation_WarningLabel").Replace("{validateMessage}", validateMessage);
                        if (MessageBox.Show(outputMessage, _globalizer.GetResourceString("ProfileValidation_Title"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                            return;
                    }

                    await server.StartAsync();

                    var startupMessage = Config.Default.Alert_ServerStartedMessage;
                    if (Config.Default.Alert_ServerStartedMessageIncludeIPandPort && !string.IsNullOrWhiteSpace(Config.Default.Alert_ServerStartedMessageIPandPort))
                    {
                        var ipAndPortMessage = Config.Default.Alert_ServerStartedMessageIPandPort
                            .Replace("{ipaddress}", Config.Default.MachinePublicIP)
                            .Replace("{port}", server.Profile.QueryPort.ToString());
                        startupMessage += $" {ipAndPortMessage}";
                    }
                    PluginHelper.Instance.ProcessAlert(AlertType.Startup, server.Profile.ProfileName, startupMessage);

                    await Task.Delay(2000);
                }
                else
                {
                    // display an error message and exit
                    MessageBox.Show(_globalizer.GetResourceString("ServerSettings_StartServer_MutexFailedLabel"), _globalizer.GetResourceString("ServerSettings_StartServer_FailedTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, _globalizer.GetResourceString("ServerSettings_StartServer_FailedTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (mutex != null)
                {
                    if (createdNew)
                    {
                        mutex.ReleaseMutex();
                        mutex.Dispose();
                    }
                }
            }
        }

        private async Task ShutdownServerAsync(Server server)
        {
            if (server == null || server.Profile == null || server.Runtime == null || server.Runtime.Status != ServerStatus.Running)
                return;

            try
            {
                var shutdownWindow = ShutdownWindow.OpenShutdownWindow(server);
                if (shutdownWindow == null)
                {
                    MessageBox.Show(_globalizer.GetResourceString("ServerSettings_ShutdownServer_AlreadyOpenLabel"), _globalizer.GetResourceString("ServerSettings_ShutdownServer_FailedTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                server.Runtime.UpdateServerStatus(ServerStatus.Stopping, AvailabilityStatus.Unavailable, false);

                shutdownWindow.CloseShutdownWindowWhenFinished = true;
                shutdownWindow.Owner = this;
                shutdownWindow.Closed += Window_Closed;
                shutdownWindow.Show();
                await shutdownWindow.StartShutdownAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, _globalizer.GetResourceString("ServerSettings_ShutdownServer_FailedTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}

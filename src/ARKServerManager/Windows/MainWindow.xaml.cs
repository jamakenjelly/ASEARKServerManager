using EO.Wpf;
using NLog;
using ServerManagerTool.Common;
using ServerManagerTool.Common.Enums;
using ServerManagerTool.Common.Lib;
using ServerManagerTool.Common.Utils;
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

        private readonly GlobalizedApplication _globalizer = GlobalizedApplication.Instance;
        private readonly ActionQueue versionChecker;
        private readonly ActionQueue scheduledTaskChecker;
        private readonly ActionQueue webDashboardChecker;


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
        public static readonly DependencyProperty WebDashboardStateProperty = DependencyProperty.Register(nameof(WebDashboardState), typeof(string), typeof(MainWindow), new PropertyMetadata("Disabled"));
        public static readonly DependencyProperty WebDashboardStateStringProperty = DependencyProperty.Register(nameof(WebDashboardStateString), typeof(string), typeof(MainWindow), new PropertyMetadata(string.Empty));
        public static readonly DependencyProperty WebDashboardButtonLabelProperty = DependencyProperty.Register(nameof(WebDashboardButtonLabel), typeof(string), typeof(MainWindow), new PropertyMetadata(string.Empty));
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
            ManagerVersionText.Text = "v" + App.Instance.Version;
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
            this.webDashboardChecker = new ActionQueue();

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

        // "Disabled" (setting off), "Offline" (enabled but not listening), "Online".
        public string WebDashboardState
        {
            get { return (string)GetValue(WebDashboardStateProperty); }
            set { SetValue(WebDashboardStateProperty, value); }
        }

        public string WebDashboardStateString
        {
            get { return (string)GetValue(WebDashboardStateStringProperty); }
            set { SetValue(WebDashboardStateStringProperty, value); }
        }

        public string WebDashboardButtonLabel
        {
            get { return (string)GetValue(WebDashboardButtonLabelProperty); }
            set { SetValue(WebDashboardButtonLabelProperty, value); }
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

        private void UpdateThemeButtons()
        {
            var selected = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x5B, 0x9B, 0xD5));
            var normal = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x33, 0x33, 0x33));
            DefaultThemeButton.Background = AppTheme.Current == AppTheme.DefaultName ? selected : normal;
            LightThemeButton.Background = AppTheme.Current == AppTheme.LightName ? selected : normal;
        }

        private void DefaultTheme_Click(object sender, RoutedEventArgs e)
        {
            AppTheme.Apply(AppTheme.DefaultName);
            UpdateThemeButtons();
        }

        private void LightTheme_Click(object sender, RoutedEventArgs e)
        {
            AppTheme.Apply(AppTheme.LightName);
            UpdateThemeButtons();
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateThemeButtons();
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

                // let the bar and the count paint before the next profile is read
                // (ApplicationIdle: everything queued, including the paint, runs first; a short pause keeps the count readable)
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                await Task.Delay(120);
            }

            // the bar stays full while the servers are sorted and checked, until the normal header comes back
            ServerLoadStatus = _globalizer.GetResourceString("MainWindow_ServerLoadPreparing");
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);

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
            StartUpdateCheckTimer();
            StartFreezeMonitor();
            this.scheduledTaskChecker.PostAction(CheckForScheduledTasks).DoNotWait();
            this.webDashboardChecker.PostAction(CheckForWebDashboard).DoNotWait();
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
            this.webDashboardChecker.DisposeAsync().DoNotWait();
        }

        private void ResourceDictionaryChangedEvent(object source, ResourceDictionaryChangedEventArgs e)
        {
            if (IsAdministrator)
                this.Title = _globalizer.GetResourceString("MainWindow_TitleWithAdmin");
            else
                this.Title = _globalizer.GetResourceString("MainWindow_Title");

            this.versionChecker.PostAction(CheckForUpdates).DoNotWait();
            this.scheduledTaskChecker.PostAction(CheckForScheduledTasks).DoNotWait();
            this.webDashboardChecker.PostAction(CheckForWebDashboard).DoNotWait();
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

            Process.Start(new ProcessStartInfo(Config.Default.DonationUrl) { UseShellExecute = true });
        }

        private void UpdateNotes_Click(object sender, RoutedEventArgs e)
        {
            new UpdateNotesWindow { Owner = this }.ShowDialog();
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

        private void WebDashboardTaskState_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ServerManagerTool.Utils.WebDashboard.IsRunning)
                    ServerManagerTool.Utils.WebDashboard.Stop();
                else
                    ServerManagerTool.Utils.WebDashboard.Start();

                var running = ServerManagerTool.Utils.WebDashboard.IsRunning;
                this.WebDashboardState = running ? "Online" : "Offline";
                this.WebDashboardStateString = _globalizer.GetResourceString(running ? "MainWindow_WebDashboardStateOnlineLabel" : "MainWindow_WebDashboardStateOfflineLabel");
                this.WebDashboardButtonLabel = _globalizer.GetResourceString(running ? "MainWindow_WebDashboardTaskStopLabel" : "MainWindow_WebDashboardTaskStartLabel");
            }
            catch (Exception)
            {
                // Ignore.
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
            await CheckForUpdatesOnce();

            await Task.Delay(Config.Default.UpdateCheckTime * 60 * 1000);
            this.versionChecker.PostAction(CheckForUpdates).DoNotWait();
        }

        // one check, without scheduling the next one (the timer below does the scheduling)
        private async Task CheckForUpdatesOnce()
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
        }

        private System.Windows.Threading.DispatcherTimer _updateCheckTimer;
        private DateTime _nextUpdateCheck;

        // Records in the log whenever the window stops responding for more than about two seconds, and for how long, so a freeze can be matched with what
        // was happening at that time (a Start, a Stop, a server closing ...). A timer on the window's thread runs every 250 ms; if it comes late, the
        // thread was busy.
        private System.Windows.Threading.DispatcherTimer _freezeMonitorTimer;
        private DateTime _freezeMonitorLastTick;

        private void StartFreezeMonitor()
        {
            _freezeMonitorLastTick = DateTime.UtcNow;
            _freezeMonitorTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _freezeMonitorTimer.Tick += (s, e) =>
            {
                var now = DateTime.UtcNow;
                var late = now - _freezeMonitorLastTick;
                _freezeMonitorLastTick = now;
                if (late > TimeSpan.FromSeconds(2))
                    Logger.Warn($"The manager's window was not responding for {late.TotalSeconds:0.0} seconds (until {DateTime.Now:HH:mm:ss}).");
            };
            _freezeMonitorTimer.Start();
        }

        private static TimeSpan UpdateCheckInterval()
            => TimeSpan.FromMinutes(Math.Max(5, Math.Min(1440, Config.Default.ManagerUpdateCheckMinutes)));

        private void StartUpdateCheckTimer()
        {
            _nextUpdateCheck = DateTime.Now + UpdateCheckInterval();
            _updateCheckTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _updateCheckTimer.Tick += (s, e) =>
            {
                try
                {
                    var tip = "Check now if there is a newer version of the manager.";
                    if (!Config.Default.ManagerUpdateAutoCheck)
                    {
                        tip += "\r\nThe automatic check is off (Global Settings > Manager Updates).";
                    }
                    else
                    {
                        var left = _nextUpdateCheck - DateTime.Now;
                        if (left <= TimeSpan.Zero)
                        {
                            _nextUpdateCheck = DateTime.Now + UpdateCheckInterval();
                            this.versionChecker.PostAction(CheckForUpdatesOnce).DoNotWait();
                            left = _nextUpdateCheck - DateTime.Now;
                        }
                        // a changed interval takes effect at once, never later than the new interval
                        if (left > UpdateCheckInterval())
                            _nextUpdateCheck = DateTime.Now + UpdateCheckInterval();
                        tip += $"\r\nNext automatic check in {(int)left.TotalMinutes}:{left.Seconds:00} (every {(int)UpdateCheckInterval().TotalMinutes} minutes).";
                    }

                    CheckUpdateButton.ToolTip = tip;
                }
                catch (Exception)
                {
                    // Ignore.
                }
            };
            _updateCheckTimer.Start();
        }

        // The green "Check for update" button: asks GitHub now and says what it found; a newer version also shows the green Upgrade now bar.
        private async void CheckForUpdateNow_Click(object sender, RoutedEventArgs e)
        {
            var url = App.Instance.BetaVersion ? Config.Default.LatestASMBetaVersionUrl : Config.Default.LatestASMVersionUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                MessageBox.Show("This copy of the manager has no update address to check.", "Check for update", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var previous = CheckUpdateButton.Content;
            CheckUpdateButton.IsEnabled = false;
            CheckUpdateButton.Content = "Checking...";
            try
            {
                // a throw-away query keeps a cached copy of the version file from being used
                var newVersion = await NetworkUtils.GetLatestServerManagerVersion(url + (url.Contains("?") ? "&" : "?") + "t=" + DateTime.UtcNow.Ticks);

                if (newVersion.Major == 0 && newVersion.Minor == 0)
                {
                    MessageBox.Show("The manager could not reach the update page. Check the internet connection and try again.", "Check for update", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                Version.TryParse(App.Instance.Version, out var appVersion);
                this.LatestServerManagerVersion = newVersion;
                this.NewServerManagerAvailable = appVersion < newVersion;
                _nextUpdateCheck = DateTime.Now + UpdateCheckInterval();
                Logger.Info("Check for update (button): latest " + newVersion + ", this manager " + App.Instance.Version);

                if (this.NewServerManagerAvailable)
                    MessageBox.Show($"A new version is available: {newVersion}\r\n\r\nThis manager is {App.Instance.Version}. Click \"Upgrade now\" in the green bar at the top to update.", "Check for update", MessageBoxButton.OK, MessageBoxImage.Information);
                else
                    MessageBox.Show($"You have the latest version ({App.Instance.Version}).", "Check for update", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Logger.Error("Check for update (button) failed: " + ex.Message);
                MessageBox.Show("The check for an update failed: " + ex.Message, "Check for update", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                CheckUpdateButton.Content = previous;
                CheckUpdateButton.IsEnabled = true;
            }
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

                Logger.Debug($"{nameof(CheckForScheduledTasks)} performed");
            }
            catch (Exception ex)
            {
                Logger.Debug($"{nameof(CheckForScheduledTasks)} was unable to read task state. {ex.Message}");
            }

            await Task.Delay(Config.Default.ScheduledTasksCheckTime * 1 * 1000);
            this.scheduledTaskChecker.PostAction(CheckForScheduledTasks).DoNotWait();
        }

        private async Task CheckForWebDashboard()
        {
            TaskUtils.RunOnUIThreadAsync(() =>
            {
                try
                {
                    var webState = !Config.Default.WebDashboardEnabled ? "Disabled" : (ServerManagerTool.Utils.WebDashboard.IsRunning ? "Online" : "Offline");
                    this.WebDashboardState = webState;
                    this.WebDashboardStateString = _globalizer.GetResourceString("MainWindow_WebDashboardState" + webState + "Label");
                    this.WebDashboardButtonLabel = _globalizer.GetResourceString(webState == "Online" ? "MainWindow_WebDashboardTaskStopLabel" : "MainWindow_WebDashboardTaskStartLabel");

                    Logger.Info($"{nameof(CheckForWebDashboard)} performed");
                }
                catch (Exception)
                {
                    // Ignore.
                }
            }).DoNotWait();

            await Task.Delay(5000);
            this.webDashboardChecker.PostAction(CheckForWebDashboard).DoNotWait();
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

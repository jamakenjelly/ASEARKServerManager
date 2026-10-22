using ServerManagerTool.Common.Utils;
using ServerManagerTool.Lib;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using WPFSharp.Globalizer;

namespace ServerManagerTool
{
    /// <summary>
    /// Interaction logic for ArkApiPluginSyncWindow.xaml
    /// </summary>
    public partial class ArkApiPluginSyncWindow : Window
    {
        public class SyncProfile : DependencyObject
        {
            public static readonly DependencyProperty SelectedProperty = DependencyProperty.Register(nameof(Selected), typeof(bool), typeof(SyncProfile), new PropertyMetadata(false));
            public static readonly DependencyProperty ProfileNameProperty = DependencyProperty.Register(nameof(ProfileName), typeof(string), typeof(SyncProfile), new PropertyMetadata(string.Empty));
            public static readonly DependencyProperty ProfileProperty = DependencyProperty.Register(nameof(Profile), typeof(ServerProfile), typeof(SyncProfile), new PropertyMetadata(null));

            public bool Selected
            {
                get { return (bool)GetValue(SelectedProperty); }
                set { SetValue(SelectedProperty, value); }
            }
            public string ProfileName
            {
                get { return (string)GetValue(ProfileNameProperty); }
                set { SetValue(ProfileNameProperty, value); }
            }
            public ServerProfile Profile
            {
                get { return (ServerProfile)GetValue(ProfileProperty); }
                set { SetValue(ProfileProperty, value); }
            }

            // Not currently read anywhere - Remove Plugin used to gate on this
            // server's live running status before deleting anything, but that
            // checked the wrong thing (see PerformRemovePlugin) and was
            // removed. Left in place in case some future action here
            // genuinely needs runtime state, since Profile alone doesn't
            // carry it.
            public Server Server { get; set; }
        }

        private readonly GlobalizedApplication _globalizer = GlobalizedApplication.Instance;

        public static readonly DependencyProperty SyncProfilesProperty = DependencyProperty.Register(nameof(SyncProfiles), typeof(ObservableCollection<SyncProfile>), typeof(ArkApiPluginSyncWindow), new PropertyMetadata(new ObservableCollection<SyncProfile>()));
        public static readonly DependencyProperty AvailablePluginsProperty = DependencyProperty.Register(nameof(AvailablePlugins), typeof(ObservableCollection<string>), typeof(ArkApiPluginSyncWindow), new PropertyMetadata(new ObservableCollection<string>()));
        public static readonly DependencyProperty AvailableConfigFilesProperty = DependencyProperty.Register(nameof(AvailableConfigFiles), typeof(ObservableCollection<string>), typeof(ArkApiPluginSyncWindow), new PropertyMetadata(new ObservableCollection<string>()));
        public static readonly DependencyProperty HasNoConfigFilesProperty = DependencyProperty.Register(nameof(HasNoConfigFiles), typeof(bool), typeof(ArkApiPluginSyncWindow), new PropertyMetadata(false));

        public ArkApiPluginSyncWindow(ServerManager serverManager, ServerProfile serverProfile)
        {
            InitializeComponent();
            WindowUtils.RemoveDefaultResourceDictionary(this, Config.Default.DefaultGlobalizationFile);

            OverlayGrid.Visibility = Visibility.Collapsed;

            this.Title = string.Format(_globalizer.GetResourceString("ArkApiPluginSyncWindow_ProfileTitle"), serverProfile?.ProfileName);

            this.ServerManager = serverManager;
            this.ServerProfile = serverProfile;
            this.DataContext = this;
        }

        public ServerManager ServerManager { get; private set; }
        public ServerProfile ServerProfile { get; private set; }

        public ObservableCollection<SyncProfile> SyncProfiles
        {
            get { return (ObservableCollection<SyncProfile>)GetValue(SyncProfilesProperty); }
            set { SetValue(SyncProfilesProperty, value); }
        }

        public ObservableCollection<string> AvailablePlugins
        {
            get { return (ObservableCollection<string>)GetValue(AvailablePluginsProperty); }
            set { SetValue(AvailablePluginsProperty, value); }
        }

        public ObservableCollection<string> AvailableConfigFiles
        {
            get { return (ObservableCollection<string>)GetValue(AvailableConfigFilesProperty); }
            set { SetValue(AvailableConfigFilesProperty, value); }
        }

        public bool HasNoConfigFiles
        {
            get { return (bool)GetValue(HasNoConfigFilesProperty); }
            set { SetValue(HasNoConfigFilesProperty, value); }
        }

        // Add Plugin doesn't require the plugin to already exist on this
        // server - it installs from any folder you point it at (e.g.
        // straight from wherever you just built it), so it needs its own
        // source path independent of the "already installed here" plugin
        // list the Sync Config / Remove Plugin actions use.
        public static readonly DependencyProperty AddPluginSourceFolderProperty = DependencyProperty.Register(nameof(AddPluginSourceFolder), typeof(string), typeof(ArkApiPluginSyncWindow), new PropertyMetadata(string.Empty));
        public string AddPluginSourceFolder
        {
            get { return (string)GetValue(AddPluginSourceFolderProperty); }
            set { SetValue(AddPluginSourceFolderProperty, value); }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                CreateSyncProfileList();
                CreateAvailablePluginsList();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, _globalizer.GetResourceString("ArkApiPluginSyncWindow_Load_FailedTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CreateSyncProfileList()
        {
            SyncProfiles.Clear();

            if (this.ServerManager?.Servers == null)
                return;

            foreach (var server in this.ServerManager.Servers)
            {
                SyncProfiles.Add(new SyncProfile() { Selected = false, Profile = server.Profile, ProfileName = server.Profile.ProfileName, Server = server });
            }
        }

        private void CreateAvailablePluginsList()
        {
            AvailablePlugins.Clear();

            // A plugin can be installed on some servers but not on the one this window
            // was opened from (or have just been removed from it). Listing only this
            // server's plugins meant those could never be picked, so Remove Plugin had
            // nothing to select and couldn't clear the servers that still had it. List
            // every plugin found on any server instead.
            var pluginsDirs = new List<string>();
            try { pluginsDirs.Add(ServerProfile?.GetArkApiPluginsDir()); } catch { }

            if (ServerManager?.Servers != null)
            {
                foreach (var server in ServerManager.Servers)
                {
                    try { pluginsDirs.Add(server?.Profile?.GetArkApiPluginsDir()); } catch { }
                }
            }

            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var pluginsDir in pluginsDirs.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(pluginsDir))
                    continue;

                try
                {
                    foreach (var directory in Directory.GetDirectories(pluginsDir))
                        names.Add(Path.GetFileName(directory));
                }
                catch
                {
                    // An unreadable plugins folder on one server shouldn't hide the rest.
                }
            }

            foreach (var name in names)
            {
                AvailablePlugins.Add(name);
            }
        }

        private void PluginComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            AvailableConfigFiles.Clear();

            var pluginName = PluginComboBox.SelectedItem as string;
            var pluginsDir = ServerProfile?.GetArkApiPluginsDir();

            if (string.IsNullOrWhiteSpace(pluginName) || string.IsNullOrWhiteSpace(pluginsDir))
            {
                HasNoConfigFiles = false;
                return;
            }

            var pluginDir = Path.Combine(pluginsDir, pluginName);
            if (!Directory.Exists(pluginDir))
            {
                HasNoConfigFiles = false;
                return;
            }

            // Every plugin names its config differently (config.json,
            // settings.ini, PluginConfig.json, whatever) - rather than
            // guessing at names or extensions and risking hiding the actual
            // file, list everything in the folder except the plugin binary
            // itself (that's a build output, not something to sync) and let
            // the person pick whichever one is actually the config.
            // List every file in the plugin folder, subfolders included.
            var pluginDirPrefix = Path.GetFullPath(pluginDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (var file in Directory.GetFiles(pluginDir, "*", SearchOption.AllDirectories).OrderBy(f => f))
                AvailableConfigFiles.Add(Path.GetFullPath(file).Substring(pluginDirPrefix.Length));

            HasNoConfigFiles = AvailableConfigFiles.Count == 0;

            if (AvailableConfigFiles.Count > 0)
                ConfigFileComboBox.SelectedIndex = 0;
        }

        private static readonly string[] BinaryExtensions = { ".dll", ".pdb", ".exe", ".lib", ".zip", ".png", ".jpg", ".bin", ".dat" };
        private string _editorLoadedText;
        private string _editorFile;

        private void ConfigFileComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            _editorFile = null;
            _editorLoadedText = null;
            ConfigEditorTextBox.Text = string.Empty;
            ConfigEditorTextBox.IsEnabled = false;

            var pluginName = PluginComboBox.SelectedItem as string;
            var fileName = ConfigFileComboBox.SelectedItem as string;
            var pluginsDir = ServerProfile?.GetArkApiPluginsDir();
            if (string.IsNullOrWhiteSpace(pluginName) || string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(pluginsDir))
                return;

            var path = Path.Combine(pluginsDir, pluginName, fileName);
            if (BinaryExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()) || !File.Exists(path))
                return;

            try
            {
                var text = File.ReadAllText(path);
                _editorFile = path;
                _editorLoadedText = text;
                ConfigEditorTextBox.Text = text;
                ConfigEditorTextBox.IsEnabled = true;
            }
            catch
            {
                // Unreadable (locked?) - file can still be synced as-is.
            }
        }

        private void ConfigEditorTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
        }

        private void BrowseConfigFile_Click(object sender, RoutedEventArgs e)
        {
            var pluginName = PluginComboBox.SelectedItem as string;
            var pluginsDir = ServerProfile?.GetArkApiPluginsDir();

            if (string.IsNullOrWhiteSpace(pluginName) || string.IsNullOrWhiteSpace(pluginsDir))
            {
                MessageBox.Show(_globalizer.GetResourceString("ArkApiPluginSyncWindow_Process_NoPluginSelectedLabel"), _globalizer.GetResourceString("ProfileSyncWindow_Process_Title"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var pluginDir = Path.Combine(pluginsDir, pluginName);
            Directory.CreateDirectory(pluginDir);

            // A plain OpenFileDialog rather than trying to guess at names or
            // extensions again - this covers a config tucked in a subfolder,
            // an unusual extension, or anything else the auto-populated list
            // above might not anticipate.
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                InitialDirectory = pluginDir,
                Filter = _globalizer.GetResourceString("ArkApiPluginSyncWindow_BrowseFileFilter"),
                CheckFileExists = true,
            };

            if (dialog.ShowDialog(this) != true)
                return;

            // The synced file always ends up directly under the target's
            // <PluginName> folder (see PerformPluginConfigSync), so a
            // browsed file needs to actually be within this plugin's own
            // folder tree, not somewhere unrelated - otherwise "sync this
            // file to the same relative place on other servers" stops
            // meaning anything.
            var selectedFullPath = Path.GetFullPath(dialog.FileName);
            var pluginDirFullPath = Path.GetFullPath(pluginDir) + Path.DirectorySeparatorChar;

            if (!selectedFullPath.StartsWith(pluginDirFullPath, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(_globalizer.GetResourceString("ArkApiPluginSyncWindow_Process_FileOutsidePluginFolderLabel"), _globalizer.GetResourceString("ProfileSyncWindow_Process_Title"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var relativeFileName = selectedFullPath.Substring(pluginDirFullPath.Length);

            if (!AvailableConfigFiles.Contains(relativeFileName))
                AvailableConfigFiles.Add(relativeFileName);

            ConfigFileComboBox.SelectedItem = relativeFileName;
        }

        private void ProfileSelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var profile in SyncProfiles)
            {
                profile.Selected = true;
            }
        }

        private void ProfileUnselectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var profile in SyncProfiles)
            {
                profile.Selected = false;
            }
        }

        // PVE and PVP servers are often installed under completely separate
        // folders (e.g. separate drives/trees per mode) - GetArkApiPluginsDir
        // already resolves each profile's plugin folder from that profile's
        // OWN InstallDirectory, so syncing already targets the right install
        // location per server automatically. These two buttons just make it
        // one click to select every server of one mode, instead of hand-
        // picking checkboxes when you only want to push a sync to (say)
        // every PVP server and leave PVE alone.
        private void ProfileSelectAllPVE_Click(object sender, RoutedEventArgs e)
        {
            foreach (var profile in SyncProfiles)
            {
                profile.Selected = profile.Profile?.ManagerEnablePVP == false;
            }
        }

        private void ProfileSelectAllPVP_Click(object sender, RoutedEventArgs e)
        {
            foreach (var profile in SyncProfiles)
            {
                profile.Selected = profile.Profile?.ManagerEnablePVP == true;
            }
        }

        private async void Process_Click(object sender, RoutedEventArgs e)
        {
            var cursor = this.Cursor;

            try
            {
                var pluginName = PluginComboBox.SelectedItem as string;
                var configFileName = ConfigFileComboBox.SelectedItem as string;

                if (string.IsNullOrWhiteSpace(pluginName))
                {
                    MessageBox.Show(_globalizer.GetResourceString("ArkApiPluginSyncWindow_Process_NoPluginSelectedLabel"), _globalizer.GetResourceString("ProfileSyncWindow_Process_Title"), MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (string.IsNullOrWhiteSpace(configFileName))
                {
                    MessageBox.Show(_globalizer.GetResourceString("ArkApiPluginSyncWindow_Process_NoConfigFileSelectedLabel"), _globalizer.GetResourceString("ProfileSyncWindow_Process_Title"), MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (!SyncProfiles.Any(s => s.Selected))
                {
                    MessageBox.Show(_globalizer.GetResourceString("ProfileSyncWindow_Process_NoProfilesSelectedLabel"), _globalizer.GetResourceString("ProfileSyncWindow_Process_Title"), MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (MessageBox.Show(_globalizer.GetResourceString("ProfileSyncWindow_Process_ConfirmLabel"), _globalizer.GetResourceString("ProfileSyncWindow_Process_Title"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;

                Application.Current.Dispatcher.Invoke(() => this.Cursor = System.Windows.Input.Cursors.Wait);

                OverlayProfile.Content = string.Empty;
                OverlayGrid.Visibility = Visibility.Visible;
                await Task.Delay(100);

                var sourceFile = Path.Combine(ServerProfile.GetArkApiPluginsDir(), pluginName, configFileName);
                if (!File.Exists(sourceFile))
                {
                    MessageBox.Show(string.Format(_globalizer.GetResourceString("ArkApiPluginSyncWindow_Process_SourceFileMissingFormat"), sourceFile), _globalizer.GetResourceString("ProfileSyncWindow_Process_Title"), MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Save any edits made in the editor to the source file first,
                // so what gets synced is what's on screen.
                if (ConfigEditorTextBox.IsEnabled && _editorFile == sourceFile && ConfigEditorTextBox.Text != _editorLoadedText)
                {
                    File.WriteAllText(sourceFile, ConfigEditorTextBox.Text);
                    _editorLoadedText = ConfigEditorTextBox.Text;
                }

                var failures = await PerformPluginConfigSync(sourceFile, pluginName, configFileName);

                if (failures.Count == 0)
                {
                    MessageBox.Show(_globalizer.GetResourceString("ProfileSyncWindow_Process_SuccessLabel"), _globalizer.GetResourceString("ProfileSyncWindow_Process_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    var failureLines = string.Join(Environment.NewLine, failures.Select(f => $"{f.ProfileName}: {f.Error}"));
                    var message = string.Format(_globalizer.GetResourceString("ProfileSyncWindow_Process_PartialFailureFormat"), failures.Count, failureLines);
                    MessageBox.Show(message, _globalizer.GetResourceString("ProfileSyncWindow_Process_Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, _globalizer.GetResourceString("ProfileSyncWindow_Process_FailedTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                OverlayGrid.Visibility = Visibility.Collapsed;

                Application.Current.Dispatcher.Invoke(() => this.Cursor = cursor);
            }
        }

        private async Task<List<(string ProfileName, string Error)>> PerformPluginConfigSync(string sourceFile, string pluginName, string configFileName)
        {
            // Same bounded-retry approach as the profile Sync window: a
            // target server that's currently running may have this file
            // open (some plugins keep their config open/watch it live), so
            // give a transient lock a real chance to clear before finally
            // giving up on that one server - without letting it hang the
            // whole batch indefinitely.
            const int maxRetries = 6;
            const int retryDelaySeconds = 5;

            var failures = new List<(string ProfileName, string Error)>();

            foreach (var syncProfile in SyncProfiles)
            {
                if (!syncProfile.Selected)
                    continue;

                OverlayProfile.Content = syncProfile.ProfileName ?? string.Empty;

                try
                {
                    var targetPluginDir = Path.Combine(syncProfile.Profile.GetArkApiPluginsDir(), pluginName);
                    var targetFile = Path.Combine(targetPluginDir, configFileName);

                    // The source server can now appear in its own sync list -
                    // skip the actual copy when target resolves to the same
                    // file as the source (File.Copy throws on that).
                    if (string.Equals(Path.GetFullPath(targetFile), Path.GetFullPath(sourceFile), StringComparison.OrdinalIgnoreCase))
                        continue;

                    Directory.CreateDirectory(Path.GetDirectoryName(targetFile));

                    for (var attempt = 1; ; attempt++)
                    {
                        try
                        {
                            File.Copy(sourceFile, targetFile, true);
                            break;
                        }
                        catch (IOException) when (attempt < maxRetries)
                        {
                            await Task.Delay(retryDelaySeconds * 1000);
                        }
                    }
                }
                catch (Exception ex)
                {
                    var reason = ex is IOException
                        ? _globalizer.GetResourceString("ProfileSyncWindow_Process_FileInUseReason")
                        : ex.Message;

                    failures.Add((syncProfile.ProfileName ?? string.Empty, reason));
                }
            }

            return failures;
        }
        private void BrowseAddPluginFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.WindowsAPICodePack.Dialogs.CommonOpenFileDialog
            {
                IsFolderPicker = true,
                Title = _globalizer.GetResourceString("ArkApiPluginSyncWindow_BrowseAddPluginFolder_Title")
            };

            if (!string.IsNullOrWhiteSpace(AddPluginSourceFolder))
                dialog.InitialDirectory = AddPluginSourceFolder;

            if (dialog.ShowDialog(this) != Microsoft.WindowsAPICodePack.Dialogs.CommonFileDialogResult.Ok)
                return;

            AddPluginSourceFolder = dialog.FileName;
        }

        private async void AddPlugin_Click(object sender, RoutedEventArgs e)
        {
            var cursor = this.Cursor;

            try
            {
                if (string.IsNullOrWhiteSpace(AddPluginSourceFolder) || !Directory.Exists(AddPluginSourceFolder))
                {
                    MessageBox.Show(_globalizer.GetResourceString("ArkApiPluginSyncWindow_Process_NoSourceFolderSelectedLabel"), _globalizer.GetResourceString("ProfileSyncWindow_Process_Title"), MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var pluginName = Path.GetFileName(AddPluginSourceFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

                if (!SyncProfiles.Any(s => s.Selected))
                {
                    MessageBox.Show(_globalizer.GetResourceString("ProfileSyncWindow_Process_NoProfilesSelectedLabel"), _globalizer.GetResourceString("ProfileSyncWindow_Process_Title"), MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (MessageBox.Show(string.Format(_globalizer.GetResourceString("ArkApiPluginSyncWindow_AddPlugin_ConfirmFormat"), pluginName), _globalizer.GetResourceString("ProfileSyncWindow_Process_Title"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;

                Application.Current.Dispatcher.Invoke(() => this.Cursor = System.Windows.Input.Cursors.Wait);

                OverlayProfile.Content = string.Empty;
                OverlayGrid.Visibility = Visibility.Visible;
                await Task.Delay(100);

                var (failures, skipped) = await PerformAddPlugin(AddPluginSourceFolder, pluginName);

                ShowActionResult(failures, skipped, "ArkApiPluginSyncWindow_AddPlugin_SkippedFormat");

                // The plugin now exists on at least some servers - refresh
                // the "already installed here" list (used by Sync Config /
                // Remove Plugin) in case it just got added to this window's
                // own source server too.
                CreateAvailablePluginsList();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, _globalizer.GetResourceString("ProfileSyncWindow_Process_FailedTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                OverlayGrid.Visibility = Visibility.Collapsed;
                Application.Current.Dispatcher.Invoke(() => this.Cursor = cursor);
            }
        }


        private async void RemovePlugin_Click(object sender, RoutedEventArgs e)
        {
            var cursor = this.Cursor;

            try
            {
                var pluginName = PluginComboBox.SelectedItem as string;
                if (string.IsNullOrWhiteSpace(pluginName))
                {
                    MessageBox.Show(_globalizer.GetResourceString("ArkApiPluginSyncWindow_Process_NoPluginSelectedLabel"), _globalizer.GetResourceString("ProfileSyncWindow_Process_Title"), MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (!SyncProfiles.Any(s => s.Selected))
                {
                    MessageBox.Show(_globalizer.GetResourceString("ProfileSyncWindow_Process_NoProfilesSelectedLabel"), _globalizer.GetResourceString("ProfileSyncWindow_Process_Title"), MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (MessageBox.Show(string.Format(_globalizer.GetResourceString("ArkApiPluginSyncWindow_RemovePlugin_ConfirmFormat"), pluginName), _globalizer.GetResourceString("ProfileSyncWindow_Process_Title"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;

                Application.Current.Dispatcher.Invoke(() => this.Cursor = System.Windows.Input.Cursors.Wait);

                OverlayProfile.Content = string.Empty;
                OverlayGrid.Visibility = Visibility.Visible;
                await Task.Delay(100);

                var (failures, movedAside) = await PerformRemovePlugin(pluginName);

                ShowActionResult(failures, movedAside, "ArkApiPluginSyncWindow_RemovePlugin_MovedAsideFormat");

                // The plugin may no longer exist on this window's own source server -
                // refresh the "already installed here" list so it isn't still offered.
                CreateAvailablePluginsList();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, _globalizer.GetResourceString("ProfileSyncWindow_Process_FailedTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                OverlayGrid.Visibility = Visibility.Collapsed;
                Application.Current.Dispatcher.Invoke(() => this.Cursor = cursor);
            }
        }

        private void ShowActionResult(List<(string ProfileName, string Error)> failures, List<string> skipped, string skippedFormatKey)
        {
            if (failures.Count == 0 && skipped.Count == 0)
            {
                MessageBox.Show(_globalizer.GetResourceString("ProfileSyncWindow_Process_SuccessLabel"), _globalizer.GetResourceString("ProfileSyncWindow_Process_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var lines = new List<string>();
            if (skipped.Count > 0)
                lines.Add(string.Format(_globalizer.GetResourceString(skippedFormatKey), string.Join(", ", skipped)));
            lines.AddRange(failures.Select(f => $"{f.ProfileName}: {f.Error}"));

            var icon = failures.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information;
            MessageBox.Show(string.Join(Environment.NewLine, lines), _globalizer.GetResourceString("ProfileSyncWindow_Process_Title"), MessageBoxButton.OK, icon);
        }

        private async Task<(List<(string ProfileName, string Error)> Failures, List<string> Skipped)> PerformAddPlugin(string sourcePluginDir, string pluginName)
        {
            const int maxRetries = 6;
            const int retryDelaySeconds = 5;

            var failures = new List<(string ProfileName, string Error)>();
            var skipped = new List<string>();

            foreach (var syncProfile in SyncProfiles)
            {
                if (!syncProfile.Selected)
                    continue;

                OverlayProfile.Content = syncProfile.ProfileName ?? string.Empty;

                var targetPluginDir = Path.Combine(syncProfile.Profile.GetArkApiPluginsDir(), pluginName);

                try
                {
                    for (var attempt = 1; ; attempt++)
                    {
                        try
                        {
                            // Now overwrites an existing install instead of skipping it -
                            // delete the old copy first (rather than just copying over
                            // it) so a plugin update that removed a file doesn't leave
                            // that stale file behind alongside the new version. If the
                            // old version's .dll is still loaded this delete will throw
                            // and retry same as everywhere else here - unload the old
                            // plugin first if this keeps failing.
                            if (Directory.Exists(targetPluginDir))
                                Directory.Delete(targetPluginDir, true);

                            CopyDirectoryRecursive(sourcePluginDir, targetPluginDir);
                            break;
                        }
                        catch (IOException) when (attempt < maxRetries)
                        {
                            await Task.Delay(retryDelaySeconds * 1000);
                        }
                    }
                }
                catch (Exception ex)
                {
                    var reason = ex is IOException
                        ? _globalizer.GetResourceString("ProfileSyncWindow_Process_FileInUseReason")
                        : ex.Message;

                    failures.Add((syncProfile.ProfileName ?? string.Empty, reason));
                }
            }

            return (failures, skipped);
        }

        private async Task<(List<(string ProfileName, string Error)> Failures, List<string> MovedAside)> PerformRemovePlugin(string pluginName)
        {
            // Removes the plugin whether the server is online or offline.
            //
            // A plugin that's still loaded in a running server has its .dll locked by
            // Windows, so it can't be deleted - but it CAN be moved (a rename on the same
            // volume is allowed even for a mapped DLL). So: delete everything that can be
            // deleted, and for any file that's locked, move it out of the Plugins folder
            // into ArkApi\PendingDelete instead. The plugin is then gone from Plugins
            // right away (ArkApi won't load it next start), the running copy keeps
            // running until it's unloaded/restarted, and the moved files are purged the
            // next time this runs. Only a genuinely unmovable file counts as a failure.
            var failures = new List<(string ProfileName, string Error)>();
            var movedAside = new List<string>();

            foreach (var syncProfile in SyncProfiles)
            {
                if (!syncProfile.Selected)
                    continue;

                OverlayProfile.Content = syncProfile.ProfileName ?? string.Empty;
                await Task.Delay(50);

                var pluginsDir = syncProfile.Profile.GetArkApiPluginsDir();
                var targetPluginDir = Path.Combine(pluginsDir, pluginName);
                if (!Directory.Exists(targetPluginDir))
                    continue;

                try
                {
                    var moved = await Task.Run(() => RemovePluginDirectory(pluginsDir, targetPluginDir));
                    if (moved > 0)
                        movedAside.Add($"{syncProfile.ProfileName} ({moved} file(s))");
                }
                catch (Exception ex)
                {
                    failures.Add((syncProfile.ProfileName ?? string.Empty, ex.Message));
                }
            }

            return (failures, movedAside);
        }

        // Returns how many locked files had to be moved aside rather than deleted.
        private static int RemovePluginDirectory(string pluginsDir, string targetPluginDir)
        {
            // <install>\...\ArkApi\Plugins -> <install>\...\ArkApi\PendingDelete
            var pendingRoot = Path.Combine(Path.GetDirectoryName(pluginsDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), "PendingDelete");
            PurgePendingDelete(pendingRoot);

            var movedAside = 0;

            foreach (var file in Directory.GetFiles(targetPluginDir, "*", SearchOption.AllDirectories))
            {
                if (TryDeleteFile(file))
                    continue;

                // Locked (still loaded) - move it out of the plugin folder instead.
                Directory.CreateDirectory(pendingRoot);
                File.Move(file, Path.Combine(pendingRoot, $"{Path.GetFileName(file)}.{Guid.NewGuid():N}"));
                movedAside++;
            }

            // Everything left in the plugin folder is now just empty directories.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    Directory.Delete(targetPluginDir, true);
                    break;
                }
                catch (IOException) when (attempt < 5)
                {
                    System.Threading.Thread.Sleep(300);
                }
            }

            return movedAside;
        }

        private static bool TryDeleteFile(string file)
        {
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                    return true;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    if (attempt < 3)
                        System.Threading.Thread.Sleep(250);
                }
            }

            return false;
        }

        // Best-effort cleanup of files moved aside by earlier removals - anything still
        // locked (plugin not restarted/unloaded yet) is just left for next time.
        private static void PurgePendingDelete(string pendingRoot)
        {
            try
            {
                if (!Directory.Exists(pendingRoot))
                    return;

                foreach (var file in Directory.GetFiles(pendingRoot, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); File.Delete(file); } catch { }
                }

                if (!Directory.EnumerateFileSystemEntries(pendingRoot).Any())
                    Directory.Delete(pendingRoot);
            }
            catch
            {
                // Purely housekeeping - never let this block a removal.
            }
        }

        private static void CopyDirectoryRecursive(string sourceDir, string targetDir)
        {
            Directory.CreateDirectory(targetDir);

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), false);
            }

            foreach (var subDir in Directory.GetDirectories(sourceDir))
            {
                CopyDirectoryRecursive(subDir, Path.Combine(targetDir, Path.GetFileName(subDir)));
            }
        }

    }
}

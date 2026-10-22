using ServerManagerTool.Common.Utils;
using ServerManagerTool.Lib;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using WPFSharp.Globalizer;

namespace ServerManagerTool
{
    /// <summary>
    /// Edit one plugin file, then Save and Sync: saves it into the open
    /// profile's plugin folder and copies it into the same plugin on every
    /// selected server that has that plugin installed.
    /// </summary>
    public partial class ArkApiConfigEditWindow : Window
    {
        public class TargetServer : INotifyPropertyChanged
        {
            private bool _selected;
            public bool Selected { get { return _selected; } set { _selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected))); } }
            public string ProfileName { get; set; }
            public string Mode { get; set; }
            public ServerProfile Profile { get; set; }
            public event PropertyChangedEventHandler PropertyChanged;
        }

        private readonly GlobalizedApplication _globalizer = GlobalizedApplication.Instance;
        private readonly string _pluginName;
        private readonly string _fileName;
        private readonly string _sourceFile;
        private readonly bool _isText;

        private static readonly string[] BinaryExtensions = { ".dll", ".pdb", ".exe", ".lib", ".zip", ".png", ".jpg", ".bin", ".dat" };

        public ArkApiConfigEditWindow(ServerManager serverManager, ServerProfile serverProfile, string pluginName, string fileName)
        {
            InitializeComponent();
            WindowUtils.RemoveDefaultResourceDictionary(this, Config.Default.DefaultGlobalizationFile);

            this.Title = string.Format(_globalizer.GetResourceString("ArkApiConfigSyncWindow_ProfileTitle"), serverProfile?.ProfileName);
            this.ServerManager = serverManager;
            this.ServerProfile = serverProfile;
            this.SyncProfiles = new ObservableCollection<TargetServer>();
            this.DataContext = this;

            _pluginName = pluginName;
            _fileName = fileName;
            _sourceFile = Path.Combine(serverProfile.GetArkApiPluginsDir(), pluginName, fileName);
            _isText = !BinaryExtensions.Contains(Path.GetExtension(fileName).ToLowerInvariant());

            FileNameLabel.Text = pluginName + "\\" + fileName;
            if (_isText)
            {
                try { ConfigEditorTextBox.Text = File.ReadAllText(_sourceFile); }
                catch (Exception ex) { ConfigEditorTextBox.Text = ex.Message; ConfigEditorTextBox.IsEnabled = false; _isText = false; }
            }
            else
            {
                ConfigEditorTextBox.Text = _globalizer.GetResourceString("ArkApiConfigSyncWindow_BinaryFileLabel");
                ConfigEditorTextBox.IsEnabled = false;
            }
        }

        public ServerManager ServerManager { get; }
        public ServerProfile ServerProfile { get; }
        public ObservableCollection<TargetServer> SyncProfiles { get; }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (ServerManager?.Servers == null)
                return;

            foreach (var server in ServerManager.Servers)
                SyncProfiles.Add(new TargetServer { Profile = server.Profile, ProfileName = server.Profile.ProfileName, Mode = server.Profile.ManagerEnablePVP ? "PVP" : "PVE" });
        }

        private void ProfileSelectAll_Click(object sender, RoutedEventArgs e) { foreach (var p in SyncProfiles) p.Selected = true; }
        private void ProfileUnselectAll_Click(object sender, RoutedEventArgs e) { foreach (var p in SyncProfiles) p.Selected = false; }
        private void ProfileSelectAllPVE_Click(object sender, RoutedEventArgs e) { foreach (var p in SyncProfiles) p.Selected = p.Mode == "PVE"; }
        private void ProfileSelectAllPVP_Click(object sender, RoutedEventArgs e) { foreach (var p in SyncProfiles) p.Selected = p.Mode == "PVP"; }

        private async void SaveAndSync_Click(object sender, RoutedEventArgs e)
        {
            var cursor = this.Cursor;
            try
            {
                // Save into the open profile first, so it always has the edit
                // even if no other server is ticked.
                if (_isText)
                    File.WriteAllText(_sourceFile, ConfigEditorTextBox.Text);

                var targets = SyncProfiles.Where(s => s.Selected).ToList();
                if (targets.Count == 0)
                {
                    MessageBox.Show(_globalizer.GetResourceString("ArkApiConfigSyncWindow_SavedLabel"), _globalizer.GetResourceString("ProfileSyncWindow_Process_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                if (MessageBox.Show(_globalizer.GetResourceString("ProfileSyncWindow_Process_ConfirmLabel"), _globalizer.GetResourceString("ProfileSyncWindow_Process_Title"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;

                this.Cursor = System.Windows.Input.Cursors.Wait;
                OverlayProfile.Content = string.Empty;
                OverlayGrid.Visibility = Visibility.Visible;
                await Task.Delay(100);

                var failures = new List<string>();
                var skipped = new List<string>();

                foreach (var target in targets)
                {
                    OverlayProfile.Content = target.ProfileName ?? string.Empty;
                    try
                    {
                        var targetPluginDir = Path.Combine(target.Profile.GetArkApiPluginsDir(), _pluginName);
                        var targetFile = Path.Combine(targetPluginDir, _fileName);

                        if (string.Equals(Path.GetFullPath(targetFile), Path.GetFullPath(_sourceFile), StringComparison.OrdinalIgnoreCase))
                            continue;

                        // Only sync into servers that actually have this plugin.
                        if (!Directory.Exists(targetPluginDir))
                        {
                            skipped.Add(target.ProfileName);
                            continue;
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(targetFile));

                        for (var attempt = 1; ; attempt++)
                        {
                            try { File.Copy(_sourceFile, targetFile, true); break; }
                            catch (IOException) when (attempt < 6) { await Task.Delay(5000); }
                        }
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{target.ProfileName}: {ex.Message}");
                    }
                }

                var message = failures.Count == 0
                    ? _globalizer.GetResourceString("ProfileSyncWindow_Process_SuccessLabel")
                    : string.Format(_globalizer.GetResourceString("ProfileSyncWindow_Process_PartialFailureFormat"), failures.Count, string.Join(Environment.NewLine, failures));

                if (skipped.Count > 0)
                    message += Environment.NewLine + Environment.NewLine + string.Format(_globalizer.GetResourceString("ArkApiConfigSyncWindow_SkippedFormat"), string.Join(", ", skipped));

                MessageBox.Show(message, _globalizer.GetResourceString("ProfileSyncWindow_Process_Title"), MessageBoxButton.OK, failures.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, _globalizer.GetResourceString("ProfileSyncWindow_Process_FailedTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                OverlayGrid.Visibility = Visibility.Collapsed;
                this.Cursor = cursor;
            }
        }
    }
}

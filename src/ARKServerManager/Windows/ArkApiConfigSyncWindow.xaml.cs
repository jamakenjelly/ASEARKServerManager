using ServerManagerTool.Common.Utils;
using ServerManagerTool.Lib;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using WPFSharp.Globalizer;

namespace ServerManagerTool
{
    /// <summary>
    /// Lists this server's plugins and their files. Selecting a file and
    /// clicking Edit opens ArkApiConfigEditWindow to edit it and sync it.
    /// </summary>
    public partial class ArkApiConfigSyncWindow : Window
    {
        private readonly GlobalizedApplication _globalizer = GlobalizedApplication.Instance;

        public ArkApiConfigSyncWindow(ServerManager serverManager, ServerProfile serverProfile)
        {
            InitializeComponent();
            WindowUtils.RemoveDefaultResourceDictionary(this, Config.Default.DefaultGlobalizationFile);

            this.Title = string.Format(_globalizer.GetResourceString("ArkApiConfigSyncWindow_ProfileTitle"), serverProfile?.ProfileName);
            this.ServerManager = serverManager;
            this.ServerProfile = serverProfile;
            this.AvailablePlugins = new ObservableCollection<string>();
            this.PluginFiles = new ObservableCollection<string>();
            this.DataContext = this;
        }

        public ServerManager ServerManager { get; }
        public ServerProfile ServerProfile { get; }
        public ObservableCollection<string> AvailablePlugins { get; }
        public ObservableCollection<string> PluginFiles { get; }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var dir = ServerProfile.GetArkApiPluginsDir();
                if (Directory.Exists(dir))
                {
                    foreach (var d in Directory.GetDirectories(dir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                        AvailablePlugins.Add(Path.GetFileName(d));
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, _globalizer.GetResourceString("ArkApiPluginSyncWindow_Load_FailedTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void PluginListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            PluginFiles.Clear();

            var pluginName = PluginListBox.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(pluginName))
                return;

            var pluginDir = Path.Combine(ServerProfile.GetArkApiPluginsDir(), pluginName);
            if (!Directory.Exists(pluginDir))
                return;

            var prefix = Path.GetFullPath(pluginDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (var file in Directory.GetFiles(pluginDir, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                PluginFiles.Add(Path.GetFullPath(file).Substring(prefix.Length));
        }

        private void FileListBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (FileListBox.SelectedItem != null)
                Edit_Click(sender, e);
        }

        private void Edit_Click(object sender, RoutedEventArgs e)
        {
            var pluginName = PluginListBox.SelectedItem as string;
            var fileName = FileListBox.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(pluginName) || string.IsNullOrWhiteSpace(fileName))
            {
                MessageBox.Show(_globalizer.GetResourceString("ArkApiConfigSyncWindow_NoFileTickedLabel"), _globalizer.GetResourceString("ProfileSyncWindow_Process_Title"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var window = new ArkApiConfigEditWindow(ServerManager, ServerProfile, pluginName, fileName) { Owner = this };
            window.ShowDialog();
        }
    }
}

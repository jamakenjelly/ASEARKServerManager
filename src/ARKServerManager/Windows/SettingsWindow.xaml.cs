using ServerManagerTool.Common.Utils;
using ServerManagerTool.Lib;
using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using WPFSharp.Globalizer;

namespace ServerManagerTool
{
    /// <summary>
    /// Interaction logic for SettingsWindow.xaml
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private readonly GlobalizedApplication _globalizer = GlobalizedApplication.Instance;

        public SettingsWindow()
        {
            InitializeComponent();
            WindowUtils.RemoveDefaultResourceDictionary(this, Config.Default.DefaultGlobalizationFile);
        }

        private void SettingsWindow_Closing(object sender, CancelEventArgs e)
        {
            if (!globalSettingsControl.IsEnabled)
                e.Cancel = true;
        }

        protected override void OnClosed(EventArgs e)
        {
            globalSettingsControl.CloseControl();
            globalSettingsControl.ApplyChangesToConfig();

            if (SecurityUtils.IsAdministrator())
            {
                // check if the Auto Update has been enabled.
                if (Config.Default.AutoUpdate_EnableUpdate)
                {
                    // check if either ruleset actually has an update period set.
                    var pveUpdatePeriod = Config.Default.AutoUpdate_IncludePVE ? Config.Default.AutoUpdate_PVEPeriod : 0;
                    var pvpUpdatePeriod = Config.Default.AutoUpdate_IncludePVP ? Config.Default.AutoUpdate_PVPPeriod : 0;
                    if (pveUpdatePeriod <= 0 && pvpUpdatePeriod <= 0)
                    {
                        MessageBox.Show(_globalizer.GetResourceString("GlobalSettings_UpdateTaskUpdate_DisabledLabel"), _globalizer.GetResourceString("GlobalSettings_UpdateTaskUpdate_DisabledTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }
                    // check if the cache directory has been set and it exists.
                    if (string.IsNullOrWhiteSpace(Config.Default.AutoUpdate_CacheDir) || !Directory.Exists(Config.Default.AutoUpdate_CacheDir))
                    {
                        MessageBox.Show(_globalizer.GetResourceString("GlobalSettings_CacheDirectory_ErrorLabel"), _globalizer.GetResourceString("GlobalSettings_CacheDirectory_ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                }

                var taskKey = TaskSchedulerUtils.ComputeKey(Config.Default.DataDir);

                var command = Assembly.GetEntryAssembly().Location;

                // PVE and PVP Auto-Update are two entirely separate scheduled
                // tasks - each registered (or removed, if its ruleset isn't
                // included or Auto-Update itself is off) independently, with
                // its own period, the same way Auto-Backup already works.
                var updatePveOk = TaskSchedulerUtils.ScheduleAutoUpdate(taskKey, "PVE", command, (Config.Default.AutoUpdate_EnableUpdate && Config.Default.AutoUpdate_IncludePVE) ? Config.Default.AutoUpdate_PVEPeriod : 0, Config.Default.AutoUpdate_TaskPriority);
                var updatePvpOk = TaskSchedulerUtils.ScheduleAutoUpdate(taskKey, "PVP", command, (Config.Default.AutoUpdate_EnableUpdate && Config.Default.AutoUpdate_IncludePVP) ? Config.Default.AutoUpdate_PVPPeriod : 0, Config.Default.AutoUpdate_TaskPriority);

                if (!updatePveOk || !updatePvpOk)
                {
                    MessageBox.Show(_globalizer.GetResourceString("GlobalSettings_UpdateTaskUpdate_ErrorLabel"), _globalizer.GetResourceString("GlobalSettings_UpdateTaskUpdate_ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
                else if (Config.Default.AutoUpdate_EnableUpdate && (Config.Default.AutoUpdate_IncludePVE || Config.Default.AutoUpdate_IncludePVP))
                {
                    var updateSummary = new System.Text.StringBuilder();
                    if (Config.Default.AutoUpdate_IncludePVE)
                        updateSummary.AppendLine(String.Format(_globalizer.GetResourceString("GlobalSettings_UpdateTaskUpdate_EnabledLabel"), Config.Default.AutoUpdate_PVEPeriod) + " (PVE)");
                    if (Config.Default.AutoUpdate_IncludePVP)
                        updateSummary.AppendLine(String.Format(_globalizer.GetResourceString("GlobalSettings_UpdateTaskUpdate_EnabledLabel"), Config.Default.AutoUpdate_PVPPeriod) + " (PVP)");
                    MessageBox.Show(updateSummary.ToString(), _globalizer.GetResourceString("GlobalSettings_UpdateTaskUpdate_EnabledTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(_globalizer.GetResourceString("GlobalSettings_UpdateTaskUpdate_DisabledLabel"), _globalizer.GetResourceString("GlobalSettings_UpdateTaskUpdate_DisabledTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
                }

                // PVE and PVP Auto-Backup are two entirely separate scheduled
                // tasks - each registered (or removed, if its ruleset isn't
                // included) independently, with its own period.
                var pveOk = TaskSchedulerUtils.ScheduleAutoBackup(taskKey, "PVE", command, Config.Default.AutoBackup_IncludePVE ? Config.Default.AutoBackup_PVEPeriod : 0, Config.Default.AutoBackup_TaskPriority);
                var pvpOk = TaskSchedulerUtils.ScheduleAutoBackup(taskKey, "PVP", command, Config.Default.AutoBackup_IncludePVP ? Config.Default.AutoBackup_PVPPeriod : 0, Config.Default.AutoBackup_TaskPriority);

                if (!pveOk || !pvpOk)
                {
                    MessageBox.Show(_globalizer.GetResourceString("GlobalSettings_BackupTaskUpdate_ErrorLabel"), _globalizer.GetResourceString("GlobalSettings_BackupTaskUpdate_ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
                else if (Config.Default.AutoBackup_IncludePVE || Config.Default.AutoBackup_IncludePVP)
                {
                    var summary = new System.Text.StringBuilder();
                    if (Config.Default.AutoBackup_IncludePVE)
                        summary.AppendLine(String.Format(_globalizer.GetResourceString("GlobalSettings_BackupTaskUpdate_EnabledLabel"), Config.Default.AutoBackup_PVEPeriod) + " (PVE)");
                    if (Config.Default.AutoBackup_IncludePVP)
                        summary.AppendLine(String.Format(_globalizer.GetResourceString("GlobalSettings_BackupTaskUpdate_EnabledLabel"), Config.Default.AutoBackup_PVPPeriod) + " (PVP)");
                    MessageBox.Show(summary.ToString(), _globalizer.GetResourceString("GlobalSettings_BackupTaskUpdate_EnabledTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(_globalizer.GetResourceString("GlobalSettings_BackupTaskUpdate_DisabledLabel"), _globalizer.GetResourceString("GlobalSettings_BackupTaskUpdate_DisabledTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }

            if (Config.Default.SteamCmdRedirectOutput && !Config.Default.SteamCmd_UseAnonymousCredentials)
            {
                MessageBox.Show(_globalizer.GetResourceString("GlobalSettings_SteamCMDAuthentication_DisabledLabel"), _globalizer.GetResourceString("GlobalSettings_SteamCMDAuthentication_DisabledTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                Config.Default.SteamCmd_UseAnonymousCredentials = true;
            }

            App.SaveConfigFiles(false);

            base.OnClosed(e);
        }
    }
}

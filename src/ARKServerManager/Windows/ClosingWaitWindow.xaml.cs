using ServerManagerTool.Common.Utils;
using ServerManagerTool.Lib;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace ServerManagerTool
{
    // Shown instead of a blocking "close anyway?" prompt when one or more
    // servers are mid save/backup at the moment the user tries to close the
    // manager. Polls the same shared ActiveProfileIds status file that drives
    // the per-tab backup icon, lists which servers are still in progress, and
    // automatically triggers the real close once they've all finished -
    // rather than making the user manually retry closing.
    public partial class ClosingWaitWindow : Window
    {
        private readonly DispatcherTimer _pollTimer;
        public bool ForceClose { get; private set; }
        public bool UserCancelled { get; private set; }

        public ObservableCollection<string> ActiveProfileNames { get; } = new ObservableCollection<string>();

        public ClosingWaitWindow()
        {
            InitializeComponent();
            DataContext = this;

            RefreshActiveProfiles();

            _pollTimer = new DispatcherTimer { Interval = System.TimeSpan.FromSeconds(2) };
            _pollTimer.Tick += PollTimer_Tick;
            _pollTimer.Start();
        }

        private void PollTimer_Tick(object sender, System.EventArgs e)
        {
            if (!RefreshActiveProfiles())
            {
                // Nothing left in progress - proceed with the real close.
                _pollTimer.Stop();
                DialogResult = true;
                Close();
            }
        }

        // Returns true if anything is still actively backing up.
        private bool RefreshActiveProfiles()
        {
            var activeIds = BackupProgressUtils.GetActiveProfileIds(Config.Default.DataDir);

            var names = ServerManager.Instance.Servers
                .Where(s => s?.Profile?.ProfileID != null && activeIds.Contains(s.Profile.ProfileID))
                .Select(s => s.Profile.ProfileName)
                .ToList();

            if (names.Count == 0 && activeIds.Count > 0)
            {
                // Fall back to raw ids if a profile can't be matched (e.g.
                // deleted mid-backup) so the list is never silently empty
                // while a backup is still genuinely active.
                names = activeIds.ToList();
            }

            ActiveProfileNames.Clear();
            foreach (var name in names)
                ActiveProfileNames.Add(name);

            return activeIds.Count > 0;
        }

        private void CloseNow_Click(object sender, RoutedEventArgs e)
        {
            ForceClose = true;
            _pollTimer.Stop();
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            UserCancelled = true;
            _pollTimer.Stop();
            DialogResult = false;
            Close();
        }
    }
}

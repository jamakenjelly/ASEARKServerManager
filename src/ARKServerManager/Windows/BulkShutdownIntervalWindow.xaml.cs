using ServerManagerTool.Common.Utils;
using System.Windows;
using WPFSharp.Globalizer;

namespace ServerManagerTool.Windows
{
    /// <summary>
    /// Interaction logic for BulkShutdownIntervalWindow.xaml
    /// </summary>
    public partial class BulkShutdownIntervalWindow : Window
    {
        private readonly GlobalizedApplication _globalizer = GlobalizedApplication.Instance;

        public static readonly DependencyProperty WindowTitleProperty = DependencyProperty.Register(nameof(WindowTitle), typeof(string), typeof(BulkShutdownIntervalWindow), new PropertyMetadata(string.Empty));
        public static readonly DependencyProperty TargetSummaryProperty = DependencyProperty.Register(nameof(TargetSummary), typeof(string), typeof(BulkShutdownIntervalWindow), new PropertyMetadata(string.Empty));
        public static readonly DependencyProperty ConfirmButtonLabelProperty = DependencyProperty.Register(nameof(ConfirmButtonLabel), typeof(string), typeof(BulkShutdownIntervalWindow), new PropertyMetadata(string.Empty));
        public static readonly DependencyProperty ShutdownIntervalProperty = DependencyProperty.Register(nameof(ShutdownInterval), typeof(int), typeof(BulkShutdownIntervalWindow), new PropertyMetadata(Config.Default.ServerShutdown_GracePeriod));

        // restart: true shows "Restart"-flavored copy, false shows "Shutdown"-flavored.
        public BulkShutdownIntervalWindow(int targetCount, bool restart)
        {
            InitializeComponent();
            WindowUtils.RemoveDefaultResourceDictionary(this, Config.Default.DefaultGlobalizationFile);

            WindowTitle = _globalizer.GetResourceString(restart ? "ServerMonitor_BulkShutdown_RestartTitle" : "ServerMonitor_BulkShutdown_ShutdownTitle");
            TargetSummary = string.Format(_globalizer.GetResourceString(restart ? "ServerMonitor_BulkShutdown_RestartSummaryFormat" : "ServerMonitor_BulkShutdown_ShutdownSummaryFormat"), targetCount);
            ConfirmButtonLabel = _globalizer.GetResourceString(restart ? "ServerMonitor_BulkShutdown_RestartButtonLabel" : "ServerMonitor_BulkShutdown_ShutdownButtonLabel");

            this.DataContext = this;
        }

        public string WindowTitle
        {
            get { return (string)GetValue(WindowTitleProperty); }
            set { SetValue(WindowTitleProperty, value); }
        }

        public string TargetSummary
        {
            get { return (string)GetValue(TargetSummaryProperty); }
            set { SetValue(TargetSummaryProperty, value); }
        }

        public string ConfirmButtonLabel
        {
            get { return (string)GetValue(ConfirmButtonLabelProperty); }
            set { SetValue(ConfirmButtonLabelProperty, value); }
        }

        // Same default as the single-server ShutdownWindow (ServerShutdown_GracePeriod),
        // and the value the caller reads back out via DialogResult == true to apply
        // as ServerApp.ShutdownInterval for every selected server in this batch.
        public int ShutdownInterval
        {
            get { return (int)GetValue(ShutdownIntervalProperty); }
            set { SetValue(ShutdownIntervalProperty, value); }
        }

        private void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }
    }
}

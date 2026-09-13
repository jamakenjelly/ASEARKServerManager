using ServerManagerTool.Common.Utils;
using System.Windows;
using WPFSharp.Globalizer;

namespace ServerManagerTool.Windows
{
    /// <summary>
    /// Interaction logic for SendRconCommandWindow.xaml
    /// </summary>
    public partial class SendRconCommandWindow : Window
    {
        private readonly GlobalizedApplication _globalizer = GlobalizedApplication.Instance;

        public static readonly DependencyProperty TargetSummaryProperty = DependencyProperty.Register(nameof(TargetSummary), typeof(string), typeof(SendRconCommandWindow), new PropertyMetadata(string.Empty));
        public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(nameof(Command), typeof(string), typeof(SendRconCommandWindow), new PropertyMetadata(string.Empty, OnCommandChanged));
        public static readonly DependencyProperty HasCommandProperty = DependencyProperty.Register(nameof(HasCommand), typeof(bool), typeof(SendRconCommandWindow), new PropertyMetadata(false));

        public SendRconCommandWindow(int targetCount)
        {
            InitializeComponent();
            WindowUtils.RemoveDefaultResourceDictionary(this, Config.Default.DefaultGlobalizationFile);

            TargetSummary = string.Format(_globalizer.GetResourceString("ServerMonitor_SendRconCommand_TargetSummaryFormat"), targetCount);
            this.DataContext = this;
        }

        public string TargetSummary
        {
            get { return (string)GetValue(TargetSummaryProperty); }
            set { SetValue(TargetSummaryProperty, value); }
        }

        public string Command
        {
            get { return (string)GetValue(CommandProperty); }
            set { SetValue(CommandProperty, value); }
        }

        public bool HasCommand
        {
            get { return (bool)GetValue(HasCommandProperty); }
            set { SetValue(HasCommandProperty, value); }
        }

        private static void OnCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is SendRconCommandWindow window)
                window.HasCommand = !string.IsNullOrWhiteSpace(window.Command);
        }

        private void SendButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }
    }
}

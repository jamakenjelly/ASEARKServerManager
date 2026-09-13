using ServerManagerTool.Common.Model;
using ServerManagerTool.Common.Utils;
using ServerManagerTool.Lib;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using WPFSharp.Globalizer;

namespace ServerManagerTool
{
    /// <summary>
    /// Interaction logic for ProcessorAffinityWindow.xaml
    /// </summary>
    public partial class ProcessorAffinityWindow : Window
    {
        private readonly GlobalizedApplication _globalizer = GlobalizedApplication.Instance;

        public static readonly DependencyProperty ProcessorAffinityListProperty = DependencyProperty.Register(nameof(ProcessorAffinityList), typeof(ProcessorAffinityList), typeof(ProcessorAffinityWindow), new PropertyMetadata(null));
        public ProcessorAffinityList ProcessorAffinityList
        {
            get { return (ProcessorAffinityList)GetValue(ProcessorAffinityListProperty); }
            set { SetValue(ProcessorAffinityListProperty, value); }
        }

        public ICollectionView ProcessorAffinityView { get; private set; }

        // Exposed so the Process Priority combo box in this window can bind
        // directly to the profile - priority applies immediately like it does
        // in the main Administration tab, no separate "Process" step needed
        // (unlike affinity, which is only committed when Process is clicked).
        public ServerProfile Profile { get; private set; }
        public ComboBoxItemList ProcessPriorities { get; private set; }

        public ProcessorAffinityWindow(ServerProfile profile, ComboBoxItemList processPriorities)
        {
            Profile = profile;
            ProcessPriorities = processPriorities;
            AffinityValue = profile.ProcessAffinity;

            InitializeComponent();
            WindowUtils.RemoveDefaultResourceDictionary(this, Config.Default.DefaultGlobalizationFile);

            this.Title = string.Format(_globalizer.GetResourceString("ProcessorAffinity_ProfileTitle"), profile.ProfileName);
            this.ProcessorAffinityList = new ProcessorAffinityList(AffinityValue);

            this.ProcessorAffinityView = CollectionViewSource.GetDefaultView(this.ProcessorAffinityList);
            this.ProcessorAffinityView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ProcessorAffinityItem.CpuGroup)));

            this.DataContext = this;
        }

        public System.Numerics.BigInteger AffinityValue
        {
            get;
            set;
        }

        private void Process_Click(object sender, RoutedEventArgs e)
        {
            AffinityValue = this.ProcessorAffinityList.AffinityValue;

            DialogResult = true;
            Close();
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in ProcessorAffinityList)
            {
                item.Selected = true;
            }
        }

        private void UnselectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in ProcessorAffinityList)
            {
                item.Selected = false;
            }
        }
    }
}

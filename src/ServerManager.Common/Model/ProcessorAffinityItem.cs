using System.Numerics;
using System.Windows;

namespace ServerManagerTool.Common.Model
{
    public class ProcessorAffinityItem : DependencyObject
    {
        public static readonly DependencyProperty SelectedProperty = DependencyProperty.Register(nameof(Selected), typeof(bool), typeof(ProcessorAffinityItem), new PropertyMetadata(false));
        public bool Selected
        {
            get { return (bool)GetValue(SelectedProperty); }
            set { SetValue(SelectedProperty, value); }
        }

        public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(nameof(Description), typeof(string), typeof(ProcessorAffinityItem), new PropertyMetadata(string.Empty));
        public string Description
        {
            get { return (string)GetValue(DescriptionProperty); }
            set { SetValue(DescriptionProperty, value); }
        }

        public static readonly DependencyProperty AffinityValueProperty = DependencyProperty.Register(nameof(AffinityValue), typeof(BigInteger), typeof(ProcessorAffinityItem), new PropertyMetadata(BigInteger.Zero));
        public BigInteger AffinityValue
        {
            get { return (BigInteger)GetValue(AffinityValueProperty); }
            set { SetValue(AffinityValueProperty, value); }
        }

        // Which physical CPU package (0-based) this logical thread belongs to.
        public static readonly DependencyProperty CpuGroupProperty = DependencyProperty.Register(nameof(CpuGroup), typeof(int), typeof(ProcessorAffinityItem), new PropertyMetadata(0));
        public int CpuGroup
        {
            get { return (int)GetValue(CpuGroupProperty); }
            set { SetValue(CpuGroupProperty, value); }
        }

        // True for the first 2 threads within each CPU's own group - Windows and
        // background processes lean on these, so leaving them free tends to keep
        // the system more responsive. Purely advisory: still fully selectable.
        public static readonly DependencyProperty IsRecommendedForWindowsProperty = DependencyProperty.Register(nameof(IsRecommendedForWindows), typeof(bool), typeof(ProcessorAffinityItem), new PropertyMetadata(false));
        public bool IsRecommendedForWindows
        {
            get { return (bool)GetValue(IsRecommendedForWindowsProperty); }
            set { SetValue(IsRecommendedForWindowsProperty, value); }
        }
    }
}

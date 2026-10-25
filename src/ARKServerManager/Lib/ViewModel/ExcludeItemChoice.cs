using System;
using System.Windows;

namespace ServerManagerTool.Lib.ViewModel
{
    /// <summary>One item in the Exclude Items tick list: its name and ID number, and whether it is on the server's exclude list.</summary>
    public class ExcludeItemChoice : DependencyObject
    {
        public static readonly DependencyProperty IsCheckedProperty = DependencyProperty.Register(nameof(IsChecked), typeof(bool), typeof(ExcludeItemChoice),
            new PropertyMetadata(false, (d, e) => ((ExcludeItemChoice)d).Toggled?.Invoke((ExcludeItemChoice)d)));

        public ExcludeItemChoice(long id, string className, string displayName)
        {
            Id = id;
            ClassName = className;
            DisplayName = displayName;
        }

        public long Id { get; }
        public string ClassName { get; }
        public string DisplayName { get; }

        // what the list shows: the name and the ID number the setting takes
        public string Label => $"{DisplayName}  ({Id})";

        public bool IsChecked
        {
            get { return (bool)GetValue(IsCheckedProperty); }
            set { SetValue(IsCheckedProperty, value); }
        }

        /// <summary>Raised when the tick changes.</summary>
        public Action<ExcludeItemChoice> Toggled { get; set; }
    }
}

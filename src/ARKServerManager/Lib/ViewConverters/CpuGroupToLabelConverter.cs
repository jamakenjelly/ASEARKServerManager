using System;
using System.Windows.Data;

namespace ServerManagerTool.Lib.ViewModel
{
    // Turns a 0-based CPU package index (from ProcessorAffinityItem.CpuGroup) into
    // a human "CPU 1", "CPU 2", etc. label for the Processor Affinity window's
    // group headers.
    public class CpuGroupToLabelConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value == null)
                return string.Empty;

            if (int.TryParse(value.ToString(), out var groupIndex))
                return $"CPU {groupIndex + 1}";

            return value.ToString();
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}

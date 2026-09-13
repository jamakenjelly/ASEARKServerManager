using System;
using System.Linq;
using System.Windows.Markup;
using System.Windows.Media.Imaging;

namespace ServerManagerTool.Plugin.Discord
{
    // A local copy of ServerManagerTool.Plugin.Common.IconExtension.
    //
    // WPF's XAML compiler (XamlMarkupCompilePass1) can be unreliable at
    // resolving a custom MarkupExtension that lives in a different project
    // than the XAML file using it - even with a correct ProjectReference and a
    // full rebuild, it can keep reporting "tag does not exist" for {pc:Icon}
    // here because Plugin.Common.dll's type info isn't guaranteed to be
    // available to Pass1 at the point it processes this project's XAML.
    // Defining it locally, in the same assembly as the XAML that uses it,
    // sidesteps that entirely since there's no cross-project resolution
    // involved.
    //
    // Usage sample:
    // Image Source="{local:Icon Path=/Assembly;component/icons/custom.ico,Size=16}"
    public class IconExtension : MarkupExtension
    {
        private string _path;

        public string Path
        {
            get
            {
                return _path;
            }
            set
            {
                // Have to make full pack URI from short form, so System.Uri recognizes it.
                _path = $"pack://application:,,,{value}";
            }
        }

        public int Size { get; set; }

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            var decoder = BitmapDecoder.Create(new Uri(Path), BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand);

            var result = decoder.Frames.SingleOrDefault(f => f.Width == Size);
            if (result == default(BitmapFrame))
            {
                result = decoder.Frames.OrderBy(f => f.Width).First();
            }

            return result;
        }
    }
}

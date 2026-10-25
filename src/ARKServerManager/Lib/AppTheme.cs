using System.Windows;
using System.Windows.Media;

namespace ServerManagerTool.Lib
{
    /// <summary>
    /// The looks of the manager (Default and Light). The colours live in application level resources that the windows refer to
    /// with DynamicResource, so changing the theme restyles every open window straight away.
    /// </summary>
    public static class AppTheme
    {
        private static readonly string[] TintColours = { "B3EBFF", "FF8B8B", "FBFBBE", "F4E9DB", "F4E2DB", "EFDAF5", "E7F4DB", "D9F7C4", "98BBCC", "FFD6D6", "FFFFD6D6", "FFF5DFB8", "FFB8E7F5" };

        private static SolidColorBrush Solid(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        private static LinearGradientBrush Vertical(string top, string bottom)
        {
            var brush = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(top), (Color)ColorConverter.ConvertFromString(bottom), new Point(0.5, 0), new Point(0.5, 1));
            brush.Freeze();
            return brush;
        }

        public const string DefaultName = "Default";
        public const string LightName = "Light";

        public static string Current
        {
            get
            {
                var name = Config.Default.ThemeName;
                return name == LightName ? name : DefaultName;
            }
        }

        public static void Apply(string theme, bool save = true)
        {
            var app = Application.Current;
            if (app == null)
                return;

            theme = theme == LightName ? theme : DefaultName;
            Config.Default.ThemeName = theme;
            if (save)
            {
                try { Config.Default.Save(); } catch { }
            }

            var blue = theme == LightName;

            // window colour, panel (category headers), panel label, gradient top / bottom, border, locked field, input, text, outline, buttons
            var window = blue ? "#FFE8EDF3" : "#FFF0F0F0";
            var panel = blue ? "#FFE8EDF3" : "#FFEAE7E0";
            var label = blue ? "#FFE8EDF3" : "#FFE6DFD8";
            var gradTop = blue ? "#FFF4F6F9" : "#FFEAE8E6";
            var gradBottom = blue ? "#FFE8EDF3" : "#FFECE1D4";
            var border = blue ? "#FFC5CFDA" : "#FFD8CCBC";
            var locked = blue ? "#FFDCE5EF" : "#FFF5DEB3";
            var input = "#FFFFFFFF";
            var text = "#FF000000";
            var outline = "#FF000000";
            var buttonTop = blue ? "#FFF6F9FC" : "#FFF2F2F2";
            var buttonBottom = blue ? "#FFDCE6F1" : "#FFDDDDDD";
            var buttonBorder = blue ? "#FF9FB3C8" : "#FF707070";
            var header = "#FFF0F0F0";

            var r = app.Resources;

            r[SystemColors.ControlBrushKey] = Solid(window);
            r[SystemColors.WindowBrushKey] = Solid(input);
            r[SystemColors.ControlTextBrushKey] = Solid(text);
            r[SystemColors.WindowTextBrushKey] = Solid(text);
            r[SystemColors.GrayTextBrushKey] = Solid("#FF6D6D6D");

            r["BeigeLabel"] = Solid(label);
            r["BeigeBorder"] = Solid(border);
            r["HeaderBrush"] = Solid(header);
            r["GradientBackground"] = Vertical(gradTop, gradBottom);
            r["BeigeGradient"] = Vertical(gradTop, gradBottom);
            r["ThemePanel"] = Solid(panel);
            r["ThemeLocked"] = Solid(locked);
            r["ThemeText"] = Solid(text);
            r["ThemeInput"] = Solid(input);
            r["ThemeBeige"] = Solid("#FFF5F5DC");

            // pastel row colours of the lists: shown as they are
            foreach (var hex in TintColours)
            {
                var color = (Color)ColorConverter.ConvertFromString("#" + hex);
                r["Tint_" + hex] = Solid(color.ToString());
            }
            r["ThemeDisabledText"] = Solid("#FF6D6D6D");
            r["UnSyncedSetting"] = Solid("#FF4169E1");
            r["FoundSetting"] = Solid("#FFF0F8FF");
            r["ThemeOutline"] = Solid(outline);
            r["ThemeButtonBrush"] = Vertical(buttonTop, buttonBottom);
            r["ThemeButtonBorder"] = Solid(buttonBorder);
            r["ThemeIconBack"] = Brushes.Transparent;
            // the Run / Disable / Enable buttons in the header have no outline
            r["ThemeTaskBorder"] = Brushes.Transparent;
        }
    }
}

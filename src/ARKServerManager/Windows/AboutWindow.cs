using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ServerManagerTool.Windows
{
    /// <summary>
    /// The window of the "?" button in the header: who made this manager, the links to the Discord and the GitHub page, and the project it is based
    /// on. Built in code, no XAML.
    /// </summary>
    public class AboutWindow : Window
    {
        public const string DiscordUrl = "https://discord.gg/TkwW6F27dM";
        public const string GitHubUrl = "https://github.com/jamakenjelly/ServerManagers-source-ase";
        public const string OriginalUrl = "https://github.com/Bletch1971/ServerManagers";

        public AboutWindow()
        {
            Title = "About";
            Width = 460;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };

            panel.Children.Add(new TextBlock { Text = "ASE ARK Server Manager", FontSize = 22, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center });
            panel.Children.Add(new TextBlock { Text = "Version " + App.Instance.Version, Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 12) });
            panel.Children.Add(new TextBlock { Text = "Made by Zen0rg for O.W.S.C Servers", FontSize = 15, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });

            var links = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            links.Children.Add(LinkButton("Join the Discord", DiscordUrl, new SolidColorBrush(Color.FromRgb(0x58, 0x65, 0xF2))));
            links.Children.Add(LinkButton("GitHub", GitHubUrl, new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33))));
            panel.Children.Add(links);

            panel.Children.Add(new Separator { Margin = new Thickness(0, 18, 0, 12) });

            panel.Children.Add(new TextBlock { Text = "Based on the original ARK Server Manager", TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
            var original = new Button { Content = OriginalUrl, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(10, 3, 10, 3), HorizontalAlignment = HorizontalAlignment.Center, ToolTip = "Open the original project on GitHub" };
            original.Click += (s, e) => Open(OriginalUrl);
            panel.Children.Add(original);

            var close = new Button { Content = "Close", Margin = new Thickness(0, 18, 0, 0), Padding = new Thickness(24, 4, 24, 4), HorizontalAlignment = HorizontalAlignment.Center, IsDefault = true, IsCancel = true };
            close.Click += (s, e) => Close();
            panel.Children.Add(close);

            Content = panel;
        }

        private static Button LinkButton(string text, string url, Brush background)
        {
            var button = new Button
            {
                Content = text,
                Margin = new Thickness(6, 0, 6, 0),
                Padding = new Thickness(18, 6, 18, 6),
                MinWidth = 140,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                Background = background,
                ToolTip = url,
            };
            button.Click += (s, e) => Open(url);
            return button;
        }

        private static void Open(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show("The link could not be opened: " + ex.Message, "About", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}

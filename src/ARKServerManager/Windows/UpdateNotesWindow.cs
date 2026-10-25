using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace ServerManagerTool.Windows
{
    /// <summary>
    /// Shows the update notes (UpdateNotes.txt next to the program), newest version first. Built in code, no XAML.
    /// </summary>
    public class UpdateNotesWindow : Window
    {
        public UpdateNotesWindow()
        {
            Title = "Update notes";
            Width = 560;
            Height = 460;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            string text;
            try
            {
                var file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UpdateNotes.txt");
                text = File.Exists(file) ? File.ReadAllText(file) : "No update notes were found.";
            }
            catch (Exception ex)
            {
                text = "The update notes could not be read: " + ex.Message;
            }

            var box = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 13 };
            var close = new Button { Content = "Close", Padding = new Thickness(18, 4, 18, 4), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0), IsDefault = true, IsCancel = true };
            close.Click += (s, e) => Close();

            var dock = new DockPanel { Margin = new Thickness(12) };
            DockPanel.SetDock(close, Dock.Bottom);
            dock.Children.Add(close);
            dock.Children.Add(box);
            Content = dock;
        }
    }
}

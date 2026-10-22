using ServerManagerTool.Common.Utils;
using ServerManagerTool.Utils;
using System;
using System.Windows;
using System.Windows.Controls;
using WPFSharp.Globalizer;

namespace ServerManagerTool
{
    /// <summary>
    /// Add, change the password of, or remove web dashboard logins.
    /// </summary>
    public partial class WebDashboardUsersWindow : Window
    {
        private readonly GlobalizedApplication _globalizer = GlobalizedApplication.Instance;

        public WebDashboardUsersWindow()
        {
            InitializeComponent();
            WindowUtils.RemoveDefaultResourceDictionary(this, Config.Default.DefaultGlobalizationFile);
        }

        private void Window_Loaded(object sender, RoutedEventArgs e) => RefreshUsers();

        private void RefreshUsers()
        {
            UserListBox.Items.Clear();
            foreach (var name in WebDashboardUsers.GetUsernames())
                UserListBox.Items.Add(name);
        }

        private void UserListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (UserListBox.SelectedItem is string name)
            {
                UsernameTextBox.Text = name;
                DiscordUserIdTextBox.Text = WebDashboardUsers.GetDiscordUserId(name) ?? string.Empty;
                PasswordBox.Password = string.Empty;
            }
        }

        private void SaveUser_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                WebDashboardUsers.SetUser(UsernameTextBox.Text, PasswordBox.Password, DiscordUserIdTextBox.Text);
                PasswordBox.Password = string.Empty;
                RefreshUsers();
                MessageBox.Show(_globalizer.GetResourceString("WebDashboard_UserSavedLabel"), _globalizer.GetResourceString("WebDashboard_UsersTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, _globalizer.GetResourceString("WebDashboard_UsersTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RemoveUser_Click(object sender, RoutedEventArgs e)
        {
            if (!(UserListBox.SelectedItem is string name))
                return;

            if (MessageBox.Show(string.Format(_globalizer.GetResourceString("WebDashboard_RemoveUserConfirmFormat"), name), _globalizer.GetResourceString("WebDashboard_UsersTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            WebDashboardUsers.Remove(name);
            RefreshUsers();
        }
    }
}

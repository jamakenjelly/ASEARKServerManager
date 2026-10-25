using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ServerManagerTool.Installer
{
    internal static class Program
    {
        public const string AppName = "ASEARKServerManager";
        public const string ExeName = "ARK Server Manager.exe";
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ASEARKServerManager";

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args.Any(a => a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase)))
            {
                Application.Run(new UninstallForm());
                return 0;
            }

            Application.Run(new SetupForm());
            return 0;
        }

        public static Icon LoadIcon()
        {
            try
            {
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("favicon.ico"))
                    return stream == null ? null : new Icon(stream);
            }
            catch
            {
                return null;
            }
        }

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        // tells Explorer that icons changed, so a shortcut does not keep showing an icon it remembered from an older copy
        public static void RefreshIcons()
        {
            try { SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); } catch { }
        }

        // the program's icon as a file in the install folder: the shortcuts point at it, under a name Explorer has never cached an icon for
        public static string WriteIconFile(string directory)
        {
            var file = Path.Combine(directory, "ASEARKServerManager.ico");
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("favicon.ico"))
            {
                if (stream == null)
                    return null;
                using (var output = File.Create(file))
                    stream.CopyTo(output);
            }
            return file;
        }

        public static string StartMenuFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), AppName);
        public static string StartMenuLink => Path.Combine(StartMenuFolder, AppName + ".lnk");
        public static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), AppName + ".lnk");

        // a shortcut that always starts the program as administrator (the manager needs it for tasks, firewall rules and process control)
        public static void CreateShortcut(string linkPath, string target, string workingDirectory, string description, string iconFile = null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath));
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(shellType);
            dynamic link = shell.CreateShortcut(linkPath);
            link.TargetPath = target;
            link.WorkingDirectory = workingDirectory;
            link.IconLocation = (string.IsNullOrEmpty(iconFile) ? target : iconFile) + ",0";
            link.Description = description;
            link.Save();

            // "Run as administrator" is bit 0x20 of the byte at offset 0x15 in a .lnk file
            var bytes = File.ReadAllBytes(linkPath);
            if (bytes.Length > 0x15)
            {
                bytes[0x15] |= 0x20;
                File.WriteAllBytes(linkPath, bytes);
            }
        }

        public static bool CloseRunningManager(IWin32Window owner, string installDirectory)
        {
            var name = Path.GetFileNameWithoutExtension(ExeName);
            var running = Process.GetProcessesByName(name).Where(p =>
            {
                try { return installDirectory == null || (p.MainModule?.FileName ?? string.Empty).StartsWith(installDirectory, StringComparison.OrdinalIgnoreCase); }
                catch { return true; }
            }).ToList();
            if (running.Count == 0)
                return true;

            if (MessageBox.Show(owner, AppName + " is running. It has to be closed to continue.\r\n\r\nClose it now?", AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return false;

            foreach (var process in running)
            {
                try
                {
                    process.CloseMainWindow();
                    if (!process.WaitForExit(8000))
                        process.Kill();
                }
                catch
                {
                }
            }
            return true;
        }
    }

    internal class SetupForm : Form
    {
        private readonly TextBox _folder = new TextBox { Width = 360 };
        private readonly CheckBox _desktop = new CheckBox { Text = "Create a desktop shortcut", Checked = true, AutoSize = true };
        private readonly CheckBox _startMenu = new CheckBox { Text = "Create a Start menu shortcut", Checked = true, AutoSize = true };
        private readonly CheckBox _launch = new CheckBox { Text = "Start " + Program.AppName + " when the setup is done", Checked = true, AutoSize = true };
        private readonly ProgressBar _bar = new ProgressBar { Width = 520, Height = 18, Minimum = 0, Maximum = 100 };
        private readonly Label _status = new Label { AutoSize = false, Width = 520, Height = 36 };
        private readonly Button _install = new Button { Text = "Install", Width = 100, Height = 30 };
        private readonly Button _cancel = new Button { Text = "Close", Width = 100, Height = 30 };

        public SetupForm()
        {
            Text = Program.AppName + " Setup";
            Icon = Program.LoadIcon();
            ClientSize = new Size(560, 400);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);

            var header = new Panel { Dock = DockStyle.Top, Height = 78, BackColor = Color.FromArgb(14, 52, 86) };
            var logo = new PictureBox { Image = Program.LoadIcon()?.ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Left = 16, Top = 14, Width = 50, Height = 50 };
            var title = new Label { Text = Program.AppName, ForeColor = Color.White, Font = new Font("Segoe UI", 16f, FontStyle.Bold), AutoSize = true, Left = 78, Top = 12, BackColor = Color.Transparent };
            var sub = new Label { Text = "Server manager for ARK: Survival Evolved", ForeColor = Color.FromArgb(200, 225, 245), AutoSize = true, Left = 80, Top = 46, BackColor = Color.Transparent };
            header.Controls.AddRange(new Control[] { logo, title, sub });

            var folderLabel = new Label { Text = "Install the program in this folder:", AutoSize = true, Left = 20, Top = 98 };
            _folder.Left = 20;
            _folder.Top = 122;
            _folder.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Program.AppName);
            var browse = new Button { Text = "Browse...", Left = 392, Top = 120, Width = 100, Height = 26 };
            browse.Click += (s, e) =>
            {
                using (var dialog = new FolderBrowserDialog { Description = "Choose the install folder", SelectedPath = _folder.Text })
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                        _folder.Text = Path.Combine(dialog.SelectedPath, Path.GetFileName(dialog.SelectedPath).Equals(Program.AppName, StringComparison.OrdinalIgnoreCase) ? string.Empty : Program.AppName).TrimEnd('\\');
            };

            _desktop.Left = 20; _desktop.Top = 168;
            _startMenu.Left = 20; _startMenu.Top = 194;
            _launch.Left = 20; _launch.Top = 220;

            var note = new Label
            {
                Left = 20, Top = 252, Width = 520, Height = 36, ForeColor = Color.DimGray,
                Text = "Your settings and servers are kept in your own data folder, not here. Installing again over an older copy updates it and keeps them."
            };

            _bar.Left = 20; _bar.Top = 300;
            _status.Left = 20; _status.Top = 322;
            _install.Left = 330; _install.Top = 356;
            _cancel.Left = 440; _cancel.Top = 356;
            _install.Click += async (s, e) => await InstallAsync();
            _cancel.Click += (s, e) => Close();

            Controls.AddRange(new Control[] { header, folderLabel, _folder, browse, _desktop, _startMenu, _launch, note, _bar, _status, _install, _cancel });
            AcceptButton = _install;

            if (!HasPayload())
                _status.Text = "This setup file has no program files inside it (make-installer.ps1 builds it with them).";
        }

        private static bool HasPayload() => Assembly.GetExecutingAssembly().GetManifestResourceNames().Contains("payload.zip");

        private void SetStatus(string text, int percent)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => SetStatus(text, percent))); return; }
            _status.Text = text;
            _bar.Value = Math.Max(0, Math.Min(100, percent));
        }

        private async Task InstallAsync()
        {
            var directory = _folder.Text.Trim().TrimEnd('\\');
            if (directory.Length < 4 || !Path.IsPathRooted(directory))
            {
                MessageBox.Show(this, "Choose a folder to install in.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!HasPayload())
                return;
            if (!Program.CloseRunningManager(this, directory))
                return;

            var desktop = _desktop.Checked;
            var startMenu = _startMenu.Checked;
            var launch = _launch.Checked;
            _install.Enabled = false;
            _folder.Enabled = false;

            try
            {
                var version = await Task.Run(() => Install(directory, desktop, startMenu));
                SetStatus("Installed.", 100);
                MessageBox.Show(this, Program.AppName + " " + version + " is installed in\r\n" + directory, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                if (launch)
                {
                    try { Process.Start(new ProcessStartInfo(Path.Combine(directory, Program.ExeName)) { WorkingDirectory = directory, UseShellExecute = true, Verb = "runas" }); } catch { }
                }
                Close();
            }
            catch (Exception ex)
            {
                SetStatus("The install failed: " + ex.Message, 0);
                MessageBox.Show(this, "The install failed.\r\n\r\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                _install.Enabled = true;
                _folder.Enabled = true;
            }
        }

        private string Install(string directory, bool desktop, bool startMenu)
        {
            Directory.CreateDirectory(directory);

            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                var entries = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
                var done = 0;
                foreach (var entry in entries)
                {
                    var relative = entry.FullName.Replace('/', '\\');
                    var target = Path.GetFullPath(Path.Combine(directory, relative));
                    if (!target.StartsWith(directory + "\\", StringComparison.OrdinalIgnoreCase))
                        continue; // never write outside the install folder

                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    if (File.Exists(target))
                        File.SetAttributes(target, FileAttributes.Normal);
                    entry.ExtractToFile(target, true);

                    done++;
                    if (done % 5 == 0 || done == entries.Count)
                        SetStatus("Copying files... " + relative, 5 + done * 85 / entries.Count);
                }
            }

            SetStatus("Setting up shortcuts...", 92);

            // the setup program itself becomes the uninstaller
            var uninstaller = Path.Combine(directory, "Uninstall.exe");
            File.Copy(Assembly.GetExecutingAssembly().Location, uninstaller, true);

            var exe = Path.Combine(directory, Program.ExeName);
            var version = FileVersionInfo.GetVersionInfo(exe).FileVersion ?? "1.0";

            var icon = Program.WriteIconFile(directory);
            if (startMenu)
                Program.CreateShortcut(Program.StartMenuLink, exe, directory, Program.AppName, icon);
            if (desktop)
                Program.CreateShortcut(Program.DesktopLink, exe, directory, Program.AppName, icon);

            using (var key = Registry.LocalMachine.CreateSubKey(Program.UninstallKey))
            {
                key.SetValue("DisplayName", Program.AppName);
                key.SetValue("DisplayVersion", version);
                key.SetValue("Publisher", Program.AppName);
                key.SetValue("InstallLocation", directory);
                key.SetValue("DisplayIcon", icon ?? exe);
                key.SetValue("UninstallString", "\"" + uninstaller + "\" /uninstall");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }

            Program.RefreshIcons();
            return version;
        }
    }

    internal class UninstallForm : Form
    {
        public UninstallForm()
        {
            ShowInTaskbar = false;
            WindowState = FormWindowState.Minimized;
            Opacity = 0;
            Load += (s, e) => { BeginInvoke(new Action(Run)); };
        }

        private void Run()
        {
            string directory = null;
            using (var key = Registry.LocalMachine.OpenSubKey(Program.UninstallKey))
                directory = key?.GetValue("InstallLocation") as string;
            if (string.IsNullOrWhiteSpace(directory))
                directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            if (MessageBox.Show(Program.AppName + " will be removed from\r\n" + directory + "\r\n\r\nYour settings, server profiles and servers are not deleted.\r\n\r\nContinue?",
                    Program.AppName + " Uninstall", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                Close();
                return;
            }

            if (!Program.CloseRunningManager(null, directory))
            {
                Close();
                return;
            }

            try { if (File.Exists(Program.DesktopLink)) File.Delete(Program.DesktopLink); } catch { }
            try { if (Directory.Exists(Program.StartMenuFolder)) Directory.Delete(Program.StartMenuFolder, true); } catch { }
            try { Registry.LocalMachine.DeleteSubKeyTree(Program.UninstallKey, false); } catch { }

            // this program runs from the install folder, so the folder is removed by a command that waits for it to close
            var cmd = "/c ping 127.0.0.1 -n 3 > nul & rd /s /q \"" + directory + "\"";
            Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });

            MessageBox.Show(Program.AppName + " was removed.", Program.AppName + " Uninstall", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }
    }
}

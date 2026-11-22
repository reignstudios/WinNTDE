using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WinNTDE
{
    public partial class App : Application
    {
        private Mutex instance;
        private bool ownsInstance;
        private SettingsWindow settings;
        private TaskManagerWindow taskManager;
        private TaskbarWindow taskbar;
        private ResourceDictionary darkControls;

        internal ShellPreferences Preferences { get; private set; }
        internal bool IsShell { get; private set; }
        internal WindowsUpdates Updates { get; } = new WindowsUpdates();
        internal StoreAppSupport StoreApps { get; } = new StoreAppSupport();

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            if (e.Args.Length != 0 && e.Args[0] == WindowsUpdates.InstallCommand)
            {
                Shutdown(WindowsUpdates.InstallFromArguments(e.Args));
                return;
            }
            instance = new Mutex(true, @"Local\WinNTDE.Desktop", out ownsInstance);
            if (!ownsInstance)
            {
                Shutdown();
                return;
            }

            Preferences = new ShellPreferences();
            ApplyTheme();
            ShowDesktop();
            StoreApps.Start(Preferences, IsShell, Dispatcher);
            if (e.Args.Length == 0) return;
            if (string.Equals(e.Args[0], "--settings", StringComparison.OrdinalIgnoreCase)) OpenSettings();
            else if (e.Args.Length == 2 && string.Equals(e.Args[0], "--explore", StringComparison.OrdinalIgnoreCase))
                OpenExplorer(e.Args[1]);
        }

        private void ShowDesktop()
        {
            IsShell = NativeDesktop.FindWindow("Shell_TrayWnd", null) == IntPtr.Zero &&
                NativeDesktop.GetShellWindow() == IntPtr.Zero;
            MainWindow = new MainWindow(this);
            MainWindow.Show();
            taskbar = new TaskbarWindow(this);
            taskbar.Show();
        }

        internal void ApplyTheme()
        {
            bool light = Preferences.LightTheme;
            SetBrush("Surface", light ? "#F5F6F5" : "#1C2126");
            SetBrush("Panel", light ? "#E8ECEA" : "#242B30");
            SetBrush("Raised", light ? "#FFFFFF" : "#2D353B");
            SetBrush("Line", light ? "#C9D2CF" : "#465157");
            SetBrush("Text", light ? "#1C2928" : "#F1F5F4");
            SetBrush("Muted", light ? "#506561" : "#B1C0BD");

            if (light)
            {
                if (darkControls != null) Resources.MergedDictionaries.Remove(darkControls);
                Resources["ShellMenu"] = Resources["ShellMenuLight"];
                Resources["SettingsNavItem"] = Resources["SettingsNavItemLight"];
            }
            else
            {
                if (darkControls == null)
                    darkControls = new ResourceDictionary { Source = new Uri("/WinNTDE;component/DarkControls.xaml", UriKind.Relative) };
                if (!Resources.MergedDictionaries.Contains(darkControls)) Resources.MergedDictionaries.Add(darkControls);
                Resources["ShellMenu"] = darkControls["ShellMenuDark"];
                Resources["SettingsNavItem"] = darkControls["SettingsNavItemDark"];
            }

            foreach (Window window in Windows) NativeDesktop.UpdateTitleBar(window, light);
        }

        private void SetBrush(string name, string color)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            Resources[name] = brush;
        }

        internal void SavePreferences(Window owner)
        {
            DesktopFiles.Try(owner, Preferences.Save);
        }

        internal void OpenExplorer(string path)
        {
            new ExplorerWindow(path).Show();
        }

        internal void OpenSettings(string page = null)
        {
            if (settings == null)
            {
                settings = new SettingsWindow(this);
                settings.Closed += (s, e) => settings = null;
                settings.Show();
            }
            if (page != null) settings.SelectPage(page);
            if (settings.WindowState == WindowState.Minimized) settings.WindowState = WindowState.Normal;
            settings.Activate();
        }

        internal void OpenTaskManager()
        {
            if (taskManager == null)
            {
                taskManager = new TaskManagerWindow();
                taskManager.Closed += (s, e) => taskManager = null;
                taskManager.Show();
            }
            if (taskManager.WindowState == WindowState.Minimized) taskManager.WindowState = WindowState.Normal;
            taskManager.Activate();
        }

        internal static ContextMenu Menu()
        {
            var menu = new ContextMenu();
            menu.SetResourceReference(FrameworkElement.StyleProperty, "ShellMenu");
            return menu;
        }

        internal static void AddMenuItem(ContextMenu menu, string label, Action action)
        {
            var item = new MenuItem { Header = label };
            item.Click += (s, e) => action();
            menu.Items.Add(item);
        }

        internal static void ConfirmPowerAction(Window owner, string label, Action action)
        {
            if (MessageBox.Show(owner, label + " now?", owner.Title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                DesktopFiles.Try(owner, action);
        }

        internal void ExitDesktop(Window owner)
        {
            if (MessageBox.Show(owner, "Exit WinNTDE and return to Windows Explorer?", "Exit WinNTDE",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;

            ProcessStartInfo explorer = null;
            if (!DesktopFiles.Try(owner, () =>
            {
                if (SystemSettings.IsExplorerRunning()) return;
                var startInfo = SystemSettings.PrepareExplorerShell();
                if (!SystemSettings.IsExplorerRunning()) explorer = startInfo;
            })) return;

            if (explorer != null)
            {
                // Release the tray registration and desktop window before Explorer claims the shell.
                taskbar.Close();
                MainWindow.Close();
                try
                {
                    using (var process = Process.Start(explorer))
                        if (process == null) throw new InvalidOperationException("Windows Explorer did not start.");
                }
                catch (Exception error) when (error is Win32Exception || error is InvalidOperationException)
                {
                    ShowDesktop();
                    MessageBox.Show(taskbar, "Could not start Windows Explorer. " + error.Message, "Exit WinNTDE",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (ownsInstance) instance.ReleaseMutex();
            if (instance != null) instance.Close();
            base.OnExit(e);
        }
    }
}

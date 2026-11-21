using System;
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
        private ResourceDictionary darkControls;

        internal ShellPreferences Preferences { get; private set; }
        internal bool IsShell { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            instance = new Mutex(true, @"Local\WinNTDE.Desktop", out ownsInstance);
            if (!ownsInstance)
            {
                Shutdown();
                return;
            }

            IsShell = NativeDesktop.FindWindow("Shell_TrayWnd", null) == IntPtr.Zero &&
                NativeDesktop.GetShellWindow() == IntPtr.Zero;
            Preferences = new ShellPreferences();
            ApplyTheme();
            var desktop = new MainWindow(this);
            MainWindow = desktop;
            desktop.Show();
            new TaskbarWindow(this).Show();
            if (e.Args.Length == 0) return;
            if (string.Equals(e.Args[0], "--settings", StringComparison.OrdinalIgnoreCase)) OpenSettings();
            else if (e.Args.Length == 2 && string.Equals(e.Args[0], "--explore", StringComparison.OrdinalIgnoreCase))
                OpenExplorer(e.Args[1]);
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

        internal void ExitDesktop(Window owner)
        {
            if (!IsShell || MessageBox.Show(owner, "Exit WinNTDE and close its windows?", "WinNTDE",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
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

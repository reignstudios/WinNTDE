using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace WinNTDE
{
    internal partial class StartMenuWindow : Window
    {
        private const double MarginFromEdge = 8;
        private readonly App app;
        private readonly Action run;
        private StartApp[] apps = new StartApp[0];
        private string[] defaultPins = new string[0];
        private DateTime refreshAfter;
        private bool allApps, loading, loadFailed;
        private ContextMenu openMenu;

        internal StartMenuWindow(App app, Action run)
        {
            this.app = app;
            this.run = run;
            InitializeComponent();
            SearchBox.TextChanged += (s, e) => UpdateView();
            PreviewKeyDown += HandleKeys;
            Deactivated += (s, e) => DismissWhenInactive();
        }

        internal void Open(NativeDesktop.Rect bar, int workTop)
        {
            Position(bar, workTop);
            allApps = false;
            SearchBox.Clear();
            RefreshApps();
            UpdateView();
            Show();
            Activate();
            SearchBox.Focus();
        }

        internal void Position(NativeDesktop.Rect bar, int workTop)
        {
            double scale = NativeDesktop.ChromeScale;
            double left = bar.Left / scale, right = bar.Right / scale, bottom = bar.Top / scale;
            Width = Math.Min(520, Math.Max(1, right - left - 2 * MarginFromEdge));
            Height = Math.Min(454, Math.Max(1, bottom - workTop / scale - 2 * MarginFromEdge));
            Left = app.Preferences.AlignLeft ? left + MarginFromEdge : (left + right - Width) / 2;
            Top = bottom - Height - MarginFromEdge;
        }

        internal void Dismiss()
        {
            DismissMenu();
            if (IsVisible) Hide();
        }

        private void DismissWhenInactive()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (IsVisible && !IsActive && (openMenu == null || !openMenu.IsOpen)) Dismiss();
            }));
        }

        private void RefreshApps()
        {
            if (loading || DateTime.UtcNow < refreshAfter) return;
            loading = true;
            // Shell AppsFolder uses COM. Scan away from the UI thread in its own STA.
            var worker = new Thread(() =>
            {
                StartApp[] results = null;
                try { results = StartAppCatalog.Read(); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException ||
                    error is System.Security.SecurityException || error is ArgumentException || error is System.Runtime.InteropServices.COMException) { }
                if (Dispatcher.HasShutdownStarted) return;
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    loading = false;
                    loadFailed = results == null;
                    refreshAfter = DateTime.UtcNow.AddSeconds(15);
                    if (!loadFailed)
                    {
                        apps = results;
                        defaultPins = StartAppCatalog.DefaultPins(apps);
                    }
                    if (IsVisible) UpdateView();
                }));
            }) { IsBackground = true };
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
        }

        private string[] CurrentPins { get { return app.Preferences.PinnedApps ?? defaultPins; } }

        private void UpdateView()
        {
            string query = SearchBox.Text.Trim();
            bool searching = query.Length != 0;
            bool showList = searching || allApps;
            AppList.Visibility = showList ? Visibility.Visible : Visibility.Collapsed;
            PinnedView.Visibility = showList ? Visibility.Collapsed : Visibility.Visible;
            ViewButton.Content = showList ? "Pinned" : "All apps";
            ViewTitle.Text = searching ? "Search results" : allApps ? "All apps" : "Pinned apps";

            string emptyText;
            if (showList)
            {
                var results = searching ? StartAppCatalog.Search(apps, query) : apps;
                AppList.ItemsSource = results;
                if (results.Length != 0) AppList.ScrollIntoView(results[0]);
                emptyText = results.Length == 0 ? (searching ? "No apps match your search." : "No apps found.") : null;
            }
            else
            {
                var pinned = CurrentPins.Join(apps, key => key, item => item.Key, (key, item) => item,
                    StringComparer.OrdinalIgnoreCase).ToArray();
                Pins.ItemsSource = pinned;
                emptyText = pinned.Length == 0 ? "Right-click an app in All apps to pin it here." : null;
            }

            if (emptyText != null && loading && apps.Length == 0) emptyText = "Loading apps…";
            if (emptyText != null && loadFailed && apps.Length == 0) emptyText = "The app list could not be loaded.";
            Status.Text = emptyText;
            Status.Visibility = emptyText == null ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ToggleApps(object sender, RoutedEventArgs e)
        {
            allApps = AppList.Visibility != Visibility.Visible;
            SearchBox.Clear();
            UpdateView();
            SearchBox.Focus();
        }

        private void HandleKeys(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (openMenu != null && openMenu.IsOpen) DismissMenu();
                else Dismiss();
                e.Handled = true;
            }
            else if (e.Key == Key.Down && SearchBox.IsKeyboardFocusWithin && AppList.IsVisible && AppList.Items.Count > 0)
            {
                AppList.SelectedIndex = 0;
                AppList.ScrollIntoView(AppList.SelectedItem);
                AppList.UpdateLayout();
                var selected = AppList.ItemContainerGenerator.ContainerFromItem(AppList.SelectedItem) as ListBoxItem;
                if (selected != null) selected.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && AppList.IsVisible && AppList.Items.Count > 0 &&
                (SearchBox.IsKeyboardFocusWithin || (AppList.IsKeyboardFocusWithin && !(Keyboard.FocusedElement is Button))))
            {
                Launch((StartApp)(AppList.SelectedItem ?? AppList.Items[0]));
                e.Handled = true;
            }
        }

        private void OpenApp(object sender, RoutedEventArgs e)
        {
            var entry = ((Button)sender).DataContext as StartApp;
            if (entry != null) Launch(entry);
        }

        private void Launch(StartApp entry)
        {
            Dismiss();
            DesktopFiles.Try(Owner ?? this, entry.Launch);
        }

        private void OpenPinMenu(object sender, RoutedEventArgs e)
        {
            var menu = (ContextMenu)sender;
            var target = menu.PlacementTarget as FrameworkElement;
            var entry = target == null ? null : target.DataContext as StartApp;
            if (entry == null) return;
            openMenu = menu;
            bool pinned = CurrentPins.Contains(entry.Key, StringComparer.OrdinalIgnoreCase);
            menu.Items.Clear();
            App.AddMenuItem(menu, pinned ? "Unpin from Start" : "Pin to Start", () => TogglePin(entry));
        }

        private void CloseMenu(object sender, RoutedEventArgs e)
        {
            if (!ReferenceEquals(openMenu, sender)) return;
            openMenu = null;
            DismissWhenInactive();
        }

        private void TogglePin(StartApp entry)
        {
            DismissMenu();
            var previous = app.Preferences.PinnedApps;
            var pins = CurrentPins.ToList();
            int index = pins.FindIndex(key => string.Equals(key, entry.Key, StringComparison.OrdinalIgnoreCase));
            if (index < 0) pins.Add(entry.Key);
            else pins.RemoveAt(index);

            app.Preferences.PinnedApps = pins.ToArray();
            if (!DesktopFiles.Try(this, app.Preferences.Save)) app.Preferences.PinnedApps = previous;
            if (PinnedView.Visibility == Visibility.Visible) UpdateView();
        }

        private void DismissMenu()
        {
            var menu = openMenu;
            openMenu = null;
            if (menu != null) menu.IsOpen = false;
        }

        private void OpenPowerMenu(object sender, RoutedEventArgs e)
        {
            DismissMenu();
            var menu = App.Menu();
            menu.PlacementTarget = (Button)sender;
            menu.Placement = PlacementMode.Top;
            menu.Closed += CloseMenu;
            AddPowerAction(menu, "Shut down", () => SystemSettings.EndSession("s"));
            AddPowerAction(menu, "Restart", () => SystemSettings.EndSession("r"));
            AddPowerAction(menu, "Sleep", SystemSettings.Sleep);
            openMenu = menu;
            menu.IsOpen = true;
        }

        private void AddPowerAction(ContextMenu menu, string label, Action action)
        {
            App.AddMenuItem(menu, label, () =>
            {
                Dismiss();
                App.ConfirmPowerAction(Owner ?? this, label, action);
            });
        }

        private void OpenSettings(object sender, RoutedEventArgs e)
        {
            Dismiss();
            app.OpenSettings();
        }

        private void RunCommand(object sender, RoutedEventArgs e)
        {
            Dismiss();
            run();
        }

        private void OpenTaskManager(object sender, RoutedEventArgs e)
        {
            Dismiss();
            DesktopFiles.Try(Owner ?? this, () => Process.Start("taskmgr.exe"));
        }

        private void ExitDesktop(object sender, RoutedEventArgs e)
        {
            Dismiss();
            app.ExitDesktop(Owner ?? this);
        }
    }
}

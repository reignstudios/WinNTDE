using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace WinNTDE
{
    internal sealed class TaskbarWindow : Window
    {
        private const int BarHeight = 43;
        private readonly App app;
        private readonly Grid bar = new Grid();
        private readonly StackPanel group = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        private readonly StackPanel tasks = new StackPanel { Orientation = Orientation.Horizontal };
        private readonly ScrollViewer taskScroll;
        private readonly StackPanel right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        private readonly StackPanel trayIcons = new StackPanel { Orientation = Orientation.Vertical };
        private readonly Button clock, trayButton;
        private readonly Popup tray;
        private readonly DispatcherTimer taskRefresh = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        private readonly DispatcherTimer edgeRefresh = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        private readonly Dictionary<IntPtr, TaskItem> windows = new Dictionary<IntPtr, TaskItem>();
        private NotificationArea notifications;
        private ContextMenu openMenu;
        private NativeDesktop.Rect previousWork, lastScreen, bounds;
        private IntPtr hwnd, desktop, activeApp;
        private bool fullscreen, collapsed, workAreaChanged, trayRegistrationFailed;
        private DateTime edgeSeen, mouseInside;

        internal TaskbarWindow(App app)
        {
            this.app = app;
            Title = "WinNTDE Taskbar";
            SetResourceReference(StyleProperty, "ShellWindow");
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Height = BarHeight;
            var frame = new Border { Child = bar, BorderThickness = new Thickness(0, 1, 0, 0) };
            frame.SetResourceReference(Border.BackgroundProperty, "Panel");
            frame.SetResourceReference(Border.BorderBrushProperty, "Line");
            Content = frame;

            bar.Children.Add(group);
            var menuButton = IconButton("▦", "WinNTDE menu");
            menuButton.Click += (s, e) => OpenLauncher(menuButton);
            group.Children.Add(menuButton);
            var explorer = IconButton(DesktopFiles.FolderIcon, "File Explorer");
            explorer.Click += (s, e) => app.OpenExplorer(DesktopFiles.Home);
            group.Children.Add(explorer);
            taskScroll = new ScrollViewer { Content = tasks, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
            taskScroll.PreviewMouseWheel += (s, e) =>
            {
                taskScroll.ScrollToHorizontalOffset(taskScroll.HorizontalOffset - e.Delta / 3.0);
                e.Handled = true;
            };
            group.Children.Add(taskScroll);

            right.Margin = new Thickness(0, 0, 8, 0);
            right.VerticalAlignment = VerticalAlignment.Center;
            bar.Children.Add(right);
            trayButton = IconButton("⌃", "Notification area");
            trayButton.Click += (s, e) => { RebuildTray(); tray.IsOpen = !tray.IsOpen; };
            right.Children.Add(trayButton);
            clock = new Button { Margin = new Thickness(4, 0, 0, 0), MinWidth = 65, Padding = new Thickness(8, 3, 8, 3) };
            clock.Click += (s, e) => OpenClock();
            right.Children.Add(clock);

            var trayContents = new StackPanel { Margin = new Thickness(9) };
            trayContents.Children.Add(trayIcons);
            var shortcuts = new UniformGrid { Columns = 3, Margin = new Thickness(0, 6, 0, 0) };
            foreach (var page in new[] { "Wi-Fi", "Audio", "Power" })
            {
                var button = new Button { Content = page, Margin = new Thickness(2) };
                button.Click += (s, e) => { tray.IsOpen = false; app.OpenSettings(page); };
                shortcuts.Children.Add(button);
            }
            trayContents.Children.Add(shortcuts);
            var trayBorder = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Child = trayContents };
            trayBorder.SetResourceReference(Border.BackgroundProperty, "Panel");
            trayBorder.SetResourceReference(Border.BorderBrushProperty, "Line");
            tray = new Popup { PlacementTarget = trayButton, Placement = PlacementMode.Top, StaysOpen = false,
                VerticalOffset = -7, Child = trayBorder };

            var barMenu = App.Menu();
            App.AddMenuItem(barMenu, "Taskbar settings", () => app.OpenSettings("Taskbar"));
            ContextMenu = barMenu;
            app.Preferences.Changed += PreferencesChanged;
            UpdateAlignment();
            taskRefresh.Tick += (s, e) => RefreshTasks();
            edgeRefresh.Tick += (s, e) => RefreshEdge();
            bar.SizeChanged += (s, e) => SizeTasks();
            right.SizeChanged += (s, e) => SizeTasks();
            Closed += (s, e) =>
            {
                taskRefresh.Stop();
                edgeRefresh.Stop();
                app.Preferences.Changed -= PreferencesChanged;
                if (notifications != null) notifications.Dispose();
                if (workAreaChanged)
                {
                    var screen = NativeDesktop.Screen;
                    var restore = lastScreen.Width == screen.Width && lastScreen.Height == screen.Height ? previousWork : screen;
                    NativeDesktop.SetWorkArea(restore);
                }
            };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            hwnd = new WindowInteropHelper(this).Handle;
            desktop = new WindowInteropHelper(app.MainWindow).Handle;
            HwndSource.FromHwnd(hwnd).AddHook(HandleMessage);
            previousWork = NativeDesktop.WorkArea;
            lastScreen = NativeDesktop.Screen;
            if (app.IsShell)
            {
                try
                {
                    notifications = new NotificationArea(() => bounds, () => app.Preferences.AutoHide);
                    notifications.Changed += (s, args) =>
                    {
                        if (tray.IsOpen) Dispatcher.BeginInvoke(new Action(RebuildTray));
                    };
                }
                catch (Win32Exception) { trayRegistrationFailed = true; }
            }
            mouseInside = DateTime.UtcNow;
            ApplyPosition(true);
            RefreshTasks();
            taskRefresh.Start();
            edgeRefresh.Start();
        }

        private IntPtr HandleMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == NativeDesktop.DisplayChanged || message == 0x02E0)
                Dispatcher.BeginInvoke(new Action(() => ApplyPosition(true)));
            if (message == 0x0021)
            {
                handled = true;
                return new IntPtr(3); // MA_NOACTIVATE allows the click while keeping the active task.
            }
            return IntPtr.Zero;
        }

        private void PreferencesChanged(object sender, EventArgs e)
        {
            UpdateAlignment();
            collapsed = false;
            mouseInside = DateTime.UtcNow;
            ApplyPosition(true);
        }

        private void UpdateAlignment()
        {
            group.HorizontalAlignment = app.Preferences.AlignLeft ? HorizontalAlignment.Left : HorizontalAlignment.Center;
            group.Margin = new Thickness(8, 0, 0, 0);
            SizeTasks();
        }

        private void SizeTasks()
        {
            double remaining = bar.ActualWidth - 98 - right.ActualWidth * (app.Preferences.AlignLeft ? 1 : 2);
            taskScroll.MaxWidth = Math.Max(0, remaining);
        }

        private void ApplyPosition(bool adjustWorkArea)
        {
            if (hwnd == IntPtr.Zero) return;
            var screen = NativeDesktop.Screen;
            if (screen.Width != lastScreen.Width || screen.Height != lastScreen.Height)
            {
                lastScreen = screen;
                previousWork = screen;
            }
            var area = app.IsShell ? screen : NativeDesktop.WorkArea;
            int height = (int)Math.Round(BarHeight * NativeDesktop.ChromeScale);
            bounds = new NativeDesktop.Rect { Left = area.Left, Right = area.Right, Top = area.Bottom - height, Bottom = area.Bottom };
            if (notifications != null && adjustWorkArea) notifications.Place(bounds);
            int top = collapsed ? area.Bottom - 2 : bounds.Top;
            NativeDesktop.SetWindowPos(hwnd, fullscreen ? NativeDesktop.NotTopmost : NativeDesktop.Topmost,
                area.Left, top, area.Width, collapsed ? 2 : height, 0x0010);
            if (adjustWorkArea && app.IsShell)
            {
                var desired = app.Preferences.AutoHide ? screen : bounds;
                desired.Top = screen.Top;
                NativeDesktop.Rect current = NativeDesktop.WorkArea;
                if (desired.Bottom != current.Bottom || desired.Right != current.Right || desired.Top != current.Top || desired.Left != current.Left)
                {
                    NativeDesktop.SetWorkArea(desired);
                    workAreaChanged = true;
                }
            }
        }

        private void RefreshEdge()
        {
            IntPtr active = NativeDesktop.GetForegroundWindow();
            if (active != hwnd && active != IntPtr.Zero && NativeDesktop.WindowProcess(active) != NativeDesktop.ProcessId)
                activeApp = active;
            bool nowFullscreen = active != hwnd && NativeDesktop.IsFullscreen(active, desktop);
            if (nowFullscreen != fullscreen)
            {
                fullscreen = nowFullscreen;
                if (fullscreen) tray.IsOpen = false;
                ApplyPosition(false);
            }
            if (!app.Preferences.AutoHide || fullscreen) return;
            NativeDesktop.Point cursor;
            if (!NativeDesktop.GetCursorPos(out cursor)) return;
            DateTime now = DateTime.UtcNow;
            bool overEdge = cursor.X >= bounds.Left && cursor.X < bounds.Right && cursor.Y >= bounds.Bottom - 2 && cursor.Y < bounds.Bottom;
            bool overBar = cursor.X >= bounds.Left && cursor.X < bounds.Right && cursor.Y >= bounds.Top && cursor.Y < bounds.Bottom;
            if (!collapsed && (overBar || tray.IsOpen || (openMenu != null && openMenu.IsOpen) || ContextMenu.IsOpen)) mouseInside = now;
            if (!overEdge) edgeSeen = DateTime.MinValue;
            else if (edgeSeen == DateTime.MinValue) edgeSeen = now;
            if (collapsed && overEdge && now - edgeSeen > TimeSpan.FromMilliseconds(140))
            {
                collapsed = false;
                mouseInside = now;
                ApplyPosition(false);
            }
            else if (!collapsed && now - mouseInside > TimeSpan.FromMilliseconds(500))
            {
                collapsed = true;
                ApplyPosition(false);
            }
        }

        private void RefreshTasks()
        {
            var now = DateTime.Now;
            string time = now.ToShortTimeString();
            if (!Equals(clock.Content, time))
            {
                clock.Content = time;
                clock.ToolTip = now.ToLongDateString();
            }
            if (notifications != null) notifications.RemoveClosedWindows();
            IntPtr current = NativeDesktop.GetForegroundWindow();
            if (current == hwnd) current = activeApp;
            else if (current != IntPtr.Zero) activeApp = current;
            IntPtr owner = NativeDesktop.GetWindow(current, 4);
            var live = new HashSet<IntPtr>();
            NativeDesktop.EnumWindows((window, data) =>
            {
                if (window == hwnd || window == desktop || !NativeDesktop.IsApplicationWindow(window)) return true;
                string title = NativeDesktop.WindowTitle(window);
                if (string.IsNullOrWhiteSpace(title)) return true;
                live.Add(window);
                TaskItem item;
                int process = NativeDesktop.WindowProcess(window);
                if (windows.TryGetValue(window, out item) && item.Process != process) RemoveTask(window);
                if (!windows.TryGetValue(window, out item))
                {
                    var button = IconButton(NativeDesktop.WindowIcon(window), title);
                    button.Click += (s, e) => ToggleTask(window);
                    tasks.Children.Add(button);
                    item = new TaskItem { Process = process, Button = button };
                    windows.Add(window, item);
                }
                if (!Equals(item.Button.ToolTip, title))
                {
                    item.Button.ToolTip = title;
                    System.Windows.Automation.AutomationProperties.SetName(item.Button, title);
                }
                bool focused = window == current || window == owner;
                if (item.Active != focused)
                {
                    item.Active = focused;
                    item.Button.SetResourceReference(BackgroundProperty, focused ? "Selection" : "Raised");
                }
                return true;
            }, IntPtr.Zero);
            foreach (IntPtr stale in windows.Keys.Where(window => !live.Contains(window)).ToArray()) RemoveTask(stale);
        }

        private void RemoveTask(IntPtr window)
        {
            tasks.Children.Remove(windows[window].Button);
            windows.Remove(window);
        }

        private void ToggleTask(IntPtr window)
        {
            IntPtr current = NativeDesktop.GetForegroundWindow();
            if (current == hwnd) current = activeApp;
            if (current == window && !NativeDesktop.IsIconic(window)) NativeDesktop.ShowWindowAsync(window, 6);
            else
            {
                var popup = NativeDesktop.GetLastActivePopup(window);
                if (!NativeDesktop.IsWindowVisible(popup)) popup = window;
                if (NativeDesktop.IsIconic(window)) NativeDesktop.ShowWindowAsync(window, 9);
                NativeDesktop.SetForegroundWindow(popup);
            }
            RefreshTasks();
        }

        private static Button IconButton(object content, string title)
        {
            var source = content as ImageSource;
            var button = new Button { Content = source == null ? content : new Image { Source = source, Width = 23, Height = 23 },
                Width = 42, Height = 34, Padding = new Thickness(2), Margin = new Thickness(2, 0, 2, 0),
                FontSize = 19, ToolTip = title };
            System.Windows.Automation.AutomationProperties.SetName(button, title);
            return button;
        }

        private void RebuildTray()
        {
            trayIcons.Children.Clear();
            var icons = notifications == null ? null : notifications.VisibleIcons.ToArray();
            if (icons == null || icons.Length == 0)
            {
                trayIcons.Children.Add(new TextBlock { Margin = new Thickness(4), Text = trayRegistrationFailed ?
                    "The notification area is unavailable." : !app.IsShell ? "Windows Explorer is hosting notification icons." :
                    "No notification icons." });
                return;
            }
            var grid = new UniformGrid { Columns = 5 };
            foreach (var icon in icons)
            {
                var button = IconButton(icon.Image, string.IsNullOrEmpty(icon.Name) ? "Notification icon" : icon.Name);
                button.Margin = new Thickness(2);
                bool doubleClick = false;
                button.PreviewMouseLeftButtonDown += (s, e) => doubleClick = e.ClickCount == 2;
                button.Click += (s, e) => { icon.Click(false, doubleClick); doubleClick = false; };
                button.PreviewMouseRightButtonUp += (s, e) => { tray.IsOpen = false; icon.Click(true); e.Handled = true; };
                grid.Children.Add(button);
            }
            trayIcons.Children.Add(new ScrollViewer { MaxHeight = 245, Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        }

        private void OpenLauncher(Button button)
        {
            var menu = App.Menu();
            App.AddMenuItem(menu, "File Explorer", () => app.OpenExplorer(DesktopFiles.Home));
            App.AddMenuItem(menu, "Settings", () => app.OpenSettings());
            App.AddMenuItem(menu, "Run…", OpenRun);
            App.AddMenuItem(menu, "Task Manager", () => DesktopFiles.Try(this, () => Process.Start("taskmgr.exe")));
            menu.Items.Add(new Separator());
            App.AddMenuItem(menu, "Exit WinNTDE", () => app.ExitDesktop(this));
            OpenMenu(menu, button);
        }

        private void OpenClock()
        {
            var menu = App.Menu();
            menu.Items.Add(new MenuItem { Header = DateTime.Now.ToLongDateString(), IsEnabled = false });
            App.AddMenuItem(menu, "Date and time settings", () => DesktopFiles.Try(this, () => SystemSettings.OpenControl("timedate.cpl")));
            OpenMenu(menu, clock);
        }

        private void OpenMenu(ContextMenu menu, Button button)
        {
            openMenu = menu;
            menu.Closed += (s, e) => { if (openMenu == menu) openMenu = null; };
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Top;
            menu.IsOpen = true;
        }

        private void OpenRun()
        {
            var window = new Window { Title = "Run", Width = 395, Height = 126, ResizeMode = ResizeMode.NoResize };
            NativeDesktop.ThemeWindow(window);
            var form = new DockPanel { Margin = new Thickness(12) };
            var path = new TextBox { ToolTip = "Program, file or folder" };
            var run = new Button { Content = "Run", IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
            DockPanel.SetDock(run, Dock.Right);
            form.Children.Add(run);
            form.Children.Add(path);
            window.Content = form;
            run.Click += (s, e) =>
            {
                if (DesktopFiles.Try(window, () =>
                {
                    string value = Environment.ExpandEnvironmentVariables(path.Text.Trim());
                    if (System.IO.Directory.Exists(value)) app.OpenExplorer(value);
                    else SystemSettings.LaunchCommand(value);
                })) window.Close();
            };
            window.Loaded += (s, e) => path.Focus();
            window.Show();
        }

        private sealed class TaskItem
        {
            internal int Process;
            internal bool Active;
            internal Button Button;
        }
    }
}

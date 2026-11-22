using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WinNTDE
{
    internal sealed class TaskbarWindow : Window
    {
        private const int BarHeight = 43;
        private const int ToggleStartMessage = 0x8001; // WM_APP + 1
        private const int ChangeVolumeMessage = 0x8002;
        private readonly App app;
        private readonly Grid bar = new Grid();
        private readonly StackPanel group = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        private readonly StackPanel tasks = new StackPanel { Orientation = Orientation.Horizontal };
        private readonly ScrollViewer taskScroll;
        private readonly StackPanel right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        private readonly StackPanel trayIcons = new StackPanel { Orientation = Orientation.Vertical };
        private readonly StackPanel mediaItems = new StackPanel();
        private readonly TextBlock mediaStatus = new TextBlock { TextWrapping = TextWrapping.Wrap };
        private readonly Button clock, trayButton;
        private readonly Popup tray, mediaPopup;
        private readonly DispatcherTimer taskRefresh = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        private readonly DispatcherTimer edgeRefresh = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        private readonly Dictionary<IntPtr, TaskItem> windows = new Dictionary<IntPtr, TaskItem>();
        private NotificationArea notifications;
        private TaskbarAppBar appBar;
        private ShellKeyHook shellKeys;
        private StartMenuWindow startMenu;
        private VolumeWindow volumeWindow;
        private ContextMenu openMenu;
        private NativeDesktop.Rect previousWork, lastScreen, bounds;
        private NativeDesktop.Rect? reservedShellWork;
        private IntPtr hwnd, desktop, activeApp, fullscreenWindow;
        private bool collapsed, positionPending, trayRefreshPending, mediaBusy, mediaRefreshPending, closed;
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
            var logo = BitmapFrame.Create(new Uri("pack://application:,,,/WinNTDE;component/LogoIcon.ico"));
            var menuButton = IconButton(logo, "Start");
            menuButton.Click += (s, e) => ToggleStartMenu();
            var startCommands = CreateButtonMenu(menuButton);
            App.AddMenuItem(startCommands, "Device Manager", () => DesktopFiles.Try(this, () => SystemSettings.OpenManagementConsole("devmgmt.msc")));
            App.AddMenuItem(startCommands, "Disk Manager", () => DesktopFiles.Try(this, () => SystemSettings.OpenManagementConsole("diskmgmt.msc")));
            App.AddMenuItem(startCommands, "Terminal", () => DesktopFiles.Try(this, () => SystemSettings.OpenTerminal(false)));
            App.AddMenuItem(startCommands, "Terminal (Admin)", () => DesktopFiles.Try(this, () => SystemSettings.OpenTerminal(true)));
            menuButton.ContextMenu = startCommands;
            menuButton.ContextMenuOpening += (s, e) => CloseButtonPopups();
            group.Children.Add(menuButton);
            var explorer = IconButton(DesktopFiles.FolderIcon, "File Explorer");
            explorer.Click += (s, e) =>
            {
                DismissStartMenu();
                app.OpenExplorer(DesktopFiles.Home);
            };
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
            trayButton.Click += (s, e) =>
            {
                DismissStartMenu();
                mediaPopup.IsOpen = false;
                if (!tray.IsOpen) RebuildTray();
                tray.IsOpen = !tray.IsOpen;
            };
            right.Children.Add(trayButton);
            var ejectIcon = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 2,11 L 10,2 18,11 Z M 2,14 H 18 V 17 H 2 Z"),
                Width = 18, Height = 18, Stretch = Stretch.Uniform };
            ejectIcon.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Text");
            var mediaButton = IconButton(ejectIcon, "Eject removable media");
            mediaButton.Click += (s, e) =>
            {
                DismissStartMenu();
                tray.IsOpen = false;
                mediaPopup.IsOpen = !mediaPopup.IsOpen;
                if (mediaPopup.IsOpen) RefreshMedia();
            };
            right.Children.Add(mediaButton);
            clock = new Button { Margin = new Thickness(4, 0, 0, 0), MinWidth = 65, Padding = new Thickness(8, 3, 8, 3) };
            clock.Click += (s, e) => OpenClock();
            right.Children.Add(clock);

            tray = CreatePopup(trayButton, trayIcons);
            var mediaPanel = new StackPanel { MinWidth = 210, MaxWidth = 380 };
            mediaPanel.Children.Add(mediaStatus);
            mediaPanel.Children.Add(new ScrollViewer { MaxHeight = 245, Content = mediaItems,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            mediaPopup = CreatePopup(mediaButton, mediaPanel);

            var barMenu = App.Menu();
            barMenu.Opened += (s, e) => DismissStartMenu();
            App.AddMenuItem(barMenu, "Taskbar settings", () => app.OpenSettings("Taskbar"));
            ContextMenu = barMenu;
            app.Preferences.Changed += PreferencesChanged;
            UpdateAlignment();
            taskRefresh.Tick += (s, e) => RefreshTasks();
            edgeRefresh.Tick += (s, e) => RefreshEdge();
            bar.SizeChanged += (s, e) => SizeTasks();
            right.SizeChanged += (s, e) => SizeTasks();
            Closing += (s, e) => { if (appBar != null) appBar.Release(); };
            Closed += (s, e) =>
            {
                closed = true;
                tray.IsOpen = mediaPopup.IsOpen = false;
                taskRefresh.Stop();
                edgeRefresh.Stop();
                app.Preferences.Changed -= PreferencesChanged;
                if (shellKeys != null) shellKeys.Dispose();
                if (notifications != null) notifications.Dispose();
                if (reservedShellWork.HasValue && NativeDesktop.WorkArea.Equals(reservedShellWork.Value))
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
            try
            {
                notifications = new NotificationArea(() => bounds, () => app.Preferences.AutoHide, !app.IsShell);
                notifications.Changed += OnTrayChanged;
            }
            catch (Win32Exception) { }
            if (!app.IsShell) appBar = new TaskbarAppBar(hwnd, notifications);
            mouseInside = DateTime.UtcNow;
            UpdateBounds();
            RefreshTasks();
            try { shellKeys = new ShellKeyHook(QueueStartMenu, QueueVolumeChange); }
            catch (Win32Exception error) { Trace.TraceWarning("Could not register shell keys: {0}", error.Message); }
            taskRefresh.Start();
            edgeRefresh.Start();
        }

        private IntPtr HandleMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == ToggleStartMessage)
            {
                handled = true;
                if (!closed) ToggleStartMenu();
                return IntPtr.Zero;
            }
            if (message == ChangeVolumeMessage)
            {
                handled = true;
                if (!closed) ChangeVolume(wParam.ToInt32());
                return IntPtr.Zero;
            }
            if (message == NativeDesktop.DisplayChanged || message == 0x02E0 ||
                (appBar != null && (message == NotificationArea.TaskbarCreated ||
                    (app.Preferences.AutoHide && message == NativeDesktop.SettingChanged && wParam == new IntPtr(NativeDesktop.SetWorkAreaAction)) ||
                    (message == TaskbarAppBar.CallbackMessage && wParam == new IntPtr(TaskbarAppBar.PositionChanged)))))
                QueuePositionUpdate();
            if (appBar != null && message == TaskbarAppBar.CallbackMessage && wParam == new IntPtr(TaskbarAppBar.FullscreenChanged))
            {
                bool opening = lParam != IntPtr.Zero;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (opening) RefreshEdge();
                    else SetFullscreenWindow(IntPtr.Zero);
                }));
            }
            if (message == 0x0219 && mediaPopup.IsOpen) // WM_DEVICECHANGE
            {
                int change = wParam.ToInt32();
                if (change == 7 || change == 0x8000 || change == 0x8004)
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => RefreshMedia()));
            }
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
            UpdateBounds();
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

        private void QueuePositionUpdate()
        {
            if (positionPending || closed) return;
            positionPending = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                positionPending = false;
                UpdateBounds();
            }));
        }

        private void UpdateBounds()
        {
            if (hwnd == IntPtr.Zero || closed) return;
            var screen = NativeDesktop.Screen;
            if (screen.Width != lastScreen.Width || screen.Height != lastScreen.Height)
            {
                lastScreen = screen;
                previousWork = screen;
            }
            int height = (int)Math.Round(BarHeight * NativeDesktop.ChromeScale);
            NativeDesktop.Rect? appBarBounds = null;
            if (appBar != null)
            {
                if (app.Preferences.AutoHide) appBar.Release();
                else appBarBounds = appBar.Reserve(BottomBar(screen, height));
            }
            bounds = appBarBounds ?? BottomBar(app.IsShell ? screen : NativeDesktop.WorkArea, height);
            if (app.IsShell)
            {
                var desired = screen;
                if (!app.Preferences.AutoHide) desired.Bottom = bounds.Top;
                if (!NativeDesktop.WorkArea.Equals(desired) && NativeDesktop.SetWorkArea(desired))
                    reservedShellWork = desired;
            }
            if (notifications != null) notifications.Place(bounds);
            ApplyPosition();
        }

        private static NativeDesktop.Rect BottomBar(NativeDesktop.Rect area, int height)
        {
            area.Top = Math.Max(area.Top, area.Bottom - height);
            return area;
        }

        private void ApplyPosition()
        {
            var visible = bounds;
            if (collapsed) visible.Top = Math.Max(visible.Top, visible.Bottom - 2);
            NativeDesktop.Rect current;
            bool moved = !NativeDesktop.GetWindowRect(hwnd, out current) || !visible.Equals(current);
            IntPtr after = fullscreenWindow == IntPtr.Zero ? NativeDesktop.Topmost : fullscreenWindow;
            bool positioned = NativeDesktop.SetWindowPos(hwnd, after, visible.Left, visible.Top, visible.Width, visible.Height, 0x0010);
            if (!positioned && fullscreenWindow != IntPtr.Zero)
            {
                // The fullscreen window can close between enumeration and positioning.
                fullscreenWindow = IntPtr.Zero;
                positioned = NativeDesktop.SetWindowPos(hwnd, NativeDesktop.Topmost, visible.Left, visible.Top, visible.Width, visible.Height, 0x0010);
            }
            if (positioned && moved && appBar != null) appBar.NotifyPositionChanged();
            if (startMenu != null && startMenu.IsVisible) startMenu.Position(bounds, NativeDesktop.WorkArea.Top);
            if (volumeWindow != null) volumeWindow.Position(bounds, NativeDesktop.WorkArea.Top);
        }

        private void RefreshEdge()
        {
            if (closed) return;
            IntPtr active = NativeDesktop.GetForegroundWindow();
            if (active != hwnd && active != IntPtr.Zero && NativeDesktop.WindowProcess(active) != NativeDesktop.ProcessId)
                activeApp = active;
            SetFullscreenWindow(NativeDesktop.FindFullscreenWindow(hwnd, desktop, active));
            if (!app.Preferences.AutoHide || fullscreenWindow != IntPtr.Zero) return;
            NativeDesktop.Point cursor;
            if (!NativeDesktop.GetCursorPos(out cursor)) return;
            DateTime now = DateTime.UtcNow;
            bool overEdge = cursor.X >= bounds.Left && cursor.X < bounds.Right && cursor.Y >= bounds.Bottom - 2 && cursor.Y < bounds.Bottom;
            bool overBar = cursor.X >= bounds.Left && cursor.X < bounds.Right && cursor.Y >= bounds.Top && cursor.Y < bounds.Bottom;
            if (!collapsed && (overBar || tray.IsOpen || mediaPopup.IsOpen || (openMenu != null && openMenu.IsOpen) ||
                ContextMenu.IsOpen || (startMenu != null && startMenu.IsVisible))) mouseInside = now;
            if (!overEdge) edgeSeen = DateTime.MinValue;
            else if (edgeSeen == DateTime.MinValue) edgeSeen = now;
            if (collapsed && overEdge && now - edgeSeen > TimeSpan.FromMilliseconds(140))
            {
                collapsed = false;
                mouseInside = now;
                ApplyPosition();
            }
            else if (!collapsed && now - mouseInside > TimeSpan.FromMilliseconds(500))
            {
                collapsed = true;
                ApplyPosition();
            }
        }

        private void SetFullscreenWindow(IntPtr window)
        {
            if (closed || window == fullscreenWindow) return;
            fullscreenWindow = window;
            if (window != IntPtr.Zero)
            {
                CloseButtonPopups();
                ContextMenu.IsOpen = false;
            }
            else mouseInside = DateTime.UtcNow;
            ApplyPosition();
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
            if (notifications != null) notifications.Refresh();
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
            DismissStartMenu();
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
                trayIcons.Children.Add(new TextBlock { Text = notifications == null ?
                    "The notification area is unavailable." : "No app notification icons." });
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

        private void OnTrayChanged(object sender, EventArgs e)
        {
            if (!tray.IsOpen || trayRefreshPending) return;
            trayRefreshPending = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                trayRefreshPending = false;
                if (tray.IsOpen) RebuildTray();
            }));
        }

        private static Popup CreatePopup(Button target, FrameworkElement content)
        {
            var border = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8), Child = content };
            border.SetResourceReference(Border.BackgroundProperty, "Panel");
            border.SetResourceReference(Border.BorderBrushProperty, "Line");
            border.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Text");
            return new Popup { PlacementTarget = target, Placement = PlacementMode.Top, StaysOpen = false,
                VerticalOffset = -7, Child = border };
        }

        private void RefreshMedia(RemovableMedia.Device eject = null, string notice = null)
        {
            if (closed || !mediaPopup.IsOpen) return;
            if (mediaBusy)
            {
                mediaRefreshPending = true;
                return;
            }
            mediaBusy = true;
            mediaItems.IsEnabled = false;
            SetMediaStatus(eject == null ? notice ?? "Looking for removable media…" : "Ejecting " + eject.Name + "…");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool ejected = false;
                if (eject != null)
                {
                    notice = MediaError(() => RemovableMedia.Eject(eject));
                    ejected = notice == null;
                    if (ejected) notice = eject.OpticalDrive == null ? "Safe to remove " + eject.Name + "." : "Ejected " + eject.Name + ".";
                }
                RemovableMedia.Device[] devices = null;
                string scanError = MediaError(() => devices = RemovableMedia.ListDevices());
                if (ejected && devices != null) devices = devices.Where(device => !eject.Matches(device)).ToArray();
                if (Dispatcher.HasShutdownStarted) return;
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    mediaBusy = false;
                    bool refresh = mediaRefreshPending;
                    mediaRefreshPending = false;
                    if (closed || !mediaPopup.IsOpen) return;
                    mediaItems.IsEnabled = true;
                    mediaItems.Children.Clear();
                    if (devices != null)
                    {
                        foreach (var device in devices)
                        {
                            var item = new Button { Content = new TextBlock { Text = "Eject " + device.Name, TextWrapping = TextWrapping.Wrap },
                                Margin = new Thickness(0, 3, 0, 0) };
                            System.Windows.Automation.AutomationProperties.SetName(item, "Eject " + device.Name);
                            item.Click += (s, e) => RefreshMedia(device);
                            mediaItems.Children.Add(item);
                        }
                    }
                    SetMediaStatus(notice ?? scanError ?? (mediaItems.Children.Count == 0 ? "No removable media connected." : null));
                    if (refresh) RefreshMedia(notice: notice);
                }));
            });
        }

        private void SetMediaStatus(string text)
        {
            mediaStatus.Text = text;
            mediaStatus.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        private static string MediaError(Action work)
        {
            try { work(); return null; }
            catch (Exception error) when (error is Win32Exception || error is IOException || error is InvalidOperationException ||
                error is UnauthorizedAccessException || error is System.Security.SecurityException)
            {
                return error.Message;
            }
        }

        private bool QueueStartMenu()
        {
            if (closed || fullscreenWindow != IntPtr.Zero) return false;
            return NativeDesktop.PostMessage(hwnd, ToggleStartMessage, IntPtr.Zero, IntPtr.Zero);
        }

        private bool QueueVolumeChange(int percentagePoints)
        {
            return !closed && NativeDesktop.PostMessage(hwnd, ChangeVolumeMessage, new IntPtr(percentagePoints), IntPtr.Zero);
        }

        private void ChangeVolume(int percentagePoints)
        {
            float? level = null;
            try { level = AudioDevices.AdjustVolume(percentagePoints); }
            catch (ExternalException error) { Trace.TraceWarning("Could not change the output volume: {0}", error.Message); }
            if (volumeWindow == null)
            {
                volumeWindow = new VolumeWindow { Owner = this };
                volumeWindow.Closed += (s, e) => volumeWindow = null;
            }
            volumeWindow.ShowLevel(level, bounds, NativeDesktop.WorkArea.Top);
        }

        private void ToggleStartMenu()
        {
            if (startMenu != null && startMenu.IsVisible)
            {
                startMenu.Dismiss();
                return;
            }
            if (collapsed)
            {
                collapsed = false;
                mouseInside = DateTime.UtcNow;
                ApplyPosition();
            }
            tray.IsOpen = mediaPopup.IsOpen = false;
            if (openMenu != null) openMenu.IsOpen = false;
            ContextMenu.IsOpen = false;
            if (startMenu == null)
            {
                startMenu = new StartMenuWindow(app, OpenRun) { Owner = this };
                startMenu.Closed += (s, e) => startMenu = null;
            }
            startMenu.Open(bounds, NativeDesktop.WorkArea.Top);
        }

        private void DismissStartMenu()
        {
            if (startMenu != null) startMenu.Dismiss();
        }

        private void OpenClock()
        {
            CloseButtonPopups();
            var menu = CreateButtonMenu(clock);
            menu.Items.Add(new MenuItem { Header = DateTime.Now.ToLongDateString(), IsEnabled = false });
            App.AddMenuItem(menu, "Date and time settings", () => DesktopFiles.Try(this, () => SystemSettings.OpenControl("timedate.cpl")));
            menu.IsOpen = true;
        }

        private ContextMenu CreateButtonMenu(Button button)
        {
            var menu = App.Menu();
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Top;
            menu.Opened += (s, e) => openMenu = menu;
            menu.Closed += (s, e) => { if (openMenu == menu) openMenu = null; };
            return menu;
        }

        private void CloseButtonPopups()
        {
            DismissStartMenu();
            tray.IsOpen = mediaPopup.IsOpen = false;
            if (openMenu != null) openMenu.IsOpen = false;
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

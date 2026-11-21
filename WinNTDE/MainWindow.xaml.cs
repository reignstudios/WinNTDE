using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace WinNTDE
{
    public partial class MainWindow : Window
    {
        private readonly App app;
        private readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
        private readonly DispatcherTimer refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        private readonly string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        private readonly string sharedDesktop = DesktopFiles.SharedDesktop;
        private IntPtr hwnd;
        private string appliedWallpaper;

        internal MainWindow(App app)
        {
            this.app = app;
            InitializeComponent();
            ReadWallpaper(null, EventArgs.Empty);
            app.Preferences.Changed += ReadWallpaper;
            ContextMenu = App.Menu();
            App.AddMenuItem(ContextMenu, "Open File Explorer", () => app.OpenExplorer(desktop));
            App.AddMenuItem(ContextMenu, "Personalization", () => app.OpenSettings("Personalization"));
            App.AddMenuItem(ContextMenu, "Refresh Desktop", RefreshIcons);
            Icons.MouseDoubleClick += (s, e) =>
            {
                if (e.ChangedButton == MouseButton.Left && DesktopFiles.ItemAt(e.OriginalSource) != null) OpenSelection();
            };
            Icons.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) OpenSelection();
                if (e.Key == Key.F5) RefreshIcons();
            };
            Icons.PreviewMouseRightButtonUp += (s, e) =>
            {
                var item = DesktopFiles.ItemAt(e.OriginalSource);
                if (item == null) return;
                Icons.SelectedItem = item.DataContext;
                var entry = (FileEntry)item.DataContext;
                e.Handled = ShellFileMenu.Show(this, new[] { entry.Path }, entry.IsFolder ? (Action)OpenSelection : null);
            };
            refresh.Tick += (s, e) => { refresh.Stop(); RefreshIcons(); };
            Loaded += (s, e) =>
            {
                RefreshIcons();
                foreach (string path in new[] { desktop, sharedDesktop }.Distinct(StringComparer.OrdinalIgnoreCase)) Watch(path);
            };
            Closed += (s, e) =>
            {
                refresh.Stop();
                foreach (var watcher in watchers) watcher.Dispose();
                app.Preferences.Changed -= ReadWallpaper;
                NativeDesktop.UnregisterHotKey(hwnd, 1);
                NativeDesktop.UnregisterHotKey(hwnd, 2);
            };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(hwnd).AddHook(HandleMessage);
            if (app.IsShell)
            {
                NativeDesktop.RegisterShell(hwnd);
                NativeDesktop.RegisterHotKey(hwnd, 1, 8, (uint)'E');
                NativeDesktop.RegisterHotKey(hwnd, 2, 8, (uint)'I');
            }
            NativeDesktop.PlaceDesktop(hwnd);
        }

        private IntPtr HandleMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == NativeDesktop.DisplayChanged)
                Dispatcher.BeginInvoke(new Action(() => NativeDesktop.PlaceDesktop(hwnd)));
            if (message == NativeDesktop.HotKey)
            {
                if (wParam.ToInt32() == 1) app.OpenExplorer(DesktopFiles.Home);
                if (wParam.ToInt32() == 2) app.OpenSettings();
            }
            // Keep the desktop below other normal windows even when an icon takes focus.
            if (message == 0x0046 && lParam != IntPtr.Zero)
            {
                int flagsOffset = 2 * IntPtr.Size + 16;
                int flags = System.Runtime.InteropServices.Marshal.ReadInt32(lParam, flagsOffset);
                if ((flags & 0x0004) == 0)
                    System.Runtime.InteropServices.Marshal.WriteIntPtr(lParam, IntPtr.Size, NativeDesktop.Bottom);
            }
            return IntPtr.Zero;
        }

        private void ReadWallpaper(object sender, EventArgs e)
        {
            string path = app.Preferences.Wallpaper;
            if (string.Equals(appliedWallpaper, path, StringComparison.Ordinal)) return;
            appliedWallpaper = path;
            Wallpaper.Source = string.IsNullOrEmpty(path) ? null : DesktopFiles.DecodeImage(path, 2560);
        }

        private void RefreshIcons()
        {
            var previous = Icons.SelectedItem as FileEntry;
            var entries = new List<FileEntry>();
            foreach (string path in new[] { desktop, sharedDesktop }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(path)) continue;
                try { entries.AddRange(DesktopFiles.ReadFolder(path, false)); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            Icons.ItemsSource = entries.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            if (previous != null) Icons.SelectedItem = entries.FirstOrDefault(item => item.Path == previous.Path);
        }

        private void OpenSelection()
        {
            var item = Icons.SelectedItem as FileEntry;
            if (item != null) DesktopFiles.Try(this, () => DesktopFiles.Open(item));
        }

        private void Watch(string path)
        {
            if (!Directory.Exists(path)) return;
            FileSystemWatcher watcher = null;
            try
            {
                watcher = new FileSystemWatcher(path);
                watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Attributes;
                watcher.Created += QueueRefresh;
                watcher.Deleted += QueueRefresh;
                watcher.Changed += QueueRefresh;
                watcher.Renamed += QueueRefresh;
                watcher.Error += (s, e) => QueueRefresh(s, null);
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
            }
            catch (IOException) { if (watcher != null) watcher.Dispose(); }
            catch (UnauthorizedAccessException) { if (watcher != null) watcher.Dispose(); }
        }

        private void QueueRefresh(object sender, FileSystemEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!IsLoaded) return;
                refresh.Stop();
                refresh.Start();
            }));
        }
    }
}

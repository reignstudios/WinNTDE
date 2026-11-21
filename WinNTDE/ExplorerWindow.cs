using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace WinNTDE
{
    internal sealed class ExplorerWindow : Window
    {
        private readonly App app = (App)Application.Current;
        private readonly List<string> history = new List<string>();
        private readonly TextBox address = new TextBox();
        private readonly ListView files = new ListView { SelectionMode = SelectionMode.Extended };
        private readonly TextBlock status = new TextBlock { Margin = new Thickness(12, 6, 12, 6) };
        private readonly DispatcherTimer refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        private readonly Button back, forward, up;
        private FileSystemWatcher watcher;
        private int historyIndex = -1, request;
        private string folder;
        private bool pending;

        internal ExplorerWindow(string initialPath)
        {
            Title = "File Explorer";
            Width = 970;
            Height = 620;
            MinWidth = 440;
            MinHeight = 250;
            NativeDesktop.ThemeWindow(this);

            var root = new DockPanel();
            Content = root;
            var navigation = new DockPanel { Margin = new Thickness(8) };
            DockPanel.SetDock(navigation, Dock.Top);
            root.Children.Add(navigation);
            back = AddButton(navigation, "‹", "Back", () => Travel(-1));
            forward = AddButton(navigation, "›", "Forward", () => Travel(1));
            up = AddButton(navigation, "↑", "Parent folder", Up);
            AddButton(navigation, "↻", "Refresh", Reload);
            System.Windows.Automation.AutomationProperties.SetName(address, "Folder path");
            address.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) { Navigate(address.Text); e.Handled = true; }
                if (e.Key == Key.Escape) { address.Text = folder; files.Focus(); }
            };
            navigation.Children.Add(address);

            var tools = new DockPanel { Margin = new Thickness(8, 0, 8, 8) };
            DockPanel.SetDock(tools, Dock.Top);
            root.Children.Add(tools);
            AddButton(tools, "Cut", "Cut (Ctrl+X)", () => Copy(true));
            AddButton(tools, "Copy", "Copy (Ctrl+C)", () => Copy(false));
            AddButton(tools, "Paste", "Paste (Ctrl+V)", Paste);
            Button view = AddButton(tools, "View ▾", "Change folder view", null);
            view.Click += (s, e) => ShowViewMenu(view);
            tools.Children.Add(new Border());

            var side = new StackPanel { Margin = new Thickness(8) };
            var sidebar = new ScrollViewer { Width = 148, Content = side, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            sidebar.SetResourceReference(BackgroundProperty, "Panel");
            DockPanel.SetDock(sidebar, Dock.Left);
            root.Children.Add(sidebar);

            var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddPlace(side, added, "Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            AddPlace(side, added, "Documents", DesktopFiles.Home);
            AddPlace(side, added, "Downloads", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
            AddPlace(side, added, "Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
            AddPlace(side, added, "Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
            AddPlace(side, added, "Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
            AddPlace(side, added, "Home", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            foreach (var drive in DriveInfo.GetDrives()) AddPlace(side, added, drive.Name, drive.Name, false);

            DockPanel.SetDock(status, Dock.Bottom);
            status.SetResourceReference(ForegroundProperty, "Muted");
            root.Children.Add(status);
            root.Children.Add(files);
            VirtualizingStackPanel.SetVirtualizationMode(files, VirtualizationMode.Recycling);
            files.SelectionChanged += (s, e) => { if (!pending) UpdateStatus(); };
            files.MouseDoubleClick += (s, e) =>
            {
                if (e.ChangedButton == MouseButton.Left && DesktopFiles.ItemAt(e.OriginalSource) != null) OpenSelected();
            };
            files.PreviewMouseRightButtonUp += (s, e) =>
            {
                var item = DesktopFiles.ItemAt(e.OriginalSource);
                if (item != null)
                {
                    if (!item.IsSelected) files.SelectedItem = item.DataContext;
                    e.Handled = true;
                    ShowFileMenu();
                }
            };
            var background = App.Menu();
            App.AddMenuItem(background, "Paste", Paste);
            App.AddMenuItem(background, "Refresh", Reload);
            files.ContextMenu = background;

            PreviewKeyDown += HandleKeys;
            refresh.Tick += (s, e) => { refresh.Stop(); Reload(); };
            Closed += (s, e) =>
            {
                request++;
                refresh.Stop();
                if (watcher != null) watcher.Dispose();
                app.Preferences.Changed -= PreferencesChanged;
            };
            app.Preferences.Changed += PreferencesChanged;
            CreateView();
            Navigate(string.IsNullOrEmpty(initialPath) ? DesktopFiles.Home : initialPath);
        }

        private static Button AddButton(DockPanel bar, string text, string help, Action action)
        {
            var button = new Button { Content = text, ToolTip = help, Margin = new Thickness(0, 0, 5, 0) };
            System.Windows.Automation.AutomationProperties.SetName(button, help);
            if (action != null) button.Click += (s, e) => action();
            bar.Children.Add(button);
            return button;
        }

        private void AddPlace(StackPanel side, HashSet<string> added, string title, string path, bool check = true)
        {
            if (string.IsNullOrEmpty(path) || (check && !Directory.Exists(path)) || !added.Add(path)) return;
            var button = new Button { Content = title, ToolTip = path, Margin = new Thickness(0, 0, 0, 4) };
            button.Click += (s, e) => Navigate(path);
            side.Children.Add(button);
        }

        private void PreferencesChanged(object sender, EventArgs e)
        {
            bool details = app.Preferences.Details;
            bool thumbnails = app.Preferences.Thumbnails;
            if (details == shownDetails && thumbnails == shownThumbnails) return;
            CreateView();
            Reload();
        }

        private bool shownDetails, shownThumbnails;

        private void CreateView()
        {
            shownDetails = app.Preferences.Details;
            shownThumbnails = app.Preferences.Thumbnails;
            var row = new FrameworkElementFactory(typeof(StackPanel));
            row.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            var icon = new FrameworkElementFactory(typeof(Image));
            icon.SetValue(WidthProperty, shownThumbnails ? 56.0 : 22.0);
            icon.SetValue(HeightProperty, shownThumbnails ? 44.0 : 22.0);
            icon.SetValue(MarginProperty, new Thickness(2, 2, 8, 2));
            icon.SetBinding(Image.SourceProperty, new Binding("Icon"));
            row.AppendChild(icon);
            var name = new FrameworkElementFactory(typeof(TextBlock));
            name.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            name.SetBinding(TextBlock.TextProperty, new Binding("Name"));
            row.AppendChild(name);
            var template = new DataTemplate { VisualTree = row };
            files.View = null;
            files.ItemTemplate = shownDetails ? null : template;
            if (!shownDetails) return;

            var grid = new GridView();
            grid.Columns.Add(new GridViewColumn { Header = "Name", Width = 310, CellTemplate = template });
            grid.Columns.Add(new GridViewColumn { Header = "Type", Width = 115, DisplayMemberBinding = new Binding("Kind") });
            grid.Columns.Add(new GridViewColumn { Header = "Size", Width = 85, DisplayMemberBinding = new Binding("Size") });
            grid.Columns.Add(new GridViewColumn { Header = "Modified", Width = 160, DisplayMemberBinding = new Binding("Modified") });
            files.View = grid;
        }

        private void ShowViewMenu(Button button)
        {
            var menu = App.Menu();
            App.AddMenuItem(menu, "Details", () => SetView(true));
            App.AddMenuItem(menu, "List", () => SetView(false));
            var thumbnails = new MenuItem { Header = "Image thumbnails", IsCheckable = true, IsChecked = app.Preferences.Thumbnails };
            thumbnails.Click += (s, e) => { app.Preferences.Thumbnails = thumbnails.IsChecked; app.SavePreferences(this); };
            menu.Items.Add(new Separator());
            menu.Items.Add(thumbnails);
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }

        private void SetView(bool details)
        {
            app.Preferences.Details = details;
            app.SavePreferences(this);
        }

        private void Navigate(string path)
        {
            DesktopFiles.Try(this, () =>
            {
                string expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
                string target = Path.GetFullPath(expanded);
                if (target.Length > Path.GetPathRoot(target).Length) target = target.TrimEnd(Path.DirectorySeparatorChar);
                LoadFolder(target, -1);
            });
        }

        private void Travel(int direction)
        {
            int index = historyIndex + direction;
            if (index >= 0 && index < history.Count) LoadFolder(history[index], index);
        }

        private void Up()
        {
            if (folder == null) return;
            var parent = Directory.GetParent(folder);
            if (parent != null) Navigate(parent.FullName);
        }

        private void Reload()
        {
            if (folder != null && !pending) LoadFolder(folder, historyIndex);
        }

        private void LoadFolder(string path, int index)
        {
            int generation = ++request;
            bool thumbnails = app.Preferences.Thumbnails;
            pending = true;
            status.Text = "Loading " + path;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var entries = DesktopFiles.ReadFolder(path, thumbnails)
                        .OrderByDescending(item => item.IsFolder)
                        .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (generation != request) return;
                        pending = false;
                        if (thumbnails != app.Preferences.Thumbnails) { LoadFolder(path, index); return; }
                        CommitFolder(path, index, entries);
                    }));
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException ||
                    error is System.Security.SecurityException || error is ArgumentException)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (generation != request) return;
                        pending = false;
                        address.Text = folder ?? path;
                        status.Text = error.Message;
                    }));
                }
            });
        }

        private void CommitFolder(string path, int index, FileEntry[] entries)
        {
            bool changed = !string.Equals(folder, path, StringComparison.OrdinalIgnoreCase);
            var selected = new HashSet<string>(Selection().Select(item => item.Path), StringComparer.OrdinalIgnoreCase);
            folder = path;
            if (index >= 0) historyIndex = index;
            else if (changed)
            {
                history.RemoveRange(historyIndex + 1, history.Count - historyIndex - 1);
                history.Add(path);
                historyIndex = history.Count - 1;
            }

            address.Text = path;
            Title = (Path.GetFileName(path).Length == 0 ? path : Path.GetFileName(path)) + " | File Explorer";
            files.ItemsSource = entries;
            if (!changed)
                foreach (var item in entries.Where(item => selected.Contains(item.Path))) files.SelectedItems.Add(item);
            else WatchFolder(path);
            back.IsEnabled = historyIndex > 0;
            forward.IsEnabled = historyIndex + 1 < history.Count;
            up.IsEnabled = Directory.GetParent(path) != null;
            UpdateStatus();
        }

        private void WatchFolder(string path)
        {
            if (watcher != null) watcher.Dispose();
            watcher = null;
            try
            {
                watcher = new FileSystemWatcher(path);
                watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                    NotifyFilters.Attributes | NotifyFilters.LastWrite | NotifyFilters.Size;
                watcher.Changed += QueueRefresh;
                watcher.Created += QueueRefresh;
                watcher.Deleted += QueueRefresh;
                watcher.Renamed += QueueRefresh;
                watcher.Error += (s, e) => QueueRefresh(s, null);
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException)
            {
                if (watcher != null) watcher.Dispose();
                watcher = null;
            }
        }

        private void QueueRefresh(object sender, FileSystemEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!ReferenceEquals(watcher, sender)) return;
                refresh.Stop();
                refresh.Start();
            }));
        }

        private void UpdateStatus()
        {
            int total = files.Items.Count, count = files.SelectedItems.Count;
            status.Text = total + (total == 1 ? " item" : " items") + (count == 0 ? "" : "   ·   " + count + " selected");
        }

        private FileEntry[] Selection() { return files.SelectedItems.Cast<FileEntry>().ToArray(); }

        private void OpenSelected()
        {
            var item = files.SelectedItem as FileEntry;
            if (item == null) return;
            if (item.IsFolder) Navigate(item.Path);
            else DesktopFiles.Try(this, () => DesktopFiles.Open(item));
        }

        private void Copy(bool move)
        {
            var selected = Selection();
            if (selected.Length != 0) DesktopFiles.Try(this, () => DesktopFiles.PutOnClipboard(selected, move));
        }

        private void Paste()
        {
            if (folder == null || pending) return;
            DesktopFiles.Try(this, () => DesktopFiles.Paste(this, folder));
            Reload();
        }

        private void ShowFileMenu()
        {
            var selected = Selection();
            if (selected.Length == 0)
            {
                files.ContextMenu.PlacementTarget = files;
                files.ContextMenu.IsOpen = true;
                return;
            }
            if (ShellFileMenu.Show(this, selected.Select(item => item.Path).ToArray(),
                selected.Length == 1 && selected[0].IsFolder ? (Action)OpenSelected : null)) return;
            var menu = App.Menu();
            if (selected.Length == 1) App.AddMenuItem(menu, "Open", OpenSelected);
            App.AddMenuItem(menu, "Cut", () => Copy(true));
            App.AddMenuItem(menu, "Copy", () => Copy(false));
            App.AddMenuItem(menu, "Paste", Paste);
            menu.PlacementTarget = files;
            menu.IsOpen = true;
        }

        private void HandleKeys(object sender, KeyEventArgs e)
        {
            var modifiers = Keyboard.Modifiers;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.F5) Reload();
            else if (modifiers == ModifierKeys.Control && key == Key.L) { address.Focus(); address.SelectAll(); }
            else if (modifiers == ModifierKeys.Alt && key == Key.Left) Travel(-1);
            else if (modifiers == ModifierKeys.Alt && key == Key.Right) Travel(1);
            else if (modifiers == ModifierKeys.Alt && key == Key.Up) Up();
            else if (address.IsKeyboardFocusWithin) return;
            else if (key == Key.Enter) OpenSelected();
            else if (key == Key.Apps || (key == Key.F10 && modifiers == ModifierKeys.Shift)) ShowFileMenu();
            else if (modifiers == ModifierKeys.Control && key == Key.C) Copy(false);
            else if (modifiers == ModifierKeys.Control && key == Key.X) Copy(true);
            else if (modifiers == ModifierKeys.Control && key == Key.V) Paste();
            else return;
            e.Handled = true;
        }
    }
}

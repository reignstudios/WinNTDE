using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace WinNTDE
{
    internal partial class TaskManagerWindow : Window
    {
        private readonly DispatcherTimer timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
        private readonly HashSet<Tuple<int, long>> expanded = new HashSet<Tuple<int, long>>();
        private TaskManagerData.Snapshot snapshot;
        private bool busy, closed;

        internal TaskManagerWindow()
        {
            InitializeComponent();
            NativeDesktop.ThemeWindow(this);
            ContentRendered += (s, e) => Run();
            timer.Tick += (s, e) => Run();
            Closed += (s, e) => { closed = true; timer.Stop(); };
        }

        private void RefreshClicked(object sender, RoutedEventArgs e)
        {
            if (AutoRefresh.IsChecked != true) Run();
        }

        private void AutoRefreshChanged(object sender, RoutedEventArgs e)
        {
            if (AutoRefresh.IsChecked == true) timer.Start();
            else timer.Stop();
            UpdateButtons();
        }

        private void SelectionChanged(object sender, SelectionChangedEventArgs e) { UpdateButtons(); }

        private void SearchChanged(object sender, TextChangedEventArgs e)
        {
            if (snapshot == null) return;
            int matches = RenderRows();
            if (!busy) Status.Text = FormatProcessCount(matches);
        }

        private void EndSelected(object sender, RoutedEventArgs e)
        {
            var targets = ProcessList.SelectedItems.Cast<Row>().Where(row => !row.ContextOnly)
                .Select(row => new TaskManagerData.Target(row.Process, row.Group != null)).ToArray();
            if (targets.Any(target => target.Process.CanEnd)) Run(targets);
        }

        private void UpdateButtons()
        {
            RefreshButton.IsEnabled = !busy && AutoRefresh.IsChecked != true;
            EndTaskButton.IsEnabled = !busy && ProcessList.SelectedItems.Cast<Row>().Any(row => !row.ContextOnly && row.Process.CanEnd);
        }

        private void Run(TaskManagerData.Target[] targets = null)
        {
            if (busy || closed) return;
            busy = true;
            Status.Text = targets == null ? "Reading processes…" : "Ending selected tasks…";
            UpdateButtons();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                TaskManagerData.Snapshot updated = null;
                string message = null;
                try
                {
                    if (targets != null) message = TaskManagerData.EndTasks(targets);
                    updated = TaskManagerData.Capture();
                }
                catch (Win32Exception error) { message = message == null ? error.Message : message + " " + error.Message; }
                if (Dispatcher.HasShutdownStarted) return;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (closed) return;
                    busy = false;
                    int matches = 0;
                    if (updated != null)
                    {
                        snapshot = updated;
                        expanded.IntersectWith(snapshot.Apps.Select(group => group.Owner.Identity));
                        CpuUsage.Text = FormatPercent(updated.CpuPercent);
                        MemoryUsage.Text = FormatPercent(updated.MemoryPercent);
                        MemoryTotal.Text = FormatMemoryTotal(updated.TotalMemory);
                        DiskUsage.Text = FormatPercent(updated.DiskActivePercent);
                        matches = RenderRows();
                    }
                    Status.Text = message ?? FormatProcessCount(matches);
                    UpdateButtons();
                }));
            });
        }

        private static string FormatPercent(int? percent)
        {
            return percent.HasValue ? percent.Value + "%" : "Unavailable";
        }

        private static string FormatMemoryTotal(ulong total)
        {
            if (total == 0) return string.Empty;
            const double MiB = 1024 * 1024, GiB = MiB * 1024;
            return total >= GiB ? string.Format("{0:0.#} GiB total", total / GiB) : string.Format("{0:0} MiB total", total / MiB);
        }

        private string FormatProcessCount(int matches)
        {
            if (!string.IsNullOrWhiteSpace(SearchBox.Text))
                return matches == 0 ? "No matching processes." : matches + (matches == 1 ? " matching process" : " matching processes");

            int count = snapshot.Background.Length + snapshot.Apps.Sum(group => 1 + group.Members.Count);
            return count + (count == 1 ? " process" : " processes");
        }

        private void ToggleGroup(object sender, RoutedEventArgs e)
        {
            var row = (Row)((Button)sender).DataContext;
            var identity = row.Process.Identity;
            if (!expanded.Add(identity)) expanded.Remove(identity);
            RenderRows();
            var group = ProcessList.Items.Cast<Row>().FirstOrDefault(item => item.Process.Identity.Equals(identity));
            if (group != null) ProcessList.ScrollIntoView(group);
        }

        private int RenderRows()
        {
            string query = SearchBox.Text.Trim();
            bool filtering = query.Length != 0;
            int pid;
            if (!int.TryParse(query, out pid)) pid = -1;
            var selected = new HashSet<Tuple<int, long>>(ProcessList.SelectedItems.Cast<Row>().Select(row => row.Process.Identity));
            var rows = new List<Row>();
            foreach (var group in snapshot.Apps)
            {
                bool ownerMatches = !filtering || Matches(group.Owner, query, pid);
                var members = filtering ? group.Members.Where(process => Matches(process, query, pid)) : group.Members;
                if (!ownerMatches && !members.Any()) continue;

                bool open = filtering || expanded.Contains(group.Owner.Identity);
                rows.Add(new Row(group.Owner, group, false, open, canExpand: !filtering, contextOnly: !ownerMatches));
                if (open) rows.AddRange(members.Select(process => new Row(process, null, true, false)));
            }
            rows.AddRange(snapshot.Background.Where(process => !filtering || Matches(process, query, pid))
                .Select(process => new Row(process, null, false, false)));
            ProcessList.ItemsSource = rows;
            foreach (var row in rows.Where(row => !row.ContextOnly && selected.Contains(row.Process.Identity))) ProcessList.SelectedItems.Add(row);
            return rows.Count(row => !row.ContextOnly);
        }

        private static bool Matches(TaskManagerData.ProcessEntry process, string query, int pid)
        {
            return process.Id == pid || (process.Executable != null && process.Executable.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) ||
                (process.WindowTitle != null && process.WindowTitle.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0);
        }

        private sealed class Row
        {
            internal readonly TaskManagerData.ProcessEntry Process;
            internal readonly TaskManagerData.Group Group;
            private readonly bool child, expanded, canExpand;

            internal Row(TaskManagerData.ProcessEntry process, TaskManagerData.Group group, bool child, bool expanded,
                bool canExpand = false, bool contextOnly = false)
            {
                Process = process;
                Group = group;
                this.child = child;
                this.expanded = expanded;
                this.canExpand = canExpand;
                ContextOnly = contextOnly;
            }

            public string Name { get { return Group == null ? Process.Executable : Process.WindowTitle; } }
            public string Executable { get { return Process.Executable; } }
            public int ProcessId { get { return Process.Id; } }
            public Thickness Indent { get { return new Thickness(child ? 19 : 0, 0, 0, 0); } }
            public string Expander { get { return expanded ? "▾" : "▸"; } }
            public string ExpanderName { get { return (expanded ? "Collapse " : "Expand ") + Name; } }
            public bool ContextOnly { get; private set; }
            public Visibility ExpanderVisibility { get { return canExpand && Group.Members.Count != 0 ? Visibility.Visible : Visibility.Hidden; } }
        }
    }
}

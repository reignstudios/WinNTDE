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

        private void EndSelected(object sender, RoutedEventArgs e)
        {
            var targets = ProcessList.SelectedItems.Cast<Row>()
                .Select(row => new TaskManagerData.Target(row.Process, row.Group != null)).ToArray();
            if (targets.Any(target => target.Process.CanEnd)) Run(targets);
        }

        private void UpdateButtons()
        {
            RefreshButton.IsEnabled = !busy && AutoRefresh.IsChecked != true;
            EndTaskButton.IsEnabled = !busy && ProcessList.SelectedItems.Cast<Row>().Any(row => row.Process.CanEnd);
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
                    if (updated != null)
                    {
                        snapshot = updated;
                        expanded.IntersectWith(snapshot.Apps.Select(group => group.Owner.Identity));
                        CpuUsage.Text = updated.CpuPercent.HasValue ? updated.CpuPercent.Value + "%" : "Unavailable";
                        MemoryUsage.Text = FormatMemory(updated.UsedMemory, updated.TotalMemory);
                        RenderRows();
                    }
                    Status.Text = message ?? FormatProcessCount(updated);
                    UpdateButtons();
                }));
            });
        }

        private static string FormatMemory(ulong used, ulong total)
        {
            if (total == 0) return "Unavailable";
            const double MiB = 1024 * 1024, GiB = MiB * 1024;
            return total >= GiB ? string.Format("{0:0.#} / {1:0.#} GiB", used / GiB, total / GiB) :
                string.Format("{0:0} / {1:0} MiB", used / MiB, total / MiB);
        }

        private static string FormatProcessCount(TaskManagerData.Snapshot data)
        {
            int count = data.Background.Length + data.Apps.Sum(group => 1 + group.Members.Count);
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

        private void RenderRows()
        {
            var selected = new HashSet<Tuple<int, long>>(ProcessList.SelectedItems.Cast<Row>().Select(row => row.Process.Identity));
            var rows = new List<Row>();
            foreach (var group in snapshot.Apps)
            {
                bool open = expanded.Contains(group.Owner.Identity);
                rows.Add(new Row(group.Owner, group, false, open));
                if (open) rows.AddRange(group.Members.Select(process => new Row(process, null, true, false)));
            }
            rows.AddRange(snapshot.Background.Select(process => new Row(process, null, false, false)));
            ProcessList.ItemsSource = rows;
            foreach (var row in rows.Where(row => selected.Contains(row.Process.Identity))) ProcessList.SelectedItems.Add(row);
        }

        private sealed class Row
        {
            internal readonly TaskManagerData.ProcessEntry Process;
            internal readonly TaskManagerData.Group Group;
            private readonly bool child, expanded;

            internal Row(TaskManagerData.ProcessEntry process, TaskManagerData.Group group, bool child, bool expanded)
            {
                Process = process;
                Group = group;
                this.child = child;
                this.expanded = expanded;
            }

            public string Name { get { return Group == null ? Process.Executable : Process.WindowTitle; } }
            public string Executable { get { return Process.Executable; } }
            public int ProcessId { get { return Process.Id; } }
            public Thickness Indent { get { return new Thickness(child ? 19 : 0, 0, 0, 0); } }
            public string Expander { get { return expanded ? "▾" : "▸"; } }
            public string ExpanderName { get { return (expanded ? "Collapse " : "Expand ") + Name; } }
            public Visibility ExpanderVisibility { get { return Group != null && Group.Members.Count != 0 ? Visibility.Visible : Visibility.Hidden; } }
        }
    }
}

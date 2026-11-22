using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;

namespace WinNTDE
{
    internal static class TaskManagerData
    {
        private const uint SnapshotProcesses = 2, ProcessTerminate = 1, Synchronize = 0x00100000;
        private const int NoMoreFiles = 18, InvalidParameter = 87;
        private static readonly uint QueryProcess = SystemSettings.WindowsVersion.Major >= 6 ? 0x1000u : 0x0400u;
        private static readonly bool SupportsCriticalCheck = SystemSettings.WindowsVersion >= new Version(6, 3);

        internal sealed class ProcessEntry
        {
            internal readonly Tuple<int, long> Identity;
            internal readonly int ParentId;
            internal readonly string Executable;
            internal string WindowTitle;
            internal int WindowOwnerId;

            internal ProcessEntry(int id, int parentId, string executable, long creation)
            {
                Identity = Tuple.Create(id, creation);
                ParentId = parentId;
                Executable = executable;
            }

            internal int Id { get { return Identity.Item1; } }
            internal long Created { get { return Identity.Item2; } }
            internal bool CanEnd { get { return Id > 0 && Id != NativeDesktop.ProcessId && Created != 0; } }
        }

        internal sealed class Group
        {
            internal readonly ProcessEntry Owner;
            internal readonly List<ProcessEntry> Members = new List<ProcessEntry>();

            internal Group(ProcessEntry owner) { Owner = owner; }
        }

        internal sealed class Snapshot
        {
            internal int? CpuPercent, MemoryPercent, DiskActivePercent;
            internal ulong TotalMemory;
            internal Group[] Apps;
            internal ProcessEntry[] Background;
        }

        internal sealed class Target
        {
            internal readonly ProcessEntry Process;
            internal readonly bool IncludeGroup;

            internal Target(ProcessEntry process, bool includeGroup)
            {
                Process = process;
                IncludeGroup = includeGroup;
            }
        }

        internal static Snapshot Capture()
        {
            using (var disk = new PerformanceCounter { CategoryName = "LogicalDisk", CounterName = "% Idle Time", InstanceName = "C:" })
            {
                var diskStart = ReadDiskSample(disk);
                long idleStart, kernelStart, userStart;
                bool cpuAvailable = GetSystemTimes(out idleStart, out kernelStart, out userStart);
                var sample = Stopwatch.StartNew();
                var entries = ReadProcesses();
                var groups = GroupProcesses(entries, entries.Values.Where(process => process.WindowTitle != null).Select(process => process.Id));
                var memory = new MemoryStatus { Size = (uint)Marshal.SizeOf(typeof(MemoryStatus)) };
                bool memoryAvailable = GlobalMemoryStatusEx(ref memory) && memory.TotalPhysical != 0;
                int remaining = 250 - (int)sample.ElapsedMilliseconds;
                if ((cpuAvailable || diskStart.HasValue) && remaining > 0) Thread.Sleep(remaining);

                int? cpu = null;
                long idleEnd, kernelEnd, userEnd;
                if (cpuAvailable && GetSystemTimes(out idleEnd, out kernelEnd, out userEnd))
                {
                    long total = kernelEnd - kernelStart + userEnd - userStart;
                    if (total > 0) cpu = RoundPercent(100.0 * (total - (idleEnd - idleStart)) / total);
                }

                var appMembers = new HashSet<int>(groups.Values.SelectMany(group => group.Members.Select(member => member.Id)));
                return new Snapshot
                {
                    CpuPercent = cpu,
                    MemoryPercent = memoryAvailable ? (int?)memory.Load : null,
                    TotalMemory = memoryAvailable ? memory.TotalPhysical : 0,
                    DiskActivePercent = ReadDiskActivity(disk, diskStart),
                    Apps = groups.Values.OrderBy(group => group.Owner.WindowTitle, StringComparer.CurrentCultureIgnoreCase)
                        .ThenBy(group => group.Owner.Id).ToArray(),
                    Background = entries.Values.Where(process => !groups.ContainsKey(process.Id) && !appMembers.Contains(process.Id))
                        .OrderBy(process => process.Executable, StringComparer.CurrentCultureIgnoreCase).ThenBy(process => process.Id).ToArray()
                };
            }
        }

        private static CounterSample? ReadDiskSample(PerformanceCounter disk)
        {
            try { return disk.NextSample(); }
            catch (Exception error) when (error is InvalidOperationException || error is Win32Exception ||
                error is IOException || error is UnauthorizedAccessException || error is SecurityException)
            {
                return null;
            }
        }

        private static int? ReadDiskActivity(PerformanceCounter disk, CounterSample? start)
        {
            if (!start.HasValue) return null;
            var end = ReadDiskSample(disk);
            if (!end.HasValue || end.Value.BaseValue <= start.Value.BaseValue || end.Value.RawValue < start.Value.RawValue)
                return null;

            return RoundPercent(100 - CounterSample.Calculate(start.Value, end.Value));
        }

        private static int RoundPercent(double value)
        {
            return (int)Math.Round(Math.Max(0, Math.Min(100, value)));
        }

        internal static string EndTasks(Target[] targets)
        {
            var requested = targets.Select(target => target.Process).ToList();
            var trees = targets.Where(target => target.IncludeGroup && target.Process.CanEnd).ToArray();
            if (trees.Length != 0)
            {
                var entries = ReadProcesses();
                var liveTrees = trees.Where(target => IsSameProcess(entries, target.Process)).Select(target => target.Process.Id).ToArray();
                var owners = entries.Values.Where(process => process.WindowTitle != null).Select(process => process.Id).Union(liveTrees);
                var groups = GroupProcesses(entries, owners);
                foreach (int id in liveTrees) requested.AddRange(groups[id].Members);
            }

            int ended = 0, exited = 0, failed = 0;
            string firstError = null;
            var processed = new HashSet<Tuple<int, long>>();
            foreach (var process in requested)
            {
                if (!processed.Add(process.Identity)) continue;
                try
                {
                    if (EndProcess(process)) ended++;
                    else exited++;
                }
                catch (Exception error) when (error is Win32Exception || error is InvalidOperationException)
                {
                    failed++;
                    if (firstError == null) firstError = process.Executable + " (PID " + process.Id + "). " + error.Message;
                }
            }

            string status = "Ended " + ended + (ended == 1 ? " process." : " processes.");
            if (exited != 0) status += " " + exited + " already exited.";
            if (failed != 0) status += " Could not end " + failed + (failed == 1 ? " process. " : " processes. ") + firstError;
            return status;
        }

        private static bool IsSameProcess(Dictionary<int, ProcessEntry> entries, ProcessEntry process)
        {
            ProcessEntry live;
            return process.Created != 0 && entries.TryGetValue(process.Id, out live) && live.Created == process.Created;
        }

        private static bool EndProcess(ProcessEntry process)
        {
            if (!process.CanEnd) throw new InvalidOperationException("This process cannot be ended from WinNTDE.");
            IntPtr handle = OpenProcess(QueryProcess | ProcessTerminate | Synchronize, false, process.Id);
            if (handle == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == InvalidParameter) return false;
                throw new Win32Exception(error);
            }
            try
            {
                long creation, exit, kernel, user;
                if (!GetProcessTimes(handle, out creation, out exit, out kernel, out user)) throw new Win32Exception();
                if (creation != process.Created || WaitForSingleObject(handle, 0) == 0) return false;
                bool critical;
                if (SupportsCriticalCheck && IsProcessCritical(handle, out critical) && critical)
                    throw new InvalidOperationException("Windows marks this process as critical.");
                if (!TerminateProcess(handle, 1))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (WaitForSingleObject(handle, 0) == 0) return false;
                    throw new Win32Exception(error);
                }
                WaitForSingleObject(handle, 500);
                return true;
            }
            finally { CloseHandle(handle); }
        }

        private static Dictionary<int, ProcessEntry> ReadProcesses()
        {
            var entries = new Dictionary<int, ProcessEntry>();
            long snapshotStarted = DateTime.UtcNow.ToFileTimeUtc();
            IntPtr snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
            if (snapshot == new IntPtr(-1)) throw new Win32Exception();
            try
            {
                var data = new ProcessData { Size = (uint)Marshal.SizeOf(typeof(ProcessData)) };
                bool hasEntry = Process32First(snapshot, ref data);
                while (hasEntry)
                {
                    long creation = GetCreationTime(data.Id);
                    // A reused PID can refer to a different process by the time its handle is opened.
                    if (creation > snapshotStarted) creation = 0;
                    entries[data.Id] = new ProcessEntry(data.Id, data.ParentId, data.Executable, creation);
                    hasEntry = Process32Next(snapshot, ref data);
                }
                if (Marshal.GetLastWin32Error() != NoMoreFiles) throw new Win32Exception();
            }
            finally { CloseHandle(snapshot); }

            NativeDesktop.EnumWindows((window, parameter) =>
            {
                if (!NativeDesktop.IsWindowVisible(window)) return true;
                IntPtr owner = NativeDesktop.RootWindowOwner(window);
                if (owner == IntPtr.Zero || !NativeDesktop.IsApplicationWindow(owner)) owner = window;
                if (!NativeDesktop.IsApplicationWindow(owner)) return true;
                ProcessEntry root, child;
                if (!entries.TryGetValue(NativeDesktop.WindowProcess(owner), out root)) return true;
                if (root.WindowTitle == null)
                {
                    string title = NativeDesktop.WindowTitle(owner);
                    if (string.IsNullOrWhiteSpace(title)) return true;
                    root.WindowTitle = title;
                }
                if (owner != window && entries.TryGetValue(NativeDesktop.WindowProcess(window), out child) && child.Id != root.Id)
                    child.WindowOwnerId = root.Id;
                return true;
            }, IntPtr.Zero);
            return entries;
        }

        private static long GetCreationTime(int id)
        {
            IntPtr process = OpenProcess(QueryProcess, false, id);
            if (process == IntPtr.Zero) return 0;
            try
            {
                long creation, exit, kernel, user;
                return GetProcessTimes(process, out creation, out exit, out kernel, out user) ? creation : 0;
            }
            finally { CloseHandle(process); }
        }

        private static Dictionary<int, Group> GroupProcesses(Dictionary<int, ProcessEntry> entries, IEnumerable<int> owners)
        {
            var groups = owners.ToDictionary(id => id, id => new Group(entries[id]));
            foreach (var process in entries.Values.OrderBy(item => item.Executable, StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.Id))
            {
                var ancestor = process;
                for (int visited = 0; ancestor != null && visited < entries.Count; visited++)
                {
                    Group group;
                    if (groups.TryGetValue(ancestor.Id, out group) || groups.TryGetValue(ancestor.WindowOwnerId, out group))
                    {
                        if (group.Owner != process) group.Members.Add(process);
                        break;
                    }
                    ProcessEntry parent;
                    // A parent PID can be reused. Verify that the parent was running before the child started.
                    if (!entries.TryGetValue(ancestor.ParentId, out parent) || parent.Id == ancestor.Id ||
                        parent.Created == 0 || ancestor.Created == 0 || parent.Created > ancestor.Created) break;
                    ancestor = parent;
                }
            }
            return groups;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessData
        {
            internal uint Size, Usage;
            internal int Id;
            internal UIntPtr DefaultHeap;
            internal uint Module, Threads;
            internal int ParentId, Priority;
            internal uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string Executable;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatus
        {
            internal uint Size, Load;
            internal ulong TotalPhysical, AvailablePhysical, TotalPage, AvailablePage, TotalVirtual, AvailableVirtual, ExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint process);
        [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern bool Process32First(IntPtr snapshot, ref ProcessData data);
        [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern bool Process32Next(IntPtr snapshot, ref ProcessData data);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int id);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(IntPtr process, out long creation,
            out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll")] private static extern bool IsProcessCritical(IntPtr process, out bool critical);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
        [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
        [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    }
}

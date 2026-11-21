using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WinNTDE
{
    internal static class RemovableMedia
    {
        private static readonly Guid DiskInterface = new Guid("53F56307-B6BF-11D0-94F2-00A0C91EFB8B");
        private const uint FileDeviceDisk = 7, DiskNumberControl = 0x002D1080, VolumeExtentsControl = 0x00560000;
        private const uint LockVolumeControl = 0x00090018, EjectMediaControl = 0x002D4808;
        private const uint DeviceDescription = 1, Service = 5, FriendlyName = 13, Capabilities = 16;
        private const uint EjectSupported = 2, Removable = 4, DockDevice = 8;
        private const int DeviceIdLength = 200;

        internal sealed class Device
        {
            internal readonly string Name, InstanceId, OpticalDrive;

            internal Device(string name, string instanceId, string opticalDrive = null)
            {
                Name = name;
                InstanceId = instanceId;
                OpticalDrive = opticalDrive;
            }

            internal bool Matches(Device other)
            {
                return InstanceId != null ? string.Equals(InstanceId, other.InstanceId, StringComparison.OrdinalIgnoreCase) :
                    OpticalDrive != null && string.Equals(OpticalDrive, other.OpticalDrive, StringComparison.OrdinalIgnoreCase);
            }
        }

        internal static Device[] ListDevices()
        {
            var result = new List<Device>();
            var volumes = new Dictionary<int, List<string>>();
            string windowsDrive = Path.GetPathRoot(Environment.SystemDirectory);
            var protectedDisks = new HashSet<int>(VolumeDisks(windowsDrive));

            foreach (var drive in DriveInfo.GetDrives())
            {
                DriveType type = drive.DriveType;
                if (type == DriveType.CDRom && drive.IsReady)
                    result.Add(new Device("Optical drive (" + drive.Name.TrimEnd('\\') + ")", null, drive.Name));
                if (type != DriveType.Fixed && type != DriveType.Removable) continue;
                foreach (int disk in VolumeDisks(drive.Name))
                {
                    List<string> names;
                    if (!volumes.TryGetValue(disk, out names)) volumes.Add(disk, names = new List<string>());
                    names.Add(drive.Name.TrimEnd('\\'));
                }
            }

            // Keep any ejection target containing the Windows volume out of the menu.
            if (protectedDisks.Count != 0)
                result.AddRange(ListDisks(volumes, protectedDisks));
            return result.OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }

        private static IEnumerable<Device> ListDisks(Dictionary<int, List<string>> volumes, HashSet<int> protectedDisks)
        {
            var diskClass = DiskInterface;
            IntPtr devices = SetupDiGetClassDevs(ref diskClass, null, IntPtr.Zero, 0x12); // Present device interfaces
            if (devices == new IntPtr(-1)) throw new Win32Exception();

            var disks = new List<Disk>();
            try
            {
                var data = new InterfaceData { Size = Marshal.SizeOf(typeof(InterfaceData)) };
                for (uint i = 0; SetupDiEnumDeviceInterfaces(devices, IntPtr.Zero, ref diskClass, i, ref data); i++)
                {
                    var info = new DeviceInfo { Size = Marshal.SizeOf(typeof(DeviceInfo)) };
                    string path = InterfacePath(devices, ref data, ref info);
                    if (path == null) continue;
                    using (var handle = Open(path))
                    {
                        int number = DiskNumber(handle);
                        if (number < 0) continue;
                        disks.Add(new Disk { Number = number, Node = info.Node, Target = EjectTarget(info.Node) });
                    }
                }
                int error = Marshal.GetLastWin32Error();
                if (error != 259) throw new Win32Exception(error); // ERROR_NO_MORE_ITEMS
            }
            finally { SetupDiDestroyDeviceInfoList(devices); }

            var protectedNodes = new HashSet<uint>();
            foreach (var disk in disks.Where(disk => protectedDisks.Contains(disk.Number)))
            {
                uint node = disk.Node;
                while (protectedNodes.Add(node) && CM_Get_Parent(out node, node, 0) == 0) { }
            }

            foreach (var group in disks.Where(disk => disk.Target != 0 && !protectedNodes.Contains(disk.Target) &&
                !protectedDisks.Contains(disk.Number)).GroupBy(disk => disk.Target))
            {
                var id = new StringBuilder(DeviceIdLength);
                if (CM_Get_Device_ID(group.Key, id, id.Capacity, 0) != 0) continue;
                var first = group.First();
                string name = StringProperty(first.Node, FriendlyName) ?? StringProperty(first.Node, DeviceDescription) ?? "Removable disk";
                var letters = group.Where(disk => volumes.ContainsKey(disk.Number)).SelectMany(disk => volumes[disk.Number])
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(letter => letter).ToArray();
                if (letters.Length != 0) name += " (" + string.Join(", ", letters) + ")";
                yield return new Device(name, id.ToString());
            }
        }

        private static uint EjectTarget(uint disk)
        {
            if (IsEjectable(disk)) return disk;

            // With USB storage the disk can be a child of the removable USB mass-storage function.
            uint parent = disk;
            while (CM_Get_Parent(out parent, parent, 0) == 0)
            {
                string service = StringProperty(parent, Service);
                if ((string.Equals(service, "USBSTOR", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(service, "UASPStor", StringComparison.OrdinalIgnoreCase)) && IsEjectable(parent)) return parent;
            }
            return 0;
        }

        private static bool IsEjectable(uint node)
        {
            byte[] data = GetProperty(node, Capabilities, sizeof(uint));
            if (data == null) return false;
            uint flags = BitConverter.ToUInt32(data, 0);
            return (flags & (EjectSupported | Removable)) != 0 && (flags & DockDevice) == 0;
        }

        private static string StringProperty(uint node, uint property)
        {
            var data = GetProperty(node, property, 1024);
            return data == null ? null : Encoding.Unicode.GetString(data).TrimEnd('\0');
        }

        private static byte[] GetProperty(uint node, uint property, int length)
        {
            var data = new byte[length];
            uint type;
            return CM_Get_DevNode_Registry_Property(node, property, out type, data, ref length, 0) == 0 ? data : null;
        }

        private static string InterfacePath(IntPtr devices, ref InterfaceData data, ref DeviceInfo info)
        {
            int size;
            SetupDiGetDeviceInterfaceDetail(devices, ref data, IntPtr.Zero, 0, out size, IntPtr.Zero);
            if (size <= 0) return null;
            IntPtr detail = Marshal.AllocHGlobal(size);
            try
            {
                // SP_DEVICE_INTERFACE_DETAIL_DATA_W has a platform-sized header, with the path at offset 4.
                Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                return SetupDiGetDeviceInterfaceDetail(devices, ref data, detail, size, out size, ref info) ?
                    Marshal.PtrToStringUni(IntPtr.Add(detail, sizeof(int))) : null;
            }
            finally { Marshal.FreeHGlobal(detail); }
        }

        private static SafeFileHandle Open(string devicePath, uint access = 0)
        {
            return CreateFile(devicePath, access, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        }

        private static int DiskNumber(SafeFileHandle handle)
        {
            var buffer = new byte[12];
            int count;
            if (handle.IsInvalid || !DeviceIoControl(handle, DiskNumberControl, IntPtr.Zero, 0, buffer, buffer.Length,
                out count, IntPtr.Zero) || count < buffer.Length || BitConverter.ToUInt32(buffer, 0) != FileDeviceDisk) return -1;
            return BitConverter.ToInt32(buffer, 4);
        }

        private static int[] VolumeDisks(string drive)
        {
            if (string.IsNullOrEmpty(drive)) return new int[0];
            using (var handle = Open(VolumePath(drive)))
            {
                if (handle.IsInvalid) return new int[0];
                var extents = new byte[1024];
                int count;
                if (DeviceIoControl(handle, VolumeExtentsControl, IntPtr.Zero, 0, extents, extents.Length,
                    out count, IntPtr.Zero) && count >= sizeof(int))
                {
                    int first = Marshal.OffsetOf(typeof(VolumeExtents), "First").ToInt32();
                    int stride = Marshal.SizeOf(typeof(DiskExtent));
                    int length = BitConverter.ToInt32(extents, 0);
                    if (length > 0 && length <= (count - first) / stride)
                        return Enumerable.Range(0, length).Select(i => BitConverter.ToInt32(extents, first + i * stride))
                            .Distinct().ToArray();
                }
                int disk = DiskNumber(handle);
                return disk < 0 ? new int[0] : new[] { disk };
            }
        }

        private static string VolumePath(string drive) { return @"\\.\" + drive.TrimEnd('\\'); }

        internal static void Eject(Device requested)
        {
            // Resolve the current device again because Windows can reuse a DEVINST after unplugging a device.
            var device = ListDevices().FirstOrDefault(requested.Matches);
            if (device == null) throw new InvalidOperationException("This device is no longer available to eject.");
            if (device.OpticalDrive != null)
            {
                EjectOpticalDisc(device.OpticalDrive);
                return;
            }

            uint node;
            if (CM_Locate_DevNode(out node, device.InstanceId, 0) != 0 || !IsEjectable(node))
                throw new InvalidOperationException("This device is no longer available to eject.");
            int veto;
            var vetoName = new StringBuilder(260);
            uint result = CM_Request_Device_Eject(node, out veto, vetoName, vetoName.Capacity, 0);
            if (result == 0) return;
            if (veto >= 2 && veto <= 7)
                throw new InvalidOperationException("The device is in use. Close files or apps using it and try again.");
            if (veto == 12 || result == 0x33)
                throw new InvalidOperationException("Windows denied permission to eject this device.");
            throw new InvalidOperationException("Windows could not safely eject this device (0x" + result.ToString("X") + ").");
        }

        private static void EjectOpticalDisc(string drive)
        {
            using (var handle = Open(VolumePath(drive), 0x80000000))
            {
                if (handle.IsInvalid) throw new Win32Exception();
                int count;
                // Locking fails if another process still has files open on the disc.
                if (!DeviceIoControl(handle, LockVolumeControl, IntPtr.Zero, 0, null, 0, out count, IntPtr.Zero))
                    throw new InvalidOperationException("Windows could not lock the disc. Close files or apps using it and try again.");
                if (!DeviceIoControl(handle, EjectMediaControl, IntPtr.Zero, 0, null, 0, out count, IntPtr.Zero))
                    throw new Win32Exception();
            }
        }

        private sealed class Disk
        {
            internal int Number;
            internal uint Node, Target;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct InterfaceData { internal int Size; internal Guid Class; internal int Flags; internal IntPtr Reserved; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DeviceInfo { internal int Size; internal Guid Class; internal uint Node; internal IntPtr Reserved; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DiskExtent { internal int Number; internal long Start, Length; }

        [StructLayout(LayoutKind.Sequential)]
        private struct VolumeExtents { internal int Count; internal DiskExtent First; }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string enumerator, IntPtr owner, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr device, ref Guid classGuid, uint index, ref InterfaceData data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData data, IntPtr detail, int size,
            out int required, IntPtr info);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData data, IntPtr detail, int size,
            out int required, ref DeviceInfo info);
        [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern uint CM_Get_DevNode_Registry_Property(uint node, uint property, out uint type, [Out] byte[] buffer,
            ref int length, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern uint CM_Get_Device_ID(uint node, StringBuilder value, int size, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern uint CM_Locate_DevNode(out uint node, string id, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern uint CM_Request_Device_Eject(uint node, out int veto, StringBuilder name, int size, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation,
            uint attributes, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, int inputSize,
            [Out] byte[] output, int outputSize, out int bytes, IntPtr overlapped);
    }
}

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Xml.Linq;

namespace WinNTDE
{
    internal static class Wireless
    {
        internal sealed class Network
        {
            internal Guid Adapter;
            internal string AdapterName, Name, Profile;
            internal int Signal, Authentication, Cipher, BssType;
            internal byte[] Ssid;
            internal bool Connected, Connectable;
            internal bool CanCreateProfile
            {
                get { return (Authentication == 1 && Cipher == 0) ||
                    ((Authentication == 4 || Authentication == 7) && (Cipher == 2 || Cipher == 4)); }
            }
            internal bool NeedsPassword { get { return string.IsNullOrEmpty(Profile) && Authentication != 1; } }
            public override string ToString()
            {
                return Name + "   ·   " + (Connected ? "Connected" : Signal + "% signal") +
                    (string.IsNullOrEmpty(Profile) ? "" : "   ·   Saved") + "   ·   " + AdapterName;
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct AdapterInfo
        {
            internal Guid Id;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Description;
            internal int State;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct AvailableNetwork
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Profile;
            internal uint SsidLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] internal byte[] Ssid;
            internal int BssType, BssidCount;
            [MarshalAs(UnmanagedType.Bool)] internal bool Connectable;
            internal int Reason, PhyTypeCount;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] internal int[] PhyTypes;
            [MarshalAs(UnmanagedType.Bool)] internal bool MorePhyTypes;
            internal int Signal;
            [MarshalAs(UnmanagedType.Bool)] internal bool Secure;
            internal int Authentication, Cipher, Flags, Reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Connection
        {
            internal int Mode;
            internal string Profile;
            internal IntPtr Ssid, DesiredBssids;
            internal int BssType, Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BluetoothSearch
        {
            internal int Size;
            [MarshalAs(UnmanagedType.Bool)] internal bool Authenticated, Remembered, Unknown, Connected, Inquiry;
            internal byte Timeout;
            internal IntPtr Radio;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SystemTime
        {
            internal ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct BluetoothDevice
        {
            internal int Size;
            internal ulong Address;
            internal uint Class;
            [MarshalAs(UnmanagedType.Bool)] internal bool Connected, Remembered, Authenticated;
            internal SystemTime LastSeen, LastUsed;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] internal string Name;
        }

        internal static Network[] Networks(bool scan)
        {
            return WithWlan(handle =>
            {
                var adapters = new List<AdapterInfo>();
                IntPtr list;
                SystemSettings.Check(WlanEnumInterfaces(handle, IntPtr.Zero, out list));
                try
                {
                    int count = Marshal.ReadInt32(list);
                    int stride = Marshal.SizeOf(typeof(AdapterInfo));
                    for (int i = 0; i < count; i++)
                    {
                        var adapter = (AdapterInfo)Marshal.PtrToStructure(IntPtr.Add(list, 8 + i * stride), typeof(AdapterInfo));
                        adapters.Add(adapter);
                    }
                }
                finally { WlanFreeMemory(list); }
                if (adapters.Count == 0) return new Network[0];
                if (scan)
                {
                    foreach (var adapter in adapters)
                    {
                        Guid id = adapter.Id;
                        SystemSettings.Check(WlanScan(handle, ref id, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
                    }
                    // Native Wi-Fi refreshes its scan cache asynchronously.
                    Thread.Sleep(2500);
                }
                var networks = new List<Network>();
                foreach (var adapter in adapters)
                {
                    Guid id = adapter.Id;
                    uint flags = SystemSettings.WindowsVersion.Major >= 6 ? 2u : 0;
                    SystemSettings.Check(WlanGetAvailableNetworkList(handle, ref id, flags, IntPtr.Zero, out list));
                    try
                    {
                        int count = Marshal.ReadInt32(list);
                        int stride = Marshal.SizeOf(typeof(AvailableNetwork));
                        for (int i = 0; i < count; i++)
                        {
                            var value = (AvailableNetwork)Marshal.PtrToStructure(IntPtr.Add(list, 8 + i * stride), typeof(AvailableNetwork));
                            byte[] ssid = value.Ssid.Take((int)Math.Min(32, value.SsidLength)).ToArray();
                            if (ssid.Length == 0 && string.IsNullOrEmpty(value.Profile)) continue;
                            string name = ssid.Length == 0 ? value.Profile : Encoding.UTF8.GetString(ssid);
                            networks.Add(new Network { Adapter = id, AdapterName = adapter.Description, Name = name,
                                Profile = value.Profile, Signal = value.Signal, Ssid = ssid, Authentication = value.Authentication,
                                Cipher = value.Cipher, BssType = value.BssType, Connected = (value.Flags & 1) != 0,
                                Connectable = value.Connectable });
                        }
                    }
                    finally { WlanFreeMemory(list); }
                }
                return networks.OrderByDescending(n => n.Connected).ThenByDescending(n => n.Signal).ToArray();
            });
        }

        internal static void Connect(Network network, string password)
        {
            WithWlan(handle =>
            {
                Guid id = network.Adapter;
                string profile = network.Profile;
                if (string.IsNullOrEmpty(profile))
                {
                    if (!network.CanCreateProfile || network.BssType != 1)
                        throw new InvalidOperationException("Use Windows network settings to set up this network.");
                    profile = network.Name;
                    uint reason;
                    uint flags = SystemSettings.WindowsVersion.Major >= 6 ? 2u : 0;
                    uint result = WlanSetProfile(handle, ref id, flags, CreateProfile(network, password),
                        null, false, IntPtr.Zero, out reason);
                    if (result != 0 && reason != 0)
                    {
                        var message = new StringBuilder(512);
                        if (WlanReasonCodeToString(reason, (uint)message.Capacity, message, IntPtr.Zero) == 0)
                            throw new InvalidOperationException(message.ToString());
                    }
                    SystemSettings.Check(result);
                }

                var connection = new Connection { Profile = profile, BssType = network.BssType };
                SystemSettings.Check(WlanConnect(handle, ref id, ref connection, IntPtr.Zero));
                return true;
            });
        }

        internal static void Disconnect(Network network)
        {
            WithWlan(handle =>
            {
                Guid id = network.Adapter;
                SystemSettings.Check(WlanDisconnect(handle, ref id, IntPtr.Zero));
                return true;
            });
        }

        private static string CreateProfile(Network network, string password)
        {
            XNamespace wlan = "http://www.microsoft.com/networking/WLAN/profile/v1";
            string authentication = network.Authentication == 1 ? "open" : network.Authentication == 4 ? "WPAPSK" : "WPA2PSK";
            string encryption = network.Cipher == 0 ? "none" : network.Cipher == 2 ? "TKIP" : "AES";
            var security = new XElement(wlan + "security",
                new XElement(wlan + "authEncryption", new XElement(wlan + "authentication", authentication),
                    new XElement(wlan + "encryption", encryption), new XElement(wlan + "useOneX", false)));
            if (network.NeedsPassword)
            {
                bool hex = password.Length == 64 && password.All(Uri.IsHexDigit);
                if (!hex && (password.Length < 8 || password.Length > 63))
                    throw new ArgumentException("Enter a passphrase of 8 to 63 characters or a 64-digit hexadecimal key.");
                security.Add(new XElement(wlan + "sharedKey", new XElement(wlan + "keyType", hex ? "networkKey" : "passPhrase"),
                    new XElement(wlan + "protected", false), new XElement(wlan + "keyMaterial", password)));
            }
            string ssid = BitConverter.ToString(network.Ssid).Replace("-", "");
            return new XElement(wlan + "WLANProfile", new XElement(wlan + "name", network.Name),
                new XElement(wlan + "SSIDConfig", new XElement(wlan + "SSID", new XElement(wlan + "hex", ssid),
                    new XElement(wlan + "name", network.Name))),
                new XElement(wlan + "connectionType", "ESS"), new XElement(wlan + "connectionMode", "manual"),
                new XElement(wlan + "MSM", security)).ToString(SaveOptions.DisableFormatting);
        }

        private static T WithWlan<T>(Func<IntPtr, T> work)
        {
            uint negotiated;
            IntPtr handle;
            SystemSettings.Check(WlanOpenHandle(SystemSettings.WindowsVersion.Major >= 6 ? 2u : 1, IntPtr.Zero, out negotiated, out handle));
            try { return work(handle); }
            finally { WlanCloseHandle(handle, IntPtr.Zero); }
        }

        internal static string[] BluetoothDevices(bool scan)
        {
            var devices = new List<string>();
            var search = new BluetoothSearch { Size = Marshal.SizeOf(typeof(BluetoothSearch)), Authenticated = true,
                Remembered = true, Connected = true, Unknown = scan, Inquiry = scan, Timeout = 4 };
            var device = new BluetoothDevice { Size = Marshal.SizeOf(typeof(BluetoothDevice)) };
            IntPtr find = BluetoothFindFirstDevice(ref search, ref device);
            if (find == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                if (error != 259 && error != 1168 && error != 0) throw new Win32Exception(error);
                return devices.ToArray();
            }
            try
            {
                do
                {
                    string name = string.IsNullOrWhiteSpace(device.Name) ? device.Address.ToString("X12") : device.Name;
                    devices.Add(name + "   ·   " + (device.Connected ? "Connected" : device.Authenticated ? "Paired" : "Available"));
                    device.Size = Marshal.SizeOf(typeof(BluetoothDevice));
                } while (BluetoothFindNextDevice(find, ref device));
            }
            finally { BluetoothFindDeviceClose(find); }
            return devices.Distinct().OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }

        [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr handle);
        [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);
        [DllImport("wlanapi.dll")] private static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
        [DllImport("wlanapi.dll")] private static extern uint WlanScan(IntPtr handle, ref Guid id, IntPtr ssid, IntPtr data, IntPtr reserved);
        [DllImport("wlanapi.dll")] private static extern uint WlanGetAvailableNetworkList(IntPtr handle, ref Guid id, uint flags,
            IntPtr reserved, out IntPtr list);
        [DllImport("wlanapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint WlanSetProfile(IntPtr handle, ref Guid id, uint flags, string profile, string security,
            [MarshalAs(UnmanagedType.Bool)] bool overwrite, IntPtr reserved, out uint reason);
        [DllImport("wlanapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint WlanReasonCodeToString(uint reason, uint size, StringBuilder message, IntPtr reserved);
        [DllImport("wlanapi.dll", ExactSpelling = true)] private static extern uint WlanConnect(IntPtr handle, ref Guid id, ref Connection connection,
            IntPtr reserved);
        [DllImport("wlanapi.dll")] private static extern uint WlanDisconnect(IntPtr handle, ref Guid id, IntPtr reserved);
        [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr memory);

        [DllImport("bthprops.cpl", SetLastError = true)] private static extern IntPtr BluetoothFindFirstDevice(ref BluetoothSearch search,
            ref BluetoothDevice info);
        [DllImport("bthprops.cpl")] private static extern bool BluetoothFindNextDevice(IntPtr find, ref BluetoothDevice info);
        [DllImport("bthprops.cpl")] private static extern bool BluetoothFindDeviceClose(IntPtr find);
    }
}

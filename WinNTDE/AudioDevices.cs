using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace WinNTDE
{
    internal static class AudioDevices
    {
        internal sealed class Device
        {
            internal string Id, Name;
            internal bool Default;
            public override string ToString() { return Name; }
        }

        private static readonly Guid EnumeratorClass = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        private static readonly Guid PolicyClass = new Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");
        private static readonly Guid VistaPolicyClass = new Guid("294935CE-F637-4E7C-A41B-AB255460B862");
        private const int DefaultBeep = ('D' << 8) | 'S'; // SND_ALIAS_SYSTEMDEFAULT
        private const uint SoundAsync = 0x0001, SoundNoDefault = 0x0002;
        private const uint SoundAliasId = 0x00110000, SoundSystem = 0x00200000;

        internal static Device[] GetDevices(bool input)
        {
            var devices = new List<Device>();
            if (SystemSettings.WindowsVersion.Major < 6) return devices.ToArray();
            var enumerator = (IEndpointEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(EnumeratorClass, true));
            IEndpointCollection collection = null;
            IEndpoint defaultDevice = null;
            try
            {
                int flow = input ? 1 : 0;
                string defaultId = null;
                if (enumerator.GetDefaultAudioEndpoint(flow, 0, out defaultDevice) == 0) defaultId = defaultDevice.GetId();
                collection = enumerator.EnumAudioEndpoints(flow, 1);
                int count = collection.GetCount();
                for (int index = 0; index < count; index++)
                {
                    IEndpoint endpoint = collection.Item(index);
                    try
                    {
                        string id = endpoint.GetId();
                        devices.Add(new Device { Id = id, Name = FriendlyName(endpoint), Default = id == defaultId });
                    }
                    finally { SystemSettings.Release(endpoint); }
                }
                return devices.ToArray();
            }
            finally
            {
                SystemSettings.Release(defaultDevice);
                SystemSettings.Release(collection);
                SystemSettings.Release(enumerator);
            }
        }

        private static string FriendlyName(IEndpoint endpoint)
        {
            IPropertyStore properties = endpoint.OpenPropertyStore(0);
            try
            {
                var key = new PropertyKey { Format = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Property = 14 };
                Variant variant = properties.GetValue(ref key);
                try { return variant.Type == 31 ? Marshal.PtrToStringUni(variant.Pointer) : "Audio device"; }
                finally { PropVariantClear(ref variant); }
            }
            finally { SystemSettings.Release(properties); }
        }

        internal static void SetDefault(string id)
        {
            bool vista = SystemSettings.WindowsVersion.Major == 6 && SystemSettings.WindowsVersion.Minor == 0;
            object policy = Activator.CreateInstance(Type.GetTypeFromCLSID(vista ? VistaPolicyClass : PolicyClass, true));
            try
            {
                for (int role = 0; role < 3; role++)
                {
                    if (vista) ((IVistaPolicy)policy).SetDefaultEndpoint(id, role);
                    else ((IPolicy)policy).SetDefaultEndpoint(id, role);
                }
            }
            finally { SystemSettings.Release(policy); }
        }

        private static void WithDefaultVolume(Action<IEndpointVolume> action)
        {
            var enumerator = (IEndpointEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(EnumeratorClass, true));
            IEndpoint device = null;
            IEndpointVolume volume = null;
            try
            {
                Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(0, 0, out device));
                Guid id = typeof(IEndpointVolume).GUID;
                volume = (IEndpointVolume)device.Activate(ref id, 23, IntPtr.Zero);
                action(volume);
            }
            finally
            {
                SystemSettings.Release(volume);
                SystemSettings.Release(device);
                SystemSettings.Release(enumerator);
            }
        }

        internal static float Volume()
        {
            return UpdateVolume(null);
        }

        internal static void SetVolume(float level)
        {
            ChangeVolume(current => level);
        }

        internal static float AdjustVolume(int percentagePoints)
        {
            return ChangeVolume(current => OffsetVolume(current, percentagePoints));
        }

        private static float ChangeVolume(Func<float, float> change)
        {
            float previous = 0;
            float level = UpdateVolume(current =>
            {
                previous = current;
                return change(current);
            });
            if (level != previous)
            {
                uint flags = SoundAliasId | SoundAsync | SoundNoDefault;
                if (SystemSettings.WindowsVersion.Major >= 6) flags |= SoundSystem;
                PlaySoundW(new IntPtr(DefaultBeep), IntPtr.Zero, flags);
            }
            return level;
        }

        private static float OffsetVolume(float current, int percentagePoints)
        {
            return Math.Max(0f, Math.Min(1f, current + percentagePoints / 100f));
        }

        private static float UpdateVolume(Func<float, float> change)
        {
            if (SystemSettings.WindowsVersion.Major < 6) return LegacyVolume.ReadOrUpdate(change);
            float level = 0;
            WithDefaultVolume(volume =>
            {
                level = volume.GetMasterVolumeLevelScalar();
                if (change == null) return;
                float target = change(level);
                if (target == level) return;
                volume.SetMasterVolumeLevelScalar(target, IntPtr.Zero);
                level = volume.GetMasterVolumeLevelScalar();
            });
            return level;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey { internal Guid Format; internal uint Property; }

        [StructLayout(LayoutKind.Sequential)]
        private struct Variant
        {
            internal ushort Type, Reserved1, Reserved2, Reserved3;
            internal IntPtr Pointer, Padding;
        }

        [DllImport("ole32.dll")] private static extern int PropVariantClear(ref Variant variant);
        [DllImport("winmm.dll", ExactSpelling = true)]
        private static extern bool PlaySoundW(IntPtr sound, IntPtr module, uint flags);

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IEndpointEnumerator
        {
            IEndpointCollection EnumAudioEndpoints(int direction, uint state);
            [PreserveSig] int GetDefaultAudioEndpoint(int direction, int role, out IEndpoint endpoint);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IEndpointCollection
        {
            int GetCount();
            IEndpoint Item(int index);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IEndpoint
        {
            [return: MarshalAs(UnmanagedType.IUnknown)]
            object Activate(ref Guid id, uint context, IntPtr parameters);
            IPropertyStore OpenPropertyStore(uint access);
            [return: MarshalAs(UnmanagedType.LPWStr)]
            string GetId();
        }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            void GetCount();
            void GetAt();
            Variant GetValue(ref PropertyKey key);
        }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IEndpointVolume
        {
            void RegisterControlChangeNotify();
            void UnregisterControlChangeNotify();
            void GetChannelCount();
            void SetMasterVolumeLevel();
            void SetMasterVolumeLevelScalar(float level, IntPtr eventContext);
            void GetMasterVolumeLevel();
            float GetMasterVolumeLevelScalar();
        }

        // Windows exposes endpoint selection through a private COM interface, with a different layout on Vista.
        [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPolicy
        {
            void GetMixFormat();
            void GetDeviceFormat();
            void ResetDeviceFormat();
            void SetDeviceFormat();
            void GetProcessingPeriod();
            void SetProcessingPeriod();
            void GetShareMode();
            void SetShareMode();
            void GetPropertyValue();
            void SetPropertyValue();
            void SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        }

        [ComImport, Guid("568B9108-44BF-40B4-9006-86AFE5B5A620"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IVistaPolicy
        {
            void GetMixFormat();
            void GetDeviceFormat();
            void SetDeviceFormat();
            void GetProcessingPeriod();
            void SetProcessingPeriod();
            void GetShareMode();
            void SetShareMode();
            void GetPropertyValue();
            void SetPropertyValue();
            void SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        }
    }
}

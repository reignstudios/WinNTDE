using System;
using System.Runtime.InteropServices;

namespace WinNTDE
{
    internal static class LegacyVolume
    {
        private static readonly UIntPtr WaveMapper = new UIntPtr(uint.MaxValue);
        private static readonly uint[] OutputComponents = { 4, 5, 1 }; // Speakers, headphones, digital.
        private const uint PreferredDevice = 0x2015; // DRVM_MAPPER_PREFERRED_GET
        private const uint WaveOutObject = 0x10000000, MixerHandle = 0x80000000;
        private const uint ByComponentType = 3, OneByType = 2;
        private const uint VolumeControl = 0x50030001, Uniform = 1;

        internal static float ReadOrUpdate(Func<float, float> change)
        {
            uint device, flags;
            IntPtr mixer;
            if (waveOutMessage(WaveMapper, PreferredDevice, out device, out flags) == 0 &&
                mixerOpen(out mixer, device, IntPtr.Zero, IntPtr.Zero, WaveOutObject) == 0)
            {
                try
                {
                    MixerControl control;
                    uint channels;
                    if (FindVolumeControl(mixer, out control, out channels))
                        return ReadOrUpdateMixer(mixer, control, channels, change);
                }
                finally { mixerClose(mixer); }
            }

            // Some drivers expose only a waveform output volume.
            float level = ReadWaveVolume();
            if (change == null) return level;
            float target = change(level);
            if (target == level) return level;
            uint channel = (uint)Math.Round(target * 65535);
            SystemSettings.Check(waveOutSetVolume(WaveMapper, channel | (channel << 16)));
            return ReadWaveVolume();
        }

        private static float ReadWaveVolume()
        {
            uint stereo;
            SystemSettings.Check(waveOutGetVolume(WaveMapper, out stereo));
            return ((stereo & 0xFFFF) + (stereo >> 16)) / 131070.0f;
        }

        private static bool FindVolumeControl(IntPtr mixer, out MixerControl control, out uint channels)
        {
            int controlSize = Marshal.SizeOf(typeof(MixerControl));
            IntPtr data = Marshal.AllocHGlobal(controlSize);
            try
            {
                foreach (uint component in OutputComponents)
                {
                    var line = new MixerLine { Size = (uint)Marshal.SizeOf(typeof(MixerLine)), Component = component };
                    if (mixerGetLineInfo(mixer, ref line, MixerHandle | ByComponentType) != 0) continue;
                    var controls = new MixerLineControls { Size = (uint)Marshal.SizeOf(typeof(MixerLineControls)),
                        LineId = line.Id, Type = VolumeControl, Count = 1, ControlSize = (uint)controlSize, Data = data };
                    Marshal.WriteInt32(data, controlSize);
                    if (mixerGetLineControls(mixer, ref controls, MixerHandle | OneByType) != 0) continue;
                    control = (MixerControl)Marshal.PtrToStructure(data, typeof(MixerControl));
                    channels = (control.Flags & Uniform) != 0 ? 1 : line.Channels;
                    if (channels != 0 && control.Maximum > control.Minimum) return true;
                }
            }
            finally { Marshal.FreeHGlobal(data); }
            control = new MixerControl();
            channels = 0;
            return false;
        }

        private static float ReadOrUpdateMixer(IntPtr mixer, MixerControl control, uint channels, Func<float, float> change)
        {
            const int ChannelSize = sizeof(int);
            var details = new MixerControlDetails { Size = (uint)Marshal.SizeOf(typeof(MixerControlDetails)),
                ControlId = control.Id, Channels = channels, DetailSize = ChannelSize,
                Data = Marshal.AllocHGlobal(checked((int)channels * ChannelSize)) };
            try
            {
                uint loudest = ReadLoudestChannel(mixer, ref details);
                float range = control.Maximum - control.Minimum;
                float level = (loudest - control.Minimum) / range;
                if (change == null) return level;
                float target = change(level);
                if (target == level) return level;
                for (int channel = 0; channel < channels; channel++)
                {
                    int offset = channel * ChannelSize;
                    uint value = unchecked((uint)Marshal.ReadInt32(details.Data, offset));
                    // Scale the channels together to keep the current speaker balance.
                    float balance = loudest == control.Minimum ? 1 : (value - control.Minimum) / (loudest - (float)control.Minimum);
                    uint updated = control.Minimum + (uint)Math.Round(target * balance * range);
                    Marshal.WriteInt32(details.Data, offset, unchecked((int)updated));
                }
                SystemSettings.Check(mixerSetControlDetails(mixer, ref details, MixerHandle));
                return (ReadLoudestChannel(mixer, ref details) - control.Minimum) / range;
            }
            finally { Marshal.FreeHGlobal(details.Data); }
        }

        private static uint ReadLoudestChannel(IntPtr mixer, ref MixerControlDetails details)
        {
            SystemSettings.Check(mixerGetControlDetails(mixer, ref details, MixerHandle));
            uint loudest = 0;
            for (int channel = 0; channel < details.Channels; channel++)
                loudest = Math.Max(loudest, unchecked((uint)Marshal.ReadInt32(details.Data, channel * sizeof(int))));
            return loudest;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
        private struct MixerLine
        {
            internal uint Size, Destination, Source, Id, Flags;
            internal UIntPtr User;
            internal uint Component, Channels, Connections, Controls;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)] internal string ShortName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string Name;
            internal uint TargetType, TargetDevice;
            internal ushort Manufacturer, Product;
            internal uint DriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string ProductName;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
        private struct MixerControl
        {
            internal uint Size, Id, Type, Flags, MultipleItems;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)] internal string ShortName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string Name;
            internal uint Minimum, Maximum, BoundsReserved1, BoundsReserved2, BoundsReserved3, BoundsReserved4;
            internal uint Steps, MetricsReserved1, MetricsReserved2, MetricsReserved3, MetricsReserved4, MetricsReserved5;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct MixerLineControls
        {
            internal uint Size, LineId, Type, Count, ControlSize;
            internal IntPtr Data;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct MixerControlDetails
        {
            internal uint Size, ControlId, Channels;
            internal UIntPtr MultipleItems;
            internal uint DetailSize;
            internal IntPtr Data;
        }

        [DllImport("winmm.dll")] private static extern uint waveOutMessage(UIntPtr device, uint message, out uint id, out uint flags);
        [DllImport("winmm.dll")] private static extern uint waveOutGetVolume(UIntPtr device, out uint stereo);
        [DllImport("winmm.dll")] private static extern uint waveOutSetVolume(UIntPtr device, uint stereo);
        [DllImport("winmm.dll")] private static extern uint mixerOpen(out IntPtr mixer, uint id, IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] private static extern uint mixerClose(IntPtr mixer);
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern uint mixerGetLineInfo(IntPtr mixer, ref MixerLine line, uint flags);
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern uint mixerGetLineControls(IntPtr mixer, ref MixerLineControls controls, uint flags);
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern uint mixerGetControlDetails(IntPtr mixer, ref MixerControlDetails details, uint flags);
        [DllImport("winmm.dll")] private static extern uint mixerSetControlDetails(IntPtr mixer, ref MixerControlDetails details, uint flags);
    }
}

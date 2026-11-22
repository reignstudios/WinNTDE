using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WinNTDE
{
    internal sealed class ShellKeyHook : IDisposable
    {
        private const int KeyboardLowLevel = 13;
        private const int LeftWindowsKey = 0x5B, RightWindowsKey = 0x5C;
        private const int VolumeDownKey = 0xAE, VolumeUpKey = 0xAF;
        private const int KeyReleased = 0x80; // LLKHF_UP
        private const byte MenuMaskKey = 0xE8; // Unassigned virtual key.
        private readonly Func<bool> queueStartMenu;
        private readonly Func<int, bool> queueVolumeChange;
        private readonly KeyboardProc callback;
        private IntPtr hook;
        private int heldWindowsKeys;
        private bool standalone;

        internal ShellKeyHook(Func<bool> queueStartMenu, Func<int, bool> queueVolumeChange)
        {
            this.queueStartMenu = queueStartMenu;
            this.queueVolumeChange = queueVolumeChange;
            callback = HandleKey;
            hook = SetWindowsHookEx(KeyboardLowLevel, callback, GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        private IntPtr HandleKey(int code, IntPtr message, IntPtr data)
        {
            if (code == 0) // HC_ACTION
            {
                int key = Marshal.ReadInt32(data); // KBDLLHOOKSTRUCT.vkCode
                if (key == VolumeUpKey || key == VolumeDownKey)
                {
                    standalone = false;
                    // Claim the press so Explorer doesn't also apply its own volume step.
                    if (IsReleased(data) || queueVolumeChange(key == VolumeUpKey ? 5 : -5)) return new IntPtr(1);
                }
                if (heldWindowsKeys != 0 || key == LeftWindowsKey || key == RightWindowsKey)
                {
                    if (IsStandaloneRelease(key, IsReleased(data)) && queueStartMenu())
                    {
                        // Mask Explorer's Start gesture before passing through the real key-up.
                        // Both Windows-key events still reach the system, so shortcuts stay intact.
                        keybd_event(MenuMaskKey, 0, 0, UIntPtr.Zero);
                        keybd_event(MenuMaskKey, 0, 2, UIntPtr.Zero); // KEYEVENTF_KEYUP
                    }
                }
            }
            return CallNextHookEx(hook, code, message, data);
        }

        private static bool IsReleased(IntPtr data)
        {
            return (Marshal.ReadInt32(data, 8) & KeyReleased) != 0; // KBDLLHOOKSTRUCT.flags
        }

        private bool IsStandaloneRelease(int key, bool released)
        {
            int bit = key == LeftWindowsKey ? 1 : key == RightWindowsKey ? 2 : 0;
            if (bit == 0)
            {
                standalone = false;
                return false;
            }

            if (!released)
            {
                if ((heldWindowsKeys & bit) == 0)
                {
                    standalone = heldWindowsKeys == 0 && !IsDown(0x10) && !IsDown(0x11) && !IsDown(0x12); // Shift, Ctrl, Alt
                    heldWindowsKeys |= bit;
                }
                return false;
            }

            bool tapped = heldWindowsKeys == bit && standalone;
            heldWindowsKeys &= ~bit;
            return tapped;
        }

        private static bool IsDown(int key) { return (GetAsyncKeyState(key) & 0x8000) != 0; }

        public void Dispose()
        {
            if (hook == IntPtr.Zero) return;
            UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        }

        private delegate IntPtr KeyboardProc(int code, IntPtr message, IntPtr data);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int id, KeyboardProc procedure, IntPtr module, uint threadId);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scanCode, uint flags, UIntPtr extraInfo);
    }
}

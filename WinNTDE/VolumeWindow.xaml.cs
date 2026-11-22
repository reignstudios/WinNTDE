using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace WinNTDE
{
    internal partial class VolumeWindow : Window
    {
        private const double EdgeMargin = 8;
        private readonly DispatcherTimer dismiss = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };

        internal VolumeWindow()
        {
            InitializeComponent();
            dismiss.Tick += (s, e) => Close();
            Closed += (s, e) => dismiss.Stop();
        }

        internal void ShowLevel(float? level, NativeDesktop.Rect bar, int workTop)
        {
            dismiss.Stop();
            Position(bar, workTop);
            VolumeLabel.Text = level.HasValue ? "Volume" : "Volume unavailable";
            LevelBar.Visibility = level.HasValue ? Visibility.Visible : Visibility.Hidden;
            LevelBar.Value = level.GetValueOrDefault() * 100;
            VolumeValue.Text = level.HasValue ? level.Value.ToString("P0") : string.Empty;
            if (!IsVisible) Show();
            dismiss.Start();
        }

        internal void Position(NativeDesktop.Rect bar, int workTop)
        {
            double scale = NativeDesktop.ChromeScale;
            Left = Math.Max(bar.Left / scale, bar.Right / scale - Width - EdgeMargin);
            Top = Math.Max(workTop / scale, bar.Top / scale - Height - EdgeMargin);
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle).AddHook(HandleMessage);
        }

        private static IntPtr HandleMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message != 0x0021) return IntPtr.Zero; // WM_MOUSEACTIVATE
            handled = true;
            return new IntPtr(4); // MA_NOACTIVATEANDEAT
        }
    }
}

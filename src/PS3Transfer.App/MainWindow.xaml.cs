using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using PS3Transfer.App.Dialogs;
using PS3Transfer.App.ViewModels;

namespace PS3Transfer.App;

public partial class MainWindow : Window
{
    private const int WmDeviceChange = 0x0219;
    private const int WmGetMinMaxInfo = 0x0024;
    private const int DeviceArrival = 0x8000;
    private const int DeviceRemoveComplete = 0x8004;
    private readonly MainViewModel _model;

    public MainWindow()
    {
        InitializeComponent();
        _model = new MainViewModel(new WindowDialogs(this), Dispatcher);
        DataContext = _model;
        _model.CloseRequested += () => Dispatcher.BeginInvoke(new Action(Close));
        StateChanged += (_, _) => MaxGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        SquareCorners(hwnd);
        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(WndProc);
    }

    private static void SquareCorners(IntPtr hwnd)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            return;
        int preference = 1;
        _ = DwmSetWindowAttribute(hwnd, 33, ref preference, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_model.Transferring || _model.Joining)
        {
            e.Cancel = true;
            _model.RequestClose();
            return;
        }

        _model.SaveSettings();
        base.OnClosing(e);
    }

    private void Minimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void ToggleMax(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseWindow(object sender, RoutedEventArgs e) => Close();

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && _model.CanEdit
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (_model.CanEdit && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            _ = _model.AddPathsAsync(paths);
    }

    private void QueueKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && _model.CanEdit)
        {
            _model.RemoveCommand.Execute(null);
            e.Handled = true;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmDeviceChange)
        {
            var ev = wParam.ToInt64();
            if (ev is DeviceArrival or DeviceRemoveComplete)
                _model.ScheduleDriveRefresh();
        }
        else if (msg == WmGetMinMaxInfo)
        {
            FitWorkArea(hwnd, lParam);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private static void FitWorkArea(IntPtr hwnd, IntPtr lParam)
    {
        var monitor = MonitorFromWindow(hwnd, 2);
        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
            return;
        var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var work = info.rcWork;
        var bounds = info.rcMonitor;
        mmi.ptMaxPosition.x = work.Left - bounds.Left;
        mmi.ptMaxPosition.y = work.Top - bounds.Top;
        mmi.ptMaxSize.x = work.Right - work.Left;
        mmi.ptMaxSize.y = work.Bottom - work.Top;
        Marshal.StructureToPtr(mmi, lParam, true);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct PointWin
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public PointWin ptReserved;
        public PointWin ptMaxSize;
        public PointWin ptMaxPosition;
        public PointWin ptMinTrackSize;
        public PointWin ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectWin
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int cbSize;
        public RectWin rcMonitor;
        public RectWin rcWork;
        public int dwFlags;
    }
}

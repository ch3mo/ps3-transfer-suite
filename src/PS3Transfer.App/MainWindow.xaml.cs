using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
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
    private bool _fitting;
    private bool _placedFromSettings;

    public MainWindow()
    {
        InitializeComponent();
        _model = new MainViewModel(new WindowDialogs(this), Dispatcher);
        DataContext = _model;
        _model.CloseRequested += () => Dispatcher.BeginInvoke(new Action(Close));
        _model.Drives.CollectionChanged += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(FitDrives));
        StateChanged += (_, _) =>
        {
            MaxGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
            if (WindowState == WindowState.Normal)
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(FitDrives));
        };
        ContentRendered += (_, _) => FitDrives();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyPlacement();
        var hwnd = new WindowInteropHelper(this).Handle;
        SquareCorners(hwnd);
        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(WndProc);
    }

    private void ApplyPlacement()
    {
        if (!_model.TryReadPlacement(out var left, out var top, out var width, out var height, out var maximized))
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        var work = WorkAreaDips();
        width = Math.Clamp(width, MinWidth, Math.Max(MinWidth, work.Width));
        height = Math.Clamp(height, MinHeight, Math.Max(MinHeight, work.Height));
        if (!TitleOnScreen(left, top, width))
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Width = width;
            Height = height;
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left;
        Top = top;
        Width = width;
        Height = height;
        _placedFromSettings = true;
        if (maximized)
            WindowState = WindowState.Maximized;
    }

    private bool TitleOnScreen(double left, double top, double width)
    {
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is null)
            return true;
        var device = source.CompositionTarget.TransformToDevice;
        var point = device.Transform(new Point(left + Math.Min(width, 160) / 2, top + 24));
        var probe = new PointWin { x = (int)Math.Round(point.X), y = (int)Math.Round(point.Y) };
        return MonitorFromPoint(probe, 0) != IntPtr.Zero;
    }

    private void FitDrives()
    {
        if (_fitting || WindowState != WindowState.Normal || DriveList.Items.Count == 0)
            return;
        var work = WorkAreaDips();
        if (work.Width < MinWidth)
            return;

        const double card = 260;
        const double row = 92;
        var count = DriveList.Items.Count;
        var targetWidth = Math.Min(work.Width, Math.Max(ActualWidth, count * card + 48));
        var inner = Math.Max(card, targetWidth - 48);
        var perRow = Math.Max(1, (int)Math.Floor(inner / card));
        var rows = (int)Math.Ceiling(count / (double)perRow);
        var driveHeight = Math.Min(rows * row, Math.Max(row, work.Height - 520));
        DriveScroll.MaxHeight = driveHeight;
        var visibleRows = Math.Max(1, (int)Math.Round(driveHeight / row));
        var targetHeight = ActualHeight;
        if (visibleRows > 1)
            targetHeight = Math.Max(ActualHeight, Math.Min(work.Height, 760 + (visibleRows - 1) * row));
        if (targetWidth <= ActualWidth + 1 && targetHeight <= ActualHeight + 1)
            return;

        _fitting = true;
        try
        {
            if (targetWidth > ActualWidth + 1)
                Width = targetWidth;
            if (targetHeight > ActualHeight + 1)
                Height = targetHeight;
            if (Top + Height > work.Top + work.Height)
                Top = Math.Max(work.Top, work.Top + work.Height - Height);
            if (Left + Width > work.Left + work.Width)
                Left = Math.Max(work.Left, work.Left + work.Width - Width);
            if (!_placedFromSettings)
            {
                Left = work.Left + Math.Max(0, (work.Width - Width) / 2);
                Top = work.Top + Math.Max(0, (work.Height - Height) / 2);
            }
        }
        finally
        {
            _fitting = false;
        }
    }

    private Rect WorkAreaDips()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var monitor = hwnd == IntPtr.Zero ? MonitorFromPoint(new PointWin(), 1) : MonitorFromWindow(hwnd, 2);
        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
            return SystemParameters.WorkArea;
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is null)
        {
            return new Rect(
                info.rcWork.Left,
                info.rcWork.Top,
                info.rcWork.Right - info.rcWork.Left,
                info.rcWork.Bottom - info.rcWork.Top);
        }

        var toDip = source.CompositionTarget.TransformFromDevice;
        var origin = toDip.Transform(new Point(info.rcWork.Left, info.rcWork.Top));
        var extent = toDip.Transform(new Point(info.rcWork.Right, info.rcWork.Bottom));
        return new Rect(origin, extent);
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

        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _model.WritePlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height, WindowState == WindowState.Maximized);
        _model.SaveSettings();
        base.OnClosing(e);
    }

    private void Minimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void ToggleMax(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseWindow(object sender, RoutedEventArgs e) => Close();

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            _ = _model.AddPathsAsync(paths);
    }

    private void QueueSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListBox list)
            return;
        var items = list.SelectedItems.OfType<QueueItemViewModel>().ToList();
        var current = e.AddedItems.OfType<QueueItemViewModel>().LastOrDefault() ?? items.LastOrDefault();
        _model.SetQueueSelection(items, current);
    }

    private void LibrarySelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListBox list)
            return;
        _model.SetLibrarySelection(list.SelectedItems.OfType<LibraryRowViewModel>().ToList());
    }

    private void QueueKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && _model.RemoveCommand.CanExecute(null))
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

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(PointWin point, uint flags);

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

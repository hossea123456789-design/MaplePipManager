using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace CropPipViewer;

public partial class BurstMonitorWindow : Window
{
    private bool _isDragging;
    private Point _dragStartMouse;
    private Point _dragStartWindow;
    private bool _forceClosing;
    private bool _clickThrough;

    public event Action<BurstMonitorWindow>? MonitorMoved;

    public BurstMonitorWindow()
    {
        InitializeComponent();
    }

    public void ForceClose()
    {
        _forceClosing = true;
        Close();
    }

    public void ForceTopMost()
    {
        if (!IsVisible) return;
        Topmost = false;
        Topmost = true;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE |
                NativeMethods.SWP_NOSIZE |
                NativeMethods.SWP_NOACTIVATE |
                NativeMethods.SWP_SHOWWINDOW);
        }
    }

    public void SetFooter(string text)
    {
        // v29a: footer text removed from the monitor PiP to keep the combat overlay compact.
    }

    public void SetMonitorOpacity(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) value = 0.88;
        Opacity = Math.Round(Math.Clamp(value, 0.10, 1.0), 2);
    }


    public void SetBackgroundOpacity(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) value = 0.18;
        value = Math.Clamp(value, 0.0, 1.0);
        var alpha = (byte)Math.Round(value * 255.0);
        RootBorder.Background = new SolidColorBrush(Color.FromArgb(alpha, 0x10, 0x18, 0x20));
    }

    public void SetClickThrough(bool enabled)
    {
        _clickThrough = enabled;
        RootBorder.BorderThickness = enabled ? new Thickness(0) : new Thickness(1);
        RootBorder.BorderBrush = enabled ? Brushes.Transparent : new SolidColorBrush(Color.FromArgb(0x66, 0xD9, 0xFF, 0xFF));
        RootBorder.Cursor = enabled ? Cursors.Arrow : Cursors.SizeAll;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        exStyle |= NativeMethods.WS_EX_TOOLWINDOW;
        if (enabled) exStyle |= NativeMethods.WS_EX_TRANSPARENT;
        else exStyle &= ~NativeMethods.WS_EX_TRANSPARENT;
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, exStyle);
    }

    public void UpdateRole(BurstRole role, string timeText, string stateText, BurstMonitorVisualKind visualKind)
    {
        var (time, state, border) = role switch
        {
            BurstRole.SemiBurst => (SemiTimeText, SemiStateText, SemiStateBorder),
            BurstRole.Burst => (BurstTimeText, BurstStateText, BurstStateBorder),
            BurstRole.OriginBurst => (OriginTimeText, OriginStateText, OriginStateBorder),
            _ => (SemiTimeText, SemiStateText, SemiStateBorder)
        };

        time.Text = timeText;
        state.Text = stateText;
        border.Background = new SolidColorBrush(GetBackground(visualKind));
        border.BorderBrush = new SolidColorBrush(GetBackground(visualKind));
    }

    private static Color GetBackground(BurstMonitorVisualKind kind) => kind switch
    {
        BurstMonitorVisualKind.Ready => Color.FromRgb(35, 125, 72),
        BurstMonitorVisualKind.Cooling => Color.FromRgb(70, 82, 99),
        BurstMonitorVisualKind.Imminent => DateTime.UtcNow.Millisecond < 500 ? Color.FromRgb(235, 145, 32) : Color.FromRgb(255, 205, 60),
        BurstMonitorVisualKind.Unstable => Color.FromRgb(145, 80, 150),
        _ => Color.FromRgb(69, 83, 99)
    };

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_clickThrough) return;
        _isDragging = true;
        _dragStartMouse = PointToScreen(e.GetPosition(this));
        _dragStartWindow = new Point(Left, Top);
        Mouse.Capture(RootBorder, CaptureMode.Element);
        e.Handled = true;
    }

    private void Root_MouseMove(object sender, MouseEventArgs e)
    {
        if (_clickThrough) return;
        if (!_isDragging || e.LeftButton != MouseButtonState.Pressed) return;
        var current = PointToScreen(e.GetPosition(this));
        var dx = current.X - _dragStartMouse.X;
        var dy = current.Y - _dragStartMouse.Y;
        Left = Math.Round(_dragStartWindow.X + dx, 2);
        Top = Math.Round(_dragStartWindow.Y + dy, 2);
        MonitorMoved?.Invoke(this);
        e.Handled = true;
    }

    private void Root_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_clickThrough) return;
        if (!_isDragging) return;
        _isDragging = false;
        if (Mouse.Captured == RootBorder) Mouse.Capture(null);
        MonitorMoved?.Invoke(this);
        e.Handled = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!_forceClosing) MonitorMoved?.Invoke(this);
    }
}

public enum BurstMonitorVisualKind
{
    Idle,
    Ready,
    Cooling,
    Imminent,
    Unstable
}

using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace CropPipViewer;

public partial class PipVisibilityPillWindow : Window
{
    public const double FixedWidth = 76;
    public const double FixedHeight = 20;

    private bool _isDragging;
    private bool _suppressToggle;
    private Point _dragStartMouse;
    private Point _dragStartWindow;
    private bool _forceClosing;

    public event Action? ToggleRequested;
    public event Action<PipVisibilityPillWindow>? PillMoved;

    public PipVisibilityPillWindow()
    {
        InitializeComponent();
        EnforceFixedSize();
        Loaded += (_, _) => EnforceFixedSize();
        UpdateTooltip();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        EnforceFixedSize();
    }

    public void EnforceFixedSize()
    {
        MinWidth = FixedWidth;
        MaxWidth = FixedWidth;
        Width = FixedWidth;
        MinHeight = FixedHeight;
        MaxHeight = FixedHeight;
        Height = FixedHeight;
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

    public void SetPipsHidden(bool hidden)
    {
        StateText.Text = hidden ? "PIP OFF" : "PIP ON";
        PillBorder.Background = new SolidColorBrush(hidden
            ? Color.FromRgb(112, 47, 47)
            : Color.FromRgb(47, 111, 84));
        PillBorder.BorderBrush = new SolidColorBrush(hidden
            ? Color.FromRgb(82, 32, 32)
            : Color.FromRgb(24, 74, 53));
        StatusDot.Fill = new SolidColorBrush(hidden
            ? Color.FromRgb(255, 119, 119)
            : Color.FromRgb(125, 255, 192));
        UpdateTooltip();
    }

    private void UpdateTooltip()
    {
        ToolTip = "클릭: PIP 전체 표시/숨김\n드래그: 알약 위치 이동";
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!_forceClosing)
        {
            PillMoved?.Invoke(this);
        }
    }

    private void Pill_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDragging = true;
        _suppressToggle = false;
        _dragStartMouse = PointToScreen(e.GetPosition(this));
        _dragStartWindow = new Point(Left, Top);
        Mouse.Capture(PillBorder, CaptureMode.Element);
        e.Handled = true;
    }

    private void Pill_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging || e.LeftButton != MouseButtonState.Pressed) return;

        var current = PointToScreen(e.GetPosition(this));
        var dx = current.X - _dragStartMouse.X;
        var dy = current.Y - _dragStartMouse.Y;
        if (Math.Abs(dx) > 2 || Math.Abs(dy) > 2) _suppressToggle = true;

        Left = Math.Round(_dragStartWindow.X + dx, 2);
        Top = Math.Round(_dragStartWindow.Y + dy, 2);
        PillMoved?.Invoke(this);
        e.Handled = true;
    }

    private void Pill_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        if (Mouse.Captured == PillBorder) Mouse.Capture(null);

        if (_suppressToggle)
        {
            PillMoved?.Invoke(this);
        }
        else
        {
            ToggleRequested?.Invoke();
        }

        e.Handled = true;
    }
}

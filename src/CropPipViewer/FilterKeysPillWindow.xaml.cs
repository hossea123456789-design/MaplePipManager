using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace CropPipViewer;

public partial class FilterKeysPillWindow : Window
{
    private bool _isDragging;
    private bool _suppressToggle;
    private Point _dragStartMouse;
    private Point _dragStartWindow;
    private bool _forceClosing;
    private bool _dragLocked;

    public bool DragLocked
    {
        get => _dragLocked;
        set
        {
            _dragLocked = value;
            UpdateCursorAndTooltipHint();
        }
    }

    public event Action? ToggleRequested;
    public event Action<FilterKeysPillWindow>? PillMoved;

    public FilterKeysPillWindow()
    {
        InitializeComponent();
        UpdateCursorAndTooltipHint();
    }

    private void UpdateCursorAndTooltipHint()
    {
        if (PillBorder != null) PillBorder.Cursor = _dragLocked ? Cursors.Hand : Cursors.SizeAll;
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

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!_forceClosing)
        {
            // Window chrome is hidden, but keep this hook safe for Alt+F4/system close cases.
            PillMoved?.Invoke(this);
        }
    }

    public void SetState(string badgeText, string detailText, Color background, Color border, Color dot)
    {
        StateText.Text = badgeText switch
        {
            "ON" => "ON",
            "대기" => "대기",
            "오류" => "오류",
            _ => "OFF"
        };
        ToolTip = detailText + (_dragLocked
            ? "\n클릭: 필터키 사용 ON/OFF\n위치 잠금: 드래그 이동 비활성"
            : "\n클릭: 필터키 사용 ON/OFF\n드래그: 버튼 위치 이동");
        PillBorder.Background = new SolidColorBrush(background);
        PillBorder.BorderBrush = new SolidColorBrush(border);
        StatusDot.Fill = new SolidColorBrush(dot);
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
        if (_dragLocked)
        {
            e.Handled = true;
            return;
        }

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

        if (_dragLocked)
        {
            ToggleRequested?.Invoke();
        }
        else if (_suppressToggle)
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

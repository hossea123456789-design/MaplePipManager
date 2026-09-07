using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace CropPipViewer;

public partial class OverlayWindow : Window
{
    private readonly ObservableCollection<CropItem> _crops;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _timer = new();
    private readonly DispatcherTimer _topmostTimer = new();
    private bool _captureRefreshQueued;
    private bool _captureRefreshRunning;
    private int _frameEventDispatchQueued;
    private DateTime _lastCaptureRefreshUtc = DateTime.MinValue;
    private DateTime _lastPerfWindowUtc = DateTime.UtcNow;
    private int _perfFrameCount;
    private double _perfTotalMs;
    private int _lastMeasuredFps;
    private double _lastMeasuredFrameMs;
    private DateTime _lastTopmostReapplyUtc = DateTime.MinValue;
    private readonly DispatcherTimer _rotateTimer = new();
    private readonly Dictionary<string, Image> _images = new();
    private readonly Dictionary<string, Grid> _containers = new();
    private readonly Dictionary<string, Border> _contentBorders = new();
    private readonly Dictionary<string, Shape> _cropBorderShapes = new();
    private readonly Dictionary<string, Button> _rotateRightButtons = new();
    private readonly Dictionary<string, Button> _rotateLeftButtons = new();
    private readonly HashSet<string> _selectedCropIds = new();
    private Point? _dragStart;
    private CropItem? _dragCrop;
    private readonly List<CropItem> _dragCrops = new();
    private readonly Dictionary<string, DragStartState> _dragStartStates = new();
    private bool _resizeDrag;
    private bool _contextMenuOpen;
    private HwndSource? _source;

    private const double ResizeHitTestThickness = 8.0;
    private const int WmNcHitTest = 0x0084;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;

    private List<CropItem> _rotatingCrops = new();
    private int _rotateDirection = 1;

    private WgcCaptureManager? _capture;
    private IntPtr _targetHwnd;
    private bool _wasMinimized;
    private DateTime _restoreHoldUntilUtc = DateTime.MinValue;
    private DateTime _lastRestoreLogUtc = DateTime.MinValue;

    private bool _resizeItemsWithWindow;
    private bool _isLoadedForResize;
    private bool _isApplyingWindowScale;
    private double _lastCanvasWidth;
    private double _lastCanvasHeight;
    private DateTime _lastResizeSaveUtc = DateTime.MinValue;
    private DateTime _lastSettingsSaveUtc = DateTime.MinValue;
    private bool _allowClose;
    private readonly string? _detachedGroupId;
    private readonly bool _persistWindowBounds;
    private bool _pipSelectedRuntime;
    private bool _pipHoverRuntime;
    private Point? _pipDragStartScreenDip;
    private Point? _pipMouseDownScreenDip;
    private bool _pipMouseDownWasSelected;
    private bool _pipDragMoved;
    private double _pipBackgroundOpacity;
    private double _cropOpacity = 0.92;
    private bool _refreshQueued;
    private double? _targetLockedOffsetX;
    private double? _targetLockedOffsetY;
    private int _targetLockBaselineWidth;
    private int _targetLockBaselineHeight;
    private bool _isApplyingTargetLockedPosition;

    public event Action<IReadOnlyList<CropItem>>? DetachRequested;
    public event Action<IReadOnlyList<CropItem>>? ReattachRequested;
    public event Action<OverlayWindow, bool>? PipSelectionRequested;
    public event Action<OverlayWindow, Point, bool>? PipSelectionCycleRequested;
    public event Action<OverlayWindow>? MergeSelectedPipsRequested;
    public event Action? PipSelectionClearRequested;
    public event Action? HistoryCheckpointRequested;
    public event Action? UndoRequested;
    public event Action? RedoRequested;
    public event Action<OverlayWindow, double, double>? PipMoveRequested;
    public event Action<OverlayWindow, bool>? PipStackAlignRequested;
    public event Action<OverlayWindow>? PipBoundsChanged;

    public string? DetachedGroupId => _detachedGroupId;
    public bool IsDetachedOverlay => !string.IsNullOrWhiteSpace(_detachedGroupId);
    public bool IsPipSelectedRuntime => _pipSelectedRuntime;
    public double? TargetLockedOffsetX => _targetLockedOffsetX;
    public double? TargetLockedOffsetY => _targetLockedOffsetY;
    public int TargetLockBaselineWidth => _targetLockBaselineWidth;
    public int TargetLockBaselineHeight => _targetLockBaselineHeight;

    // Normal rotation is intentionally faster so press-and-hold feels practical.
    // Hold Ctrl while pressing the rotate button for the old fine-control speed.
    private const double HoldRotateStepDegrees = 2.5;
    private const double ClickRotateStepDegrees = 5.0;
    private const double FineHoldRotateStepDegrees = 0.75;
    private const double FineClickRotateStepDegrees = 1.0;

    public OverlayWindow(ObservableCollection<CropItem> crops, AppSettings settings, string? detachedGroupId = null, bool persistWindowBounds = true)
    {
        InitializeComponent();
        _crops = crops;
        _settings = settings;
        _detachedGroupId = detachedGroupId;
        _persistWindowBounds = persistWindowBounds;
        _resizeItemsWithWindow = settings.ResizeItemsWithWindow;
        _pipBackgroundOpacity = GetInitialBackgroundOpacity();
        ConfigureCaptureRefreshTimer();
        _timer.Tick += CaptureTick;
        _timer.Start();

        // Selection highlight should disappear almost immediately when MapleStory
        // or another non-app window takes focus, while TopMost reapplication can stay throttled.
        _topmostTimer.Interval = TimeSpan.FromMilliseconds(150);
        _topmostTimer.Tick += (_, _) =>
        {
            if ((DateTime.UtcNow - _lastTopmostReapplyUtc).TotalMilliseconds >= 1000)
            {
                _lastTopmostReapplyUtc = DateTime.UtcNow;
                ReapplyTopMost();
            }
            ClearSelectionIfForegroundMovedOutsideApp();
        };
        _topmostTimer.Start();

        _rotateTimer.Interval = TimeSpan.FromMilliseconds(60);
        _rotateTimer.Tick += (_, _) =>
        {
            if (_rotatingCrops.Count > 0)
            {
                foreach (var crop in _rotatingCrops.ToList())
                {
                    RotateCrop(crop, _rotateDirection * GetRotateStepDegrees(isHold: true), save: false);
                }
            }
        };

        _crops.CollectionChanged += Crops_CollectionChanged;
        foreach (var crop in _crops) crop.PropertyChanged += Crop_PropertyChanged;

        Loaded += (_, _) =>
        {
            RefreshItems();
            SetPipBackgroundOpacity(_pipBackgroundOpacity, save: false);
            SetCropOpacity(GetInitialCropOpacity());
            SetClickThrough(_settings.ClickThrough);
            ReapplyTopMost();
            _lastCanvasWidth = Math.Max(1, CanvasHost.ActualWidth);
            _lastCanvasHeight = Math.Max(1, CanvasHost.ActualHeight);
            _isLoadedForResize = true;
            FocusForKeyboard();
            InstallResizeHitTestHook();
            UpdatePerformanceStatsVisibility();
        };
        RootBorder.MouseEnter += (_, _) =>
        {
            if (_settings.ClickThrough) return;
            _pipHoverRuntime = true;
            ApplyPipFrameVisual();
        };
        RootBorder.MouseLeave += (_, _) =>
        {
            _pipHoverRuntime = false;
            ApplyPipFrameVisual();
        };
        SizeChanged += OverlayWindow_SizeChanged;
        LocationChanged += (_, _) => OnOverlayLocationChanged();
        Closing += (_, e) =>
        {
            if (_allowClose)
            {
                StopTimersAndUnsubscribe();
                return;
            }

            e.Cancel = true;
            if (IsDetachedOverlay)
            {
                var cropsInPip = GetVisibleCrops().ToList();
                if (cropsInPip.Count > 0)
                {
                    ReattachRequested?.Invoke(cropsInPip);
                    return;
                }
            }

            Hide();
        };
        Deactivated += (_, _) =>
        {
            if (_settings.ClickThrough) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_contextMenuOpen || _dragCrop != null || _rotateTimer.IsEnabled) return;
                if (IsForegroundInsideThisApp()) return;
                ClearSelection();
            }), DispatcherPriority.Background);
        };
    }

    public void ForceClose()
    {
        _allowClose = true;
        try
        {
            StopTimersAndUnsubscribe();
            Close();
        }
        catch
        {
            // ignore shutdown race
        }
    }

    private void StopTimersAndUnsubscribe()
    {
        _timer.Stop();
        _topmostTimer.Stop();
        _rotateTimer.Stop();
        if (_capture != null)
        {
            _capture.FrameUpdated -= Capture_FrameUpdated;
        }
        if (_source != null)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }
        _crops.CollectionChanged -= Crops_CollectionChanged;
        foreach (var crop in _crops) crop.PropertyChanged -= Crop_PropertyChanged;
    }

    private void Crops_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (CropItem crop in e.OldItems) crop.PropertyChanged -= Crop_PropertyChanged;
        }
        if (e.NewItems != null)
        {
            foreach (CropItem crop in e.NewItems) crop.PropertyChanged += Crop_PropertyChanged;
        }
        QueueRefreshItems();
    }

    private void Crop_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not CropItem crop) return;

        if (e.PropertyName is nameof(CropItem.Enabled) or nameof(CropItem.Shape) or nameof(CropItem.ShapeName) or nameof(CropItem.DetachedGroupIdRuntime) or nameof(CropItem.IsDetachedRuntime))
        {
            QueueRefreshItems();
            return;
        }

        if (e.PropertyName is nameof(CropItem.X) or nameof(CropItem.Y) or nameof(CropItem.Width) or nameof(CropItem.Height) or nameof(CropItem.Name))
        {
            QueueRefreshItems();
            return;
        }

        if (_containers.TryGetValue(crop.Id, out var host))
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_containers.TryGetValue(crop.Id, out var currentHost)) LayoutCrop(crop, currentHost);
            }), DispatcherPriority.Background);
        }
    }

    private void QueueRefreshItems()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _refreshQueued = false;
            RefreshItems();
        }), DispatcherPriority.Background);
    }

    public void SetCapture(WgcCaptureManager capture, IntPtr targetHwnd)
    {
        if (!ReferenceEquals(_capture, capture) && _capture != null)
        {
            _capture.FrameUpdated -= Capture_FrameUpdated;
        }

        _capture = capture;
        _capture.FrameUpdated -= Capture_FrameUpdated;
        _capture.FrameUpdated += Capture_FrameUpdated;
        _targetHwnd = targetHwnd;
        SettingsService.Log($"overlay_capture_set | WGC | hwnd={targetHwnd} | size={capture.SourceWidth}x{capture.SourceHeight}");
    }

    public void SetCaptureRefreshOptions(int intervalMs, bool useFrameArrivedRefresh, bool showPerformanceStats)
    {
        _settings.CaptureIntervalMs = ClampCaptureInterval(intervalMs);
        _settings.UseFrameArrivedRefresh = useFrameArrivedRefresh;
        _settings.ShowCapturePerformanceStats = showPerformanceStats;
        ConfigureCaptureRefreshTimer();
        UpdatePerformanceStatsVisibility();
    }

    private static int ClampCaptureInterval(int intervalMs) => Math.Clamp(intervalMs, 16, 1000);

    private void ConfigureCaptureRefreshTimer()
    {
        var interval = ClampCaptureInterval(_settings.CaptureIntervalMs);
        // When frame-arrived mode is active, WGC events drive fast updates and the timer is only a fallback.
        var timerMs = _settings.UseFrameArrivedRefresh ? Math.Max(100, interval * 4) : interval;
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(timerMs, 16, 1000));
    }

    private void Capture_FrameUpdated(object? sender, EventArgs e)
    {
        if (!_settings.UseFrameArrivedRefresh) return;
        if (Interlocked.Exchange(ref _frameEventDispatchQueued, 1) == 1) return;
        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Interlocked.Exchange(ref _frameEventDispatchQueued, 0);
                RequestCaptureRefresh();
            }), DispatcherPriority.Render);
        }
        catch
        {
            Interlocked.Exchange(ref _frameEventDispatchQueued, 0);
            // Overlay may be closing while WGC still raises a final frame event.
        }
    }

    public void SetStatus(string text)
    {
        // PiP intentionally has no visible header. Keep this method for compatibility/logs.
        if (!string.IsNullOrWhiteSpace(text)) SettingsService.Log("overlay_status | " + text);
    }

    private void FocusForKeyboard()
    {
        if (_settings.ClickThrough) return;
        try
        {
            Activate();
            Focus();
            Keyboard.Focus(this);
        }
        catch
        {
            // Ignore focus races while overlays are being shown/hidden.
        }
    }

    public void SetResizeItemsWithWindow(bool enabled)
    {
        _resizeItemsWithWindow = enabled;
        _settings.ResizeItemsWithWindow = enabled;
        _lastCanvasWidth = Math.Max(1, CanvasHost.ActualWidth);
        _lastCanvasHeight = Math.Max(1, CanvasHost.ActualHeight);
        SettingsService.Save(_settings);
    }

    public void SetPipSelected(bool selected)
    {
        _pipSelectedRuntime = selected;
        ApplyPipFrameVisual();
    }

    public void BringToFrontForSelection()
    {
        if (!IsVisible) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (_settings.TopMost)
                {
                    Topmost = false;
                    Topmost = true;
                }
                Activate();
                FocusForKeyboard();
            }
            catch
            {
                // Ignore activation races while windows are being rearranged.
            }
        }), DispatcherPriority.Send);
    }

    public void SetPipBackgroundOpacity(double value, bool save = true)
    {
        _pipBackgroundOpacity = Math.Round(Math.Clamp(value, 0.0, 1.0), 2);
        var alpha = (byte)Math.Round(_pipBackgroundOpacity * 255);
        RootBorder.Background = new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0));

        // Native resize grip is intentionally not used.
        // 8-way resizing is handled by WM_NCHITTEST so the frame can be fully transparent
        // while edge/corner resizing still works.
        ResizeMode = ResizeMode.CanResize;

        ApplyPipFrameVisual();
        PipBoundsChanged?.Invoke(this);
        if (save) SettingsService.Save(_settings);
    }

    public double GetPipBackgroundOpacity() => _pipBackgroundOpacity;

    public void SetCropOpacity(double value)
    {
        _cropOpacity = Math.Round(Math.Clamp(value, 0.05, 1.0), 2);
        foreach (var border in _contentBorders.Values)
        {
            border.Opacity = _cropOpacity;
        }
    }

    public double GetCropOpacity() => _cropOpacity;

    private double GetInitialCropOpacity()
    {
        var key = GetPipKeyForSettings();
        if (_settings.PipCropOpacityByKey != null && _settings.PipCropOpacityByKey.TryGetValue(key, out var saved))
        {
            return Math.Round(Math.Clamp(saved, 0.05, 1.0), 2);
        }
        return Math.Round(Math.Clamp(_settings.Opacity, 0.05, 1.0), 2);
    }

    private double GetInitialBackgroundOpacity()
    {
        var key = GetPipKeyForSettings();
        if (_settings.PipBackgroundOpacityByKey.TryGetValue(key, out var saved))
        {
            return Math.Round(Math.Clamp(saved, 0.0, 1.0), 2);
        }
        return Math.Round(Math.Clamp(_settings.PipBackgroundOpacity, 0.0, 1.0), 2);
    }

    private string GetPipKeyForSettings() => IsDetachedOverlay ? _detachedGroupId! : "__main__";

    private void ApplyPipFrameVisual()
    {
        // Background opacity affects only the neutral PiP frame/background layer.
        // Selection and hover indicators must stay visible for UX recognition unless click-through is enabled.
        if (_pipSelectedRuntime && !_settings.ClickThrough)
        {
            RootBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(230, 70, 255, 150));
            RootBorder.BorderThickness = new Thickness(2.0);
        }
        else if (_pipHoverRuntime && !_settings.ClickThrough)
        {
            RootBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(135, 105, 255, 175));
            RootBorder.BorderThickness = new Thickness(1.2);
        }
        else
        {
            var neutralAlpha = (byte)Math.Round(51 * Math.Clamp(_pipBackgroundOpacity, 0.0, 1.0));
            RootBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(neutralAlpha, 255, 255, 255));
            RootBorder.BorderThickness = new Thickness(_pipBackgroundOpacity <= 0.001 ? 0 : 0.6);
        }
    }

    public bool ContainsGroup(string groupId)
    {
        return IsDetachedOverlay && string.Equals(_detachedGroupId, groupId, StringComparison.Ordinal);
    }

    public bool HasVisibleCrops()
    {
        return GetVisibleCrops().Any();
    }

    public void RefreshItems()
    {
        CanvasHost.Children.Clear();
        _images.Clear();
        _containers.Clear();
        _contentBorders.Clear();
        _cropBorderShapes.Clear();
        _rotateRightButtons.Clear();
        _rotateLeftButtons.Clear();

        var visible = GetVisibleCrops().ToList();
        _selectedCropIds.RemoveWhere(id => !visible.Any(c => c.Id == id && c.Enabled));

        foreach (var crop in visible)
        {
            var img = new Image { Stretch = Stretch.Fill, SnapsToDevicePixels = true };
            var contentBorder = new Border
            {
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Child = img,
                Opacity = _cropOpacity,
                RenderTransformOrigin = new Point(0.5, 0.5),
                ToolTip = $"{crop.Name}\n첫 클릭: 선택만 / 선택 후 드래그: PiP 안 위치 이동\nShift+클릭: 다중 선택\n방향키: 선택 크롭을 PiP 안에서 이동\nShift+방향키: 10px 이동 / Ctrl+방향키: 0.25px 이동\nESC: 선택 해제\nCtrl+드래그: 표시 크기 조절\nCtrl+휠: 표시 크기 조절 3%\nCtrl+Shift+휠: 초미세 조절 1%\n우클릭: 선택 크롭 분리/합치기\n다중 선택 후 ↺/↻ 버튼: 선택 크롭 전체 좌/우 회전\n버튼 길게 누름: 계속 회전"
            };

            var cropBorderShape = BuildCropBorderShape(crop);

            var rotateLeftButton = BuildRotateButton("↺", HorizontalAlignment.Left, new Thickness(-18, -18, 0, 0), "클릭: 왼쪽 5° 회전\n누르고 유지: 빠르게 계속 회전\nCtrl+클릭/유지: 기존처럼 1° 단위로 세밀 회전");
            var rotateRightButton = BuildRotateButton("↻", HorizontalAlignment.Right, new Thickness(0, -18, -18, 0), "클릭: 오른쪽 5° 회전\n누르고 유지: 빠르게 계속 회전\nCtrl+클릭/유지: 기존처럼 1° 단위로 세밀 회전");

            rotateLeftButton.PreviewMouseLeftButtonDown += (s, e) => BeginRotate(crop, rotateLeftButton, -1, e);
            rotateRightButton.PreviewMouseLeftButtonDown += (s, e) => BeginRotate(crop, rotateRightButton, 1, e);
            rotateLeftButton.PreviewMouseLeftButtonUp += (s, e) => EndRotate(e);
            rotateRightButton.PreviewMouseLeftButtonUp += (s, e) => EndRotate(e);
            rotateLeftButton.MouseLeave += (_, _) => { if (_rotatingCrops.Contains(crop) && Mouse.LeftButton != MouseButtonState.Pressed) StopRotate(save: true); };
            rotateRightButton.MouseLeave += (_, _) => { if (_rotatingCrops.Contains(crop) && Mouse.LeftButton != MouseButtonState.Pressed) StopRotate(save: true); };
            rotateLeftButton.LostMouseCapture += (_, _) => { if (_rotatingCrops.Contains(crop) && Mouse.LeftButton != MouseButtonState.Pressed) StopRotate(save: true); };
            rotateRightButton.LostMouseCapture += (_, _) => { if (_rotatingCrops.Contains(crop) && Mouse.LeftButton != MouseButtonState.Pressed) StopRotate(save: true); };

            var host = new Grid
            {
                Background = Brushes.Transparent,
                ToolTip = contentBorder.ToolTip
            };
            host.Children.Add(contentBorder);
            host.Children.Add(cropBorderShape);
            host.Children.Add(rotateLeftButton);
            host.Children.Add(rotateRightButton);
            host.MouseLeftButtonDown += (s, e) => BeginDrag(crop, host, e);
            host.MouseMove += (s, e) => DragMove(e);
            host.MouseLeftButtonUp += (s, e) => EndDrag(host);
            host.MouseWheel += (s, e) => ResizeCrop(crop, e);
            host.MouseRightButtonDown += (s, e) => ShowCropContextMenu(crop, host, e);

            _containers[crop.Id] = host;
            _contentBorders[crop.Id] = contentBorder;
            _cropBorderShapes[crop.Id] = cropBorderShape;
            _rotateLeftButtons[crop.Id] = rotateLeftButton;
            _rotateRightButtons[crop.Id] = rotateRightButton;
            _images[crop.Id] = img;
            CanvasHost.Children.Add(host);
            LayoutCrop(crop, host);
        }

        UpdateSelectionVisuals();
        SetPipSelected(_pipSelectedRuntime);
    }

    private static Button BuildRotateButton(string content, HorizontalAlignment alignment, Thickness margin, string toolTip)
    {
        var button = new Button
        {
            Content = content,
            Width = 22,
            Height = 22,
            FontSize = 11,
            Padding = new Thickness(0),
            Opacity = 0.82,
            Background = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)),
            BorderThickness = new Thickness(0.7),
            HorizontalAlignment = alignment,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = margin,
            Visibility = Visibility.Collapsed,
            ToolTip = toolTip
        };
        Panel.SetZIndex(button, 10);
        return button;
    }

    private IEnumerable<CropItem> GetVisibleCrops()
    {
        if (IsDetachedOverlay)
        {
            return _crops.Where(c => c.Enabled && string.Equals(c.DetachedGroupIdRuntime, _detachedGroupId, StringComparison.Ordinal));
        }

        return _crops.Where(c => c.Enabled && !c.IsDetachedRuntime);
    }

    private void LayoutCrop(CropItem crop, Grid host)
    {
        var scale = Math.Max(0.1, _settings.Scale);
        var width = Math.Max(4, crop.DisplayWidth * scale);
        var height = Math.Max(4, crop.DisplayHeight * scale);
        host.Width = width;
        host.Height = height;

        if (_contentBorders.TryGetValue(crop.Id, out var contentBorder))
        {
            contentBorder.Width = width;
            contentBorder.Height = height;
            ApplyShapeClip(crop, contentBorder, width, height);
            ApplyRotationTransform(crop, contentBorder);
        }
        if (_cropBorderShapes.TryGetValue(crop.Id, out var cropBorderShape))
        {
            UpdateCropBorderShapeGeometry(crop, cropBorderShape, width, height);
            cropBorderShape.RenderTransformOrigin = new Point(0.5, 0.5);
            cropBorderShape.RenderTransform = new RotateTransform(NormalizeAngle(crop.RotationAngle));
        }

        var basePoint = GetBasePipPosition(crop);

        // PipOffset is a PiP-local visual nudge. It never modifies the source crop rectangle.
        Canvas.SetLeft(host, basePoint.X + crop.PipOffsetX);
        Canvas.SetTop(host, basePoint.Y + crop.PipOffsetY);
    }

    private Point GetBasePipPosition(CropItem crop)
    {
        if (IsDetachedOverlay)
        {
            var groupCrops = GetVisibleCrops().ToList();
            var minX = groupCrops.Count == 0 ? 0 : groupCrops.Min(c => c.DisplayX);
            var minY = groupCrops.Count == 0 ? 0 : groupCrops.Min(c => c.DisplayY);
            return new Point(Math.Max(0, crop.DisplayX - minX), Math.Max(0, crop.DisplayY - minY));
        }

        return new Point(crop.DisplayX, crop.DisplayY);
    }

    private Rect GetPipVisualBounds(CropItem crop)
    {
        var scale = Math.Max(0.1, _settings.Scale);
        var basePoint = GetBasePipPosition(crop);
        var left = basePoint.X + crop.PipOffsetX;
        var top = basePoint.Y + crop.PipOffsetY;
        var width = Math.Max(4, crop.DisplayWidth * scale);
        var height = Math.Max(4, crop.DisplayHeight * scale);
        return new Rect(left, top, width, height);
    }

    private void SetPipVisualLeftTop(CropItem crop, double left, double top)
    {
        var basePoint = GetBasePipPosition(crop);
        crop.PipOffsetX = Math.Round(Math.Max(0, left) - basePoint.X, 2);
        crop.PipOffsetY = Math.Round(Math.Max(0, top) - basePoint.Y, 2);
        if (_containers.TryGetValue(crop.Id, out var host)) LayoutCrop(crop, host);
    }

    private static double NormalizeAngle(double angle)
    {
        var normalized = angle % 360.0;
        if (normalized < 0) normalized += 360.0;
        return Math.Round(normalized, 2);
    }

    private void ApplyRotationTransform(CropItem crop, Border border)
    {
        border.RenderTransform = new RotateTransform(NormalizeAngle(crop.RotationAngle));
    }

    private static void ApplyShapeClip(CropItem crop, FrameworkElement element, double width, double height)
    {
        element.Clip = crop.Shape switch
        {
            CropShape.Circle => new EllipseGeometry(new Rect(0, 0, width, height)),
            CropShape.Diamond => BuildDiamondGeometry(new Rect(0, 0, width, height)),
            _ => null
        };
    }

    private static Geometry BuildDiamondGeometry(Rect bounds)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            var top = new Point(bounds.Left + bounds.Width / 2.0, bounds.Top);
            var right = new Point(bounds.Right, bounds.Top + bounds.Height / 2.0);
            var bottom = new Point(bounds.Left + bounds.Width / 2.0, bounds.Bottom);
            var left = new Point(bounds.Left, bounds.Top + bounds.Height / 2.0);
            ctx.BeginFigure(top, true, true);
            ctx.LineTo(right, true, false);
            ctx.LineTo(bottom, true, false);
            ctx.LineTo(left, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private void SelectCrop(CropItem crop, bool additive)
    {
        FocusForKeyboard();
        if (!additive)
        {
            _selectedCropIds.Clear();
            _selectedCropIds.Add(crop.Id);
        }
        else
        {
            if (!_selectedCropIds.Add(crop.Id) && _selectedCropIds.Count > 1)
            {
                _selectedCropIds.Remove(crop.Id);
            }
        }
        UpdateSelectionVisuals();
    }

    private IReadOnlyList<CropItem> GetActionCrops(CropItem clickedCrop)
    {
        var visible = GetVisibleCrops().ToList();
        var selected = visible.Where(c => _selectedCropIds.Contains(c.Id)).ToList();
        if (selected.Count == 0 || selected.All(c => c.Id != clickedCrop.Id))
        {
            return new[] { clickedCrop };
        }
        return selected;
    }

    private void UpdateSelectionVisuals()
    {
        foreach (var crop in _crops)
        {
            var selected = _selectedCropIds.Contains(crop.Id);
            if (_rotateLeftButtons.TryGetValue(crop.Id, out var leftButton))
            {
                leftButton.Visibility = selected && !_settings.ClickThrough ? Visibility.Visible : Visibility.Collapsed;
            }
            if (_rotateRightButtons.TryGetValue(crop.Id, out var rightButton))
            {
                rightButton.Visibility = selected && !_settings.ClickThrough ? Visibility.Visible : Visibility.Collapsed;
            }
            if (_contentBorders.TryGetValue(crop.Id, out var border))
            {
                // Keep the editor-selection cue separate from the persisted custom crop border.
                if (selected && crop.BorderOpacity <= 0.001)
                {
                    border.BorderBrush = new SolidColorBrush(Color.FromArgb(160, 112, 255, 190));
                    border.BorderThickness = new Thickness(1.2);
                }
                else
                {
                    border.BorderBrush = Brushes.Transparent;
                    border.BorderThickness = new Thickness(0);
                }
            }
            if (_cropBorderShapes.TryGetValue(crop.Id, out var cropBorderShape))
            {
                ApplyCropBorderVisual(crop, cropBorderShape, selected);
            }
        }
    }

    private static Color ParseCropBorderColor(string? hex)
    {
        try
        {
            return string.IsNullOrWhiteSpace(hex)
                ? (Color)ColorConverter.ConvertFromString("#00FF7F")
                : (Color)ColorConverter.ConvertFromString(hex);
        }
        catch
        {
            return (Color)ColorConverter.ConvertFromString("#00FF7F");
        }
    }

    private static Shape BuildCropBorderShape(CropItem crop)
    {
        Shape shape = crop.Shape switch
        {
            CropShape.Circle => new Ellipse(),
            CropShape.Diamond => new Polygon(),
            _ => new Rectangle()
        };
        shape.Fill = Brushes.Transparent;
        shape.Stroke = Brushes.Transparent;
        shape.StrokeThickness = 0;
        shape.IsHitTestVisible = false;
        shape.SnapsToDevicePixels = true;
        Panel.SetZIndex(shape, 5);
        return shape;
    }

    private static void UpdateCropBorderShapeGeometry(CropItem crop, Shape shape, double width, double height)
    {
        shape.Width = width;
        shape.Height = height;
        if (shape is Polygon polygon)
        {
            polygon.Points = new PointCollection
            {
                new(width / 2.0, 0),
                new(width, height / 2.0),
                new(width / 2.0, height),
                new(0, height / 2.0)
            };
            polygon.Stretch = Stretch.None;
        }
    }

    private static void ApplyCropBorderVisual(CropItem crop, Shape shape, bool selected)
    {
        if (crop.BorderOpacity <= 0.001)
        {
            shape.Stroke = Brushes.Transparent;
            shape.StrokeThickness = 0;
            return;
        }

        var color = ParseCropBorderColor(crop.BorderColorHex);
        var alpha = (byte)Math.Clamp(Math.Round(crop.BorderOpacity * 255.0), 0, 255);
        if (selected) alpha = (byte)Math.Max((int)alpha, 190);
        shape.Stroke = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        shape.StrokeThickness = selected ? 2.2 : 1.6;
    }

    private void BeginRotate(CropItem crop, Button button, int direction, MouseButtonEventArgs e)
    {
        if (_settings.ClickThrough) return;
        FocusForKeyboard();
        if (!_selectedCropIds.Contains(crop.Id)) SelectCrop(crop, additive: (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
        _rotatingCrops = GetActionCrops(crop).ToList();
        HistoryCheckpointRequested?.Invoke();
        _rotateDirection = direction >= 0 ? 1 : -1;
        foreach (var target in _rotatingCrops)
        {
            RotateCrop(target, _rotateDirection * GetRotateStepDegrees(isHold: false), save: false);
        }
        button.CaptureMouse();
        _rotateTimer.Start();
        e.Handled = true;
    }

    private void EndRotate(MouseButtonEventArgs e)
    {
        StopRotate(save: true);
        e.Handled = true;
    }

    private void StopRotate(bool save)
    {
        if (_rotatingCrops.Count == 0 && !_rotateTimer.IsEnabled) return;
        _rotateTimer.Stop();
        Mouse.Capture(null);
        _rotatingCrops.Clear();
        if (save) SettingsService.Save(_settings);
    }

    private static double GetRotateStepDegrees(bool isHold)
    {
        var fineControl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (fineControl)
        {
            return isHold ? FineHoldRotateStepDegrees : FineClickRotateStepDegrees;
        }

        return isHold ? HoldRotateStepDegrees : ClickRotateStepDegrees;
    }

    private void RotateCrop(CropItem crop, double deltaDegrees, bool save)
    {
        crop.RotationAngle = NormalizeAngle(crop.RotationAngle + deltaDegrees);
        if (_contentBorders.TryGetValue(crop.Id, out var border))
        {
            ApplyRotationTransform(crop, border);
        }
        if (save) SettingsService.Save(_settings);
    }

    private void CaptureTick(object? sender, EventArgs e)
    {
        RequestCaptureRefresh();
    }

    private void RequestCaptureRefresh()
    {
        if (_capture == null || !_capture.IsCapturing || !IsVisible) return;

        var now = DateTime.UtcNow;
        var intervalMs = ClampCaptureInterval(_settings.CaptureIntervalMs);
        if ((now - _lastCaptureRefreshUtc).TotalMilliseconds < intervalMs) return;
        if (_captureRefreshQueued || _captureRefreshRunning) return;

        _captureRefreshQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _captureRefreshQueued = false;
            RefreshCaptureFrameNow();
        }), DispatcherPriority.Render);
    }

    private void RefreshCaptureFrameNow()
    {
        if (_captureRefreshRunning) return;
        if (_capture == null || !_capture.IsCapturing || !IsVisible) return;

        _captureRefreshRunning = true;
        _lastCaptureRefreshUtc = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();

        try
        {
            if (_targetHwnd != IntPtr.Zero)
            {
                var minimized = WindowService.IsMinimized(_targetHwnd);
                if (_wasMinimized && !minimized)
                {
                    _restoreHoldUntilUtc = DateTime.UtcNow.AddMilliseconds(650);
                    SettingsService.Log("[RestoreStabilize] minimized -> restored detected");
                    SettingsService.Log($"[RestoreStabilize] hold crop update until {_restoreHoldUntilUtc:O}");
                }
                _wasMinimized = minimized;
                if (minimized) return;
            }

            ApplyTargetLockedPosition();

            if (DateTime.UtcNow < _restoreHoldUntilUtc)
            {
                if ((DateTime.UtcNow - _lastRestoreLogUtc).TotalMilliseconds > 500)
                {
                    SettingsService.Log("[RestoreStabilize] holding WGC crop refresh");
                    _lastRestoreLogUtc = DateTime.UtcNow;
                }
                return;
            }
            if (_restoreHoldUntilUtc != DateTime.MinValue)
            {
                SettingsService.Log("[RestoreStabilize] crop resumed");
                _restoreHoldUntilUtc = DateTime.MinValue;
            }

            var visibleCrops = GetVisibleCrops().Where(c => c.Enabled).ToList();
            if (visibleCrops.Count == 0) return;

            foreach (var crop in visibleCrops)
            {
                if (!_images.TryGetValue(crop.Id, out var image))
                {
                    QueueRefreshItems();
                    continue;
                }
                var source = _capture.GetLatestCrop(new Int32Rect(crop.X, crop.Y, crop.Width, crop.Height));
                if (source != null) image.Source = source;
                if (_containers.TryGetValue(crop.Id, out var host)) LayoutCrop(crop, host);
            }
        }
        finally
        {
            sw.Stop();
            UpdateCapturePerformanceStats(sw.Elapsed.TotalMilliseconds);
            _captureRefreshRunning = false;
        }
    }

    private void UpdateCapturePerformanceStats(double elapsedMs)
    {
        if (!_settings.ShowCapturePerformanceStats)
        {
            if (PerformanceStatsBorder.Visibility != Visibility.Collapsed)
                PerformanceStatsBorder.Visibility = Visibility.Collapsed;
            return;
        }

        _perfFrameCount++;
        _perfTotalMs += elapsedMs;
        var now = DateTime.UtcNow;
        if ((now - _lastPerfWindowUtc).TotalMilliseconds >= 500)
        {
            var seconds = Math.Max(0.001, (now - _lastPerfWindowUtc).TotalSeconds);
            _lastMeasuredFps = (int)Math.Round(_perfFrameCount / seconds);
            _lastMeasuredFrameMs = _perfFrameCount > 0 ? _perfTotalMs / _perfFrameCount : 0;
            _perfFrameCount = 0;
            _perfTotalMs = 0;
            _lastPerfWindowUtc = now;
        }

        PerformanceStatsBorder.Visibility = Visibility.Visible;
        PerformanceStatsText.Text = $"{_lastMeasuredFps} FPS / {_lastMeasuredFrameMs:0.0}ms / {_settings.CaptureIntervalMs}ms";
    }

    private void UpdatePerformanceStatsVisibility()
    {
        if (PerformanceStatsBorder == null) return;
        PerformanceStatsBorder.Visibility = _settings.ShowCapturePerformanceStats ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BeginDrag(CropItem crop, Grid host, MouseButtonEventArgs e)
    {
        if (_settings.ClickThrough) return;
        FocusForKeyboard();
        var additive = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var wasSelected = _selectedCropIds.Contains(crop.Id);

        // 첫 클릭은 무조건 선택만 합니다.
        // Shift+클릭으로 다중 선택한 뒤에는 Shift를 떼고 선택된 크롭을 다시 드래그하면
        // 선택된 크롭 전체가 PiP 안에서 함께 이동됩니다.
        if (!wasSelected || additive)
        {
            SelectCrop(crop, additive);
            if (IsDetachedOverlay && additive) SelectThisPip(additive: true);
            e.Handled = true;
            return;
        }

        _dragCrop = crop;
        _dragCrops.Clear();
        _dragCrops.AddRange(GetActionCrops(crop));
        if (_dragCrops.Count == 0) _dragCrops.Add(crop);

        _dragStartStates.Clear();
        foreach (var target in _dragCrops.DistinctBy(c => c.Id))
        {
            var bounds = GetPipVisualBounds(target);
            _dragStartStates[target.Id] = new DragStartState(bounds.Left, bounds.Top, target.DisplayWidth, target.DisplayHeight);
        }

        _dragStart = e.GetPosition(CanvasHost);
        _resizeDrag = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        HistoryCheckpointRequested?.Invoke();
        host.CaptureMouse();
        e.Handled = true;
    }

    private void DragMove(MouseEventArgs e)
    {
        if (_dragCrop == null || _dragStart == null || _dragCrops.Count == 0) return;
        var p = e.GetPosition(CanvasHost);
        var dx = p.X - _dragStart.Value.X;
        var dy = p.Y - _dragStart.Value.Y;

        if (_resizeDrag)
        {
            var sensitivity = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 0.25 : 0.55;
            var delta = (dx + dy) * sensitivity;
            foreach (var target in _dragCrops.DistinctBy(c => c.Id))
            {
                if (!_dragStartStates.TryGetValue(target.Id, out var state)) continue;
                var aspect = state.DisplayWidth <= 0 ? 1.0 : state.DisplayHeight / state.DisplayWidth;
                var newW = Math.Max(4, Math.Round(state.DisplayWidth + delta, 2));
                var newH = Math.Max(4, Math.Round(newW * aspect, 2));
                target.DisplayWidth = newW;
                target.DisplayHeight = newH;
                if (_containers.TryGetValue(target.Id, out var targetHost)) LayoutCrop(target, targetHost);
            }
            SaveSettingsThrottled();
            return;
        }

        foreach (var target in _dragCrops.DistinctBy(c => c.Id))
        {
            if (!_dragStartStates.TryGetValue(target.Id, out var state)) continue;
            SetPipVisualLeftTop(target, state.Left + dx, state.Top + dy);
        }

        SaveSettingsThrottled();
    }

    private void EndDrag(Grid host)
    {
        _dragCrop = null;
        _dragStart = null;
        _dragCrops.Clear();
        _dragStartStates.Clear();
        _resizeDrag = false;
        host.ReleaseMouseCapture();
        SettingsService.Save(_settings);
    }

    private void ResizeCrop(CropItem crop, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        var step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 0.01 : 0.03;
        var factor = e.Delta > 0 ? 1.0 + step : 1.0 / (1.0 + step);
        var actionCrops = GetActionCrops(crop);
        HistoryCheckpointRequested?.Invoke();
        foreach (var target in actionCrops)
        {
            target.DisplayWidth = Math.Max(4, Math.Round(target.DisplayWidth * factor, 2));
            target.DisplayHeight = Math.Max(4, Math.Round(target.DisplayHeight * factor, 2));
            if (_containers.TryGetValue(target.Id, out var host)) LayoutCrop(target, host);
        }
        SettingsService.Save(_settings);
        e.Handled = true;
    }

    private void AddAlignmentMenuItems(ContextMenu menu, IReadOnlyList<CropItem> actionCrops)
    {
        var targets = actionCrops.Where(c => GetVisibleCrops().Any(v => v.Id == c.Id)).DistinctBy(c => c.Id).ToList();
        if (targets.Count < 2) return;

        menu.Items.Add(new Separator());

        var distribute = new MenuItem
        {
            Header = "균등 분할",
            Padding = new Thickness(12, 6, 12, 6),
            ToolTip = "중심 크롭을 기준으로 좌/우 가장 가까운 크롭과의 거리만큼 선택 크롭을 가로로 균등 배치합니다."
        };
        distribute.Click += (_, _) => EvenlyDistributeSelectedCropViews(targets);
        menu.Items.Add(distribute);

        var alignHorizontal = new MenuItem
        {
            Header = "상하 균등",
            Padding = new Thickness(12, 6, 12, 6),
            ToolTip = "중심 크롭을 기준으로 선택 크롭들을 위/아래 방향으로 같은 간격으로 배치합니다."
        };
        alignHorizontal.Click += (_, _) => AlignSelectedCropViewsToHorizontalLine(targets);
        menu.Items.Add(alignHorizontal);
    }

    private void EvenlyDistributeSelectedCropViews(IReadOnlyList<CropItem> crops)
    {
        var items = crops
            .Select(c => new CropBounds(c, GetPipVisualBounds(c)))
            .OrderBy(x => x.Bounds.Left + x.Bounds.Width / 2.0)
            .ToList();
        if (items.Count < 2) return;

        HistoryCheckpointRequested?.Invoke();
        var centerIndex = items.Count / 2;
        var centerX = GetCenterX(items[centerIndex].Bounds);
        var fallbackSpacing = Math.Max(8, items.Max(x => x.Bounds.Width) + 8);
        var distances = new List<double>();
        if (centerIndex > 0)
        {
            distances.Add(centerX - GetCenterX(items[centerIndex - 1].Bounds));
        }
        if (centerIndex < items.Count - 1)
        {
            distances.Add(GetCenterX(items[centerIndex + 1].Bounds) - centerX);
        }

        var spacing = distances.Where(d => d > 0.1).DefaultIfEmpty(fallbackSpacing).Min();
        if (spacing < 0.1) spacing = fallbackSpacing;

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var targetCenterX = centerX + (i - centerIndex) * spacing;
            SetPipVisualLeftTop(item.Crop, targetCenterX - item.Bounds.Width / 2.0, item.Bounds.Top);
        }

        SettingsService.Save(_settings);
    }

    private void AlignSelectedCropViewsToHorizontalLine(IReadOnlyList<CropItem> crops)
    {
        var items = crops
            .Select(c => new CropBounds(c, GetPipVisualBounds(c)))
            .OrderBy(x => x.Bounds.Top + x.Bounds.Height / 2.0)
            .ToList();
        if (items.Count < 2) return;

        HistoryCheckpointRequested?.Invoke();
        var centerIndex = items.Count / 2;
        var centerY = GetCenterY(items[centerIndex].Bounds);
        var fallbackSpacing = Math.Max(8, items.Max(x => x.Bounds.Height) + 8);
        var distances = new List<double>();
        if (centerIndex > 0)
        {
            distances.Add(centerY - GetCenterY(items[centerIndex - 1].Bounds));
        }
        if (centerIndex < items.Count - 1)
        {
            distances.Add(GetCenterY(items[centerIndex + 1].Bounds) - centerY);
        }

        var spacing = distances.Where(d => d > 0.1).DefaultIfEmpty(fallbackSpacing).Min();
        if (spacing < 0.1) spacing = fallbackSpacing;

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var targetCenterY = centerY + (i - centerIndex) * spacing;
            SetPipVisualLeftTop(item.Crop, item.Bounds.Left, targetCenterY - item.Bounds.Height / 2.0);
        }

        SettingsService.Save(_settings);
    }

    private static double GetCenterX(Rect rect) => rect.Left + rect.Width / 2.0;
    private static double GetCenterY(Rect rect) => rect.Top + rect.Height / 2.0;

    private readonly record struct CropBounds(CropItem Crop, Rect Bounds);

    private void ShowCropContextMenu(CropItem crop, FrameworkElement host, MouseButtonEventArgs e)
    {
        if (_settings.ClickThrough) return;
        FocusForKeyboard();

        // 우클릭은 "작업 메뉴 열기"로만 동작해야 합니다.
        // 이미 Shift+클릭으로 선택된 크롭을 우클릭할 때 다시 SelectCrop(additive:true)를 호출하면
        // 토글 처리로 해당 크롭이 선택 해제되어 다중 선택이 깨집니다.
        var additive = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (!_selectedCropIds.Contains(crop.Id))
        {
            SelectCrop(crop, additive);
        }

        var actionCrops = GetActionCrops(crop).ToList();

        var menu = new ContextMenu();
        if (IsDetachedOverlay)
        {
            var reattach = new MenuItem
            {
                Header = actionCrops.Count > 1 ? $"선택 {actionCrops.Count}개 메인 PIP로 합치기" : "메인 PIP로 합치기",
                Padding = new Thickness(12, 6, 12, 6)
            };
            var reattachTargets = actionCrops.ToList();
            reattach.Click += (_, _) => ReattachRequested?.Invoke(reattachTargets);
            menu.Items.Add(reattach);

            if (actionCrops.Count < GetVisibleCrops().Count())
            {
                var detachSubset = new MenuItem
                {
                    Header = actionCrops.Count > 1 ? $"선택 {actionCrops.Count}개 새 PIP로 분리" : "선택 크롭 새 PIP로 분리",
                    Padding = new Thickness(12, 6, 12, 6)
                };
                var detachSubsetTargets = actionCrops.ToList();
                detachSubset.Click += (_, _) => DetachRequested?.Invoke(detachSubsetTargets);
                menu.Items.Add(detachSubset);
            }

            var mergePips = new MenuItem
            {
                Header = "선택한 PIP 병합",
                Padding = new Thickness(12, 6, 12, 6)
            };
            mergePips.Click += (_, _) => MergeSelectedPipsRequested?.Invoke(this);
            menu.Items.Add(mergePips);
        }
        else
        {
            var detach = new MenuItem
            {
                Header = actionCrops.Count > 1 ? $"선택 {actionCrops.Count}개 PIP 분리" : "PIP 분리",
                Padding = new Thickness(12, 6, 12, 6)
            };
            var detachTargets = actionCrops.ToList();
            detach.Click += (_, _) => DetachRequested?.Invoke(detachTargets);
            menu.Items.Add(detach);
        }

        AddAlignmentMenuItems(menu, actionCrops);
        AddPipPlacementMenuItems(menu);

        OpenContextMenu(host, menu);
        e.Handled = true;
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_settings.ClickThrough) return;
        FocusForKeyboard();

        var additive = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var wasSelected = _pipSelectedRuntime;
        _pipMouseDownWasSelected = wasSelected && !additive;
        _pipDragMoved = false;
        _pipMouseDownScreenDip = GetMouseScreenDip(e);

        // 첫 클릭은 선택만 처리합니다. 이미 선택된 PIP를 드래그하면 이동하고,
        // 움직임 없이 클릭만 끝나면 겹친 PIP 선택을 순환합니다.
        if (!wasSelected || additive)
        {
            SelectThisPip(additive);
            e.Handled = true;
            return;
        }

        _pipDragStartScreenDip = _pipMouseDownScreenDip;
        HistoryCheckpointRequested?.Invoke();
        RootBorder.CaptureMouse();
        e.Handled = true;
    }

    private void Root_MouseMove(object sender, MouseEventArgs e)
    {
        if (_pipDragStartScreenDip is null || e.LeftButton != MouseButtonState.Pressed) return;
        var current = GetMouseScreenDip(e);
        var dx = current.X - _pipDragStartScreenDip.Value.X;
        var dy = current.Y - _pipDragStartScreenDip.Value.Y;
        if (Math.Abs(dx) < 0.01 && Math.Abs(dy) < 0.01) return;

        if (_pipMouseDownScreenDip is Point start &&
            (Math.Abs(current.X - start.X) >= 2.0 || Math.Abs(current.Y - start.Y) >= 2.0))
        {
            _pipDragMoved = true;
        }

        _pipDragStartScreenDip = current;
        PipMoveRequested?.Invoke(this, dx, dy);
        e.Handled = true;
    }

    private void Root_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_pipDragStartScreenDip is null) return;

        var releasePoint = GetMouseScreenDip(e);
        var shouldCycle = _pipMouseDownWasSelected && !_pipDragMoved;
        _pipDragStartScreenDip = null;
        _pipMouseDownScreenDip = null;
        _pipMouseDownWasSelected = false;
        _pipDragMoved = false;
        RootBorder.ReleaseMouseCapture();

        if (shouldCycle)
        {
            PipSelectionCycleRequested?.Invoke(this, releasePoint, false);
        }

        SettingsService.Save(_settings);
        e.Handled = true;
    }

    private Point GetMouseScreenDip(MouseEventArgs e)
    {
        var screenPx = PointToScreen(e.GetPosition(this));
        var source = PresentationSource.FromVisual(this);
        var transform = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        return transform.Transform(screenPx);
    }

    private void Root_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_settings.ClickThrough) return;
        FocusForKeyboard();

        // 우클릭한 PIP가 이미 선택되어 있으면 기존 다중 선택을 유지합니다.
        // 선택되지 않은 PIP를 우클릭한 경우에만 일반 선택/Shift 추가 선택을 적용합니다.
        var additive = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (!_pipSelectedRuntime)
        {
            SelectThisPip(additive);
        }

        var menu = new ContextMenu();
        AddPipStackAlignmentMenuItems(menu);
        AddPipPlacementMenuItems(menu);

        if (IsDetachedOverlay)
        {
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());

            var reattachAll = new MenuItem
            {
                Header = "이 PIP 전체 메인 PIP로 합치기",
                Padding = new Thickness(12, 6, 12, 6)
            };
            var reattachAllTargets = GetVisibleCrops().ToList();
            reattachAll.Click += (_, _) => ReattachRequested?.Invoke(reattachAllTargets);
            menu.Items.Add(reattachAll);

            var merge = new MenuItem
            {
                Header = "선택한 PIP 병합",
                Padding = new Thickness(12, 6, 12, 6)
            };
            merge.Click += (_, _) => MergeSelectedPipsRequested?.Invoke(this);
            menu.Items.Add(merge);
        }

        if (menu.Items.Count == 0) return;
        OpenContextMenu(RootBorder, menu);
        e.Handled = true;
    }

    private void OpenContextMenu(FrameworkElement owner, ContextMenu menu)
    {
        // ContextMenu를 요소의 영구 ContextMenu로 남겨두면 다음 우클릭 때 이전 메뉴/이전 선택 스냅샷이
        // 먼저 열릴 수 있습니다. 매번 새 메뉴를 명시적으로 열고 닫을 때 참조를 끊습니다.
        owner.ContextMenu = null;
        _contextMenuOpen = true;
        menu.PlacementTarget = owner;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(owner.ContextMenu, menu)) owner.ContextMenu = null;
            _contextMenuOpen = false;
            FocusForKeyboard();
            InstallResizeHitTestHook();
            UpdatePerformanceStatsVisibility();
        };
        owner.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void SelectThisPip(bool additive)
    {
        PipSelectionRequested?.Invoke(this, additive);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_settings.ClickThrough) return;

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (e.Key == Key.Z)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) RedoRequested?.Invoke();
                else UndoRequested?.Invoke();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Y)
            {
                RedoRequested?.Invoke();
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Escape)
        {
            ClearSelection();
            e.Handled = true;
            return;
        }

        var dx = 0.0;
        var dy = 0.0;
        var step = GetKeyboardMoveStep();
        switch (e.Key)
        {
            case Key.Left:
                dx = -step;
                break;
            case Key.Right:
                dx = step;
                break;
            case Key.Up:
                dy = -step;
                break;
            case Key.Down:
                dy = step;
                break;
            default:
                return;
        }

        if (_selectedCropIds.Count > 0)
        {
            MoveSelectedCropViewsBy(dx, dy);
        }
        else if (_pipSelectedRuntime)
        {
            PipMoveRequested?.Invoke(this, dx, dy);
        }
        e.Handled = true;
    }

    private static double GetKeyboardMoveStep()
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) return 0.25;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) return 10.0;
        return 1.0;
    }

    private void ClearSelection()
    {
        _selectedCropIds.Clear();
        StopRotate(save: false);
        UpdateSelectionVisuals();
        SetPipSelected(false);
        PipSelectionClearRequested?.Invoke();
    }

    private void MoveSelectedCropViewsBy(double dx, double dy)
    {
        var selected = GetVisibleCrops().Where(c => _selectedCropIds.Contains(c.Id)).ToList();
        if (selected.Count == 0) return;

        HistoryCheckpointRequested?.Invoke();
        foreach (var crop in selected)
        {
            crop.PipOffsetX = Math.Round(crop.PipOffsetX + dx, 2);
            crop.PipOffsetY = Math.Round(crop.PipOffsetY + dy, 2);
            if (_containers.TryGetValue(crop.Id, out var host)) LayoutCrop(crop, host);
        }

        SaveSettingsThrottled();
    }

    private bool IsForegroundInsideThisApp()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;

        foreach (Window window in Application.Current.Windows)
        {
            try
            {
                if (new WindowInteropHelper(window).Handle == foreground) return true;
            }
            catch
            {
                // Ignore windows that are closing or have no handle yet.
            }
        }

        return false;
    }

    private void ClearSelectionIfForegroundMovedOutsideApp()
    {
        if (_settings.ClickThrough) return;
        if (!_pipSelectedRuntime && _selectedCropIds.Count == 0) return;
        if (_contextMenuOpen || _dragCrop != null || _rotateTimer.IsEnabled) return;
        if (IsForegroundInsideThisApp()) return;
        ClearSelection();
    }


    private void InstallResizeHitTestHook()
    {
        if (_source != null) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmNcHitTest || _settings.ClickThrough || _contextMenuOpen || !IsVisible)
        {
            return IntPtr.Zero;
        }

        var hit = HitTestResizeBorder(lParam);
        if (hit == 0) return IntPtr.Zero;

        handled = true;
        return new IntPtr(hit);
    }

    private int HitTestResizeBorder(IntPtr lParam)
    {
        var packed = lParam.ToInt64();
        var screenX = unchecked((short)(packed & 0xFFFF));
        var screenY = unchecked((short)((packed >> 16) & 0xFFFF));
        var pt = PointFromScreen(new Point(screenX, screenY));

        var w = Math.Max(0, ActualWidth);
        var h = Math.Max(0, ActualHeight);
        if (w <= 0 || h <= 0) return 0;

        var t = Math.Min(ResizeHitTestThickness, Math.Max(4.0, Math.Min(w, h) / 3.0));
        var left = pt.X >= 0 && pt.X <= t;
        var right = pt.X >= w - t && pt.X <= w;
        var top = pt.Y >= 0 && pt.Y <= t;
        var bottom = pt.Y >= h - t && pt.Y <= h;

        if (top && left) return HtTopLeft;
        if (top && right) return HtTopRight;
        if (bottom && left) return HtBottomLeft;
        if (bottom && right) return HtBottomRight;
        if (left) return HtLeft;
        if (right) return HtRight;
        if (top) return HtTop;
        if (bottom) return HtBottom;
        return 0;
    }

    public void SetClickThrough(bool enabled)
    {
        _settings.ClickThrough = enabled;
        if (enabled) ClearSelection();
        if (!IsLoaded) return;
        UpdateSelectionVisuals();
        ApplyPipFrameVisual();
        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        exStyle |= NativeMethods.WS_EX_TOOLWINDOW;
        if (enabled) exStyle |= NativeMethods.WS_EX_TRANSPARENT;
        else exStyle &= ~NativeMethods.WS_EX_TRANSPARENT;
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, exStyle);
    }

    private void ReapplyTopMost()
    {
        if (!_settings.TopMost || !IsVisible) return;
        Topmost = false;
        Topmost = true;
        Topmost = true;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }
    }

    private void OverlayWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_isLoadedForResize || _isApplyingWindowScale) return;
        var newW = Math.Max(1, CanvasHost.ActualWidth);
        var newH = Math.Max(1, CanvasHost.ActualHeight);
        var oldW = Math.Max(1, _lastCanvasWidth);
        var oldH = Math.Max(1, _lastCanvasHeight);
        if (_resizeItemsWithWindow && (Math.Abs(newW - oldW) > 0.5 || Math.Abs(newH - oldH) > 0.5))
        {
            var sx = newW / oldW;
            var sy = newH / oldH;
            if (double.IsFinite(sx) && double.IsFinite(sy) && sx > 0.05 && sy > 0.05)
            {
                _isApplyingWindowScale = true;
                try
                {
                    foreach (var crop in GetVisibleCrops().ToList())
                    {
                        var oldBounds = GetPipVisualBounds(crop);
                        crop.DisplayWidth = Math.Max(4, Math.Round(crop.DisplayWidth * sx, 2));
                        crop.DisplayHeight = Math.Max(4, Math.Round(crop.DisplayHeight * sy, 2));
                        SetPipVisualLeftTop(crop, oldBounds.Left * sx, oldBounds.Top * sy);
                    }
                    RefreshItems();
                    SettingsService.Save(_settings);
                }
                finally
                {
                    _isApplyingWindowScale = false;
                }
            }
        }
        _lastCanvasWidth = newW;
        _lastCanvasHeight = newH;
        SaveWindowBoundsThrottled();
        PipBoundsChanged?.Invoke(this);
    }

    private void OnOverlayLocationChanged()
    {
        SaveWindowBoundsThrottled();
        PipBoundsChanged?.Invoke(this);
        if (!_settings.PipPositionLockedToTarget || _isApplyingTargetLockedPosition) return;
        CaptureTargetRelativePosition(save: true);
    }

    public void SetTargetLockState(double? offsetX, double? offsetY, int baselineWidth, int baselineHeight)
    {
        _targetLockedOffsetX = offsetX;
        _targetLockedOffsetY = offsetY;
        _targetLockBaselineWidth = Math.Max(0, baselineWidth);
        _targetLockBaselineHeight = Math.Max(0, baselineHeight);
    }

    public void SetPipPositionLockedToTarget(bool enabled, bool captureCurrentPosition)
    {
        _settings.PipPositionLockedToTarget = enabled;
        if (!enabled) return;

        if (!captureCurrentPosition && _targetLockedOffsetX is null && _targetLockedOffsetY is null && _persistWindowBounds &&
            (Math.Abs(_settings.OverlayTargetOffsetX) > 0.01 || Math.Abs(_settings.OverlayTargetOffsetY) > 0.01))
        {
            _targetLockedOffsetX = _settings.OverlayTargetOffsetX;
            _targetLockedOffsetY = _settings.OverlayTargetOffsetY;
        }

        if (captureCurrentPosition || _targetLockedOffsetX is null || _targetLockedOffsetY is null)
        {
            CaptureTargetRelativePosition(save: _persistWindowBounds);
        }
        else
        {
            ApplyTargetLockedPosition();
        }
    }

    private bool TryGetTargetClientRect(out RectI rect)
    {
        rect = default;
        if (_targetHwnd == IntPtr.Zero) return false;
        if (WindowService.IsMinimized(_targetHwnd)) return false;
        return WindowService.TryGetClientScreenRect(_targetHwnd, out rect) && rect.Width > 0 && rect.Height > 0;
    }

    private void CaptureTargetRelativePosition(bool save)
    {
        if (!TryGetTargetClientRect(out var rect)) return;
        _targetLockBaselineWidth = Math.Max(_targetLockBaselineWidth, rect.Width);
        _targetLockBaselineHeight = Math.Max(_targetLockBaselineHeight, rect.Height);
        _targetLockedOffsetX = Math.Round(Left - rect.X, 2);
        _targetLockedOffsetY = Math.Round(Top - rect.Y, 2);

        if (_persistWindowBounds)
        {
            _settings.OverlayTargetOffsetX = _targetLockedOffsetX.Value;
            _settings.OverlayTargetOffsetY = _targetLockedOffsetY.Value;
        }

        PipBoundsChanged?.Invoke(this);
        if (save) SettingsService.Save(_settings);
    }

    private bool IsTargetRectUsableForPositionLock(RectI rect)
    {
        if (_targetLockBaselineWidth <= 0 || _targetLockBaselineHeight <= 0)
        {
            _targetLockBaselineWidth = rect.Width;
            _targetLockBaselineHeight = rect.Height;
            return true;
        }

        // MapleStory의 Ctrl+Enter 등으로 클라이언트 영역이 갑자기 작아지는 경우에는
        // 요청 기준에 따라 위치 고정 보정을 하지 않습니다. 다시 원래 크기 이상으로 돌아오면 추적합니다.
        if (rect.Width < _targetLockBaselineWidth * 0.95 || rect.Height < _targetLockBaselineHeight * 0.95)
        {
            return false;
        }

        if (rect.Width > _targetLockBaselineWidth) _targetLockBaselineWidth = rect.Width;
        if (rect.Height > _targetLockBaselineHeight) _targetLockBaselineHeight = rect.Height;
        return true;
    }

    private void ApplyTargetLockedPosition()
    {
        if (!_settings.PipPositionLockedToTarget) return;
        if (_targetLockedOffsetX is null || _targetLockedOffsetY is null)
        {
            CaptureTargetRelativePosition(save: _persistWindowBounds);
            return;
        }
        if (!TryGetTargetClientRect(out var rect)) return;
        if (!IsTargetRectUsableForPositionLock(rect)) return;

        var desiredLeft = Math.Round(rect.X + _targetLockedOffsetX.Value, 2);
        var desiredTop = Math.Round(rect.Y + _targetLockedOffsetY.Value, 2);
        if (Math.Abs(Left - desiredLeft) < 0.5 && Math.Abs(Top - desiredTop) < 0.5) return;

        _isApplyingTargetLockedPosition = true;
        try
        {
            Left = desiredLeft;
            Top = desiredTop;
        }
        finally
        {
            _isApplyingTargetLockedPosition = false;
        }
    }

    private void CenterOnTargetClientRect()
    {
        if (!TryGetTargetClientRect(out var rect)) return;

        _targetLockBaselineWidth = Math.Max(_targetLockBaselineWidth, rect.Width);
        _targetLockBaselineHeight = Math.Max(_targetLockBaselineHeight, rect.Height);
        var desiredLeft = Math.Round(rect.X + Math.Max(0, (rect.Width - Width) / 2.0), 2);
        var desiredTop = Math.Round(rect.Y + Math.Max(0, (rect.Height - Height) / 2.0), 2);

        _isApplyingTargetLockedPosition = true;
        try
        {
            Left = desiredLeft;
            Top = desiredTop;
        }
        finally
        {
            _isApplyingTargetLockedPosition = false;
        }

        if (_settings.PipPositionLockedToTarget)
        {
            CaptureTargetRelativePosition(save: true);
        }
        else
        {
            SaveWindowBoundsThrottled();
        }
    }

    private void AddPipStackAlignmentMenuItems(ContextMenu menu)
    {
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());

        var overlap = new MenuItem
        {
            Header = "PIP 겹치기 + 상하 정렬",
            Padding = new Thickness(12, 6, 12, 6),
            ToolTip = "선택된 PIP들을 첫 번째 선택 PIP 위치에 겹쳐 배치합니다."
        };
        overlap.Click += (_, _) => PipStackAlignRequested?.Invoke(this, true);
        menu.Items.Add(overlap);

        var stack = new MenuItem
        {
            Header = "PIP 겹치기 X + 상하 정렬",
            Padding = new Thickness(12, 6, 12, 6),
            ToolTip = "Shift로 먼저 선택한 PIP를 맨 위 기준으로 두고, 나머지 선택 PIP들을 바로 아래에 겹치지 않게 배치합니다."
        };
        stack.Click += (_, _) => PipStackAlignRequested?.Invoke(this, false);
        menu.Items.Add(stack);
    }

    private void AddPipPlacementMenuItems(ContextMenu menu)
    {
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());

        var center = new MenuItem
        {
            Header = "메이플 화면 중앙에 배치",
            Padding = new Thickness(12, 6, 12, 6),
            ToolTip = "현재 선택된 메이플스토리 클라이언트 영역의 정중앙으로 이 PIP 창을 이동합니다.",
            IsEnabled = TryGetTargetClientRect(out _)
        };
        center.Click += (_, _) => CenterOnTargetClientRect();
        menu.Items.Add(center);

        if (_settings.PipPositionLockedToTarget)
        {
            var recapture = new MenuItem
            {
                Header = "현재 위치를 고정 좌표로 다시 저장",
                Padding = new Thickness(12, 6, 12, 6),
                ToolTip = "현재 PIP 위치를 메이플스토리 클라이언트 좌상단 기준 상대 좌표로 다시 저장합니다.",
                IsEnabled = TryGetTargetClientRect(out _)
            };
            recapture.Click += (_, _) => CaptureTargetRelativePosition(save: true);
            menu.Items.Add(recapture);
        }
    }

    private void SaveWindowBoundsThrottled()
    {
        if (!_persistWindowBounds) return;
        _settings.OverlayLeft = Left;
        _settings.OverlayTop = Top;
        _settings.OverlayWidth = Width;
        _settings.OverlayHeight = Height;
        if ((DateTime.UtcNow - _lastResizeSaveUtc).TotalMilliseconds < 250) return;
        _lastResizeSaveUtc = DateTime.UtcNow;
        SettingsService.Save(_settings);
    }

    private void SaveSettingsThrottled()
    {
        if ((DateTime.UtcNow - _lastSettingsSaveUtc).TotalMilliseconds < 250) return;
        _lastSettingsSaveUtc = DateTime.UtcNow;
        SettingsService.Save(_settings);
    }

    private readonly record struct DragStartState(double Left, double Top, double DisplayWidth, double DisplayHeight);
}

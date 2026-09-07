using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CropPipViewer;

public partial class PreviewCropWindow : Window
{
    private readonly BitmapSource _source;
    private Point _start;
    private Point _current;
    private bool _dragging;
    private bool _movingExisting;
    private bool _keyboardMoveEnabled;
    private Point _moveStartPoint;
    private Int32Rect _moveStartRegion;
    private double _zoom = 1.0;
    private readonly ScaleTransform _zoomTransform = new(1.0, 1.0);


    private bool _spacePanDragging;
    private Point _spacePanStartOnScrollViewer;
    private double _spacePanStartHorizontalOffset;
    private double _spacePanStartVerticalOffset;

    private readonly CropTemplate? _pasteTemplate;

    public CropTemplate? CopiedTemplate { get; private set; }

    private CropTemplate? CurrentPasteTemplate => CopiedTemplate ?? _pasteTemplate;

    public Int32Rect? SelectedRegion { get; private set; }
    public CropShape SelectedShape { get; private set; } = CropShape.Rectangle;

    public PreviewCropWindow(string title, BitmapSource source, Int32Rect? initialRegion = null, CropShape initialShape = CropShape.Rectangle, CropTemplate? pasteTemplate = null)
    {
        InitializeComponent();
        Title = title;
        _source = source;
        SelectedShape = initialShape;
        _pasteTemplate = pasteTemplate;
        SetShapeCombo(initialShape);

        PreviewImage.Source = source;
        PreviewHost.LayoutTransform = _zoomTransform;
        PreviewHost.Width = source.PixelWidth;
        PreviewHost.Height = source.PixelHeight;
        OverlayCanvas.Width = source.PixelWidth;
        OverlayCanvas.Height = source.PixelHeight;

        if (initialRegion is Int32Rect rect)
        {
            SelectedRegion = ClampRegion(rect);
            DrawRegion(SelectedRegion.Value);
            SelectionShape.Visibility = Visibility.Visible;
            MoveModeButton.Visibility = Visibility.Visible;
            HintText.Text = "이전 크롭 영역을 표시했습니다. 초록 영역을 클릭하면 바로 위치 이동 모드가 켜지고, 크기를 유지한 채 위치만 옮길 수 있습니다.";
        }
        PasteTemplateButton.Visibility = CurrentPasteTemplate == null ? Visibility.Collapsed : Visibility.Visible;
        UpdateMoveModeUi();
        Loaded += (_, _) => Focus();
    }


    private static bool IsSpaceDown()
    {
        return Keyboard.IsKeyDown(Key.Space);
    }

    private void PreviewHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
        {
            StopPointerModes(releaseCapture: true);
            ToggleZoom(e.GetPosition(PreviewHost));
            e.Handled = true;
            return;
        }

        if (IsSpaceDown())
        {
            _dragging = false;
            _movingExisting = false;
            _spacePanDragging = true;
            _spacePanStartOnScrollViewer = e.GetPosition(PreviewScrollViewer);
            _spacePanStartHorizontalOffset = PreviewScrollViewer.HorizontalOffset;
            _spacePanStartVerticalOffset = PreviewScrollViewer.VerticalOffset;
            PreviewHost.CaptureMouse();
            Cursor = Cursors.SizeAll;
            HintText.Text = "Space+좌클릭 드래그 화면 이동 모드입니다. 확대/축소는 마우스 휠로 사용하세요.";
            e.Handled = true;
            return;
        }


        var p = Clamp(e.GetPosition(PreviewHost));
        _dragging = true;

        // 기존 초록 영역을 클릭하면 별도 버튼 없이 즉시 위치 이동 모드로 전환합니다.
        // 영역 밖을 클릭/드래그할 때만 새 영역 선택으로 동작합니다.
        if (SelectedRegion is Int32Rect existing && Contains(existing, p))
        {
            _keyboardMoveEnabled = true;
            _movingExisting = true;
            _moveStartPoint = p;
            _moveStartRegion = existing;
            UpdateMoveModeUi();
            Focus();
            Keyboard.Focus(this);
        }
        else
        {
            _movingExisting = false;
            _moveStartPoint = p;
            _moveStartRegion = SelectedRegion ?? new Int32Rect();
            _start = p;
            _current = _start;
            SelectedRegion = null;
            _keyboardMoveEnabled = false;
            UpdateMoveModeUi();
            DrawRect();
        }

        SelectionShape.Visibility = Visibility.Visible;
        PreviewHost.CaptureMouse();
    }

    private void PreviewHost_MouseMove(object sender, MouseEventArgs e)
    {
        if (_spacePanDragging)
        {
            var p = e.GetPosition(PreviewScrollViewer);
            var dx = p.X - _spacePanStartOnScrollViewer.X;
            var dy = p.Y - _spacePanStartOnScrollViewer.Y;
            PreviewScrollViewer.ScrollToHorizontalOffset(Math.Max(0, _spacePanStartHorizontalOffset - dx));
            PreviewScrollViewer.ScrollToVerticalOffset(Math.Max(0, _spacePanStartVerticalOffset - dy));
            e.Handled = true;
            return;
        }


        if (!_dragging) return;
        var point = Clamp(e.GetPosition(PreviewHost));

        if (_movingExisting)
        {
            var dx = (int)Math.Round(point.X - _moveStartPoint.X);
            var dy = (int)Math.Round(point.Y - _moveStartPoint.Y);
            SelectedRegion = MoveRegion(_moveStartRegion, dx, dy);
            DrawRegion(SelectedRegion.Value);
            return;
        }

        _current = point;
        DrawRect();
    }

    private void PreviewHost_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_spacePanDragging)
        {
            _spacePanDragging = false;
            if (PreviewHost.IsMouseCaptured) PreviewHost.ReleaseMouseCapture();
            Cursor = null;
            HintText.Text = _zoom > 1.0
                ? $"{_zoom:0.##}배 확대 상태입니다. 마우스 휠=확대/축소, Space+좌클릭 드래그=확대 화면 이동, 더블클릭=1배/2배 전환입니다."
                : "기존 영역이 있으면 초록 영역으로 먼저 표시됩니다. 초록 영역을 클릭하면 바로 위치 이동 모드가 켜집니다. 영역 밖을 드래그하면 새 영역을 다시 잡습니다.";
            e.Handled = true;
            return;
        }


        if (!_dragging) return;
        _dragging = false;
        var p = Clamp(e.GetPosition(PreviewHost));
        PreviewHost.ReleaseMouseCapture();

        if (_movingExisting)
        {
            var dx = (int)Math.Round(p.X - _moveStartPoint.X);
            var dy = (int)Math.Round(p.Y - _moveStartPoint.Y);
            SelectedRegion = MoveRegion(_moveStartRegion, dx, dy);
            DrawRegion(SelectedRegion.Value);
        }
        else
        {
            _current = p;
            DrawRect();
            SelectedRegion = BuildRegion();
            if (SelectedRegion is Int32Rect region)
                DrawRegion(region);
        }

        _movingExisting = false;
        MoveModeButton.Visibility = SelectedRegion is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateMoveModeUi();
    }

    private void StopPointerModes(bool releaseCapture)
    {
        _dragging = false;
        _movingExisting = false;
        _spacePanDragging = false;
        Cursor = null;
        if (releaseCapture && PreviewHost.IsMouseCaptured)
            PreviewHost.ReleaseMouseCapture();
    }

    private Point Clamp(Point p)
    {
        var x = Math.Max(0, Math.Min(_source.PixelWidth - 1, p.X));
        var y = Math.Max(0, Math.Min(_source.PixelHeight - 1, p.Y));
        return new Point(x, y);
    }

    private static bool Contains(Int32Rect rect, Point p)
    {
        return p.X >= rect.X && p.X <= rect.X + rect.Width && p.Y >= rect.Y && p.Y <= rect.Y + rect.Height;
    }

    private Int32Rect ClampRegion(Int32Rect rect)
    {
        var w = Math.Max(5, Math.Min(rect.Width, _source.PixelWidth));
        var h = Math.Max(5, Math.Min(rect.Height, _source.PixelHeight));

        if (SelectedShape.RequiresSquareBounds())
        {
            var side = Math.Max(5, Math.Min(w, h));
            w = side;
            h = side;
        }

        var x = Math.Max(0, Math.Min(rect.X, _source.PixelWidth - w));
        var y = Math.Max(0, Math.Min(rect.Y, _source.PixelHeight - h));
        return new Int32Rect(x, y, w, h);
    }

    private Int32Rect MoveRegion(Int32Rect rect, int dx, int dy)
    {
        return ClampRegion(new Int32Rect(rect.X + dx, rect.Y + dy, rect.Width, rect.Height));
    }

    private Rect BuildSelectionBounds()
    {
        if (!SelectedShape.RequiresSquareBounds())
        {
            var left = Math.Min(_start.X, _current.X);
            var top = Math.Min(_start.Y, _current.Y);
            var width = Math.Abs(_current.X - _start.X);
            var height = Math.Abs(_current.Y - _start.Y);
            return new Rect(left, top, width, height);
        }

        var rawDx = _current.X - _start.X;
        var rawDy = _current.Y - _start.Y;
        var signX = rawDx < 0 ? -1 : 1;
        var signY = rawDy < 0 ? -1 : 1;
        var side = Math.Max(Math.Abs(rawDx), Math.Abs(rawDy));
        var maxX = signX > 0 ? _source.PixelWidth - 1 - _start.X : _start.X;
        var maxY = signY > 0 ? _source.PixelHeight - 1 - _start.Y : _start.Y;
        side = Math.Min(side, Math.Min(maxX, maxY));

        var squareLeft = signX > 0 ? _start.X : _start.X - side;
        var squareTop = signY > 0 ? _start.Y : _start.Y - side;
        return new Rect(squareLeft, squareTop, side, side);
    }

    private void DrawRect()
    {
        var bounds = BuildSelectionBounds();
        SelectionShape.Data = BuildShapeGeometry(bounds);
        MoveModeButton.Visibility = Visibility.Collapsed;
    }

    private void DrawRegion(Int32Rect rect)
    {
        var bounds = new Rect(rect.X, rect.Y, rect.Width, rect.Height);
        SelectionShape.Data = BuildShapeGeometry(bounds);
        PositionMoveButton(rect);
    }

    private Geometry BuildShapeGeometry(Rect bounds)
    {
        return SelectedShape switch
        {
            CropShape.Circle => new EllipseGeometry(bounds),
            CropShape.Diamond => BuildDiamondGeometry(bounds),
            _ => new RectangleGeometry(bounds)
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

    private void PositionMoveButton(Int32Rect rect)
    {
        MoveModeButton.Visibility = Visibility.Visible;
        var left = Math.Min(_source.PixelWidth - MoveModeButton.Width - 4, rect.X + rect.Width + 6);
        if (left < 0 || left + MoveModeButton.Width > _source.PixelWidth)
            left = Math.Max(0, rect.X);

        var top = rect.Y - MoveModeButton.Height - 6;
        if (top < 0) top = Math.Min(_source.PixelHeight - MoveModeButton.Height, rect.Y + rect.Height + 6);
        if (top < 0) top = 0;

        Canvas.SetLeft(MoveModeButton, left);
        Canvas.SetTop(MoveModeButton, top);
    }

    private Int32Rect? BuildRegion()
    {
        var bounds = BuildSelectionBounds();
        var x = (int)Math.Round(bounds.X);
        var y = (int)Math.Round(bounds.Y);
        var w = (int)Math.Round(bounds.Width);
        var h = (int)Math.Round(bounds.Height);
        if (w < 5 || h < 5) return null;
        return ClampRegion(new Int32Rect(x, y, w, h));
    }

    private void MoveSelectionBy(int dx, int dy)
    {
        if (SelectedRegion is not Int32Rect rect) return;
        SelectedRegion = MoveRegion(rect, dx, dy);
        DrawRegion(SelectedRegion.Value);
        SelectionShape.Visibility = Visibility.Visible;
    }

    private void ToggleMoveMode()
    {
        if (SelectedRegion is null)
        {
            MessageBox.Show("이동할 크롭 영역이 없습니다. 먼저 영역을 드래그해서 선택해주세요.", "위치 이동", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _keyboardMoveEnabled = !_keyboardMoveEnabled;
        UpdateMoveModeUi();
        if (_keyboardMoveEnabled)
        {
            Focus();
            Keyboard.Focus(this);
        }
    }

    private void UpdateMoveModeUi()
    {
        var on = _keyboardMoveEnabled && SelectedRegion is not null;
        MoveModeButton.Content = on ? "이동 ON" : "이동";
        MoveModeButton.Background = on ? new SolidColorBrush(Color.FromRgb(0, 116, 72)) : new SolidColorBrush(Color.FromRgb(21, 58, 45));
        MoveModeButton.BorderBrush = on ? new SolidColorBrush(Color.FromRgb(112, 255, 190)) : new SolidColorBrush(Color.FromRgb(0, 204, 102));

        BottomMoveModeButton.Content = on ? "위치 이동 모드 ON" : "위치 이동 모드 OFF";
        MoveModeHintText.Text = on
            ? "ON: 영역 드래그/방향키로 위치 이동, Shift+방향키=10px"
            : "OFF: 초록 영역 클릭 시 이동 ON / 영역 밖 드래그 시 새 영역 선택";
    }

    private void ToggleZoom(Point focusPoint)
    {
        var targetZoom = Math.Abs(_zoom - 1.0) < 0.01 ? 2.0 : 1.0;
        SetZoom(targetZoom, focusPoint);
    }

    private void SetZoom(double value, Point focusPoint)
    {
        var previousZoom = _zoom;
        _zoom = Math.Round(Math.Clamp(value, 0.5, 8.0), 3);
        if (Math.Abs(_zoom - previousZoom) < 0.001) return;

        _zoomTransform.ScaleX = _zoom;
        _zoomTransform.ScaleY = _zoom;

        HintText.Text = _zoom > 1.0
            ? $"{_zoom:0.##}배 확대 상태입니다. 마우스 휠=확대/축소, Space+좌클릭 드래그=확대 화면 이동, 더블클릭=1배/2배 전환입니다."
            : "기존 영역이 있으면 초록 영역으로 먼저 표시됩니다. 초록 영역을 클릭하면 바로 위치 이동 모드가 켜집니다. 영역 밖을 드래그하면 새 영역을 다시 잡습니다.";

        Dispatcher.BeginInvoke(new Action(() =>
        {
            PreviewScrollViewer.ScrollToHorizontalOffset(Math.Max(0, focusPoint.X * _zoom - PreviewScrollViewer.ViewportWidth / 2));
            PreviewScrollViewer.ScrollToVerticalOffset(Math.Max(0, focusPoint.Y * _zoom - PreviewScrollViewer.ViewportHeight / 2));
        }));
    }

    private void MoveModeButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleMoveMode();
        e.Handled = true;
    }

    private void PasteTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        PasteCurrentTemplate();
        e.Handled = true;
    }

    private void PasteCurrentTemplate()
    {
        var template = CurrentPasteTemplate;
        if (template == null) return;
        ApplyTemplate(template);
    }

    private void ApplyTemplate(CropTemplate template)
    {
        SelectedShape = template.Shape;
        SetShapeCombo(template.Shape);
        SelectedRegion = ClampRegion(template.Region);
        StopPointerModes(releaseCapture: true);
        SelectionShape.Visibility = Visibility.Visible;
        MoveModeButton.Visibility = Visibility.Visible;
        DrawRegion(SelectedRegion.Value);
        HintText.Text = $"복사된 크롭 형태를 붙여넣었습니다. {template.Shape.ToKoreanName()} / X:{SelectedRegion.Value.X} Y:{SelectedRegion.Value.Y} W:{SelectedRegion.Value.Width} H:{SelectedRegion.Value.Height}";
        UpdateMoveModeUi();
        Focus();
        Keyboard.Focus(this);
    }

    private void CopyCurrentSelectionTemplate()
    {
        if (SelectedRegion is not Int32Rect region) return;
        CopiedTemplate = new CropTemplate
        {
            X = region.X,
            Y = region.Y,
            Width = region.Width,
            Height = region.Height,
            Shape = SelectedShape
        };
        PasteTemplateButton.Visibility = Visibility.Visible;
        HintText.Text = $"현재 크롭 형태를 복사했습니다. {SelectedShape.ToKoreanName()} / X:{region.X} Y:{region.Y} W:{region.Width} H:{region.Height}";
    }

    private void PreviewHost_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        StopPointerModes(releaseCapture: true);

        var menu = new ContextMenu();
        if (SelectedRegion is not null)
        {
            var copy = new MenuItem
            {
                Header = "현재 크롭 형태 복사",
                Padding = new Thickness(12, 6, 12, 6)
            };
            copy.Click += (_, _) => CopyCurrentSelectionTemplate();
            menu.Items.Add(copy);
        }

        if (CurrentPasteTemplate is not null)
        {
            var paste = new MenuItem
            {
                Header = "크롭 형태 붙여넣기",
                Padding = new Thickness(12, 6, 12, 6)
            };
            paste.Click += (_, _) => PasteCurrentTemplate();
            menu.Items.Add(paste);
        }

        if (menu.Items.Count == 0) return;

        PreviewHost.ContextMenu = menu;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void PreviewHost_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_spacePanDragging || _dragging) return;

        var focusPoint = Clamp(e.GetPosition(PreviewHost));
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 1.05 : 1.15;
        var targetZoom = e.Delta > 0 ? _zoom * step : _zoom / step;
        SetZoom(targetZoom, focusPoint);
        e.Handled = true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        SelectedRegion ??= BuildRegion();
        if (SelectedRegion is null)
        {
            MessageBox.Show("영역을 먼저 드래그해주세요.", "영역 없음", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            return;
        }

        if (e.Key == Key.Enter)
        {
            Ok_Click(sender, e);
            return;
        }

        if (e.Key == Key.Space)
        {
            Cursor = Cursors.SizeAll;
            e.Handled = true;
            return;
        }

        // 이동 모드가 켜진 뒤에만 방향키를 가로채서 영역을 이동합니다.
        // OFF 상태에서는 ScrollViewer가 방향키를 받아 화면 스크롤에 사용합니다.
        if (!_keyboardMoveEnabled || SelectedRegion is null)
            return;

        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        switch (e.Key)
        {
            case Key.Left:
                MoveSelectionBy(-step, 0);
                e.Handled = true;
                break;
            case Key.Right:
                MoveSelectionBy(step, 0);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSelectionBy(0, -step);
                e.Handled = true;
                break;
            case Key.Down:
                MoveSelectionBy(0, step);
                e.Handled = true;
                break;
        }
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && !_spacePanDragging)
        {
            Cursor = null;
            e.Handled = true;
        }
    }

    private void ShapeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ShapeCombo?.SelectedItem is ComboBoxItem item && item.Tag is string tag && Enum.TryParse<CropShape>(tag, out var shape))
        {
            SelectedShape = shape;
            if (ShapeHintText != null)
            {
                ShapeHintText.Text = shape.RequiresSquareBounds()
                    ? $"{shape.ToKoreanName()}은 정사각형 기준으로 잡힙니다."
                    : "직사각형은 자유 비율로 잡을 수 있습니다.";
            }

            if (SelectedRegion is Int32Rect region)
            {
                SelectedRegion = ClampRegion(region);
                DrawRegion(SelectedRegion.Value);
            }
            else if (_dragging)
            {
                DrawRect();
            }
        }
    }

    private void SetShapeCombo(CropShape shape)
    {
        if (ShapeCombo == null) return;
        foreach (var item in ShapeCombo.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string tag && Enum.TryParse<CropShape>(tag, out var itemShape) && itemShape == shape)
            {
                ShapeCombo.SelectedItem = item;
                return;
            }
        }
        ShapeCombo.SelectedIndex = 0;
    }
}

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;

namespace CropPipViewer;

public sealed class AppSettings
{
    // Preset slots: fixed to 6. ActivePresetIndex is zero-based.
    // The root AppSettings properties below represent the currently loaded preset
    // and are mirrored into Presets[ActivePresetIndex] on save/switch.
    public int ActivePresetIndex { get; set; } = 0;
    public List<PipPreset> Presets { get; set; } = new();

    public string? LastTargetTitle { get; set; }
    public int LastTargetProcessId { get; set; }
    // WGC source size captured when this preset first records its baseline MapleStory target.
    // This baseline is not overwritten by later target resolution changes. Used only for visibility/matching; crop coordinates are not automatically rescaled.
    public int TargetSourceWidth { get; set; }
    public int TargetSourceHeight { get; set; }
    // v30b: true only after the user explicitly presses "현재 설정 저장" while a Maple target is connected.
    // Auto-save, target re-selection, PIP movement, or resolution changes must never establish/overwrite this reference.
    public bool TargetResolutionCapturedAtPresetSave { get; set; } = false;
    public List<CropItem> Crops { get; set; } = new();
    public double OverlayLeft { get; set; } = 1200;
    public double OverlayTop { get; set; } = 700;
    public double OverlayWidth { get; set; } = 360;
    public double OverlayHeight { get; set; } = 220;
    // Legacy/default crop image opacity. Used as the fallback for PiPs that do not yet have an individual value.
    public double Opacity { get; set; } = 0.92;
    // v33c: per-PiP crop image opacity. Key "__main__" means the main PiP; detached PiPs use their group id.
    // This affects the cropped images only and does not affect PiP background/frame opacity.
    public Dictionary<string, double> PipCropOpacityByKey { get; set; } = new();

    // Per-PiP background/frame opacity. Key "__main__" means the main PiP; detached PiPs use their group id.
    public double PipBackgroundOpacity { get; set; } = 0.13;
    public Dictionary<string, double> PipBackgroundOpacityByKey { get; set; } = new();

    // Per-PiP window placement. Key "__main__" means the main PiP; detached PiPs use their group id.
    public Dictionary<string, PipWindowPlacement> PipWindowPlacementsByKey { get; set; } = new();
    public double Scale { get; set; } = 1.0;
    public bool ClickThrough { get; set; } = false;
    public bool TopMost { get; set; } = true;
    public bool ResizeItemsWithWindow { get; set; } = false;

    // When enabled, the main PiP keeps the same screen-relative offset from the
    // selected MapleStory client area. Detached PiPs keep their own runtime offsets.
    public bool PipPositionLockedToTarget { get; set; } = false;
    public double OverlayTargetOffsetX { get; set; } = 0;
    public double OverlayTargetOffsetY { get; set; } = 0;

    // PiP refresh throttle in milliseconds. 16ms targets 60 FPS, but actual FPS depends on capture/conversion cost.
    public int CaptureIntervalMs { get; set; } = 100;

    // When true, PiP refreshes are requested as soon as WGC receives a frame, then throttled by CaptureIntervalMs.
    // The timer remains as a fallback so position lock and refresh still work if frame events are sparse.
    public bool UseFrameArrivedRefresh { get; set; } = true;

    // Shows lightweight FPS / frame processing time text inside each PiP.
    public bool ShowCapturePerformanceStats { get; set; } = false;

    // Windows FilterKeys integration. These values are saved per preset and applied when a preset is loaded.
    public bool FilterKeysEnabled { get; set; } = false;
    public bool FilterKeysMapleOnly { get; set; } = true;
    public bool FilterKeysTurnOffOnExit { get; set; } = true;
    public int FilterKeysAcceptDelayMs { get; set; } = 0;
    public int FilterKeysRepeatDelayMs { get; set; } = 250;
    public int FilterKeysRepeatRateMs { get; set; } = 25;

    // Floating pill-shaped PiP button for FilterKeys status/toggle. Saved per preset.
    public bool FilterKeysPillVisible { get; set; } = false;
    // When true, the FilterKeys pill can be clicked to toggle but cannot be dragged. Saved per preset.
    public bool FilterKeysPillDragLocked { get; set; } = false;
    public double FilterKeysPillLeft { get; set; } = 980;
    public double FilterKeysPillTop { get; set; } = 160;

    // When PIP position lock is enabled, the FilterKeys pill follows the selected MapleStory client area too.
    public double? FilterKeysPillTargetOffsetX { get; set; }
    public double? FilterKeysPillTargetOffsetY { get; set; }
    public int FilterKeysPillTargetBaselineWidth { get; set; }
    public int FilterKeysPillTargetBaselineHeight { get; set; }

    // Burst cooldown monitor. Saved per preset. v30 detection uses per-skill learned READY / post-use states; no OCR or global brightness threshold.
    public bool BurstMonitorVisible { get; set; } = false;
    public double BurstCooldownReductionSeconds { get; set; } = 0;
    public double BurstCooldownReductionPercent { get; set; } = 0;
    // Per-role visual detection delay correction. This value is subtracted from the internally calculated remaining cooldown.
    public double SemiBurstTimingCorrectionSeconds { get; set; } = 0;
    public double BurstTimingCorrectionSeconds { get; set; } = 0;
    public double OriginBurstTimingCorrectionSeconds { get; set; } = 0;
    // v33: optional configured skill keys. Only these virtual keys are polled, and only while the selected MapleStory process is foreground.
    public int SemiBurstTriggerVirtualKey { get; set; } = 0;
    public int BurstTriggerVirtualKey { get; set; } = 0;
    public int OriginBurstTriggerVirtualKey { get; set; } = 0;
    public string SemiBurstTriggerKeyName { get; set; } = string.Empty;
    public string BurstTriggerKeyName { get; set; } = string.Empty;
    public string OriginBurstTriggerKeyName { get; set; } = string.Empty;
    // Remaining seconds below which the monitor shows/flashes "임박". Saved per preset.
    public double BurstWarningThresholdSeconds { get; set; } = 5;
    // Overall opacity of the burst monitor window (text/status pills/background together).
    public double BurstMonitorOpacity { get; set; } = 0.88;
    // Opacity of only the burst monitor root background. 0 = fully transparent, 1 = opaque.
    public double BurstMonitorBackgroundOpacity { get; set; } = 0.18;
    public double BurstMonitorLeft { get; set; } = 980;
    public double BurstMonitorTop { get; set; } = 220;
    public double? BurstMonitorTargetOffsetX { get; set; }
    public double? BurstMonitorTargetOffsetY { get; set; }
    public int BurstMonitorTargetBaselineWidth { get; set; }
    public int BurstMonitorTargetBaselineHeight { get; set; }

    // v30: per-crop few-shot cooldown detector calibration.
    // The detector never guesses READY/COOLDOWN from global brightness; it uses samples explicitly learned for each crop.
    public Dictionary<string, BurstCalibrationProfile> BurstCalibrationByCropId { get; set; } = new();
}


public sealed class PipPreset
{
    public int Slot { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? LastTargetTitle { get; set; }
    public int LastTargetProcessId { get; set; }
    // WGC source size captured when this preset first records its baseline MapleStory target.
    // This baseline is not overwritten by later target resolution changes. Used only for visibility/matching; crop coordinates are not automatically rescaled.
    public int TargetSourceWidth { get; set; }
    public int TargetSourceHeight { get; set; }
    // v30b: true only after the user explicitly presses "현재 설정 저장" while a Maple target is connected.
    // Auto-save, target re-selection, PIP movement, or resolution changes must never establish/overwrite this reference.
    public bool TargetResolutionCapturedAtPresetSave { get; set; } = false;
    public List<CropItem> Crops { get; set; } = new();
    public double OverlayLeft { get; set; } = 1200;
    public double OverlayTop { get; set; } = 700;
    public double OverlayWidth { get; set; } = 360;
    public double OverlayHeight { get; set; } = 220;
    public double Opacity { get; set; } = 0.92;
    public Dictionary<string, double> PipCropOpacityByKey { get; set; } = new();
    public double PipBackgroundOpacity { get; set; } = 0.13;
    public Dictionary<string, double> PipBackgroundOpacityByKey { get; set; } = new();
    public Dictionary<string, PipWindowPlacement> PipWindowPlacementsByKey { get; set; } = new();
    public double Scale { get; set; } = 1.0;
    public bool ClickThrough { get; set; } = false;
    public bool TopMost { get; set; } = true;
    public bool ResizeItemsWithWindow { get; set; } = false;
    public bool PipPositionLockedToTarget { get; set; } = false;
    public double OverlayTargetOffsetX { get; set; } = 0;
    public double OverlayTargetOffsetY { get; set; } = 0;
    public int CaptureIntervalMs { get; set; } = 100;
    public bool UseFrameArrivedRefresh { get; set; } = true;
    public bool ShowCapturePerformanceStats { get; set; } = false;
    public bool FilterKeysEnabled { get; set; } = false;
    public bool FilterKeysMapleOnly { get; set; } = true;
    public bool FilterKeysTurnOffOnExit { get; set; } = true;
    public int FilterKeysAcceptDelayMs { get; set; } = 0;
    public int FilterKeysRepeatDelayMs { get; set; } = 250;
    public int FilterKeysRepeatRateMs { get; set; } = 25;
    public bool FilterKeysPillVisible { get; set; } = false;
    // When true, the FilterKeys pill can be clicked to toggle but cannot be dragged. Saved per preset.
    public bool FilterKeysPillDragLocked { get; set; } = false;
    public double FilterKeysPillLeft { get; set; } = 980;
    public double FilterKeysPillTop { get; set; } = 160;
    public double? FilterKeysPillTargetOffsetX { get; set; }
    public double? FilterKeysPillTargetOffsetY { get; set; }
    public int FilterKeysPillTargetBaselineWidth { get; set; }
    public int FilterKeysPillTargetBaselineHeight { get; set; }

    public bool BurstMonitorVisible { get; set; } = false;
    public double BurstCooldownReductionSeconds { get; set; } = 0;
    public double BurstCooldownReductionPercent { get; set; } = 0;
    // Per-role visual detection delay correction. This value is subtracted from the internally calculated remaining cooldown.
    public double SemiBurstTimingCorrectionSeconds { get; set; } = 0;
    public double BurstTimingCorrectionSeconds { get; set; } = 0;
    public double OriginBurstTimingCorrectionSeconds { get; set; } = 0;
    public int SemiBurstTriggerVirtualKey { get; set; } = 0;
    public int BurstTriggerVirtualKey { get; set; } = 0;
    public int OriginBurstTriggerVirtualKey { get; set; } = 0;
    public string SemiBurstTriggerKeyName { get; set; } = string.Empty;
    public string BurstTriggerKeyName { get; set; } = string.Empty;
    public string OriginBurstTriggerKeyName { get; set; } = string.Empty;
    // Remaining seconds below which the monitor shows/flashes "임박". Saved per preset.
    public double BurstWarningThresholdSeconds { get; set; } = 5;
    // Overall opacity of the burst monitor window (text/status pills/background together).
    public double BurstMonitorOpacity { get; set; } = 0.88;
    // Opacity of only the burst monitor root background. 0 = fully transparent, 1 = opaque.
    public double BurstMonitorBackgroundOpacity { get; set; } = 0.18;
    public double BurstMonitorLeft { get; set; } = 980;
    public double BurstMonitorTop { get; set; } = 220;
    public double? BurstMonitorTargetOffsetX { get; set; }
    public double? BurstMonitorTargetOffsetY { get; set; }
    public int BurstMonitorTargetBaselineWidth { get; set; }
    public int BurstMonitorTargetBaselineHeight { get; set; }
    public Dictionary<string, BurstCalibrationProfile> BurstCalibrationByCropId { get; set; } = new();

    [JsonIgnore]
    public string DisplayName => $"프리셋 {Slot} · {Name}";
}

public sealed class PipWindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double BackgroundOpacity { get; set; } = 0.13;
    public double? TargetOffsetX { get; set; }
    public double? TargetOffsetY { get; set; }
    public int TargetBaselineWidth { get; set; }
    public int TargetBaselineHeight { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CropShape
{
    Rectangle,
    Circle,
    Square,
    Diamond
}

public static class CropShapeExtensions
{
    public static string ToKoreanName(this CropShape shape) => shape switch
    {
        CropShape.Circle => "원형",
        CropShape.Square => "정사각형",
        CropShape.Diamond => "마름모",
        _ => "직사각형"
    };

    public static bool RequiresSquareBounds(this CropShape shape)
    {
        return shape is CropShape.Circle or CropShape.Square or CropShape.Diamond;
    }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BurstRole
{
    None,
    SemiBurst,
    Burst,
    OriginBurst
}

public static class BurstRoleExtensions
{
    public static string ToKoreanName(this BurstRole role) => role switch
    {
        BurstRole.SemiBurst => "준극딜",
        BurstRole.Burst => "극딜",
        BurstRole.OriginBurst => "오리진",
        _ => "없음"
    };

    public static double BaseCooldownSeconds(this BurstRole role) => role switch
    {
        BurstRole.SemiBurst => 60,
        BurstRole.Burst => 120,
        BurstRole.OriginBurst => 360,
        _ => 0
    };

    // Imminent is intentionally role-independent: flash only during the final 5 seconds.
    public static double WarningThresholdSeconds(this BurstRole role) => role == BurstRole.None ? 0 : 5;
}

public sealed class BurstCalibrationProfile
{
    // Increment when the feature extractor changes. Old profiles are then ignored instead of silently misclassifying.
    public int FeatureVersion { get; set; } = 1;
    public string CropId { get; set; } = string.Empty;
    public int CropX { get; set; }
    public int CropY { get; set; }
    public int CropWidth { get; set; }
    public int CropHeight { get; set; }
    public int TargetSourceWidth { get; set; }
    public int TargetSourceHeight { get; set; }

    public int ReadySampleCount { get; set; }
    public double[] ReadyMean { get; set; } = Array.Empty<double>();
    public double[] ReadyVariance { get; set; } = Array.Empty<double>();

    // "Cooldown" samples are intentionally the first few seconds after skill use.
    // The classifier only needs to identify the READY -> COOLDOWN onset; the internal timer takes over afterwards.
    public int CooldownSampleCount { get; set; }
    public double[] CooldownMean { get; set; } = Array.Empty<double>();
    public double[] CooldownVariance { get; set; } = Array.Empty<double>();

    public DateTime? ReadyLearnedUtc { get; set; }
    public DateTime? CooldownLearnedUtc { get; set; }
}

public sealed class CropTemplate
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public CropShape Shape { get; set; } = CropShape.Rectangle;
    public string BorderColorHex { get; set; } = "#00FF7F";
    public double BorderOpacity { get; set; } = 0;

    [JsonIgnore]
    public Int32Rect Region => new(X, Y, Width, Height);

    public static CropTemplate FromCrop(CropItem crop) => new()
    {
        X = crop.X,
        Y = crop.Y,
        Width = crop.Width,
        Height = crop.Height,
        Shape = crop.Shape,
        BorderColorHex = string.IsNullOrWhiteSpace(crop.BorderColorHex) ? "#00FF7F" : crop.BorderColorHex,
        BorderOpacity = crop.BorderOpacity
    };
}



public sealed class CropBorderStyleTemplate
{
    public string BorderColorHex { get; set; } = "#00FF7F";
    public double BorderOpacity { get; set; } = 0;

    public static CropBorderStyleTemplate FromCrop(CropItem crop) => new()
    {
        BorderColorHex = string.IsNullOrWhiteSpace(crop.BorderColorHex) ? "#00FF7F" : crop.BorderColorHex,
        BorderOpacity = crop.BorderOpacity
    };
}

public sealed class CropItem : INotifyPropertyChanged
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = "Crop";
    private bool _enabled = true;
    private int _x;
    private int _y;
    private int _width;
    private int _height;
    private double _displayX;
    private double _displayY;
    private double _displayWidth;
    private double _displayHeight;
    private double _pipOffsetX;
    private double _pipOffsetY;
    private CropShape _shape = CropShape.Rectangle;
    private BurstRole _burstRole = BurstRole.None;
    private string _borderColorHex = "#00FF7F";
    private double _borderOpacity;
    private double _rotationAngle;
    private string? _detachedGroupIdRuntime;
    private ImageSource? _thumbnail;

    public event PropertyChangedEventHandler? PropertyChanged;

    // Runtime-only thumbnail used by the main grid to identify crops quickly.
    [JsonIgnore]
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set => SetField(ref _thumbnail, value);
    }

    public string Id
    {
        get => _id;
        set => SetField(ref _id, string.IsNullOrWhiteSpace(value) ? Guid.NewGuid().ToString("N") : value);
    }

    public string Name
    {
        get => _name;
        set => SetField(ref _name, string.IsNullOrWhiteSpace(value) ? "Crop" : value);
    }

    public bool Enabled
    {
        get => _enabled;
        set => SetField(ref _enabled, value);
    }

    // Target-window client-relative crop region in physical pixels.
    public int X
    {
        get => _x;
        set => SetField(ref _x, value);
    }

    public int Y
    {
        get => _y;
        set => SetField(ref _y, value);
    }

    public int Width
    {
        get => _width;
        set => SetField(ref _width, value);
    }

    public int Height
    {
        get => _height;
        set => SetField(ref _height, value);
    }

    // Overlay arrangement coordinates in WPF device-independent pixels.
    public double DisplayX
    {
        get => _displayX;
        set => SetField(ref _displayX, value);
    }

    public double DisplayY
    {
        get => _displayY;
        set => SetField(ref _displayY, value);
    }

    public double DisplayWidth
    {
        get => _displayWidth;
        set => SetField(ref _displayWidth, value);
    }

    public double DisplayHeight
    {
        get => _displayHeight;
        set => SetField(ref _displayHeight, value);
    }

    // Extra visual-only offset applied inside the PiP overlay.
    // This does not change the source-window crop rectangle (X/Y/Width/Height)
    // and is mainly used for keyboard nudging selected crop views inside PiP.
    public double PipOffsetX
    {
        get => _pipOffsetX;
        set => SetField(ref _pipOffsetX, value);
    }

    public double PipOffsetY
    {
        get => _pipOffsetY;
        set => SetField(ref _pipOffsetY, value);
    }

    // Visual mask used inside the PiP overlay.
    public CropShape Shape
    {
        get => _shape;
        set
        {
            if (SetField(ref _shape, value))
            {
                OnPropertyChanged(nameof(ShapeName));
            }
        }
    }

    [JsonIgnore]
    public string ShapeName => Shape.ToKoreanName();

    // Optional role used by the burst cooldown monitor. One crop can belong to one burst role.
    public BurstRole BurstRole
    {
        get => _burstRole;
        set
        {
            if (SetField(ref _burstRole, value))
            {
                OnPropertyChanged(nameof(BurstRoleName));
            }
        }
    }

    [JsonIgnore]
    public string BurstRoleName => BurstRole.ToKoreanName();


    // Optional colored border drawn around the cropped region itself inside the PiP window.
    // Border opacity 0 means hidden. The PiP window border is unaffected.
    public string BorderColorHex
    {
        get => _borderColorHex;
        set => SetField(ref _borderColorHex, string.IsNullOrWhiteSpace(value) ? "#00FF7F" : value.Trim());
    }

    public double BorderOpacity
    {
        get => _borderOpacity;
        set => SetField(ref _borderOpacity, Math.Clamp(value, 0, 1));
    }

    // Persistent group key. Crops with the same group key appear in the same detached PiP.
    // Older code used the Runtime suffix, but the value is now saved so detached PiP groups survive restart.
    public string? DetachedGroupIdRuntime
    {
        get => _detachedGroupIdRuntime;
        set
        {
            if (SetField(ref _detachedGroupIdRuntime, value))
            {
                OnPropertyChanged(nameof(IsDetachedRuntime));
                OnPropertyChanged(nameof(PipLabel));
            }
        }
    }

    [JsonIgnore]
    public string PipLabel
    {
        get
        {
            var groupId = DetachedGroupIdRuntime;
            return string.IsNullOrWhiteSpace(groupId)
                ? "메인 PIP"
                : $"분리 {groupId[..Math.Min(4, groupId.Length)]}";
        }
    }

    // Runtime-only compatibility flag. The exact group id is persisted through DetachedGroupIdRuntime.
    [JsonIgnore]
    public bool IsDetachedRuntime
    {
        get => !string.IsNullOrWhiteSpace(DetachedGroupIdRuntime);
        set
        {
            if (value)
            {
                if (string.IsNullOrWhiteSpace(DetachedGroupIdRuntime)) DetachedGroupIdRuntime = Id;
            }
            else
            {
                DetachedGroupIdRuntime = null;
            }
        }
    }

    // Clockwise rotation angle for this crop inside the PiP overlay.
    public double RotationAngle
    {
        get => _rotationAngle;
        set => SetField(ref _rotationAngle, value);
    }

    public void ApplyRegion(Int32Rect region, CropShape shape)
    {
        X = region.X;
        Y = region.Y;
        Width = region.Width;
        Height = region.Height;
        Shape = shape;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class WindowInfo
{
    public IntPtr Hwnd { get; set; }
    public string Title { get; set; } = string.Empty;
    public int ProcessId { get; set; }

    public override string ToString()
    {
        var pid = ProcessId > 0 ? $" [{ProcessId}]" : string.Empty;
        return $"{Title}{pid}";
    }
}

public readonly record struct RectI(int X, int Y, int Width, int Height)
{
    [JsonIgnore]
    public int Right => X + Width;
    [JsonIgnore]
    public int Bottom => Y + Height;
}

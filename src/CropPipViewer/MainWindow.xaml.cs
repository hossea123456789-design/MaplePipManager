using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CropPipViewer;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly ObservableCollection<CropItem> _crops;
    private bool _isInitializing = true;
    private bool _isSwitchingPreset;
    private bool _isUpdatingPresetUi;
    private const int PresetSlotCount = 6;
    private WgcCaptureManager? _capture;
    private OverlayWindow? _overlay;
    private const string MainPipKey = "__main__";
    private readonly Dictionary<string, OverlayWindow> _detachedOverlays = new();
    // PiP selection order matters: Shift-first selected PiP is the anchor for stack alignment.
    private readonly List<string> _selectedPipKeys = new();
    private bool _isUpdatingPipSelector;
    private CropTemplate? _copiedCropTemplate;
    private CropBorderStyleTemplate? _copiedCropBorderStyle;
    private bool _isUpdatingCropBorderUi;
    private WindowInfo? _target;
    private DateTime _lastAutoSaveUtc = DateTime.MinValue;
    private bool _refreshQueued;
    private bool _isApplyingPipGroupChange;
    private bool _isApplyingHistory;
    private const int MaxHistoryDepth = 30;
    private readonly List<HistorySnapshot> _undoHistory = new();
    private readonly List<HistorySnapshot> _redoHistory = new();
    private readonly DispatcherTimer _filterKeysTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private bool _filterKeysAppliedByThisApp;
    private string _lastAppliedFilterKeysSignature = string.Empty;
    private bool _isUpdatingFilterKeysUi;
    private FilterKeysPillWindow? _filterKeysPillWindow;
    private int _filterKeysPillSessionBaselineWidth;
    private int _filterKeysPillSessionBaselineHeight;
    private DateTime _lastFilterKeysPillTopmostUtc = DateTime.MinValue;
    private readonly DispatcherTimer _thumbnailTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private bool _isUpdatingCropThumbnails;
    private readonly DispatcherTimer _burstMonitorTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    // v33a: polls registered burst skill keys at high frequency so immediate recasts are not missed.
    private readonly DispatcherTimer _burstInputTimer = new() { Interval = TimeSpan.FromMilliseconds(10) };
    private BurstMonitorWindow? _burstMonitorWindow;
    private readonly Dictionary<BurstRole, BurstRoleRuntimeState> _burstStates = new();
    private BurstLearningSession? _burstLearningSession;
    private BurstRole? _awaitingBurstTriggerKeyRole;
    private bool _isUpdatingBurstUi;
    private DateTime _lastBurstMonitorTopmostUtc = DateTime.MinValue;
    // Session-only global PiP visibility toggle. When true, auxiliary PiPs (including burst monitor) must stay hidden too.
    private bool _allPipsTemporarilyHidden;
    private const int BurstFeatureVersion = 30;
    private const int BurstFeatureGridSize = 12;
    private const int BurstLearningMinimumSamples = 10;
    private static readonly TimeSpan BurstLearningDuration = TimeSpan.FromSeconds(3.2);
    private const double BurstClassifierMinConfidence = 0.68;
    private const int BurstReadyConfirmFrames = 3;
    private const int BurstUseConfirmFrames = 4;
    // v33 hybrid temporal verification: normal sampling stays lightweight, while the final seconds and
    // immediate-recast grace window switch to a much faster cadence so a one-frame READY gap is not required.
    private const int BurstNormalMonitorIntervalMs = 33;
    private const int BurstFastMonitorIntervalMs = 16;
    private const double BurstFastWindowBeforeReadySeconds = 3.0;
    private const double BurstRapidRecastGraceSeconds = 2.4;
    private const double BurstRapidRecastMinConfidence = 0.82;
    private const double BurstRapidRecastMinScore = 0.35;
    private const double BurstRapidRecastMinFeatureJumpRatio = 0.30;
    private const double BurstForcedReadyMinConfidence = 0.88;
    private const double BurstForcedReadyHoldSeconds = 0.75;
    private const double BurstGlobalResetMinConfidence = 0.80;
    private const double BurstGlobalResetHoldSeconds = 0.28;
    private const double BurstInputCandidateWindowSeconds = 1.10;
    private const double BurstInputCooldownMinConfidence = 0.62;
    private const double BurstInputBaselineReadyMinConfidence = 0.58;
    private const double BurstInputNearExpirySeconds = 2.75;
    private const double BurstInputNearExpiryMinJumpRatio = 0.16;
    private const int BurstInputConfirmFrames = 2;
    private sealed class CropBorderPaletteOption
    {
        public string Name { get; init; } = string.Empty;
        public string Hex { get; init; } = "#00FF7F";
        public Brush Brush => new SolidColorBrush((Color)ColorConverter.ConvertFromString(Hex));
    }

    private static readonly IReadOnlyList<CropBorderPaletteOption> CropBorderPalette = new List<CropBorderPaletteOption>
    {
        new() { Name = "초록", Hex = "#00FF7F" },
        new() { Name = "라임", Hex = "#7CFC00" },
        new() { Name = "하늘", Hex = "#3BA7FF" },
        new() { Name = "파랑", Hex = "#1E5BFF" },
        new() { Name = "노랑", Hex = "#FFD84A" },
        new() { Name = "주황", Hex = "#FF9F1C" },
        new() { Name = "빨강", Hex = "#FF4D4F" },
        new() { Name = "분홍", Hex = "#FF5CA8" },
        new() { Name = "보라", Hex = "#9B5DFF" },
        new() { Name = "흰색", Hex = "#FFFFFF" },
        new() { Name = "민트", Hex = "#2FE6C5" },
    };

    public MainWindow()
    {
        _settings = SettingsService.Load();
        NormalizePresetsAndLoadActive();
        _settings.Crops ??= new List<CropItem>();
        _settings.PipCropOpacityByKey ??= new Dictionary<string, double>();
        _settings.PipBackgroundOpacityByKey ??= new Dictionary<string, double>();
        _settings.PipWindowPlacementsByKey ??= new Dictionary<string, PipWindowPlacement>();
        _settings.BurstCalibrationByCropId ??= new Dictionary<string, BurstCalibrationProfile>();
        _settings.CaptureIntervalMs = ClampCaptureInterval(_settings.CaptureIntervalMs);
        InitializeComponent();
        _crops = new ObservableCollection<CropItem>(_settings.Crops);
        CropGrid.ItemsSource = _crops;
        InitializeCropBorderPaletteUi();
        _crops.CollectionChanged += Crops_CollectionChanged;
        foreach (var crop in _crops) crop.PropertyChanged += Crop_PropertyChanged;

        UpdatePresetSelectorItems();
        OpacitySlider.Value = GetPipCropOpacity(MainPipKey);
        PipBackgroundOpacitySlider.Value = GetPipBackgroundOpacity(MainPipKey);
        ScaleSlider.Value = _settings.Scale;
        TopMostCheck.IsChecked = _settings.TopMost;
        ClickThroughCheck.IsChecked = _settings.ClickThrough;
        ResizeItemsWithWindowCheck.IsChecked = _settings.ResizeItemsWithWindow;
        PipPositionLockCheck.IsChecked = _settings.PipPositionLockedToTarget;
        FrameEventRefreshCheck.IsChecked = _settings.UseFrameArrivedRefresh;
        ShowPerformanceStatsCheck.IsChecked = _settings.ShowCapturePerformanceStats;
        SetCaptureSpeedComboFromSettings();
        UpdateCaptureSpeedUi();
        UpdateFilterKeysUiFromSettings();
        UpdateFilterKeysPillVisibility();
        UpdateBurstUiFromSettings();
        UpdateBurstMonitorVisibility();
        _filterKeysTimer.Tick += (_, _) =>
        {
            UpdateFilterKeysRuntime(applyWhenNeeded: true);
            TickFilterKeysPillRuntime();
        };
        _filterKeysTimer.Start();
        _thumbnailTimer.Tick += (_, _) => UpdateCropThumbnails();
        _thumbnailTimer.Start();
        _burstMonitorTimer.Tick += (_, _) => TickBurstMonitorRuntime();
        _burstMonitorTimer.Start();
        _burstInputTimer.Tick += (_, _) => TickBurstInputMonitor();
        _burstInputTimer.Start();
        RefreshWindows();
        UpdateOpacityUi(GetPipCropOpacity(MainPipKey));
        UpdatePipBackgroundOpacityUi(PipBackgroundOpacitySlider.Value);
        UpdateScaleUi(_settings.Scale);
        UpdatePipSelectorItems();
        UpdateResolutionMatchUi();
        UpdateCropBorderEditorUi();
        _isInitializing = false;
        PreviewKeyDown += Window_PreviewKeyDown;
        Loaded += (_, _) =>
        {
            SettingsService.Log("main_loaded | v33_hybrid_burst_verification");
            UpdateResolutionMatchUi();
            UpdateCropThumbnails(force: true);
        };
        Closing += (_, _) =>
        {
            SaveSettings();
            _thumbnailTimer.Stop();
            _burstMonitorTimer.Stop();
            _burstInputTimer.Stop();
            CloseBurstMonitorWindow();
            CloseFilterKeysPillWindow();
            if (_settings.FilterKeysTurnOffOnExit)
            {
                TryTurnOffFilterKeys("종료 시 필터키 끄기");
            }
            foreach (var detached in _detachedOverlays.Values.ToList())
            {
                detached.ForceClose();
            }
            _detachedOverlays.Clear();
            _overlay?.ForceClose();
            _overlay = null;
            _capture?.Dispose();
        };
    }

    private void NormalizePresetsAndLoadActive()
    {
        _settings.Presets ??= new List<PipPreset>();
        _settings.ActivePresetIndex = Math.Clamp(_settings.ActivePresetIndex, 0, PresetSlotCount - 1);

        // First run after upgrading from pre-v22: seed slot 1 from the existing root settings.
        if (_settings.Presets.Count == 0)
        {
            var seeded = CreatePresetFromCurrentRoot(slot: 1, name: "기본");
            _settings.Presets.Add(seeded);
        }

        for (var i = 0; i < PresetSlotCount; i++)
        {
            if (_settings.Presets.Count <= i)
            {
                _settings.Presets.Add(CreateEmptyPreset(i + 1));
            }
            else
            {
                NormalizePreset(_settings.Presets[i], i + 1);
            }
        }

        if (_settings.Presets.Count > PresetSlotCount)
        {
            _settings.Presets = _settings.Presets.Take(PresetSlotCount).ToList();
        }

        var activePreset = _settings.Presets[_settings.ActivePresetIndex];
        RecoverCropBorderStylesFromRootIfNeeded(activePreset);
        ApplyPresetToRootSettings(activePreset);
    }

    private void RecoverCropBorderStylesFromRootIfNeeded(PipPreset activePreset)
    {
        // v31~v33a compatibility recovery:
        // Crop border style was serialized into root Crops, but CloneCrop omitted the new fields when mirroring into presets.
        // On next launch the preset then overwrote root Crops with default border values. Before applying the preset,
        // recover any still-present root style for the active preset by stable crop Id.
        if (_settings.Crops == null || _settings.Crops.Count == 0 || activePreset.Crops == null || activePreset.Crops.Count == 0) return;

        var rootById = _settings.Crops
            .Where(c => !string.IsNullOrWhiteSpace(c.Id))
            .GroupBy(c => c.Id)
            .ToDictionary(g => g.Key, g => g.First());

        var recovered = 0;
        foreach (var presetCrop in activePreset.Crops)
        {
            if (!rootById.TryGetValue(presetCrop.Id, out var rootCrop)) continue;
            if (presetCrop.BorderOpacity > 0.001) continue;
            if (rootCrop.BorderOpacity <= 0.001) continue;

            presetCrop.BorderColorHex = string.IsNullOrWhiteSpace(rootCrop.BorderColorHex) ? "#00FF7F" : rootCrop.BorderColorHex;
            presetCrop.BorderOpacity = Math.Clamp(rootCrop.BorderOpacity, 0, 1);
            recovered++;
        }

        if (recovered > 0)
        {
            SettingsService.Log($"crop_border_style_recovered_from_root | count={recovered} | preset={activePreset.Slot}");
        }
    }

    private static void NormalizePreset(PipPreset preset, int slot)
    {
        preset.Slot = slot;
        if (string.IsNullOrWhiteSpace(preset.Name)) preset.Name = slot == 1 ? "기본" : $"프리셋 {slot}";
        preset.Crops ??= new List<CropItem>();
        preset.PipCropOpacityByKey ??= new Dictionary<string, double>();
        preset.PipBackgroundOpacityByKey ??= new Dictionary<string, double>();
        preset.PipWindowPlacementsByKey ??= new Dictionary<string, PipWindowPlacement>();
        preset.BurstCalibrationByCropId ??= new Dictionary<string, BurstCalibrationProfile>();
        preset.CaptureIntervalMs = ClampCaptureInterval(preset.CaptureIntervalMs);
        preset.FilterKeysAcceptDelayMs = ClampFilterKeyDelay(preset.FilterKeysAcceptDelayMs, allowZero: true);
        preset.FilterKeysRepeatDelayMs = ClampFilterKeyDelay(preset.FilterKeysRepeatDelayMs, allowZero: true);
        preset.FilterKeysRepeatRateMs = ClampFilterKeyDelay(preset.FilterKeysRepeatRateMs, allowZero: false);
        preset.BurstCooldownReductionSeconds = ClampBurstSeconds(preset.BurstCooldownReductionSeconds);
        preset.BurstCooldownReductionPercent = ClampBurstPercent(preset.BurstCooldownReductionPercent);
        preset.SemiBurstTimingCorrectionSeconds = ClampBurstTimingCorrectionSeconds(preset.SemiBurstTimingCorrectionSeconds);
        preset.BurstTimingCorrectionSeconds = ClampBurstTimingCorrectionSeconds(preset.BurstTimingCorrectionSeconds);
        preset.OriginBurstTimingCorrectionSeconds = ClampBurstTimingCorrectionSeconds(preset.OriginBurstTimingCorrectionSeconds);
        preset.SemiBurstTriggerVirtualKey = Math.Clamp(preset.SemiBurstTriggerVirtualKey, 0, 255);
        preset.BurstTriggerVirtualKey = Math.Clamp(preset.BurstTriggerVirtualKey, 0, 255);
        preset.OriginBurstTriggerVirtualKey = Math.Clamp(preset.OriginBurstTriggerVirtualKey, 0, 255);
        preset.SemiBurstTriggerKeyName ??= string.Empty;
        preset.BurstTriggerKeyName ??= string.Empty;
        preset.OriginBurstTriggerKeyName ??= string.Empty;
        preset.BurstWarningThresholdSeconds = ClampBurstWarningSeconds(preset.BurstWarningThresholdSeconds);
        preset.BurstMonitorOpacity = ClampBurstMonitorOpacity(preset.BurstMonitorOpacity);
        preset.BurstMonitorBackgroundOpacity = ClampBurstMonitorBackgroundOpacity(preset.BurstMonitorBackgroundOpacity);
        NormalizePresetTargetSourceSize(preset);
    }

    private static void NormalizePresetTargetSourceSize(PipPreset preset)
    {
        // v30b: matching reference is established ONLY by an explicit preset save.
        // Older versions could fill TargetSourceWidth/Height from auto-save or PIP position-lock baselines,
        // which may be a smaller client rect and therefore caused permanent false mismatches.
        if (!preset.TargetResolutionCapturedAtPresetSave)
        {
            preset.TargetSourceWidth = 0;
            preset.TargetSourceHeight = 0;
            return;
        }

        if (preset.TargetSourceWidth <= 0 || preset.TargetSourceHeight <= 0)
        {
            preset.TargetResolutionCapturedAtPresetSave = false;
            preset.TargetSourceWidth = 0;
            preset.TargetSourceHeight = 0;
        }
    }

    private bool CaptureTargetResolutionForExplicitPresetSave(bool force)
    {
        var current = GetCurrentTargetResolution();
        if (current is null) return false;

        var activePreset = _settings.Presets.ElementAtOrDefault(Math.Clamp(_settings.ActivePresetIndex, 0, PresetSlotCount - 1));
        if (activePreset == null) return false;

        if (!force && activePreset.TargetResolutionCapturedAtPresetSave)
        {
            _settings.TargetResolutionCapturedAtPresetSave = true;
            _settings.TargetSourceWidth = activePreset.TargetSourceWidth;
            _settings.TargetSourceHeight = activePreset.TargetSourceHeight;
            return false;
        }

        // Capture with the exact same WGC SourceWidth/SourceHeight pair used by the current-resolution display.
        // This avoids mixing WGC dimensions with client-rect/PIP-lock dimensions.
        activePreset.TargetSourceWidth = current.Value.Width;
        activePreset.TargetSourceHeight = current.Value.Height;
        activePreset.TargetResolutionCapturedAtPresetSave = true;
        _settings.TargetSourceWidth = current.Value.Width;
        _settings.TargetSourceHeight = current.Value.Height;
        _settings.TargetResolutionCapturedAtPresetSave = true;
        return true;
    }

    private (int Width, int Height)? GetCurrentTargetResolution()
    {
        if (_capture is { IsCapturing: true } && _capture.SourceWidth > 0 && _capture.SourceHeight > 0)
        {
            return (_capture.SourceWidth, _capture.SourceHeight);
        }
        return null;
    }

    private static (int Width, int Height)? GetSavedPresetResolution(PipPreset? preset)
    {
        if (preset == null || !preset.TargetResolutionCapturedAtPresetSave) return null;
        return preset.TargetSourceWidth > 0 && preset.TargetSourceHeight > 0
            ? (preset.TargetSourceWidth, preset.TargetSourceHeight)
            : null;
    }

    private static string FormatResolution((int Width, int Height)? resolution)
    {
        return resolution is null ? "미저장" : $"{resolution.Value.Width}x{resolution.Value.Height}";
    }

    private static bool ResolutionEquals((int Width, int Height)? a, (int Width, int Height)? b)
    {
        return a is not null && b is not null && a.Value.Width == b.Value.Width && a.Value.Height == b.Value.Height;
    }

    private void UpdateResolutionMatchUi()
    {
        if (CurrentMapleResolutionText == null || ActivePresetResolutionText == null) return;

        var current = GetCurrentTargetResolution();
        var activePreset = _settings.Presets.ElementAtOrDefault(Math.Clamp(_settings.ActivePresetIndex, 0, PresetSlotCount - 1));
        var activeSaved = GetSavedPresetResolution(activePreset);
        var isMatch = ResolutionEquals(current, activeSaved);

        CurrentMapleResolutionText.Text = current is null ? "미선택" : FormatResolution(current);
        ActivePresetResolutionText.Text = activePreset == null
            ? "미저장"
            : $"P{activePreset.Slot} {activePreset.Name} · 기준 {FormatResolution(activeSaved)}";

        if (current is null)
        {
            SetResolutionMatchBadge("미연결", Color.FromRgb(116, 130, 148));
        }
        else if (activeSaved is null)
        {
            SetResolutionMatchBadge("기준 없음", Color.FromRgb(176, 132, 34));
        }
        else if (isMatch)
        {
            SetResolutionMatchBadge("일치", Color.FromRgb(43, 128, 75));
        }
        else
        {
            SetResolutionMatchBadge("불일치", Color.FromRgb(190, 70, 65));
        }

        var presetTextBlocks = new[]
        {
            PresetResolutionP1Text,
            PresetResolutionP2Text,
            PresetResolutionP3Text,
            PresetResolutionP4Text,
            PresetResolutionP5Text,
            PresetResolutionP6Text
        };

        for (var slot = 1; slot <= PresetSlotCount; slot++)
        {
            var textBlock = presetTextBlocks.ElementAtOrDefault(slot - 1);
            if (textBlock == null) continue;

            var preset = _settings.Presets.FirstOrDefault(p => p.Slot == slot);
            var saved = GetSavedPresetResolution(preset);
            var marker = ResolutionEquals(current, saved) ? "  ✓" : string.Empty;
            var name = string.IsNullOrWhiteSpace(preset?.Name) ? $"프리셋 {slot}" : preset!.Name;
            textBlock.Text = $"P{slot} {name} · 기준 {FormatResolution(saved)}{marker}";
            textBlock.FontWeight = slot - 1 == _settings.ActivePresetIndex ? FontWeights.SemiBold : FontWeights.Normal;
            textBlock.Foreground = ResolutionEquals(current, saved)
                ? new SolidColorBrush(Color.FromRgb(27, 116, 68))
                : new SolidColorBrush(Color.FromRgb(45, 63, 78));
        }
    }

    private void SetResolutionMatchBadge(string text, Color background)
    {
        if (ResolutionMatchBadgeText != null) ResolutionMatchBadgeText.Text = text;
        if (ResolutionMatchBadgeBorder != null) ResolutionMatchBadgeBorder.Background = new SolidColorBrush(background);
    }

    private PipPreset CreatePresetFromCurrentRoot(int slot, string name)
    {
        return new PipPreset
        {
            Slot = slot,
            Name = name,
            LastTargetTitle = _settings.LastTargetTitle,
            LastTargetProcessId = _settings.LastTargetProcessId,
            TargetSourceWidth = _settings.TargetSourceWidth,
            TargetSourceHeight = _settings.TargetSourceHeight,
            TargetResolutionCapturedAtPresetSave = _settings.TargetResolutionCapturedAtPresetSave,
            Crops = CloneCrops(_settings.Crops),
            OverlayLeft = _settings.OverlayLeft,
            OverlayTop = _settings.OverlayTop,
            OverlayWidth = _settings.OverlayWidth,
            OverlayHeight = _settings.OverlayHeight,
            Opacity = _settings.Opacity,
            PipCropOpacityByKey = CloneBackgroundMap(_settings.PipCropOpacityByKey),
            PipBackgroundOpacity = _settings.PipBackgroundOpacity,
            PipBackgroundOpacityByKey = CloneBackgroundMap(_settings.PipBackgroundOpacityByKey),
            PipWindowPlacementsByKey = ClonePlacements(_settings.PipWindowPlacementsByKey),
            Scale = _settings.Scale,
            ClickThrough = _settings.ClickThrough,
            TopMost = _settings.TopMost,
            ResizeItemsWithWindow = _settings.ResizeItemsWithWindow,
            PipPositionLockedToTarget = _settings.PipPositionLockedToTarget,
            OverlayTargetOffsetX = _settings.OverlayTargetOffsetX,
            OverlayTargetOffsetY = _settings.OverlayTargetOffsetY,
            CaptureIntervalMs = ClampCaptureInterval(_settings.CaptureIntervalMs),
            UseFrameArrivedRefresh = _settings.UseFrameArrivedRefresh,
            ShowCapturePerformanceStats = _settings.ShowCapturePerformanceStats,
            FilterKeysEnabled = _settings.FilterKeysEnabled,
            FilterKeysMapleOnly = _settings.FilterKeysMapleOnly,
            FilterKeysTurnOffOnExit = _settings.FilterKeysTurnOffOnExit,
            FilterKeysPillVisible = _settings.FilterKeysPillVisible,
            FilterKeysPillDragLocked = _settings.FilterKeysPillDragLocked,
            FilterKeysPillLeft = _settings.FilterKeysPillLeft,
            FilterKeysPillTop = _settings.FilterKeysPillTop,
            FilterKeysPillTargetOffsetX = _settings.FilterKeysPillTargetOffsetX,
            FilterKeysPillTargetOffsetY = _settings.FilterKeysPillTargetOffsetY,
            FilterKeysPillTargetBaselineWidth = _settings.FilterKeysPillTargetBaselineWidth,
            FilterKeysPillTargetBaselineHeight = _settings.FilterKeysPillTargetBaselineHeight,
            BurstMonitorVisible = _settings.BurstMonitorVisible,
            BurstCooldownReductionSeconds = ClampBurstSeconds(_settings.BurstCooldownReductionSeconds),
            BurstCooldownReductionPercent = ClampBurstPercent(_settings.BurstCooldownReductionPercent),
            SemiBurstTimingCorrectionSeconds = ClampBurstTimingCorrectionSeconds(_settings.SemiBurstTimingCorrectionSeconds),
            BurstTimingCorrectionSeconds = ClampBurstTimingCorrectionSeconds(_settings.BurstTimingCorrectionSeconds),
            OriginBurstTimingCorrectionSeconds = ClampBurstTimingCorrectionSeconds(_settings.OriginBurstTimingCorrectionSeconds),
            SemiBurstTriggerVirtualKey = _settings.SemiBurstTriggerVirtualKey,
            BurstTriggerVirtualKey = _settings.BurstTriggerVirtualKey,
            OriginBurstTriggerVirtualKey = _settings.OriginBurstTriggerVirtualKey,
            SemiBurstTriggerKeyName = _settings.SemiBurstTriggerKeyName ?? string.Empty,
            BurstTriggerKeyName = _settings.BurstTriggerKeyName ?? string.Empty,
            OriginBurstTriggerKeyName = _settings.OriginBurstTriggerKeyName ?? string.Empty,
            BurstWarningThresholdSeconds = ClampBurstWarningSeconds(_settings.BurstWarningThresholdSeconds),
            BurstMonitorOpacity = ClampBurstMonitorOpacity(_settings.BurstMonitorOpacity),
            BurstMonitorBackgroundOpacity = ClampBurstMonitorBackgroundOpacity(_settings.BurstMonitorBackgroundOpacity),
            BurstMonitorLeft = _settings.BurstMonitorLeft,
            BurstMonitorTop = _settings.BurstMonitorTop,
            BurstMonitorTargetOffsetX = _settings.BurstMonitorTargetOffsetX,
            BurstMonitorTargetOffsetY = _settings.BurstMonitorTargetOffsetY,
            BurstMonitorTargetBaselineWidth = _settings.BurstMonitorTargetBaselineWidth,
            BurstMonitorTargetBaselineHeight = _settings.BurstMonitorTargetBaselineHeight,
            BurstCalibrationByCropId = CloneBurstCalibrations(_settings.BurstCalibrationByCropId),
            FilterKeysAcceptDelayMs = ClampFilterKeyDelay(_settings.FilterKeysAcceptDelayMs, allowZero: true),
            FilterKeysRepeatDelayMs = ClampFilterKeyDelay(_settings.FilterKeysRepeatDelayMs, allowZero: true),
            FilterKeysRepeatRateMs = ClampFilterKeyDelay(_settings.FilterKeysRepeatRateMs, allowZero: false)
        };
    }

    private static PipPreset CreateEmptyPreset(int slot) => new()
    {
        Slot = slot,
        Name = slot == 1 ? "기본" : $"프리셋 {slot}",
        OverlayLeft = 1200,
        OverlayTop = 700,
        OverlayWidth = 360,
        OverlayHeight = 220,
        Opacity = 0.92,
        PipBackgroundOpacity = 0.13,
        Scale = 1.0,
        TopMost = true,
        CaptureIntervalMs = 100,
        UseFrameArrivedRefresh = true,
        FilterKeysMapleOnly = true,
        FilterKeysTurnOffOnExit = true,
        FilterKeysPillVisible = false,
        FilterKeysPillDragLocked = false,
        FilterKeysPillLeft = 980,
        FilterKeysPillTop = 160,
        FilterKeysPillTargetOffsetX = null,
        FilterKeysPillTargetOffsetY = null,
        FilterKeysPillTargetBaselineWidth = 0,
        FilterKeysPillTargetBaselineHeight = 0,
        BurstMonitorVisible = false,
        BurstCooldownReductionSeconds = 0,
        BurstCooldownReductionPercent = 0,
        SemiBurstTimingCorrectionSeconds = 0,
        BurstTimingCorrectionSeconds = 0,
        OriginBurstTimingCorrectionSeconds = 0,
        SemiBurstTriggerVirtualKey = 0,
        BurstTriggerVirtualKey = 0,
        OriginBurstTriggerVirtualKey = 0,
        SemiBurstTriggerKeyName = string.Empty,
        BurstTriggerKeyName = string.Empty,
        OriginBurstTriggerKeyName = string.Empty,
        BurstWarningThresholdSeconds = 5,
        BurstMonitorOpacity = 0.88,
        BurstMonitorBackgroundOpacity = 0.18,
        BurstMonitorLeft = 980,
        BurstMonitorTop = 220,
        BurstMonitorTargetOffsetX = null,
        BurstMonitorTargetOffsetY = null,
        BurstMonitorTargetBaselineWidth = 0,
        BurstMonitorTargetBaselineHeight = 0,
        BurstCalibrationByCropId = new Dictionary<string, BurstCalibrationProfile>(),
        FilterKeysAcceptDelayMs = 0,
        FilterKeysRepeatDelayMs = 250,
        FilterKeysRepeatRateMs = 25
    };

    private void SaveActivePresetFromCurrent()
    {
        if (_settings.Presets.Count == 0) return;
        var index = Math.Clamp(_settings.ActivePresetIndex, 0, Math.Min(PresetSlotCount, _settings.Presets.Count) - 1);
        _settings.ActivePresetIndex = index;
        var preset = _settings.Presets[index];
        preset.LastTargetTitle = _settings.LastTargetTitle;
        preset.LastTargetProcessId = _settings.LastTargetProcessId;
        preset.TargetSourceWidth = _settings.TargetSourceWidth;
        preset.TargetSourceHeight = _settings.TargetSourceHeight;
        preset.TargetResolutionCapturedAtPresetSave = _settings.TargetResolutionCapturedAtPresetSave;
        preset.Crops = CloneCrops(_crops.ToList());
        preset.OverlayLeft = _settings.OverlayLeft;
        preset.OverlayTop = _settings.OverlayTop;
        preset.OverlayWidth = _settings.OverlayWidth;
        preset.OverlayHeight = _settings.OverlayHeight;
        preset.Opacity = _settings.Opacity;
        preset.PipCropOpacityByKey = CloneBackgroundMap(_settings.PipCropOpacityByKey);
        preset.PipBackgroundOpacity = _settings.PipBackgroundOpacity;
        preset.PipBackgroundOpacityByKey = CloneBackgroundMap(_settings.PipBackgroundOpacityByKey);
        preset.PipWindowPlacementsByKey = ClonePlacements(_settings.PipWindowPlacementsByKey);
        preset.Scale = _settings.Scale;
        preset.ClickThrough = _settings.ClickThrough;
        preset.TopMost = _settings.TopMost;
        preset.ResizeItemsWithWindow = _settings.ResizeItemsWithWindow;
        preset.PipPositionLockedToTarget = _settings.PipPositionLockedToTarget;
        preset.OverlayTargetOffsetX = _settings.OverlayTargetOffsetX;
        preset.OverlayTargetOffsetY = _settings.OverlayTargetOffsetY;
        preset.CaptureIntervalMs = ClampCaptureInterval(_settings.CaptureIntervalMs);
        preset.UseFrameArrivedRefresh = _settings.UseFrameArrivedRefresh;
        preset.ShowCapturePerformanceStats = _settings.ShowCapturePerformanceStats;
        preset.FilterKeysEnabled = _settings.FilterKeysEnabled;
        preset.FilterKeysMapleOnly = _settings.FilterKeysMapleOnly;
        preset.FilterKeysTurnOffOnExit = _settings.FilterKeysTurnOffOnExit;
        preset.FilterKeysPillVisible = _settings.FilterKeysPillVisible;
        preset.FilterKeysPillDragLocked = _settings.FilterKeysPillDragLocked;
        preset.FilterKeysPillLeft = _settings.FilterKeysPillLeft;
        preset.FilterKeysPillTop = _settings.FilterKeysPillTop;
        preset.FilterKeysPillTargetOffsetX = _settings.FilterKeysPillTargetOffsetX;
        preset.FilterKeysPillTargetOffsetY = _settings.FilterKeysPillTargetOffsetY;
        preset.FilterKeysPillTargetBaselineWidth = _settings.FilterKeysPillTargetBaselineWidth;
        preset.FilterKeysPillTargetBaselineHeight = _settings.FilterKeysPillTargetBaselineHeight;
        preset.BurstMonitorVisible = _settings.BurstMonitorVisible;
        preset.BurstCooldownReductionSeconds = ClampBurstSeconds(_settings.BurstCooldownReductionSeconds);
        preset.BurstCooldownReductionPercent = ClampBurstPercent(_settings.BurstCooldownReductionPercent);
        preset.SemiBurstTimingCorrectionSeconds = ClampBurstTimingCorrectionSeconds(_settings.SemiBurstTimingCorrectionSeconds);
        preset.BurstTimingCorrectionSeconds = ClampBurstTimingCorrectionSeconds(_settings.BurstTimingCorrectionSeconds);
        preset.OriginBurstTimingCorrectionSeconds = ClampBurstTimingCorrectionSeconds(_settings.OriginBurstTimingCorrectionSeconds);
        preset.SemiBurstTriggerVirtualKey = _settings.SemiBurstTriggerVirtualKey;
        preset.BurstTriggerVirtualKey = _settings.BurstTriggerVirtualKey;
        preset.OriginBurstTriggerVirtualKey = _settings.OriginBurstTriggerVirtualKey;
        preset.SemiBurstTriggerKeyName = _settings.SemiBurstTriggerKeyName ?? string.Empty;
        preset.BurstTriggerKeyName = _settings.BurstTriggerKeyName ?? string.Empty;
        preset.OriginBurstTriggerKeyName = _settings.OriginBurstTriggerKeyName ?? string.Empty;
        preset.BurstWarningThresholdSeconds = ClampBurstWarningSeconds(_settings.BurstWarningThresholdSeconds);
        preset.BurstMonitorOpacity = ClampBurstMonitorOpacity(_settings.BurstMonitorOpacity);
        preset.BurstMonitorBackgroundOpacity = ClampBurstMonitorBackgroundOpacity(_settings.BurstMonitorBackgroundOpacity);
        preset.BurstMonitorLeft = _settings.BurstMonitorLeft;
        preset.BurstMonitorTop = _settings.BurstMonitorTop;
        preset.BurstMonitorTargetOffsetX = _settings.BurstMonitorTargetOffsetX;
        preset.BurstMonitorTargetOffsetY = _settings.BurstMonitorTargetOffsetY;
        preset.BurstMonitorTargetBaselineWidth = _settings.BurstMonitorTargetBaselineWidth;
        preset.BurstMonitorTargetBaselineHeight = _settings.BurstMonitorTargetBaselineHeight;
        preset.BurstCalibrationByCropId = CloneBurstCalibrations(_settings.BurstCalibrationByCropId);
        preset.FilterKeysAcceptDelayMs = ClampFilterKeyDelay(_settings.FilterKeysAcceptDelayMs, allowZero: true);
        preset.FilterKeysRepeatDelayMs = ClampFilterKeyDelay(_settings.FilterKeysRepeatDelayMs, allowZero: true);
        preset.FilterKeysRepeatRateMs = ClampFilterKeyDelay(_settings.FilterKeysRepeatRateMs, allowZero: false);
        NormalizePreset(preset, index + 1);
    }

    private void ApplyPresetToRootSettings(PipPreset preset)
    {
        NormalizePreset(preset, preset.Slot);
        _settings.LastTargetTitle = preset.LastTargetTitle;
        _settings.LastTargetProcessId = preset.LastTargetProcessId;
        _settings.TargetSourceWidth = preset.TargetSourceWidth;
        _settings.TargetSourceHeight = preset.TargetSourceHeight;
        _settings.TargetResolutionCapturedAtPresetSave = preset.TargetResolutionCapturedAtPresetSave;
        _settings.Crops = CloneCrops(preset.Crops);
        _settings.OverlayLeft = preset.OverlayLeft;
        _settings.OverlayTop = preset.OverlayTop;
        _settings.OverlayWidth = preset.OverlayWidth;
        _settings.OverlayHeight = preset.OverlayHeight;
        _settings.Opacity = preset.Opacity;
        _settings.PipCropOpacityByKey = CloneBackgroundMap(preset.PipCropOpacityByKey);
        _settings.PipBackgroundOpacity = preset.PipBackgroundOpacity;
        _settings.PipBackgroundOpacityByKey = CloneBackgroundMap(preset.PipBackgroundOpacityByKey);
        _settings.PipWindowPlacementsByKey = ClonePlacements(preset.PipWindowPlacementsByKey);
        _settings.Scale = preset.Scale;
        _settings.ClickThrough = preset.ClickThrough;
        _settings.TopMost = preset.TopMost;
        _settings.ResizeItemsWithWindow = preset.ResizeItemsWithWindow;
        _settings.PipPositionLockedToTarget = preset.PipPositionLockedToTarget;
        _settings.OverlayTargetOffsetX = preset.OverlayTargetOffsetX;
        _settings.OverlayTargetOffsetY = preset.OverlayTargetOffsetY;
        _settings.CaptureIntervalMs = ClampCaptureInterval(preset.CaptureIntervalMs);
        _settings.UseFrameArrivedRefresh = preset.UseFrameArrivedRefresh;
        _settings.ShowCapturePerformanceStats = preset.ShowCapturePerformanceStats;
        _settings.FilterKeysEnabled = preset.FilterKeysEnabled;
        _settings.FilterKeysMapleOnly = preset.FilterKeysMapleOnly;
        _settings.FilterKeysTurnOffOnExit = preset.FilterKeysTurnOffOnExit;
        _settings.FilterKeysPillVisible = preset.FilterKeysPillVisible;
        _settings.FilterKeysPillDragLocked = preset.FilterKeysPillDragLocked;
        _settings.FilterKeysPillLeft = preset.FilterKeysPillLeft;
        _settings.FilterKeysPillTop = preset.FilterKeysPillTop;
        _settings.FilterKeysPillTargetOffsetX = preset.FilterKeysPillTargetOffsetX;
        _settings.FilterKeysPillTargetOffsetY = preset.FilterKeysPillTargetOffsetY;
        _settings.FilterKeysPillTargetBaselineWidth = preset.FilterKeysPillTargetBaselineWidth;
        _settings.FilterKeysPillTargetBaselineHeight = preset.FilterKeysPillTargetBaselineHeight;
        _settings.BurstMonitorVisible = preset.BurstMonitorVisible;
        _settings.BurstCooldownReductionSeconds = ClampBurstSeconds(preset.BurstCooldownReductionSeconds);
        _settings.BurstCooldownReductionPercent = ClampBurstPercent(preset.BurstCooldownReductionPercent);
        _settings.SemiBurstTimingCorrectionSeconds = ClampBurstTimingCorrectionSeconds(preset.SemiBurstTimingCorrectionSeconds);
        _settings.BurstTimingCorrectionSeconds = ClampBurstTimingCorrectionSeconds(preset.BurstTimingCorrectionSeconds);
        _settings.OriginBurstTimingCorrectionSeconds = ClampBurstTimingCorrectionSeconds(preset.OriginBurstTimingCorrectionSeconds);
        _settings.SemiBurstTriggerVirtualKey = preset.SemiBurstTriggerVirtualKey;
        _settings.BurstTriggerVirtualKey = preset.BurstTriggerVirtualKey;
        _settings.OriginBurstTriggerVirtualKey = preset.OriginBurstTriggerVirtualKey;
        _settings.SemiBurstTriggerKeyName = preset.SemiBurstTriggerKeyName ?? string.Empty;
        _settings.BurstTriggerKeyName = preset.BurstTriggerKeyName ?? string.Empty;
        _settings.OriginBurstTriggerKeyName = preset.OriginBurstTriggerKeyName ?? string.Empty;
        _settings.BurstWarningThresholdSeconds = ClampBurstWarningSeconds(preset.BurstWarningThresholdSeconds);
        _settings.BurstMonitorOpacity = ClampBurstMonitorOpacity(preset.BurstMonitorOpacity);
        _settings.BurstMonitorBackgroundOpacity = ClampBurstMonitorBackgroundOpacity(preset.BurstMonitorBackgroundOpacity);
        _settings.BurstMonitorLeft = preset.BurstMonitorLeft;
        _settings.BurstMonitorTop = preset.BurstMonitorTop;
        _settings.BurstMonitorTargetOffsetX = preset.BurstMonitorTargetOffsetX;
        _settings.BurstMonitorTargetOffsetY = preset.BurstMonitorTargetOffsetY;
        _settings.BurstMonitorTargetBaselineWidth = preset.BurstMonitorTargetBaselineWidth;
        _settings.BurstMonitorTargetBaselineHeight = preset.BurstMonitorTargetBaselineHeight;
        _settings.BurstCalibrationByCropId = CloneBurstCalibrations(preset.BurstCalibrationByCropId);
        _settings.FilterKeysAcceptDelayMs = ClampFilterKeyDelay(preset.FilterKeysAcceptDelayMs, allowZero: true);
        _settings.FilterKeysRepeatDelayMs = ClampFilterKeyDelay(preset.FilterKeysRepeatDelayMs, allowZero: true);
        _settings.FilterKeysRepeatRateMs = ClampFilterKeyDelay(preset.FilterKeysRepeatRateMs, allowZero: false);
    }

    private static List<CropItem> CloneCrops(IEnumerable<CropItem>? crops)
    {
        return crops?.Select(CloneCrop).ToList() ?? new List<CropItem>();
    }

    private static CropItem CloneCrop(CropItem crop) => new()
    {
        Id = crop.Id,
        Name = crop.Name,
        Enabled = crop.Enabled,
        X = crop.X,
        Y = crop.Y,
        Width = crop.Width,
        Height = crop.Height,
        DisplayX = crop.DisplayX,
        DisplayY = crop.DisplayY,
        DisplayWidth = crop.DisplayWidth,
        DisplayHeight = crop.DisplayHeight,
        PipOffsetX = crop.PipOffsetX,
        PipOffsetY = crop.PipOffsetY,
        Shape = crop.Shape,
        BurstRole = crop.BurstRole,
        BorderColorHex = string.IsNullOrWhiteSpace(crop.BorderColorHex) ? "#00FF7F" : crop.BorderColorHex,
        BorderOpacity = Math.Clamp(crop.BorderOpacity, 0, 1),
        RotationAngle = crop.RotationAngle,
        DetachedGroupIdRuntime = crop.DetachedGroupIdRuntime
    };

    private static Dictionary<string, double> CloneBackgroundMap(Dictionary<string, double>? source)
    {
        return source == null ? new Dictionary<string, double>() : new Dictionary<string, double>(source);
    }

    private static Dictionary<string, BurstCalibrationProfile> CloneBurstCalibrations(Dictionary<string, BurstCalibrationProfile>? source)
    {
        var clone = new Dictionary<string, BurstCalibrationProfile>();
        if (source == null) return clone;
        foreach (var pair in source)
        {
            var p = pair.Value;
            if (p == null) continue;
            clone[pair.Key] = new BurstCalibrationProfile
            {
                FeatureVersion = p.FeatureVersion,
                CropId = p.CropId,
                CropX = p.CropX,
                CropY = p.CropY,
                CropWidth = p.CropWidth,
                CropHeight = p.CropHeight,
                TargetSourceWidth = p.TargetSourceWidth,
                TargetSourceHeight = p.TargetSourceHeight,
                ReadySampleCount = p.ReadySampleCount,
                ReadyMean = p.ReadyMean?.ToArray() ?? Array.Empty<double>(),
                ReadyVariance = p.ReadyVariance?.ToArray() ?? Array.Empty<double>(),
                CooldownSampleCount = p.CooldownSampleCount,
                CooldownMean = p.CooldownMean?.ToArray() ?? Array.Empty<double>(),
                CooldownVariance = p.CooldownVariance?.ToArray() ?? Array.Empty<double>(),
                ReadyLearnedUtc = p.ReadyLearnedUtc,
                CooldownLearnedUtc = p.CooldownLearnedUtc
            };
        }
        return clone;
    }

    private static Dictionary<string, PipWindowPlacement> ClonePlacements(Dictionary<string, PipWindowPlacement>? source)
    {
        var clone = new Dictionary<string, PipWindowPlacement>();
        if (source == null) return clone;
        foreach (var pair in source)
        {
            var p = pair.Value;
            clone[pair.Key] = new PipWindowPlacement
            {
                Left = p.Left,
                Top = p.Top,
                Width = p.Width,
                Height = p.Height,
                BackgroundOpacity = p.BackgroundOpacity,
                TargetOffsetX = p.TargetOffsetX,
                TargetOffsetY = p.TargetOffsetY,
                TargetBaselineWidth = p.TargetBaselineWidth,
                TargetBaselineHeight = p.TargetBaselineHeight
            };
        }
        return clone;
    }

    private void UpdatePresetSelectorItems()
    {
        if (PresetCombo == null) return;
        _isUpdatingPresetUi = true;
        try
        {
            PresetCombo.ItemsSource = null;
            PresetCombo.ItemsSource = _settings.Presets;
            var active = Math.Clamp(_settings.ActivePresetIndex, 0, PresetSlotCount - 1);
            PresetCombo.SelectedItem = _settings.Presets.ElementAtOrDefault(active);
            PresetNameBox.Text = _settings.Presets.ElementAtOrDefault(active)?.Name ?? string.Empty;
            UpdateResolutionMatchUi();
        }
        finally
        {
            _isUpdatingPresetUi = false;
        }
    }

    private void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || _isUpdatingPresetUi || _isSwitchingPreset) return;
        if (PresetCombo.SelectedItem is not PipPreset preset) return;
        var newIndex = Math.Clamp(preset.Slot - 1, 0, PresetSlotCount - 1);
        if (newIndex == _settings.ActivePresetIndex)
        {
            PresetNameBox.Text = preset.Name;
            return;
        }

        SwitchPreset(newIndex);
    }

    private void SavePresetName_Click(object sender, RoutedEventArgs e)
    {
        var active = _settings.Presets.ElementAtOrDefault(_settings.ActivePresetIndex);
        if (active == null) return;
        var name = string.IsNullOrWhiteSpace(PresetNameBox.Text) ? $"프리셋 {active.Slot}" : PresetNameBox.Text.Trim();
        active.Name = name;
        UpdatePresetSelectorItems();
        SettingsService.Save(_settings);
        StatusText.Text = $"프리셋 이름 저장: {active.DisplayName}";
    }

    private void SaveCurrentPreset_Click(object sender, RoutedEventArgs e)
    {
        var baselineCaptured = CaptureTargetResolutionForExplicitPresetSave(force: false);
        SaveSettings();
        var active = _settings.Presets.ElementAtOrDefault(_settings.ActivePresetIndex);
        if (active == null)
        {
            StatusText.Text = "현재 프리셋 저장 완료";
            return;
        }

        var resolutionNote = baselineCaptured
            ? $" · 기준 해상도 {active.TargetSourceWidth}x{active.TargetSourceHeight} 최초 저장"
            : active.TargetResolutionCapturedAtPresetSave
                ? $" · 기준 해상도 {active.TargetSourceWidth}x{active.TargetSourceHeight} 유지"
                : " · 대상 미연결: 기준 해상도는 저장되지 않음";
        StatusText.Text = $"현재 설정을 저장했습니다: {active.DisplayName}{resolutionNote}";
    }

    private void ResetPresetResolutionBaseline_Click(object sender, RoutedEventArgs e)
    {
        var current = GetCurrentTargetResolution();
        if (current is null)
        {
            StatusText.Text = "대상 메이플이 연결되어 있지 않아 기준 해상도를 저장할 수 없습니다.";
            return;
        }

        if (!CaptureTargetResolutionForExplicitPresetSave(force: true)) return;
        SaveSettings();
        var active = _settings.Presets.ElementAtOrDefault(_settings.ActivePresetIndex);
        StatusText.Text = active == null
            ? $"기준 해상도를 {current.Value.Width}x{current.Value.Height}로 저장했습니다."
            : $"{active.DisplayName} 기준 해상도를 {current.Value.Width}x{current.Value.Height}로 다시 저장했습니다.";
    }

    private void SwitchPreset(int newIndex)
    {
        SaveSettings();
        _isSwitchingPreset = true;
        try
        {
            newIndex = Math.Clamp(newIndex, 0, PresetSlotCount - 1);
            _settings.ActivePresetIndex = newIndex;
            ApplyPresetToRootSettings(_settings.Presets[newIndex]);
            LoadCurrentSettingsIntoUiAndOverlays();
            SettingsService.Save(_settings);
            StatusText.Text = $"프리셋 불러오기: {_settings.Presets[newIndex].DisplayName}";
        }
        finally
        {
            _isSwitchingPreset = false;
            UpdatePresetSelectorItems();
        }
    }

    private void LoadCurrentSettingsIntoUiAndOverlays()
    {
        _isApplyingHistory = true;
        try
        {
            foreach (var existing in _crops) existing.PropertyChanged -= Crop_PropertyChanged;
            _crops.Clear();
            foreach (var crop in _settings.Crops)
            {
                _crops.Add(crop);
            }
        }
        finally
        {
            _isApplyingHistory = false;
        }

        _undoHistory.Clear();
        _redoHistory.Clear();
        _selectedPipKeys.Clear();
        _burstLearningSession = null;
        ResetBurstRuntimeAll();

        _isInitializing = true;
        try
        {
            OpacitySlider.Value = GetPipCropOpacity(MainPipKey);
            PipBackgroundOpacitySlider.Value = GetPipBackgroundOpacity(MainPipKey);
            ScaleSlider.Value = _settings.Scale;
            TopMostCheck.IsChecked = _settings.TopMost;
            ClickThroughCheck.IsChecked = _settings.ClickThrough;
            ResizeItemsWithWindowCheck.IsChecked = _settings.ResizeItemsWithWindow;
            PipPositionLockCheck.IsChecked = _settings.PipPositionLockedToTarget;
            FrameEventRefreshCheck.IsChecked = _settings.UseFrameArrivedRefresh;
            ShowPerformanceStatsCheck.IsChecked = _settings.ShowCapturePerformanceStats;
            SetCaptureSpeedComboFromSettings();
            UpdateOpacityUi(GetPipCropOpacity(MainPipKey));
            UpdatePipBackgroundOpacityUi(GetPipBackgroundOpacity(MainPipKey));
            UpdateScaleUi(_settings.Scale);
            UpdateCaptureSpeedUi();
            UpdateFilterKeysUiFromSettings();
            UpdateFilterKeysPillVisibility();
            UpdateBurstUiFromSettings();
            UpdateBurstMonitorVisibility();
        }
        finally
        {
            _isInitializing = false;
        }

        if (_overlay != null)
        {
            var mainPlacement = GetSavedPipPlacement(MainPipKey);
            _overlay.Left = mainPlacement?.Left ?? _settings.OverlayLeft;
            _overlay.Top = mainPlacement?.Top ?? _settings.OverlayTop;
            _overlay.Width = Math.Max(60, mainPlacement?.Width ?? _settings.OverlayWidth);
            _overlay.Height = Math.Max(40, mainPlacement?.Height ?? _settings.OverlayHeight);
            _overlay.SetTargetLockState(mainPlacement?.TargetOffsetX, mainPlacement?.TargetOffsetY, mainPlacement?.TargetBaselineWidth ?? 0, mainPlacement?.TargetBaselineHeight ?? 0);
        }

        foreach (var detached in _detachedOverlays.Values.ToList()) detached.ForceClose();
        _detachedOverlays.Clear();

        RefreshWindows();
        EnsureOverlay();
        if (_target != null && _capture != null) _overlay?.SetCapture(_capture, _target.Hwnd);
        SyncDetachedOverlaysWithCurrentCropGroups();
        ApplyCommonSettingsToOverlays();
        RefreshAllOverlays();
        UpdatePipSelectorItems();
        UpdateFilterKeysRuntime(applyWhenNeeded: true);
    }

    private void ApplyCommonSettingsToOverlays()
    {
        foreach (var overlay in GetAllOverlays())
        {
            overlay.Topmost = _settings.TopMost;
            overlay.SetClickThrough(_settings.ClickThrough);
            overlay.SetResizeItemsWithWindow(_settings.ResizeItemsWithWindow);
            overlay.SetCaptureRefreshOptions(_settings.CaptureIntervalMs, _settings.UseFrameArrivedRefresh, _settings.ShowCapturePerformanceStats);
            overlay.SetPipPositionLockedToTarget(_settings.PipPositionLockedToTarget, captureCurrentPosition: false);
        }
        if (_overlay != null)
        {
            _overlay.SetCropOpacity(GetPipCropOpacity(MainPipKey));
            _overlay.SetPipBackgroundOpacity(GetPipBackgroundOpacity(MainPipKey), save: false);
        }
        foreach (var pair in _detachedOverlays)
        {
            pair.Value.SetCropOpacity(GetPipCropOpacity(pair.Key));
            pair.Value.SetPipBackgroundOpacity(GetPipBackgroundOpacity(pair.Key), save: false);
        }
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
        if (_isApplyingHistory) return;
        QueueRefreshAllOverlays();
        UpdateBurstRoleComboItems();
        SaveSettingsThrottled();
    }

    private void Crop_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isInitializing || _isApplyingHistory) return;

        // 분리/병합/합치기 중에는 CropItem마다 PropertyChanged가 연속으로 발생합니다.
        // 이때 중간 상태로 Overlay를 먼저 갱신하면 첫 번째 메뉴 실행이 화면에 늦게 반영되어
        // 사용자가 같은 분리/병합 메뉴를 여러 번 눌러야 하는 것처럼 보일 수 있습니다.
        // 그룹 변경 작업이 끝난 뒤 한 번만 RefreshAllOverlays() 하도록 여기서는 큐 갱신을 막습니다.
        if (_isApplyingPipGroupChange)
        {
            SaveSettingsThrottled();
            return;
        }

        if (e.PropertyName is nameof(CropItem.Enabled) or nameof(CropItem.Shape) or nameof(CropItem.ShapeName) or nameof(CropItem.X) or nameof(CropItem.Y) or nameof(CropItem.Width) or nameof(CropItem.Height) or nameof(CropItem.DetachedGroupIdRuntime) or nameof(CropItem.IsDetachedRuntime) or nameof(CropItem.BorderColorHex) or nameof(CropItem.BorderOpacity))
        {
            QueueRefreshAllOverlays();
            if (e.PropertyName is nameof(CropItem.BorderColorHex) or nameof(CropItem.BorderOpacity)) UpdateCropBorderEditorUi();
        }
        if (e.PropertyName is nameof(CropItem.Name) or nameof(CropItem.BurstRole) or nameof(CropItem.BurstRoleName))
        {
            UpdateBurstRoleComboItems();
            if (e.PropertyName is nameof(CropItem.BurstRole) or nameof(CropItem.BurstRoleName)) ResetBurstRuntimeAll();
        }
        SaveSettingsThrottled();
    }

    private void RefreshWindows_Click(object sender, RoutedEventArgs e) => RefreshWindows();

    private void RefreshWindows()
    {
        var windows = WindowService.EnumerateWindows();
        WindowCombo.ItemsSource = windows;
        if (!string.IsNullOrWhiteSpace(_settings.LastTargetTitle))
        {
            var found = windows.FirstOrDefault(w => w.Title == _settings.LastTargetTitle && w.ProcessId == _settings.LastTargetProcessId)
                     ?? windows.FirstOrDefault(w => w.Title.Contains(_settings.LastTargetTitle, StringComparison.OrdinalIgnoreCase))
                     ?? windows.FirstOrDefault(w => w.Title.Contains("MapleStory", StringComparison.OrdinalIgnoreCase));
            if (found != null) WindowCombo.SelectedItem = found;
        }
        else
        {
            var maple = windows.FirstOrDefault(w => w.Title.Contains("MapleStory", StringComparison.OrdinalIgnoreCase));
            if (maple != null) WindowCombo.SelectedItem = maple;
        }
        StatusText.Text = $"창 {windows.Count}개 검색됨.";
        UpdateResolutionMatchUi();
    }

    private void SelectTarget_Click(object sender, RoutedEventArgs e)
    {
        if (WindowCombo.SelectedItem is not WindowInfo info)
        {
            MessageBox.Show("대상 창을 먼저 선택해 주세요.");
            return;
        }

        try
        {
            _capture ??= new WgcCaptureManager();
            if (!_capture.StartForWindow(info.Hwnd, info.Title))
            {
                MessageBox.Show("대상 창 WGC 캡처 시작에 실패했습니다. MapleStory 창이 켜져 있는지 확인해 주세요.");
                return;
            }
        }
        catch (Exception ex)
        {
            SettingsService.Log("target_select_capture_exception | " + ex);
            MessageBox.Show(ex.ToString(), "WGC 캡처 시작 오류");
            return;
        }

        _target = info;
        _settings.LastTargetTitle = info.Title;
        _settings.LastTargetProcessId = info.ProcessId;
        EnsureOverlay();
        SyncDetachedOverlaysWithCurrentCropGroups();
        _overlay!.SetCapture(_capture!, info.Hwnd);
        _overlay.SetPipBackgroundOpacity(GetPipBackgroundOpacity(MainPipKey), save: false);
        _overlay.SetCropOpacity(GetPipCropOpacity(MainPipKey));
        _overlay.SetCaptureRefreshOptions(_settings.CaptureIntervalMs, _settings.UseFrameArrivedRefresh, _settings.ShowCapturePerformanceStats);
        _overlay.SetPipPositionLockedToTarget(_settings.PipPositionLockedToTarget, captureCurrentPosition: false);
        foreach (var pair in _detachedOverlays)
        {
            var detached = pair.Value;
            detached.SetCapture(_capture!, info.Hwnd);
            detached.SetPipBackgroundOpacity(GetPipBackgroundOpacity(pair.Key), save: false);
            detached.SetCropOpacity(GetPipCropOpacity(pair.Key));
            detached.SetCaptureRefreshOptions(_settings.CaptureIntervalMs, _settings.UseFrameArrivedRefresh, _settings.ShowCapturePerformanceStats);
            detached.SetPipPositionLockedToTarget(_settings.PipPositionLockedToTarget, captureCurrentPosition: false);
        }
        _overlay.SetStatus($"Target: {info.Title}");
        SaveSettings();
        SettingsService.Log($"target_selected_wgc | {info.Title} | hwnd={info.Hwnd} | size={_capture!.SourceWidth}x{_capture.SourceHeight}");
        UpdateResolutionMatchUi();
        StatusText.Text = $"대상 선택됨: {info.Title} / WGC 캡처 사용 중 / 해상도 {_capture!.SourceWidth}x{_capture.SourceHeight}";
        if (_settings.PipPositionLockedToTarget)
        {
            ApplyFilterKeysPillTargetLockedPosition();
        }
        UpdateFilterKeysRuntime(applyWhenNeeded: true);
        UpdateBurstMonitorVisibility();
        if (_settings.PipPositionLockedToTarget) ApplyBurstMonitorTargetLockedPosition();
        UpdateCropThumbnails(force: true);
    }

    private void ToggleOverlay_Click(object sender, RoutedEventArgs e)
    {
        EnsureOverlay();
        var overlays = GetAllOverlays().ToList();
        var anyVisible = overlays.Any(o => o.IsVisible) || (_burstMonitorWindow?.IsVisible == true);
        if (anyVisible)
        {
            _allPipsTemporarilyHidden = true;
            foreach (var overlay in overlays) overlay.Hide();
            _burstMonitorWindow?.Hide();
            StatusText.Text = "모든 PiP 숨김";
        }
        else
        {
            _allPipsTemporarilyHidden = false;
            foreach (var overlay in overlays)
            {
                overlay.Show();
                if (_target != null && _capture != null) overlay.SetCapture(_capture, _target.Hwnd);
            }
            if (_settings.BurstMonitorVisible)
            {
                EnsureBurstMonitorWindow();
                UpdateBurstMonitorPipRows();
            }
            StatusText.Text = "모든 PiP 표시";
        }
    }

    private void ShowFilterKeysPillOnly_Click(object sender, RoutedEventArgs e)
    {
        _allPipsTemporarilyHidden = true;
        foreach (var overlay in GetAllOverlays().ToList())
        {
            overlay.Hide();
        }
        _burstMonitorWindow?.Hide();

        _settings.FilterKeysPillVisible = true;
        _isUpdatingFilterKeysUi = true;
        try
        {
            FilterKeysPillVisibleCheck.IsChecked = true;
            FilterKeysPillDragLockedCheck.IsChecked = _settings.FilterKeysPillDragLocked;
        }
        finally
        {
            _isUpdatingFilterKeysUi = false;
        }

        UpdateFilterKeysPillVisibility();
        if (_filterKeysPillWindow != null)
        {
            _filterKeysPillWindow.DragLocked = _settings.FilterKeysPillDragLocked;
            _filterKeysPillWindow.Topmost = true;
        }
        UpdateFilterKeysStatusText();
        StatusText.Text = "일반 PiP 숨김 / 필터키 PIP 버튼만 표시";
        SaveSettings();
    }

    private IEnumerable<OverlayWindow> GetAllOverlays()
    {
        if (_overlay != null) yield return _overlay;
        foreach (var detached in _detachedOverlays.Values) yield return detached;
    }

    private void EnsureOverlay()
    {
        if (_overlay != null) return;

        var mainPlacement = GetSavedPipPlacement(MainPipKey);
        var left = mainPlacement?.Left ?? _settings.OverlayLeft;
        var top = mainPlacement?.Top ?? _settings.OverlayTop;
        var width = mainPlacement?.Width ?? _settings.OverlayWidth;
        var height = mainPlacement?.Height ?? _settings.OverlayHeight;

        _overlay = new OverlayWindow(_crops, _settings)
        {
            Left = left,
            Top = top,
            Width = Math.Max(60, width),
            Height = Math.Max(40, height),
            Opacity = 1.0,
            Topmost = _settings.TopMost
        };
        WireOverlayEvents(_overlay);
        _overlay.SetTargetLockState(
            mainPlacement?.TargetOffsetX ?? (Math.Abs(_settings.OverlayTargetOffsetX) > 0.01 ? _settings.OverlayTargetOffsetX : null),
            mainPlacement?.TargetOffsetY ?? (Math.Abs(_settings.OverlayTargetOffsetY) > 0.01 ? _settings.OverlayTargetOffsetY : null),
            mainPlacement?.TargetBaselineWidth ?? 0,
            mainPlacement?.TargetBaselineHeight ?? 0);
        _overlay.Show();
        _overlay.SetPipBackgroundOpacity(GetPipBackgroundOpacity(MainPipKey), save: false);
        _overlay.SetCropOpacity(GetPipCropOpacity(MainPipKey));
        _overlay.SetCaptureRefreshOptions(_settings.CaptureIntervalMs, _settings.UseFrameArrivedRefresh, _settings.ShowCapturePerformanceStats);
        if (_target != null && _capture != null) _overlay.SetCapture(_capture, _target.Hwnd);
        _overlay.SetPipPositionLockedToTarget(_settings.PipPositionLockedToTarget, captureCurrentPosition: false);
        SyncDetachedOverlaysWithCurrentCropGroups();
        UpdatePipSelectorItems();
    }

    private void WireOverlayEvents(OverlayWindow overlay)
    {
        overlay.DetachRequested += DetachCropsToPip;
        overlay.ReattachRequested += ReattachCropsToMainPip;
        overlay.PipSelectionRequested += SelectDetachedPip;
        overlay.PipSelectionCycleRequested += CyclePipSelectionAtPoint;
        overlay.MergeSelectedPipsRequested += MergeSelectedPipsInto;
        overlay.PipSelectionClearRequested += ClearDetachedPipSelection;
        overlay.HistoryCheckpointRequested += PushHistoryCheckpoint;
        overlay.UndoRequested += UndoLastAction;
        overlay.RedoRequested += RedoLastAction;
        overlay.PipMoveRequested += MoveSelectedPipsBy;
        overlay.PipStackAlignRequested += AlignSelectedPipsVertically;
        overlay.PipBoundsChanged += OverlayBoundsChanged;
    }

    private async void AddCrop_Click(object sender, RoutedEventArgs e)
    {
        if (_target == null)
        {
            SelectTarget_Click(sender, e);
            if (_target == null) return;
        }

        var frame = await WaitForFrameAsync();
        if (frame == null)
        {
            MessageBox.Show("아직 캡처 프레임이 없습니다. 대상 선택 후 1초 정도 기다린 뒤 다시 시도해 주세요.");
            return;
        }

        var selector = new PreviewCropWindow("크롭 추가", frame, pasteTemplate: _copiedCropTemplate) { Owner = this };
        var dialogResult = selector.ShowDialog();
        if (selector.CopiedTemplate is not null) _copiedCropTemplate = selector.CopiedTemplate;
        if (dialogResult != true || selector.SelectedRegion is not System.Windows.Int32Rect r) return;
        if (r.Width < 5 || r.Height < 5) return;

        var nextPosition = GetNextMainCropDisplayPosition(r.Width, r.Height);
        var displayX = nextPosition.X;
        var displayY = nextPosition.Y;
        var crop = new CropItem
        {
            Name = $"Crop {_crops.Count + 1}",
            X = r.X,
            Y = r.Y,
            Width = r.Width,
            Height = r.Height,
            DisplayX = displayX,
            DisplayY = displayY,
            DisplayWidth = r.Width,
            DisplayHeight = r.Height,
            Shape = selector.SelectedShape,
            BorderColorHex = _copiedCropTemplate?.BorderColorHex ?? "#00FF7F",
            BorderOpacity = _copiedCropTemplate?.BorderOpacity ?? 0
        };

        PushHistoryCheckpoint();
        _crops.Add(crop);
        EnsureOverlay();
        EnsureMainOverlayFitsCrops();
        EnsureMainOverlayVisibleForCrops();
        RefreshAllOverlays();
        SaveSettings();
        UpdateCropThumbnails(force: true);
        StatusText.Text = $"크롭 추가: {crop.Name} / {crop.Shape.ToKoreanName()} X:{crop.X} Y:{crop.Y} W:{crop.Width} H:{crop.Height}";
    }

    private async Task<BitmapSource?> WaitForFrameAsync()
    {
        for (var i = 0; i < 20; i++)
        {
            var frame = _capture?.GetLatestFrame();
            if (frame != null) return frame;
            await Task.Delay(100);
        }
        return null;
    }


    private void UpdateCropThumbnails(bool force = false)
    {
        if (_isUpdatingCropThumbnails) return;
        if (!force && !IsVisible) return;
        if (_capture == null || !_capture.IsCapturing) return;
        if (_crops.Count == 0) return;

        _isUpdatingCropThumbnails = true;
        try
        {
            foreach (var crop in _crops)
            {
                if (!crop.Enabled)
                {
                    crop.Thumbnail = null;
                    continue;
                }

                var source = _capture.GetLatestCrop(new Int32Rect(crop.X, crop.Y, crop.Width, crop.Height));
                if (source == null) continue;
                crop.Thumbnail = CreateGridThumbnail(source);
            }
        }
        catch (Exception ex)
        {
            SettingsService.Log("thumbnail_update_error | " + ex.Message);
        }
        finally
        {
            _isUpdatingCropThumbnails = false;
        }
    }

    private static ImageSource CreateGridThumbnail(BitmapSource source)
    {
        const double maxWidth = 96.0;
        const double maxHeight = 54.0;
        if (source.PixelWidth <= 0 || source.PixelHeight <= 0) return source;

        var scale = Math.Min(maxWidth / source.PixelWidth, maxHeight / source.PixelHeight);
        scale = Math.Clamp(scale, 0.05, 1.0);
        if (scale >= 0.999)
        {
            if (source.CanFreeze) source.Freeze();
            return source;
        }

        var thumbnail = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        thumbnail.Freeze();
        return thumbnail;
    }

    private void DeleteCrop_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedGridCrops().ToList();
        if (selected.Count == 0 && CropGrid.SelectedItem is CropItem crop) selected.Add(crop);
        if (selected.Count == 0) return;

        PushHistoryCheckpoint();
        foreach (var selectedCrop in selected)
        {
            _crops.Remove(selectedCrop);
        }
        CleanupEmptyDetachedOverlays();
        RefreshAllOverlays();
        SaveSettings();
        StatusText.Text = selected.Count > 1 ? $"크롭 {selected.Count}개 삭제" : $"크롭 삭제: {selected[0].Name}";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        StatusText.Text = "저장 완료";
    }

    private void SaveSettings()
    {
        _settings.Crops = _crops.ToList();
        SaveCurrentPipWindowPlacements();
        if (!_isSwitchingPreset && !_isApplyingHistory)
        {
            SaveActivePresetFromCurrent();
        }
        SettingsService.Save(_settings);
        UpdateResolutionMatchUi();
    }

    private void SaveCurrentPipWindowPlacements()
    {
        var current = new Dictionary<string, PipWindowPlacement>(_settings.PipWindowPlacementsByKey);
        var validGroupIds = _crops.Where(c => !string.IsNullOrWhiteSpace(c.DetachedGroupIdRuntime)).Select(c => c.DetachedGroupIdRuntime!).Distinct().ToHashSet();
        foreach (var staleKey in current.Keys.Where(k => k != MainPipKey && !validGroupIds.Contains(k)).ToList()) current.Remove(staleKey);
        if (_overlay != null)
        {
            var placement = BuildPlacementFromOverlay(_overlay, MainPipKey);
            current[MainPipKey] = placement;
            _settings.OverlayLeft = placement.Left;
            _settings.OverlayTop = placement.Top;
            _settings.OverlayWidth = placement.Width;
            _settings.OverlayHeight = placement.Height;
            if (placement.TargetOffsetX is not null) _settings.OverlayTargetOffsetX = placement.TargetOffsetX.Value;
            if (placement.TargetOffsetY is not null) _settings.OverlayTargetOffsetY = placement.TargetOffsetY.Value;
        }

        foreach (var pair in _detachedOverlays)
        {
            current[pair.Key] = BuildPlacementFromOverlay(pair.Value, pair.Key);
        }

        _settings.PipWindowPlacementsByKey = current;
    }

    private PipWindowPlacement BuildPlacementFromOverlay(OverlayWindow overlay, string key) => new()
    {
        Left = Math.Round(overlay.Left, 2),
        Top = Math.Round(overlay.Top, 2),
        Width = Math.Round(Math.Max(60, overlay.Width), 2),
        Height = Math.Round(Math.Max(40, overlay.Height), 2),
        BackgroundOpacity = GetPipBackgroundOpacity(key),
        TargetOffsetX = overlay.TargetLockedOffsetX,
        TargetOffsetY = overlay.TargetLockedOffsetY,
        TargetBaselineWidth = overlay.TargetLockBaselineWidth,
        TargetBaselineHeight = overlay.TargetLockBaselineHeight
    };

    private PipWindowPlacement? GetSavedPipPlacement(string key)
    {
        return _settings.PipWindowPlacementsByKey.TryGetValue(key, out var placement) ? placement : null;
    }

    private void OverlayBoundsChanged(OverlayWindow overlay)
    {
        if (_isInitializing || _isApplyingHistory) return;
        SaveSettingsThrottled();
    }

    private void SaveSettingsThrottled()
    {
        if ((DateTime.UtcNow - _lastAutoSaveUtc).TotalMilliseconds < 250) return;
        _lastAutoSaveUtc = DateTime.UtcNow;
        SaveSettings();
    }

    private void QueueRefreshAllOverlays()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _refreshQueued = false;
            CleanupEmptyDetachedOverlays();
            RefreshAllOverlays();
        }));
    }

    private void RefreshAllOverlays()
    {
        _overlay?.RefreshItems();
        foreach (var detached in _detachedOverlays.Values.ToList()) detached.RefreshItems();
        ApplyDetachedPipSelectionVisuals();
    }

    private static int ClampCaptureInterval(int intervalMs) => Math.Clamp(intervalMs, 16, 1000);

    private void SetCaptureSpeedComboFromSettings()
    {
        if (CaptureSpeedCombo == null) return;
        var target = ClampCaptureInterval(_settings.CaptureIntervalMs).ToString();
        ComboBoxItem? fallback = null;
        foreach (var item in CaptureSpeedCombo.Items.OfType<ComboBoxItem>())
        {
            fallback ??= item;
            if (string.Equals(item.Tag?.ToString(), target, StringComparison.Ordinal))
            {
                CaptureSpeedCombo.SelectedItem = item;
                return;
            }
        }
        CaptureSpeedCombo.SelectedItem = fallback;
    }

    private void CaptureSpeedCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || CaptureSpeedCombo.SelectedItem is not ComboBoxItem item) return;
        if (!int.TryParse(item.Tag?.ToString(), out var ms)) return;
        _settings.CaptureIntervalMs = ClampCaptureInterval(ms);
        ApplyCaptureRefreshOptionsToOverlays();
        UpdateCaptureSpeedUi();
        SaveSettings();
    }

    private void CapturePerformanceOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _settings.UseFrameArrivedRefresh = FrameEventRefreshCheck.IsChecked == true;
        _settings.ShowCapturePerformanceStats = ShowPerformanceStatsCheck.IsChecked == true;
        ApplyCaptureRefreshOptionsToOverlays();
        UpdateCaptureSpeedUi();
        SaveSettings();
    }

    private void ApplyCaptureRefreshOptionsToOverlays()
    {
        foreach (var overlay in GetAllOverlays())
        {
            overlay.SetCaptureRefreshOptions(_settings.CaptureIntervalMs, _settings.UseFrameArrivedRefresh, _settings.ShowCapturePerformanceStats);
        }
    }

    private void UpdateCaptureSpeedUi()
    {
        if (CaptureSpeedText == null) return;
        var ms = ClampCaptureInterval(_settings.CaptureIntervalMs);
        var targetFps = 1000.0 / ms;
        var mode = _settings.UseFrameArrivedRefresh ? "프레임 도착 즉시 갱신" : "타이머 갱신";
        var stats = _settings.ShowCapturePerformanceStats ? "FPS 표시 ON" : "FPS 표시 OFF";
        CaptureSpeedText.Text = $"{ms}ms 목표({targetFps:0.#} FPS) / {mode} / {stats}. 처리 중이면 다음 프레임은 스킵합니다.";
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing || _isApplyingHistory || _isUpdatingPipSelector) return;
        var value = ClampAndRoundOpacity(e.NewValue);
        if (Math.Abs(OpacitySlider.Value - value) > 0.0001)
        {
            OpacitySlider.Value = value;
            return;
        }

        var targets = GetOpacityTargetPipKeys();
        foreach (var key in targets) SetPipCropOpacity(key, value, save: false);
        UpdateOpacityUi(value);
        SaveSettings();
    }

    private void ScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing || _isApplyingHistory) return;
        var value = Math.Round(Math.Clamp(e.NewValue, 0.25, 3.0), 2);
        _settings.Scale = value;
        UpdateScaleUi(value);
        RefreshAllOverlays();
        SaveSettings();
    }

    private void OptionChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _isApplyingHistory) return;
        _settings.TopMost = TopMostCheck.IsChecked == true;
        _settings.ClickThrough = ClickThroughCheck.IsChecked == true;
        _settings.ResizeItemsWithWindow = ResizeItemsWithWindowCheck.IsChecked == true;
        var newPipPositionLocked = PipPositionLockCheck.IsChecked == true;
        var pipLockChanged = _settings.PipPositionLockedToTarget != newPipPositionLocked;
        _settings.PipPositionLockedToTarget = newPipPositionLocked;
        if (_overlay != null)
        {
            _overlay.Topmost = _settings.TopMost;
            _overlay.SetClickThrough(_settings.ClickThrough);
            _overlay.SetResizeItemsWithWindow(_settings.ResizeItemsWithWindow);
            _overlay.SetPipPositionLockedToTarget(_settings.PipPositionLockedToTarget, captureCurrentPosition: pipLockChanged && _settings.PipPositionLockedToTarget);
        }
        foreach (var detached in _detachedOverlays.Values)
        {
            detached.Topmost = _settings.TopMost;
            detached.SetClickThrough(_settings.ClickThrough);
            detached.SetResizeItemsWithWindow(_settings.ResizeItemsWithWindow);
            detached.SetPipPositionLockedToTarget(_settings.PipPositionLockedToTarget, captureCurrentPosition: pipLockChanged && _settings.PipPositionLockedToTarget);
        }
        if (_burstMonitorWindow != null)
        {
            _burstMonitorWindow.SetClickThrough(_settings.ClickThrough);
            _burstMonitorWindow.Topmost = _settings.TopMost;
            ReapplyBurstMonitorTopMost(force: true);
        }
        if (pipLockChanged && _settings.PipPositionLockedToTarget)
        {
            CaptureFilterKeysPillRelativePosition(save: false);
            CaptureBurstMonitorRelativePosition(save: false);
        }
        SaveSettings();
    }

    private void ApplyPipGroupChange(Action action)
    {
        _isApplyingPipGroupChange = true;
        try
        {
            action();
        }
        finally
        {
            _isApplyingPipGroupChange = false;
        }
    }

    private void DetachCropsToPip(IReadOnlyList<CropItem> crops)
    {
        var targets = crops.Where(c => _crops.Contains(c)).DistinctBy(c => c.Id).ToList();
        if (targets.Count == 0) return;

        PushHistoryCheckpoint();
        EnsureOverlay();

        var groupId = Guid.NewGuid().ToString("N");
        var layout = BuildDetachLayoutSnapshot(targets);
        var sourceWindow = !string.IsNullOrWhiteSpace(layout.SourceGroupId) && _detachedOverlays.TryGetValue(layout.SourceGroupId, out var existingSource)
            ? existingSource
            : null;
        var sourceLeft = sourceWindow?.Left ?? _overlay?.Left ?? _settings.OverlayLeft;
        var sourceTop = sourceWindow?.Top ?? _overlay?.Top ?? _settings.OverlayTop;
        var left = sourceLeft + Math.Max(0, layout.Bounds.X);
        var top = sourceTop + Math.Max(0, layout.Bounds.Y);
        var sourcePipKey = string.IsNullOrWhiteSpace(layout.SourceGroupId) ? MainPipKey : layout.SourceGroupId!;
        _settings.PipCropOpacityByKey[groupId] = GetPipCropOpacity(sourcePipKey);

        ApplyPipGroupChange(() =>
        {
            foreach (var crop in targets)
            {
                if (layout.NormalizedOffsets.TryGetValue(crop.Id, out var offset))
                {
                    crop.PipOffsetX = offset.X;
                    crop.PipOffsetY = offset.Y;
                }
                crop.DetachedGroupIdRuntime = groupId;
            }
        });

        var width = Math.Max(80, layout.Bounds.Width + 16);
        var height = Math.Max(60, layout.Bounds.Height + 16);
        CreateDetachedOverlay(groupId, left, top, width, height);

        _selectedPipKeys.Clear();
        _selectedPipKeys.Add(groupId);
        CleanupEmptyDetachedOverlays();
        RefreshAllOverlays();
        SaveSettings();
        StatusText.Text = targets.Count > 1 ? $"PIP 분리: 크롭 {targets.Count}개" : $"PIP 분리: {targets[0].Name}";
    }

    private void ReattachCropsToMainPip(IReadOnlyList<CropItem> crops)
    {
        var targets = crops.Where(c => _crops.Contains(c)).DistinctBy(c => c.Id).ToList();
        if (targets.Count == 0) return;

        PushHistoryCheckpoint();
        EnsureOverlay();
        MoveCropsToMainPipVisibleArea(targets);

        ApplyPipGroupChange(() =>
        {
            foreach (var crop in targets)
            {
                crop.DetachedGroupIdRuntime = null;
            }
        });

        CleanupEmptyDetachedOverlays();
        EnsureMainOverlayFitsCrops();
        EnsureMainOverlayVisibleForCrops();
        RefreshAllOverlays();
        SaveSettings();
        StatusText.Text = targets.Count > 1 ? $"메인 PIP로 합치기: 크롭 {targets.Count}개" : $"메인 PIP로 합치기: {targets[0].Name}";
    }

    private void MoveCropsToMainPipVisibleArea(IReadOnlyList<CropItem> targets)
    {
        var layout = BuildCurrentGroupVisualLayout(targets);
        var destination = GetNextMainCropDisplayPosition(layout.Bounds.Width, layout.Bounds.Height);
        foreach (var crop in targets)
        {
            var visual = layout.VisualBoundsByCropId[crop.Id];
            crop.DisplayX = Math.Round(destination.X + visual.Left - layout.Bounds.Left, 2);
            crop.DisplayY = Math.Round(destination.Y + visual.Top - layout.Bounds.Top, 2);
            crop.PipOffsetX = 0;
            crop.PipOffsetY = 0;
        }
    }

    private GroupVisualLayout BuildCurrentGroupVisualLayout(IReadOnlyList<CropItem> targets)
    {
        var items = targets.DistinctBy(c => c.Id).ToList();
        if (items.Count == 0)
        {
            return new GroupVisualLayout(new Rect(0, 0, 80, 60), new Dictionary<string, Rect>());
        }

        var sourceGroupId = items.Select(c => c.DetachedGroupIdRuntime).Where(g => !string.IsNullOrWhiteSpace(g)).Distinct().SingleOrDefault();
        var sourceCrops = !string.IsNullOrWhiteSpace(sourceGroupId)
            ? _crops.Where(c => c.DetachedGroupIdRuntime == sourceGroupId).ToList()
            : _crops.Where(c => !c.IsDetachedRuntime).ToList();

        var minDisplayX = sourceCrops.Count == 0 ? 0 : sourceCrops.Min(c => c.DisplayX);
        var minDisplayY = sourceCrops.Count == 0 ? 0 : sourceCrops.Min(c => c.DisplayY);
        var visualById = new Dictionary<string, Rect>();

        foreach (var crop in items)
        {
            var left = !string.IsNullOrWhiteSpace(sourceGroupId)
                ? crop.DisplayX - minDisplayX + crop.PipOffsetX
                : crop.DisplayX + crop.PipOffsetX;
            var top = !string.IsNullOrWhiteSpace(sourceGroupId)
                ? crop.DisplayY - minDisplayY + crop.PipOffsetY
                : crop.DisplayY + crop.PipOffsetY;
            visualById[crop.Id] = new Rect(left, top, Math.Max(4, crop.DisplayWidth), Math.Max(4, crop.DisplayHeight));
        }

        var minX = visualById.Values.Min(r => r.Left);
        var minY = visualById.Values.Min(r => r.Top);
        var maxX = visualById.Values.Max(r => r.Right);
        var maxY = visualById.Values.Max(r => r.Bottom);
        return new GroupVisualLayout(new Rect(minX, minY, Math.Max(4, maxX - minX), Math.Max(4, maxY - minY)), visualById);
    }

    private void NormalizeDetachedGroupLayout(string groupId)
    {
        var groupCrops = _crops.Where(c => c.DetachedGroupIdRuntime == groupId).ToList();
        if (groupCrops.Count == 0) return;
        var layout = BuildCurrentGroupVisualLayout(groupCrops);
        foreach (var crop in groupCrops)
        {
            var visual = layout.VisualBoundsByCropId[crop.Id];
            crop.DisplayX = Math.Round(visual.Left - layout.Bounds.Left, 2);
            crop.DisplayY = Math.Round(visual.Top - layout.Bounds.Top, 2);
            crop.PipOffsetX = 0;
            crop.PipOffsetY = 0;
        }
    }

    private void EnsureDetachedOverlayFitsGroup(string groupId)
    {
        if (!_detachedOverlays.TryGetValue(groupId, out var overlay)) return;
        var groupCrops = _crops.Where(c => c.Enabled && c.DetachedGroupIdRuntime == groupId).ToList();
        if (groupCrops.Count == 0) return;
        var bounds = GetDisplayBounds(groupCrops);
        var scale = Math.Max(0.1, _settings.Scale);
        var requiredWidth = Math.Max(80, (bounds.X + bounds.Width) * scale + 24);
        var requiredHeight = Math.Max(60, (bounds.Y + bounds.Height) * scale + 24);
        if (overlay.Width < requiredWidth) overlay.Width = requiredWidth;
        if (overlay.Height < requiredHeight) overlay.Height = requiredHeight;
    }

    private void CreateDetachedOverlay(string groupId, double left, double top, double width, double height)
    {
        if (_detachedOverlays.TryGetValue(groupId, out var existing))
        {
            existing.Show();
            existing.Activate();
            return;
        }

        var saved = GetSavedPipPlacement(groupId);
        if (saved != null)
        {
            left = saved.Left;
            top = saved.Top;
            width = saved.Width;
            height = saved.Height;
        }

        var detached = new OverlayWindow(_crops, _settings, groupId, persistWindowBounds: false)
        {
            Left = left,
            Top = top,
            Width = Math.Max(60, width),
            Height = Math.Max(40, height),
            Opacity = 1.0,
            Topmost = _settings.TopMost
        };
        WireOverlayEvents(detached);
        detached.SetTargetLockState(saved?.TargetOffsetX, saved?.TargetOffsetY, saved?.TargetBaselineWidth ?? 0, saved?.TargetBaselineHeight ?? 0);
        _detachedOverlays[groupId] = detached;
        detached.Show();
        detached.SetPipBackgroundOpacity(GetPipBackgroundOpacity(groupId), save: false);
        detached.SetCropOpacity(GetPipCropOpacity(groupId));
        detached.SetCaptureRefreshOptions(_settings.CaptureIntervalMs, _settings.UseFrameArrivedRefresh, _settings.ShowCapturePerformanceStats);
        if (_target != null && _capture != null) detached.SetCapture(_capture, _target.Hwnd);
        var shouldCaptureLock = _settings.PipPositionLockedToTarget && (saved?.TargetOffsetX is null || saved?.TargetOffsetY is null);
        detached.SetPipPositionLockedToTarget(_settings.PipPositionLockedToTarget, captureCurrentPosition: shouldCaptureLock);

        ApplyDetachedPipSelectionVisuals();
        SaveSettingsThrottled();
    }

    private void CleanupEmptyDetachedOverlays()
    {
        foreach (var pair in _detachedOverlays.ToList())
        {
            var groupId = pair.Key;
            var hasCrop = _crops.Any(c => c.DetachedGroupIdRuntime == groupId);
            if (!hasCrop)
            {
                _selectedPipKeys.RemoveAll(k => k == groupId);
                _settings.PipCropOpacityByKey.Remove(groupId);
                _settings.PipBackgroundOpacityByKey.Remove(groupId);
                _settings.PipWindowPlacementsByKey.Remove(groupId);
                _detachedOverlays.Remove(groupId);
                pair.Value.ForceClose();
            }
        }
        UpdatePipSelectorItems();
    }

    private string GetPipKey(OverlayWindow overlay)
    {
        return string.IsNullOrWhiteSpace(overlay.DetachedGroupId) ? MainPipKey : overlay.DetachedGroupId!;
    }

    private OverlayWindow? GetOverlayByPipKey(string key)
    {
        if (key == MainPipKey) return _overlay;
        return _detachedOverlays.TryGetValue(key, out var overlay) ? overlay : null;
    }

    private IEnumerable<string> GetExistingPipKeys()
    {
        if (_overlay != null) yield return MainPipKey;
        foreach (var key in _detachedOverlays.Keys) yield return key;
    }

    private void SelectDetachedPip(OverlayWindow overlay, bool additive)
    {
        var key = GetPipKey(overlay);
        if (GetOverlayByPipKey(key) == null) return;

        if (!additive)
        {
            _selectedPipKeys.Clear();
            _selectedPipKeys.Add(key);
        }
        else
        {
            if (_selectedPipKeys.Contains(key))
            {
                if (_selectedPipKeys.Count > 1) _selectedPipKeys.Remove(key);
            }
            else
            {
                _selectedPipKeys.Add(key);
            }
        }

        ApplyDetachedPipSelectionVisuals();
        SelectPipInCombo(key);
        BringPipToFront(key);
    }

    private void ClearDetachedPipSelection()
    {
        _selectedPipKeys.Clear();
        ApplyDetachedPipSelectionVisuals();
    }

    private void ApplyDetachedPipSelectionVisuals()
    {
        _selectedPipKeys.RemoveAll(k => !GetExistingPipKeys().Contains(k));
        _overlay?.SetPipSelected(_selectedPipKeys.Contains(MainPipKey));
        foreach (var pair in _detachedOverlays)
        {
            pair.Value.SetPipSelected(_selectedPipKeys.Contains(pair.Key));
        }
        UpdatePipSelectorItems();
    }


    private void CyclePipSelectionAtPoint(OverlayWindow sourceOverlay, Point screenDip, bool additive)
    {
        var candidates = GetPipKeysAtScreenPoint(screenDip).ToList();
        if (candidates.Count <= 1) return;

        var sourceKey = GetPipKey(sourceOverlay);
        var currentKey = _selectedPipKeys.LastOrDefault(k => candidates.Contains(k));
        if (string.IsNullOrWhiteSpace(currentKey) || !candidates.Contains(currentKey)) currentKey = sourceKey;
        if (!candidates.Contains(currentKey)) currentKey = candidates[0];

        var currentIndex = candidates.IndexOf(currentKey);
        var nextKey = candidates[(currentIndex + 1) % candidates.Count];

        if (additive)
        {
            if (!_selectedPipKeys.Contains(nextKey)) _selectedPipKeys.Add(nextKey);
        }
        else
        {
            _selectedPipKeys.Clear();
            _selectedPipKeys.Add(nextKey);
        }

        ApplyDetachedPipSelectionVisuals();
        SelectPipInCombo(nextKey);
        BringPipToFront(nextKey);
        var nextDisplayIndex = currentIndex + 2 > candidates.Count ? 1 : currentIndex + 2;
        StatusText.Text = candidates.Count > 1
            ? $"겹친 PIP 선택 전환: {GetUserVisiblePipNameFromKey(nextKey)} ({nextDisplayIndex}/{candidates.Count})"
            : $"PIP 선택: {GetUserVisiblePipNameFromKey(nextKey)}";
    }

    private IEnumerable<string> GetPipKeysAtScreenPoint(Point screenDip)
    {
        var ordered = GetExistingPipKeys().ToList();
        foreach (var key in ordered)
        {
            var overlay = GetOverlayByPipKey(key);
            if (overlay == null || !overlay.IsVisible) continue;
            var bounds = new Rect(overlay.Left, overlay.Top, Math.Max(1, overlay.ActualWidth > 0 ? overlay.ActualWidth : overlay.Width), Math.Max(1, overlay.ActualHeight > 0 ? overlay.ActualHeight : overlay.Height));
            if (bounds.Contains(screenDip)) yield return key;
        }
    }

    private string GetUserVisiblePipNameFromKey(string key)
    {
        if (key == MainPipKey) return "메인 PIP";
        if (PipSelectCombo?.ItemsSource is IEnumerable<PipOption> options)
        {
            var option = options.FirstOrDefault(o => o.Key == key);
            if (option != null) return option.Name;
        }
        return $"분리 PIP {key[..Math.Min(4, key.Length)]}";
    }

    private void BringPipToFront(string key)
    {
        var overlay = GetOverlayByPipKey(key);
        if (overlay == null || !overlay.IsVisible) return;
        overlay.BringToFrontForSelection();
    }

    private void MergeSelectedPipsInto(OverlayWindow targetOverlay)
    {
        if (string.IsNullOrWhiteSpace(targetOverlay.DetachedGroupId)) return;
        var targetGroupId = targetOverlay.DetachedGroupId!;
        if (!_selectedPipKeys.Contains(targetGroupId)) _selectedPipKeys.Add(targetGroupId);

        var selectedGroups = _selectedPipKeys
            .Where(g => g != MainPipKey && _detachedOverlays.ContainsKey(g))
            .Distinct()
            .ToList();
        if (selectedGroups.Count < 2)
        {
            StatusText.Text = "병합할 PIP를 Shift+클릭으로 2개 이상 선택해주세요.";
            ApplyDetachedPipSelectionVisuals();
            return;
        }

        PushHistoryCheckpoint();
        NormalizeDetachedGroupLayout(targetGroupId);
        var targetCrops = _crops.Where(c => c.DetachedGroupIdRuntime == targetGroupId).ToList();
        var nextTop = targetCrops.Count == 0
            ? 10.0
            : targetCrops.Max(c => c.DisplayY + c.PipOffsetY + Math.Max(4, c.DisplayHeight)) + 8.0;

        ApplyPipGroupChange(() =>
        {
            foreach (var groupId in selectedGroups.Where(g => g != targetGroupId))
            {
                var groupCrops = _crops.Where(c => c.DetachedGroupIdRuntime == groupId).ToList();
                if (groupCrops.Count == 0) continue;

                var layout = BuildCurrentGroupVisualLayout(groupCrops);
                foreach (var crop in groupCrops)
                {
                    var visual = layout.VisualBoundsByCropId[crop.Id];
                    crop.DisplayX = Math.Round(10 + visual.Left - layout.Bounds.Left, 2);
                    crop.DisplayY = Math.Round(nextTop + visual.Top - layout.Bounds.Top, 2);
                    crop.PipOffsetX = 0;
                    crop.PipOffsetY = 0;
                    crop.DetachedGroupIdRuntime = targetGroupId;
                }
                nextTop += layout.Bounds.Height + 8.0;
            }
        });

        NormalizeDetachedGroupLayout(targetGroupId);
        EnsureDetachedOverlayFitsGroup(targetGroupId);

        foreach (var groupId in selectedGroups.Where(g => g != targetGroupId).ToList())
        {
            if (_detachedOverlays.TryGetValue(groupId, out var overlay))
            {
                _detachedOverlays.Remove(groupId);
                _settings.PipCropOpacityByKey.Remove(groupId);
                _settings.PipBackgroundOpacityByKey.Remove(groupId);
                overlay.ForceClose();
            }
        }

        _selectedPipKeys.Clear();
        _selectedPipKeys.Add(targetGroupId);
        RefreshAllOverlays();
        SaveSettings();
        StatusText.Text = $"선택한 PIP {selectedGroups.Count}개 병합 완료";
    }

    private void MoveSelectedPipsBy(OverlayWindow sourceOverlay, double dx, double dy)
    {
        var sourceKey = GetPipKey(sourceOverlay);
        if (!_selectedPipKeys.Contains(sourceKey))
        {
            _selectedPipKeys.Clear();
            _selectedPipKeys.Add(sourceKey);
            ApplyDetachedPipSelectionVisuals();
        }

        var targets = _selectedPipKeys
            .Select(GetOverlayByPipKey)
            .Where(o => o != null)
            .Cast<OverlayWindow>()
            .Distinct()
            .ToList();
        if (targets.Count == 0) targets.Add(sourceOverlay);

        PushHistoryCheckpoint();
        foreach (var overlay in targets)
        {
            overlay.Left = Math.Round(overlay.Left + dx, 2);
            overlay.Top = Math.Round(overlay.Top + dy, 2);
        }
        SaveSettingsThrottled();
    }

    private void AlignSelectedPipsVertically(OverlayWindow sourceOverlay, bool allowOverlap)
    {
        var sourceKey = GetPipKey(sourceOverlay);
        if (!_selectedPipKeys.Contains(sourceKey)) _selectedPipKeys.Add(sourceKey);

        // 선택 순서가 정렬 기준입니다. 무조건 Shift로 먼저 선택한 PIP가 상단 anchor입니다.
        var ordered = _selectedPipKeys
            .Select(k => new { Key = k, Overlay = GetOverlayByPipKey(k) })
            .Where(x => x.Overlay != null)
            .Select(x => new { x.Key, Overlay = x.Overlay! })
            .DistinctBy(x => x.Key)
            .ToList();

        if (ordered.Count < 2)
        {
            StatusText.Text = "정렬할 PIP를 Shift+클릭으로 2개 이상 선택해주세요.";
            ApplyDetachedPipSelectionVisuals();
            return;
        }

        PushHistoryCheckpoint();
        var anchor = ordered[0].Overlay;
        var anchorTop = anchor.Top;
        var anchorCenterX = anchor.Left + anchor.Width / 2.0;
        const double gap = 8.0;

        if (allowOverlap)
        {
            foreach (var item in ordered)
            {
                item.Overlay.Left = Math.Round(anchorCenterX - item.Overlay.Width / 2.0, 2);
                item.Overlay.Top = Math.Round(anchorTop, 2);
            }
            StatusText.Text = $"선택 PIP {ordered.Count}개 겹치기 + 중앙 상하 정렬 완료";
        }
        else
        {
            var nextTop = anchorTop;
            foreach (var item in ordered)
            {
                item.Overlay.Left = Math.Round(anchorCenterX - item.Overlay.Width / 2.0, 2);
                item.Overlay.Top = Math.Round(nextTop, 2);
                nextTop += Math.Max(40, item.Overlay.Height) + gap;
            }
            StatusText.Text = $"선택 PIP {ordered.Count}개 겹치기 X + 중앙 상하 정렬 완료";
        }

        SaveSettings();
        ApplyDetachedPipSelectionVisuals();
    }

    private static Rect GetDisplayBounds(IReadOnlyList<CropItem> crops)
    {
        if (crops.Count == 0) return new Rect(0, 0, 80, 60);
        var minX = crops.Min(c => c.DisplayX + c.PipOffsetX);
        var minY = crops.Min(c => c.DisplayY + c.PipOffsetY);
        var maxX = crops.Max(c => c.DisplayX + c.PipOffsetX + Math.Max(4, c.DisplayWidth));
        var maxY = crops.Max(c => c.DisplayY + c.PipOffsetY + Math.Max(4, c.DisplayHeight));
        return new Rect(minX, minY, Math.Max(4, maxX - minX), Math.Max(4, maxY - minY));
    }

    private DetachLayoutSnapshot BuildDetachLayoutSnapshot(IReadOnlyList<CropItem> targets)
    {
        var sourceGroupIds = targets
            .Select(c => c.DetachedGroupIdRuntime)
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Distinct()
            .ToList();
        var sourceGroupId = sourceGroupIds.Count == 1 ? sourceGroupIds[0] : null;
        var sourceCrops = !string.IsNullOrWhiteSpace(sourceGroupId)
            ? _crops.Where(c => c.DetachedGroupIdRuntime == sourceGroupId).ToList()
            : _crops.Where(c => !c.IsDetachedRuntime).ToList();

        var sourceMinX = !string.IsNullOrWhiteSpace(sourceGroupId) && sourceCrops.Count > 0 ? sourceCrops.Min(c => c.DisplayX) : 0.0;
        var sourceMinY = !string.IsNullOrWhiteSpace(sourceGroupId) && sourceCrops.Count > 0 ? sourceCrops.Min(c => c.DisplayY) : 0.0;
        var scale = Math.Max(0.1, _settings.Scale);

        double CurrentVisualLeft(CropItem crop) => !string.IsNullOrWhiteSpace(sourceGroupId)
            ? crop.DisplayX - sourceMinX + crop.PipOffsetX
            : crop.DisplayX + crop.PipOffsetX;
        double CurrentVisualTop(CropItem crop) => !string.IsNullOrWhiteSpace(sourceGroupId)
            ? crop.DisplayY - sourceMinY + crop.PipOffsetY
            : crop.DisplayY + crop.PipOffsetY;

        var minX = targets.Min(CurrentVisualLeft);
        var minY = targets.Min(CurrentVisualTop);
        var maxX = targets.Max(c => CurrentVisualLeft(c) + Math.Max(4, c.DisplayWidth * scale));
        var maxY = targets.Max(c => CurrentVisualTop(c) + Math.Max(4, c.DisplayHeight * scale));
        var bounds = new Rect(minX, minY, Math.Max(4, maxX - minX), Math.Max(4, maxY - minY));

        var newGroupBaseMinX = targets.Min(c => c.DisplayX);
        var newGroupBaseMinY = targets.Min(c => c.DisplayY);
        var offsets = new Dictionary<string, Point>();
        foreach (var crop in targets)
        {
            var desiredLeftInNewPip = CurrentVisualLeft(crop) - bounds.X;
            var desiredTopInNewPip = CurrentVisualTop(crop) - bounds.Y;
            var newBaseLeft = crop.DisplayX - newGroupBaseMinX;
            var newBaseTop = crop.DisplayY - newGroupBaseMinY;
            offsets[crop.Id] = new Point(
                Math.Round(desiredLeftInNewPip - newBaseLeft, 2),
                Math.Round(desiredTopInNewPip - newBaseTop, 2));
        }

        return new DetachLayoutSnapshot(bounds, offsets, sourceGroupId);
    }

    private Point GetNextMainCropDisplayPosition(double newWidth, double newHeight)
    {
        var mainCrops = _crops.Where(c => c.Enabled && !c.IsDetachedRuntime).ToList();
        if (mainCrops.Count == 0) return new Point(10, 10);

        var bottomCrop = mainCrops
            .OrderByDescending(c => c.DisplayY + c.PipOffsetY + Math.Max(4, c.DisplayHeight))
            .First();

        var x = Math.Max(0, bottomCrop.DisplayX + bottomCrop.PipOffsetX);
        var y = Math.Max(0, bottomCrop.DisplayY + bottomCrop.PipOffsetY + Math.Max(4, bottomCrop.DisplayHeight) + 8);
        return new Point(Math.Round(x, 2), Math.Round(y, 2));
    }

    private void EnsureMainOverlayFitsCrops()
    {
        if (_overlay == null) return;

        var mainCrops = _crops.Where(c => c.Enabled && !c.IsDetachedRuntime).ToList();
        if (mainCrops.Count == 0)
        {
            if (_overlay.Width < 120) _overlay.Width = 120;
            if (_overlay.Height < 80) _overlay.Height = 80;
            return;
        }

        var bounds = GetDisplayBounds(mainCrops);
        var scale = Math.Max(0.1, _settings.Scale);
        var requiredWidth = Math.Max(80, (bounds.X + bounds.Width) * scale + 24);
        var requiredHeight = Math.Max(60, (bounds.Y + bounds.Height) * scale + 24);

        if (_overlay.Width < requiredWidth) _overlay.Width = requiredWidth;
        if (_overlay.Height < requiredHeight) _overlay.Height = requiredHeight;
    }

    private void EnsureMainOverlayVisibleForCrops()
    {
        if (_overlay == null) return;
        if (!_overlay.IsVisible) _overlay.Show();
        if (_settings.TopMost) _overlay.Topmost = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_awaitingBurstTriggerKeyRole is BurstRole captureRole)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape)
            {
                _awaitingBurstTriggerKeyRole = null;
                BurstTriggerKeyCaptureHintText.Text = "키 등록을 취소했습니다.";
                e.Handled = true;
                return;
            }

            if (IsModifierOnlyKey(key))
            {
                BurstTriggerKeyCaptureHintText.Text = "Ctrl/Shift/Alt/Win 단독키는 등록할 수 없습니다. 실제 스킬키를 누르세요.";
                e.Handled = true;
                return;
            }

            var virtualKey = KeyInterop.VirtualKeyFromKey(key);
            if (virtualKey > 0)
            {
                var display = key.ToString();
                SetBurstTriggerKey(captureRole, virtualKey, display);
                _awaitingBurstTriggerKeyRole = null;
                BurstTriggerKeyCaptureHintText.Text = $"{captureRole.ToKoreanName()} 키 등록 완료: {display}";
                e.Handled = true;
                return;
            }
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (e.Key == Key.Z)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) RedoLastAction();
                else UndoLastAction();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Y)
            {
                RedoLastAction();
                e.Handled = true;
                return;
            }
        }

        if (CropGrid.IsKeyboardFocusWithin) return;

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

        if (_selectedPipKeys.Count == 0) return;
        var source = _selectedPipKeys.Select(GetOverlayByPipKey).FirstOrDefault(o => o != null);
        if (source == null) return;
        MoveSelectedPipsBy(source, dx, dy);
        e.Handled = true;
    }

    private static double GetKeyboardMoveStep()
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) return 0.25;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) return 10.0;
        return 1.0;
    }

    private void PushHistoryCheckpoint()
    {
        if (_isApplyingHistory) return;

        var snapshot = CaptureHistorySnapshot();
        var signature = snapshot.Signature;
        if (_undoHistory.Count > 0 && _undoHistory[^1].Signature == signature) return;

        _undoHistory.Add(snapshot);
        if (_undoHistory.Count > MaxHistoryDepth) _undoHistory.RemoveAt(0);
        _redoHistory.Clear();
    }

    private void UndoLastAction()
    {
        if (_undoHistory.Count == 0)
        {
            StatusText.Text = "취소할 이전 행동이 없습니다.";
            return;
        }

        var current = CaptureHistorySnapshot();
        var snapshot = _undoHistory[^1];
        _undoHistory.RemoveAt(_undoHistory.Count - 1);
        _redoHistory.Add(current);
        if (_redoHistory.Count > MaxHistoryDepth) _redoHistory.RemoveAt(0);
        ApplyHistorySnapshot(snapshot);
        StatusText.Text = $"이전 행동 취소 완료. 남은 취소 단계: {_undoHistory.Count}";
    }

    private void RedoLastAction()
    {
        if (_redoHistory.Count == 0)
        {
            StatusText.Text = "되돌릴 행동이 없습니다.";
            return;
        }

        var current = CaptureHistorySnapshot();
        var snapshot = _redoHistory[^1];
        _redoHistory.RemoveAt(_redoHistory.Count - 1);
        _undoHistory.Add(current);
        if (_undoHistory.Count > MaxHistoryDepth) _undoHistory.RemoveAt(0);
        ApplyHistorySnapshot(snapshot);
        StatusText.Text = $"행동 되돌리기 완료. 남은 되돌리기 단계: {_redoHistory.Count}";
    }

    private HistorySnapshot CaptureHistorySnapshot()
    {
        return new HistorySnapshot
        {
            Crops = _crops.Select(CropSnapshot.FromCrop).ToList(),
            OverlayLeft = _overlay?.Left ?? _settings.OverlayLeft,
            OverlayTop = _overlay?.Top ?? _settings.OverlayTop,
            OverlayWidth = _overlay?.Width ?? _settings.OverlayWidth,
            OverlayHeight = _overlay?.Height ?? _settings.OverlayHeight,
            PipWindows = GetAllPipWindowSnapshots(),
            Scale = _settings.Scale,
            Opacity = _settings.Opacity
        };
    }

    private void ApplyHistorySnapshot(HistorySnapshot snapshot)
    {
        _isApplyingHistory = true;
        try
        {
            foreach (var existing in _crops) existing.PropertyChanged -= Crop_PropertyChanged;
            _crops.Clear();

            foreach (var cropSnapshot in snapshot.Crops)
            {
                var restored = cropSnapshot.ToCropItem();
                _crops.Add(restored);
            }

            _settings.Crops = _crops.ToList();
            _settings.Scale = snapshot.Scale;
            _settings.Opacity = snapshot.Opacity;
            ScaleSlider.Value = snapshot.Scale;
            OpacitySlider.Value = snapshot.Opacity;
            UpdateScaleUi(snapshot.Scale);
            UpdateOpacityUi(snapshot.Opacity);

            EnsureOverlay();
            if (_overlay != null)
            {
                _overlay.Left = snapshot.OverlayLeft;
                _overlay.Top = snapshot.OverlayTop;
                _overlay.Width = Math.Max(60, snapshot.OverlayWidth);
                _overlay.Height = Math.Max(40, snapshot.OverlayHeight);
                _overlay.Opacity = 1.0;
                _overlay.SetCropOpacity(snapshot.Opacity);
                _overlay.SetClickThrough(_settings.ClickThrough);
            }

            SyncDetachedOverlaysWithCurrentCropGroups();
            ApplyPipWindowSnapshots(snapshot.PipWindows);
            UpdatePipSelectorItems();
            RefreshAllOverlays();
            SaveSettings();
        }
        finally
        {
            _isApplyingHistory = false;
        }
    }

    private void SyncDetachedOverlaysWithCurrentCropGroups()
    {
        var groups = _crops
            .Where(c => !string.IsNullOrWhiteSpace(c.DetachedGroupIdRuntime))
            .Select(c => c.DetachedGroupIdRuntime!)
            .Distinct()
            .ToList();

        foreach (var pair in _detachedOverlays.ToList())
        {
            if (groups.Contains(pair.Key)) continue;
            _selectedPipKeys.RemoveAll(k => k == pair.Key);
            _settings.PipCropOpacityByKey.Remove(pair.Key);
            _settings.PipBackgroundOpacityByKey.Remove(pair.Key);
            _settings.PipWindowPlacementsByKey.Remove(pair.Key);
            _detachedOverlays.Remove(pair.Key);
            pair.Value.ForceClose();
        }

        EnsureOverlay();
        var scale = Math.Max(0.1, _settings.Scale);
        foreach (var groupId in groups)
        {
            if (_detachedOverlays.ContainsKey(groupId)) continue;
            var groupCrops = _crops.Where(c => c.DetachedGroupIdRuntime == groupId).ToList();
            if (groupCrops.Count == 0) continue;
            var bounds = GetDisplayBounds(groupCrops);
            var left = (_overlay?.Left ?? _settings.OverlayLeft) + Math.Max(0, bounds.X);
            var top = (_overlay?.Top ?? _settings.OverlayTop) + Math.Max(0, bounds.Y);
            var width = Math.Max(80, bounds.Width * scale + 16);
            var height = Math.Max(60, bounds.Height * scale + 16);
            CreateDetachedOverlay(groupId, left, top, width, height);
        }

        _selectedPipKeys.RemoveAll(k => k != MainPipKey && !groups.Contains(k));
        ApplyDetachedPipSelectionVisuals();
    }

    private static double ClampAndRoundOpacity(double value)
    {
        return Math.Round(Math.Clamp(value, 0.05, 1.0), 2);
    }

    private void UpdateOpacityUi(double value)
    {
        if (OpacityText != null) OpacityText.Text = $"{Math.Round(value * 100):0}%";
    }

    private double GetPipCropOpacity(string key)
    {
        _settings.PipCropOpacityByKey ??= new Dictionary<string, double>();
        if (_settings.PipCropOpacityByKey.TryGetValue(key, out var value))
        {
            return ClampAndRoundOpacity(value);
        }
        return ClampAndRoundOpacity(_settings.Opacity);
    }

    private void SetPipCropOpacity(string key, double value, bool save)
    {
        value = ClampAndRoundOpacity(value);
        _settings.PipCropOpacityByKey ??= new Dictionary<string, double>();
        _settings.PipCropOpacityByKey[key] = value;
        GetOverlayByPipKey(key)?.SetCropOpacity(value);
        if (save) SaveSettings();
    }

    private List<string> GetOpacityTargetPipKeys()
    {
        var targets = _selectedPipKeys
            .Where(k => GetOverlayByPipKey(k) != null)
            .Distinct()
            .ToList();
        if (targets.Count == 0 && PipSelectCombo?.SelectedItem is PipOption option) targets.Add(option.Key);
        if (targets.Count == 0) targets.Add(MainPipKey);
        return targets;
    }

    private void ApplyCropOpacityToAll_Click(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _isApplyingHistory) return;
        var value = ClampAndRoundOpacity(OpacitySlider.Value);
        _settings.Opacity = value;
        var keys = GetExistingPipKeys().Distinct().ToList();
        if (!keys.Contains(MainPipKey)) keys.Insert(0, MainPipKey);
        foreach (var key in keys) SetPipCropOpacity(key, value, save: false);
        UpdateOpacityUi(value);
        SaveSettings();
        StatusText.Text = $"크롭 투명도 전체 PIP 적용: {Math.Round(value * 100):0}%";
    }

    private double GetPipBackgroundOpacity(string key)
    {
        if (_settings.PipBackgroundOpacityByKey.TryGetValue(key, out var value))
        {
            return Math.Round(Math.Clamp(value, 0.0, 1.0), 2);
        }
        if (_settings.PipWindowPlacementsByKey.TryGetValue(key, out var placement))
        {
            return Math.Round(Math.Clamp(placement.BackgroundOpacity, 0.0, 1.0), 2);
        }
        return Math.Round(Math.Clamp(_settings.PipBackgroundOpacity, 0.0, 1.0), 2);
    }

    private void SetPipBackgroundOpacity(string key, double value, bool save)
    {
        value = Math.Round(Math.Clamp(value, 0.0, 1.0), 2);
        _settings.PipBackgroundOpacityByKey[key] = value;
        if (key == MainPipKey) _settings.PipBackgroundOpacity = value;
        GetOverlayByPipKey(key)?.SetPipBackgroundOpacity(value, save: false);
        if (_settings.PipWindowPlacementsByKey.TryGetValue(key, out var placement)) placement.BackgroundOpacity = value;
        if (save) SaveSettings();
    }

    private void UpdatePipBackgroundOpacityUi(double value)
    {
        if (PipBackgroundOpacityText != null) PipBackgroundOpacityText.Text = $"{Math.Round(value * 100):0}%";
    }

    private void ApplyPipBackgroundOpacityToAll_Click(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _isApplyingHistory) return;
        var value = Math.Round(Math.Clamp(PipBackgroundOpacitySlider.Value, 0.0, 1.0), 2);
        var keys = GetExistingPipKeys().Distinct().ToList();
        if (!keys.Contains(MainPipKey)) keys.Insert(0, MainPipKey);
        foreach (var key in keys)
        {
            SetPipBackgroundOpacity(key, value, save: false);
        }
        UpdatePipBackgroundOpacityUi(value);
        RefreshAllOverlays();
        SaveSettings();
        StatusText.Text = $"PIP 배경 투명도 전체 적용: {Math.Round(value * 100):0}%";
    }

    private void UpdatePipSelectorItems()
    {
        if (PipSelectCombo == null) return;
        var currentKey = (PipSelectCombo.SelectedItem as PipOption)?.Key;
        var options = new List<PipOption>();
        if (_overlay != null) options.Add(new PipOption(MainPipKey, "메인 PIP"));
        var index = 1;
        foreach (var pair in _detachedOverlays.OrderBy(p => p.Value.Top).ThenBy(p => p.Value.Left))
        {
            options.Add(new PipOption(pair.Key, $"분리 PIP {index++}"));
        }

        _isUpdatingPipSelector = true;
        try
        {
            PipSelectCombo.ItemsSource = options;
            var selectedKey = _selectedPipKeys.LastOrDefault(k => options.Any(o => o.Key == k))
                          ?? currentKey
                          ?? options.FirstOrDefault()?.Key;
            PipSelectCombo.SelectedItem = options.FirstOrDefault(o => o.Key == selectedKey);
            var opacityKey = selectedKey ?? MainPipKey;
            var cropOpacity = GetPipCropOpacity(opacityKey);
            OpacitySlider.Value = cropOpacity;
            UpdateOpacityUi(cropOpacity);
            var value = GetPipBackgroundOpacity(opacityKey);
            PipBackgroundOpacitySlider.Value = value;
            UpdatePipBackgroundOpacityUi(value);
        }
        finally
        {
            _isUpdatingPipSelector = false;
        }
    }

    private void SelectPipInCombo(string key)
    {
        if (PipSelectCombo?.ItemsSource is not IEnumerable<PipOption> options) return;
        var option = options.FirstOrDefault(o => o.Key == key);
        if (option == null) return;
        _isUpdatingPipSelector = true;
        try
        {
            PipSelectCombo.SelectedItem = option;
            var cropOpacity = GetPipCropOpacity(key);
            OpacitySlider.Value = cropOpacity;
            UpdateOpacityUi(cropOpacity);
            var value = GetPipBackgroundOpacity(key);
            PipBackgroundOpacitySlider.Value = value;
            UpdatePipBackgroundOpacityUi(value);
        }
        finally
        {
            _isUpdatingPipSelector = false;
        }
    }

    private void PipSelectCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || _isUpdatingPipSelector) return;
        if (PipSelectCombo.SelectedItem is not PipOption option) return;
        _selectedPipKeys.Clear();
        _selectedPipKeys.Add(option.Key);
        ApplyDetachedPipSelectionVisuals();
        BringPipToFront(option.Key);
        _isUpdatingPipSelector = true;
        try
        {
            var cropOpacity = GetPipCropOpacity(option.Key);
            OpacitySlider.Value = cropOpacity;
            UpdateOpacityUi(cropOpacity);
            var value = GetPipBackgroundOpacity(option.Key);
            PipBackgroundOpacitySlider.Value = value;
            UpdatePipBackgroundOpacityUi(value);
        }
        finally
        {
            _isUpdatingPipSelector = false;
        }
    }

    private void PipBackgroundOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing || _isApplyingHistory || _isUpdatingPipSelector) return;
        var value = Math.Round(Math.Clamp(e.NewValue, 0.0, 1.0), 2);
        if (Math.Abs(PipBackgroundOpacitySlider.Value - value) > 0.0001)
        {
            PipBackgroundOpacitySlider.Value = value;
            return;
        }

        var targets = _selectedPipKeys.Where(k => GetOverlayByPipKey(k) != null).Distinct().ToList();
        if (targets.Count == 0 && PipSelectCombo.SelectedItem is PipOption option) targets.Add(option.Key);
        if (targets.Count == 0) targets.Add(MainPipKey);

        foreach (var key in targets) SetPipBackgroundOpacity(key, value, save: false);
        UpdatePipBackgroundOpacityUi(value);
        SaveSettings();
    }

    private void UpdateScaleUi(double value)
    {
        if (ScaleText != null) ScaleText.Text = $"{Math.Round(value * 100):0}%";
    }

    private void AdjustOpacity(double delta)
    {
        var value = ClampAndRoundOpacity(OpacitySlider.Value + delta);
        OpacitySlider.Value = value;
    }

    private void OpacityMinus5_Click(object sender, RoutedEventArgs e) => AdjustOpacity(-0.05);
    private void OpacityMinus1_Click(object sender, RoutedEventArgs e) => AdjustOpacity(-0.01);
    private void OpacityPlus1_Click(object sender, RoutedEventArgs e) => AdjustOpacity(0.01);
    private void OpacityPlus5_Click(object sender, RoutedEventArgs e) => AdjustOpacity(0.05);

    private async void CropGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var crop = GetCropFromGridMouseEvent(e) ?? CropGrid.SelectedItem as CropItem;
        if (crop == null) return;
        await EditCropAsync(crop);
    }

    private async Task EditCropAsync(CropItem crop)
    {
        if (_target == null)
        {
            SelectTarget_Click(this, new RoutedEventArgs());
            if (_target == null) return;
        }

        var frame = await WaitForFrameAsync();
        if (frame == null)
        {
            MessageBox.Show("아직 캡처 프레임이 없습니다. 대상 선택 후 1초 정도 기다린 뒤 다시 시도해 주세요.");
            return;
        }

        var initial = new System.Windows.Int32Rect(crop.X, crop.Y, crop.Width, crop.Height);
        var selector = new PreviewCropWindow($"크롭 편집 - {crop.Name}", frame, initial, crop.Shape, _copiedCropTemplate) { Owner = this };
        var dialogResult = selector.ShowDialog();
        if (selector.CopiedTemplate is not null) _copiedCropTemplate = selector.CopiedTemplate;
        if (dialogResult != true || selector.SelectedRegion is not System.Windows.Int32Rect r) return;
        if (r.Width < 5 || r.Height < 5) return;

        PushHistoryCheckpoint();
        crop.ApplyRegion(r, selector.SelectedShape);
        crop.DisplayWidth = r.Width;
        crop.DisplayHeight = r.Height;
        RefreshAllOverlays();
        SaveSettings();
        UpdateCropThumbnails(force: true);
        StatusText.Text = $"크롭 편집 완료: {crop.Name} / {crop.Shape.ToKoreanName()} X:{crop.X} Y:{crop.Y} W:{crop.Width} H:{crop.Height}";
    }

    private void CropGrid_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var crop = GetCropFromGridMouseEvent(e);
        if (crop != null && !CropGrid.SelectedItems.Contains(crop))
        {
            CropGrid.SelectedItems.Clear();
            CropGrid.SelectedItem = crop;
        }

        var selected = GetSelectedGridCrops().ToList();
        if (selected.Count == 0 && crop != null) selected.Add(crop);
        if (selected.Count == 0) return;

        var menu = new ContextMenu();
        var copy = new MenuItem
        {
            Header = "크롭 형태 복사",
            Padding = new Thickness(12, 6, 12, 6)
        };
        copy.Click += (_, _) =>
        {
            var source = selected.First();
            _copiedCropTemplate = CropTemplate.FromCrop(source);
            StatusText.Text = $"크롭 형태 복사: {source.Name} / {source.Shape.ToKoreanName()} X:{source.X} Y:{source.Y} W:{source.Width} H:{source.Height}";
        };
        menu.Items.Add(copy);

        var copyBorderStyle = new MenuItem
        {
            Header = "테두리 스타일 복사",
            Padding = new Thickness(12, 6, 12, 6)
        };
        copyBorderStyle.Click += (_, _) =>
        {
            var source = selected.First();
            _copiedCropBorderStyle = CropBorderStyleTemplate.FromCrop(source);
            PasteCropBorderStyleButton.IsEnabled = true;
            StatusText.Text = $"테두리 스타일 복사: {source.Name} / {source.BorderColorHex} / {Math.Round(source.BorderOpacity * 100):0}%";
        };
        menu.Items.Add(copyBorderStyle);

        var pasteBorderStyle = new MenuItem
        {
            Header = selected.Count > 1 ? $"선택 {selected.Count}개에 테두리 스타일 붙여넣기" : "테두리 스타일 붙여넣기",
            Padding = new Thickness(12, 6, 12, 6),
            IsEnabled = _copiedCropBorderStyle != null
        };
        pasteBorderStyle.Click += (_, _) =>
        {
            if (_copiedCropBorderStyle == null) return;
            ApplyCropBorderStyleToSelection(_copiedCropBorderStyle.BorderColorHex, _copiedCropBorderStyle.BorderOpacity);
        };
        menu.Items.Add(pasteBorderStyle);

        var edit = new MenuItem
        {
            Header = "크롭 편집 열기",
            Padding = new Thickness(12, 6, 12, 6)
        };
        edit.Click += async (_, _) => await EditCropAsync(selected.First());
        menu.Items.Add(edit);

        var delete = new MenuItem
        {
            Header = selected.Count > 1 ? $"선택 {selected.Count}개 삭제" : "선택 삭제",
            Padding = new Thickness(12, 6, 12, 6)
        };
        delete.Click += (_, _) => DeleteCrop_Click(sender, new RoutedEventArgs());
        menu.Items.Add(delete);

        CropGrid.ContextMenu = menu;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void CropGrid_CurrentCellChanged(object sender, EventArgs e)
    {
        if (_isInitializing || _isApplyingHistory) return;
        RefreshAllOverlays();
        SaveSettingsThrottled();
    }


    private void CropGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || _isApplyingHistory) return;
        var selected = GetSelectedGridCrops().ToList();
        if (selected.Count == 1)
        {
            var crop = selected[0];
            var pipName = GetUserVisiblePipName(crop.DetachedGroupIdRuntime);
            StatusText.Text = $"선택 크롭: {crop.Name} / 소속: {pipName}";
            SelectPipInCombo(string.IsNullOrWhiteSpace(crop.DetachedGroupIdRuntime) ? MainPipKey : crop.DetachedGroupIdRuntime!);
        }
        else if (selected.Count > 1)
        {
            var groups = selected.Select(c => GetUserVisiblePipName(c.DetachedGroupIdRuntime)).Distinct().ToList();
            StatusText.Text = $"선택 크롭 {selected.Count}개 / 소속 PIP: {string.Join(", ", groups)}";
        }
        else
        {
            StatusText.Text = "크롭 선택 없음";
        }

        UpdateCropBorderEditorUi();
    }

    private string GetUserVisiblePipName(string? groupId)
    {
        if (string.IsNullOrWhiteSpace(groupId)) return "메인 PIP";
        if (PipSelectCombo?.ItemsSource is IEnumerable<PipOption> options)
        {
            var option = options.FirstOrDefault(o => o.Key == groupId);
            if (option != null) return option.Name;
        }
        return $"분리 PIP {groupId[..Math.Min(4, groupId.Length)]}";
    }

    private IEnumerable<CropItem> GetSelectedGridCrops()
    {
        return CropGrid.SelectedItems.OfType<CropItem>();
    }

    private void InitializeCropBorderPaletteUi()
    {
        if (CropBorderColorCombo == null) return;
        CropBorderColorCombo.ItemsSource = CropBorderPalette;
        CropBorderColorCombo.SelectedIndex = 0;
        CropBorderOpacitySlider.Value = 0;
        CropBorderOpacityValueText.Text = "0%";
    }

    private CropBorderPaletteOption? FindCropBorderPaletteByHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return CropBorderPalette.FirstOrDefault();
        return CropBorderPalette.FirstOrDefault(o => string.Equals(o.Hex, hex, StringComparison.OrdinalIgnoreCase))
               ?? CropBorderPalette.FirstOrDefault();
    }

    private void UpdateCropBorderEditorUi()
    {
        if (CropBorderSelectionText == null) return;
        var selected = GetSelectedGridCrops().ToList();
        _isUpdatingCropBorderUi = true;
        try
        {
            var hasSelection = selected.Count > 0;
            CropBorderSelectionText.Text = selected.Count switch
            {
                0 => "선택 없음",
                1 => selected[0].Name,
                _ => $"선택 {selected.Count}개"
            };

            CopyCropBorderStyleButton.IsEnabled = hasSelection;
            PasteCropBorderStyleButton.IsEnabled = hasSelection && _copiedCropBorderStyle != null;
            CropBorderColorCombo.IsEnabled = hasSelection;
            CropBorderOpacitySlider.IsEnabled = hasSelection;

            if (!hasSelection)
            {
                CropBorderColorCombo.SelectedIndex = 0;
                CropBorderOpacitySlider.Value = 0;
                CropBorderOpacityValueText.Text = "0%";
                return;
            }

            var first = selected[0];
            var color = FindCropBorderPaletteByHex(first.BorderColorHex);
            CropBorderColorCombo.SelectedItem = color;
            CropBorderOpacitySlider.Value = Math.Clamp(first.BorderOpacity, 0, 1);
            CropBorderOpacityValueText.Text = $"{Math.Round(first.BorderOpacity * 100):0}%";
        }
        finally
        {
            _isUpdatingCropBorderUi = false;
        }
    }

    private void ApplyCropBorderStyleToSelection(string? colorHex = null, double? opacity = null)
    {
        if (_isInitializing || _isApplyingHistory) return;
        var selected = GetSelectedGridCrops().ToList();
        if (selected.Count == 0) return;

        PushHistoryCheckpoint();
        foreach (var crop in selected)
        {
            if (colorHex != null) crop.BorderColorHex = colorHex;
            if (opacity.HasValue) crop.BorderOpacity = Math.Clamp(opacity.Value, 0, 1);
        }

        RefreshAllOverlays();
        SaveSettings();
        UpdateCropBorderEditorUi();
    }

    private void CropBorderColorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingCropBorderUi || _isInitializing) return;
        if (CropBorderColorCombo.SelectedItem is not CropBorderPaletteOption option) return;
        ApplyCropBorderStyleToSelection(option.Hex);
    }

    private void CropBorderOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        CropBorderOpacityValueText.Text = $"{Math.Round(CropBorderOpacitySlider.Value * 100):0}%";
        if (_isUpdatingCropBorderUi || _isInitializing) return;
        ApplyCropBorderStyleToSelection(opacity: CropBorderOpacitySlider.Value);
    }

    private void SetCropBorderOpacity(double opacity)
    {
        CropBorderOpacitySlider.Value = Math.Clamp(opacity, 0, 1);
    }

    private void CropBorderOpacityZero_Click(object sender, RoutedEventArgs e) => SetCropBorderOpacity(0);
    private void CropBorderOpacity25_Click(object sender, RoutedEventArgs e) => SetCropBorderOpacity(0.25);
    private void CropBorderOpacity50_Click(object sender, RoutedEventArgs e) => SetCropBorderOpacity(0.5);
    private void CropBorderOpacity75_Click(object sender, RoutedEventArgs e) => SetCropBorderOpacity(0.75);
    private void CropBorderOpacity100_Click(object sender, RoutedEventArgs e) => SetCropBorderOpacity(1);

    private void CopyCropBorderStyle_Click(object sender, RoutedEventArgs e)
    {
        var source = GetSelectedGridCrops().FirstOrDefault();
        if (source == null) return;
        _copiedCropBorderStyle = CropBorderStyleTemplate.FromCrop(source);
        PasteCropBorderStyleButton.IsEnabled = true;
        StatusText.Text = $"크롭 테두리 스타일 복사: {source.Name} / {source.BorderColorHex} / {Math.Round(source.BorderOpacity * 100):0}%";
    }

    private void PasteCropBorderStyle_Click(object sender, RoutedEventArgs e)
    {
        if (_copiedCropBorderStyle == null) return;
        var selected = GetSelectedGridCrops().ToList();
        if (selected.Count == 0) return;
        ApplyCropBorderStyleToSelection(_copiedCropBorderStyle.BorderColorHex, _copiedCropBorderStyle.BorderOpacity);
        StatusText.Text = $"크롭 테두리 스타일 붙여넣기: 선택 {selected.Count}개 / {_copiedCropBorderStyle.BorderColorHex} / {Math.Round(_copiedCropBorderStyle.BorderOpacity * 100):0}%";
    }

    private CropItem? GetCropFromGridMouseEvent(MouseButtonEventArgs e)
    {
        DependencyObject? element = e.OriginalSource as DependencyObject;
        while (element != null && element is not DataGridRow)
        {
            element = VisualTreeHelper.GetParent(element);
        }
        return (element as DataGridRow)?.Item as CropItem;
    }

    private static int ClampFilterKeyDelay(int value, bool allowZero)
    {
        var min = allowZero ? 0 : 1;
        return Math.Clamp(value, min, 5000);
    }

    private static int ReadIntBox(TextBox box, int fallback, bool allowZero)
    {
        if (!int.TryParse(box.Text.Trim(), out var value)) value = fallback;
        return ClampFilterKeyDelay(value, allowZero);
    }

    private void UpdateFilterKeysUiFromSettings()
    {
        if (FilterKeysEnabledCheck == null) return;
        _isUpdatingFilterKeysUi = true;
        try
        {
            FilterKeysEnabledCheck.IsChecked = _settings.FilterKeysEnabled;
            FilterKeysMapleOnlyCheck.IsChecked = _settings.FilterKeysMapleOnly;
            FilterKeysTurnOffOnExitCheck.IsChecked = _settings.FilterKeysTurnOffOnExit;
            FilterKeysPillVisibleCheck.IsChecked = _settings.FilterKeysPillVisible;
            FilterKeysPillDragLockedCheck.IsChecked = _settings.FilterKeysPillDragLocked;
            FilterAcceptDelayBox.Text = ClampFilterKeyDelay(_settings.FilterKeysAcceptDelayMs, allowZero: true).ToString();
            FilterRepeatDelayBox.Text = ClampFilterKeyDelay(_settings.FilterKeysRepeatDelayMs, allowZero: true).ToString();
            FilterRepeatRateBox.Text = ClampFilterKeyDelay(_settings.FilterKeysRepeatRateMs, allowZero: false).ToString();
        }
        finally
        {
            _isUpdatingFilterKeysUi = false;
        }
        UpdateFilterKeysStatusText();
    }

    private void SaveFilterKeysUiToSettings()
    {
        _settings.FilterKeysEnabled = FilterKeysEnabledCheck.IsChecked == true;
        _settings.FilterKeysMapleOnly = FilterKeysMapleOnlyCheck.IsChecked == true;
        _settings.FilterKeysTurnOffOnExit = FilterKeysTurnOffOnExitCheck.IsChecked == true;
        _settings.FilterKeysPillVisible = FilterKeysPillVisibleCheck.IsChecked == true;
        _settings.FilterKeysPillDragLocked = FilterKeysPillDragLockedCheck.IsChecked == true;
        _settings.FilterKeysAcceptDelayMs = ReadIntBox(FilterAcceptDelayBox, _settings.FilterKeysAcceptDelayMs, allowZero: true);
        _settings.FilterKeysRepeatDelayMs = ReadIntBox(FilterRepeatDelayBox, _settings.FilterKeysRepeatDelayMs, allowZero: true);
        _settings.FilterKeysRepeatRateMs = ReadIntBox(FilterRepeatRateBox, _settings.FilterKeysRepeatRateMs, allowZero: false);
        FilterAcceptDelayBox.Text = _settings.FilterKeysAcceptDelayMs.ToString();
        FilterRepeatDelayBox.Text = _settings.FilterKeysRepeatDelayMs.ToString();
        FilterRepeatRateBox.Text = _settings.FilterKeysRepeatRateMs.ToString();
    }

    private void FilterKeysOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _isApplyingHistory || _isUpdatingFilterKeysUi) return;
        SaveFilterKeysUiToSettings();
        UpdateFilterKeysPillVisibility();
        if (_filterKeysPillWindow != null) _filterKeysPillWindow.DragLocked = _settings.FilterKeysPillDragLocked;
        UpdateFilterKeysRuntime(applyWhenNeeded: true);
        SaveSettings();
    }

    private void FilterKeysTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _isApplyingHistory || _isUpdatingFilterKeysUi) return;
        SaveFilterKeysUiToSettings();
        // 값이 바뀐 경우에만 Windows API를 다시 호출한다.
        UpdateFilterKeysRuntime(applyWhenNeeded: true);
        SaveSettings();
    }

    private void FilterKeysTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        SaveFilterKeysUiToSettings();
        // 값이 바뀐 경우에만 Windows API를 다시 호출한다.
        UpdateFilterKeysRuntime(applyWhenNeeded: true);
        SaveSettings();
        e.Handled = true;
    }

    private void ApplyFilterKeysNow_Click(object sender, RoutedEventArgs e)
    {
        SaveFilterKeysUiToSettings();
        _settings.FilterKeysEnabled = true;
        _isUpdatingFilterKeysUi = true;
        try
        {
            FilterKeysEnabledCheck.IsChecked = true;
        }
        finally
        {
            _isUpdatingFilterKeysUi = false;
        }
        ApplyFilterKeysFromSettings("수동 적용", force: true);
        SaveSettings();
    }

    private void TurnOffFilterKeys_Click(object sender, RoutedEventArgs e)
    {
        SaveFilterKeysUiToSettings();
        _settings.FilterKeysEnabled = false;
        _isUpdatingFilterKeysUi = true;
        try
        {
            FilterKeysEnabledCheck.IsChecked = false;
        }
        finally
        {
            _isUpdatingFilterKeysUi = false;
        }
        TryTurnOffFilterKeys("수동 끄기", force: true);
        SaveSettings();
    }

    private string GetFilterKeysSignature()
    {
        return $"{_settings.FilterKeysAcceptDelayMs}:{_settings.FilterKeysRepeatDelayMs}:{_settings.FilterKeysRepeatRateMs}";
    }

    private void UpdateFilterKeysRuntime(bool applyWhenNeeded)
    {
        if (FilterKeysStatusText == null) return;

        if (!_settings.FilterKeysEnabled)
        {
            if (_filterKeysAppliedByThisApp) TryTurnOffFilterKeys("프리셋 비활성");
            else SetFilterKeysVisualState("OFF", "필터키 OFF · 프리셋별 저장", FilterKeysVisualKind.Off);
            return;
        }

        if (_settings.FilterKeysMapleOnly && !IsMapleForeground())
        {
            if (_filterKeysAppliedByThisApp) TryTurnOffFilterKeys("메이플 비활성");
            else SetFilterKeysVisualState("대기", "필터키 대기 · 메이플 포커스 아님", FilterKeysVisualKind.Waiting);
            return;
        }

        var signature = GetFilterKeysSignature();
        if (applyWhenNeeded && (!_filterKeysAppliedByThisApp || _lastAppliedFilterKeysSignature != signature))
        {
            ApplyFilterKeysFromSettings("프리셋/상태 변경");
            return;
        }

        SetFilterKeysVisualState("ON", $"필터키 ON · 입력 {_settings.FilterKeysAcceptDelayMs}ms / 대기 {_settings.FilterKeysRepeatDelayMs}ms / 간격 {_settings.FilterKeysRepeatRateMs}ms", FilterKeysVisualKind.On);
    }

    private void ApplyFilterKeysFromSettings(string reason, bool force = false)
    {
        var signature = GetFilterKeysSignature();
        if (!force && _filterKeysAppliedByThisApp && _lastAppliedFilterKeysSignature == signature)
        {
            SetFilterKeysVisualState("ON", $"필터키 ON · 입력 {_settings.FilterKeysAcceptDelayMs}ms / 대기 {_settings.FilterKeysRepeatDelayMs}ms / 간격 {_settings.FilterKeysRepeatRateMs}ms", FilterKeysVisualKind.On);
            return;
        }

        try
        {
            FilterKeysService.Apply(_settings.FilterKeysAcceptDelayMs, _settings.FilterKeysRepeatDelayMs, _settings.FilterKeysRepeatRateMs);
            _filterKeysAppliedByThisApp = true;
            _lastAppliedFilterKeysSignature = signature;
            SetFilterKeysVisualState("ON", $"필터키 ON · 입력 {_settings.FilterKeysAcceptDelayMs}ms / 대기 {_settings.FilterKeysRepeatDelayMs}ms / 간격 {_settings.FilterKeysRepeatRateMs}ms", FilterKeysVisualKind.On);
            SettingsService.Log($"filterkeys_apply | {reason} | accept={_settings.FilterKeysAcceptDelayMs} delay={_settings.FilterKeysRepeatDelayMs} rate={_settings.FilterKeysRepeatRateMs} mapleOnly={_settings.FilterKeysMapleOnly}");
        }
        catch (Exception ex)
        {
            SetFilterKeysVisualState("오류", "필터키 적용 실패", FilterKeysVisualKind.Error);
            SettingsService.Log("filterkeys_apply_exception | " + ex);
        }
    }

    private void TryTurnOffFilterKeys(string reason, bool force = false)
    {
        if (!_filterKeysAppliedByThisApp && !force)
        {
            _lastAppliedFilterKeysSignature = string.Empty;
            SetFilterKeysVisualState("OFF", "필터키 OFF", FilterKeysVisualKind.Off);
            return;
        }

        try
        {
            FilterKeysService.TurnOff();
            _filterKeysAppliedByThisApp = false;
            _lastAppliedFilterKeysSignature = string.Empty;
            SetFilterKeysVisualState("OFF", "필터키 OFF", FilterKeysVisualKind.Off);
            SettingsService.Log($"filterkeys_off | {reason}");
        }
        catch (Exception ex)
        {
            SetFilterKeysVisualState("오류", "필터키 끄기 실패", FilterKeysVisualKind.Error);
            SettingsService.Log("filterkeys_off_exception | " + ex);
        }
    }

    private void UpdateFilterKeysStatusText()
    {
        if (FilterKeysStatusText == null) return;
        if (!_settings.FilterKeysEnabled)
        {
            SetFilterKeysVisualState("OFF", "필터키 OFF · 프리셋별 저장", FilterKeysVisualKind.Off);
            return;
        }
        SetFilterKeysVisualState("대기", $"필터키 대기 · 입력 {_settings.FilterKeysAcceptDelayMs} / 반복 대기 {_settings.FilterKeysRepeatDelayMs} / 반복 간격 {_settings.FilterKeysRepeatRateMs}ms", FilterKeysVisualKind.Waiting);
    }

    private enum FilterKeysVisualKind
    {
        Off,
        Waiting,
        On,
        Error
    }

    private void SetFilterKeysVisualState(string badgeText, string detailText, FilterKeysVisualKind kind)
    {
        if (FilterKeysStatusText != null) FilterKeysStatusText.Text = detailText;
        // 상태는 배지/알약에만 표시한다. 체크박스 Content를 바꾸면 폭이 달라져 UI가 줄바뀜될 수 있다.
        if (FilterKeysStateBadgeText != null) FilterKeysStateBadgeText.Text = badgeText;

        var (background, border, dot) = GetFilterKeysVisualColors(kind);
        if (FilterKeysStateBadge != null)
        {
            FilterKeysStateBadge.Background = new SolidColorBrush(background);
            FilterKeysStateBadge.BorderBrush = new SolidColorBrush(border);
        }

        _filterKeysPillWindow?.SetState(badgeText, detailText, background, border, dot);
    }

    private static (Color Background, Color Border, Color Dot) GetFilterKeysVisualColors(FilterKeysVisualKind kind) => kind switch
    {
        FilterKeysVisualKind.On => (Color.FromRgb(32, 132, 72), Color.FromRgb(20, 92, 50), Color.FromRgb(128, 255, 178)),
        FilterKeysVisualKind.Waiting => (Color.FromRgb(170, 118, 28), Color.FromRgb(122, 82, 18), Color.FromRgb(255, 210, 95)),
        FilterKeysVisualKind.Error => (Color.FromRgb(166, 45, 45), Color.FromRgb(120, 26, 26), Color.FromRgb(255, 120, 120)),
        _ => (Color.FromRgb(117, 49, 49), Color.FromRgb(82, 32, 32), Color.FromRgb(255, 120, 120))
    };

    private void UpdateFilterKeysPillVisibility()
    {
        if (_settings.FilterKeysPillVisible)
        {
            EnsureFilterKeysPillWindow();
            UpdateFilterKeysStatusText();
        }
        else
        {
            CloseFilterKeysPillWindow();
        }
    }

    private void EnsureFilterKeysPillWindow()
    {
        var left = double.IsFinite(_settings.FilterKeysPillLeft) ? _settings.FilterKeysPillLeft : 980;
        var top = double.IsFinite(_settings.FilterKeysPillTop) ? _settings.FilterKeysPillTop : 160;

        if (_filterKeysPillWindow == null)
        {
            _filterKeysPillWindow = new FilterKeysPillWindow
            {
                Left = left,
                Top = top,
                Topmost = true
            };
            _filterKeysPillWindow.ToggleRequested += FilterKeysPill_ToggleRequested;
            _filterKeysPillWindow.PillMoved += FilterKeysPill_Moved;
        }
        else
        {
            _filterKeysPillWindow.Left = left;
            _filterKeysPillWindow.Top = top;
        }

        if (!_filterKeysPillWindow.IsVisible) _filterKeysPillWindow.Show();
        _filterKeysPillWindow.Topmost = true;
        _filterKeysPillWindow.DragLocked = _settings.FilterKeysPillDragLocked;
        ReapplyFilterKeysPillTopMost(force: true);

        if (_settings.PipPositionLockedToTarget)
        {
            if (_settings.FilterKeysPillTargetOffsetX is null || _settings.FilterKeysPillTargetOffsetY is null)
            {
                CaptureFilterKeysPillRelativePosition(save: false);
            }
            else
            {
                ApplyFilterKeysPillTargetLockedPosition();
            }
        }
    }

    private void TickFilterKeysPillRuntime()
    {
        if (_filterKeysPillWindow == null || !_filterKeysPillWindow.IsVisible) return;

        if (_settings.PipPositionLockedToTarget)
        {
            ApplyFilterKeysPillTargetLockedPosition();
        }

        ReapplyFilterKeysPillTopMost(force: false);
    }

    private void ReapplyFilterKeysPillTopMost(bool force)
    {
        if (_filterKeysPillWindow == null || !_filterKeysPillWindow.IsVisible) return;
        if (!force && (DateTime.UtcNow - _lastFilterKeysPillTopmostUtc).TotalMilliseconds < 120) return;

        _lastFilterKeysPillTopmostUtc = DateTime.UtcNow;
        _filterKeysPillWindow.ForceTopMost();
    }

    private void CloseFilterKeysPillWindow()
    {
        if (_filterKeysPillWindow == null) return;
        _filterKeysPillWindow.ToggleRequested -= FilterKeysPill_ToggleRequested;
        _filterKeysPillWindow.PillMoved -= FilterKeysPill_Moved;
        _filterKeysPillWindow.ForceClose();
        _filterKeysPillWindow = null;
    }

    private void FilterKeysPill_Moved(FilterKeysPillWindow pill)
    {
        if (_isInitializing || _isApplyingHistory) return;
        _settings.FilterKeysPillLeft = Math.Round(pill.Left, 2);
        _settings.FilterKeysPillTop = Math.Round(pill.Top, 2);
        if (_settings.PipPositionLockedToTarget)
        {
            CaptureFilterKeysPillRelativePosition(save: false);
        }
        SaveSettingsThrottled();
    }

    private bool TryGetTargetClientRectForMain(out RectI rect)
    {
        rect = default;
        if (_target == null || _target.Hwnd == IntPtr.Zero) return false;
        if (WindowService.IsMinimized(_target.Hwnd)) return false;
        return WindowService.TryGetClientScreenRect(_target.Hwnd, out rect) && rect.Width > 0 && rect.Height > 0;
    }

    private void CaptureFilterKeysPillRelativePosition(bool save)
    {
        if (_filterKeysPillWindow == null) return;
        if (!TryGetTargetClientRectForMain(out var rect)) return;

        _filterKeysPillSessionBaselineWidth = rect.Width;
        _filterKeysPillSessionBaselineHeight = rect.Height;
        _settings.FilterKeysPillTargetBaselineWidth = rect.Width;
        _settings.FilterKeysPillTargetBaselineHeight = rect.Height;
        _settings.FilterKeysPillTargetOffsetX = Math.Round(_filterKeysPillWindow.Left - rect.X, 2);
        _settings.FilterKeysPillTargetOffsetY = Math.Round(_filterKeysPillWindow.Top - rect.Y, 2);

        if (save) SaveSettings();
    }

    private bool IsTargetRectUsableForFilterKeysPillLock(RectI rect)
    {
        // 필터키 알약은 사용자가 직접 눌러야 하는 조작 버튼이므로,
        // 기존 PIP와 같은 위치 고정 주기를 따르되 Ctrl+Enter 등으로 화면 크기가 바뀌어도
        // 창 밖으로 사라지지 않도록 현재 클라이언트 영역 안으로 보정한다.
        if (_filterKeysPillSessionBaselineWidth <= 0 || _filterKeysPillSessionBaselineHeight <= 0)
        {
            _filterKeysPillSessionBaselineWidth = rect.Width;
            _filterKeysPillSessionBaselineHeight = rect.Height;
        }

        if (rect.Width > _filterKeysPillSessionBaselineWidth) _filterKeysPillSessionBaselineWidth = rect.Width;
        if (rect.Height > _filterKeysPillSessionBaselineHeight) _filterKeysPillSessionBaselineHeight = rect.Height;
        _settings.FilterKeysPillTargetBaselineWidth = _filterKeysPillSessionBaselineWidth;
        _settings.FilterKeysPillTargetBaselineHeight = _filterKeysPillSessionBaselineHeight;
        return rect.Width > 0 && rect.Height > 0;
    }

    private static double ClampToRange(double value, double min, double max)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return min;
        if (max < min) return min;
        return Math.Min(Math.Max(value, min), max);
    }

    private void ApplyFilterKeysPillTargetLockedPosition()
    {
        if (!_settings.PipPositionLockedToTarget) return;
        if (_filterKeysPillWindow == null || !_filterKeysPillWindow.IsVisible) return;

        if (_settings.FilterKeysPillTargetOffsetX is null || _settings.FilterKeysPillTargetOffsetY is null)
        {
            CaptureFilterKeysPillRelativePosition(save: false);
            return;
        }

        if (!TryGetTargetClientRectForMain(out var rect)) return;
        if (!IsTargetRectUsableForFilterKeysPillLock(rect)) return;

        var pillWidth = Math.Max(1, _filterKeysPillWindow.ActualWidth > 1 ? _filterKeysPillWindow.ActualWidth : _filterKeysPillWindow.Width);
        var pillHeight = Math.Max(1, _filterKeysPillWindow.ActualHeight > 1 ? _filterKeysPillWindow.ActualHeight : _filterKeysPillWindow.Height);
        var desiredLeft = Math.Round(rect.X + _settings.FilterKeysPillTargetOffsetX.Value, 2);
        var desiredTop = Math.Round(rect.Y + _settings.FilterKeysPillTargetOffsetY.Value, 2);

        // Ctrl+Enter나 해상도 변경으로 저장된 상대 좌표가 현재 클라이언트 밖으로 나가도
        // 필터키 알약은 반드시 보이도록 현재 메이플 클라이언트 내부에 clamp한다.
        desiredLeft = Math.Round(ClampToRange(desiredLeft, rect.X, rect.X + Math.Max(0, rect.Width - pillWidth)), 2);
        desiredTop = Math.Round(ClampToRange(desiredTop, rect.Y, rect.Y + Math.Max(0, rect.Height - pillHeight)), 2);

        if (Math.Abs(_filterKeysPillWindow.Left - desiredLeft) < 0.5 && Math.Abs(_filterKeysPillWindow.Top - desiredTop) < 0.5)
        {
            ReapplyFilterKeysPillTopMost(force: false);
            return;
        }

        _filterKeysPillWindow.Left = desiredLeft;
        _filterKeysPillWindow.Top = desiredTop;
        _settings.FilterKeysPillLeft = desiredLeft;
        _settings.FilterKeysPillTop = desiredTop;
        ReapplyFilterKeysPillTopMost(force: true);
    }

    private void FilterKeysPill_ToggleRequested()
    {
        SaveFilterKeysUiToSettings();
        _settings.FilterKeysEnabled = !_settings.FilterKeysEnabled;
        _isUpdatingFilterKeysUi = true;
        try
        {
            FilterKeysEnabledCheck.IsChecked = _settings.FilterKeysEnabled;
            FilterKeysPillVisibleCheck.IsChecked = _settings.FilterKeysPillVisible;
            FilterKeysPillDragLockedCheck.IsChecked = _settings.FilterKeysPillDragLocked;
        }
        finally
        {
            _isUpdatingFilterKeysUi = false;
        }

        if (_settings.FilterKeysEnabled)
        {
            UpdateFilterKeysRuntime(applyWhenNeeded: true);
            StatusText.Text = "필터키 PIP 버튼: 사용 ON";
        }
        else
        {
            TryTurnOffFilterKeys("PIP 버튼 토글", force: true);
            StatusText.Text = "필터키 PIP 버튼: 사용 OFF";
        }
        ReapplyFilterKeysPillTopMost(force: true);
        SaveSettings();
    }

    private bool IsMapleForeground()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;

        NativeMethods.GetWindowThreadProcessId(foreground, out var foregroundPid);
        if (_target != null && _target.ProcessId > 0 && foregroundPid == _target.ProcessId) return true;

        var length = NativeMethods.GetWindowTextLength(foreground);
        if (length <= 0) return false;
        var builder = new System.Text.StringBuilder(length + 1);
        _ = NativeMethods.GetWindowText(foreground, builder, builder.Capacity);
        var title = builder.ToString();
        return title.Contains("MapleStory", StringComparison.OrdinalIgnoreCase)
            || title.Contains("메이플", StringComparison.OrdinalIgnoreCase);
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = SettingsService.AppDir;
            System.IO.Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
        }
        catch { }
    }

    private static double ClampBurstSeconds(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return 0;
        return Math.Round(Math.Clamp(value, 0, 120), 2);
    }

    private static double ClampBurstPercent(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return 0;
        return Math.Round(Math.Clamp(value, 0, 95), 2);
    }

    private static double ClampBurstWarningSeconds(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return 5;
        return Math.Round(Math.Clamp(value, 0, 600), 2);
    }

    private static double ClampBurstTimingCorrectionSeconds(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return 0;
        return Math.Round(Math.Clamp(value, 0, 30), 2);
    }

    private double GetBurstTimingCorrectionSeconds(BurstRole role) => role switch
    {
        BurstRole.SemiBurst => ClampBurstTimingCorrectionSeconds(_settings.SemiBurstTimingCorrectionSeconds),
        BurstRole.Burst => ClampBurstTimingCorrectionSeconds(_settings.BurstTimingCorrectionSeconds),
        BurstRole.OriginBurst => ClampBurstTimingCorrectionSeconds(_settings.OriginBurstTimingCorrectionSeconds),
        _ => 0
    };

    private double GetBurstRemainingSeconds(BurstRole role, DateTime usedUtc, DateTime nowUtc)
    {
        var cooldown = ComputeEffectiveCooldownSeconds(role, _settings.BurstCooldownReductionSeconds, _settings.BurstCooldownReductionPercent);
        var elapsed = Math.Max(0, (nowUtc - usedUtc).TotalSeconds);
        return Math.Max(0, cooldown - elapsed - GetBurstTimingCorrectionSeconds(role));
    }

    private static double ClampBurstMonitorOpacity(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return 0.88;
        return Math.Round(Math.Clamp(value, 0.10, 1.0), 2);
    }

    private static double ClampBurstMonitorBackgroundOpacity(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return 0.18;
        return Math.Round(Math.Clamp(value, 0.0, 1.0), 2);
    }

    private static double ComputeEffectiveCooldownSeconds(BurstRole role, double reductionSeconds, double reductionPercent)
    {
        var baseSeconds = role.BaseCooldownSeconds();
        if (baseSeconds <= 0) return 0;
        var afterPercent = baseSeconds * (1.0 - ClampBurstPercent(reductionPercent) / 100.0);
        var afterFlat = afterPercent - ClampBurstSeconds(reductionSeconds);
        return Math.Max(1, Math.Round(afterFlat, 2));
    }

    private void UpdateBurstUiFromSettings()
    {
        if (BurstMonitorVisibleCheck == null) return;
        _isUpdatingBurstUi = true;
        try
        {
            BurstMonitorVisibleCheck.IsChecked = _settings.BurstMonitorVisible;
            BurstCooldownSecondsBox.Text = ClampBurstSeconds(_settings.BurstCooldownReductionSeconds).ToString("0.##");
            BurstCooldownPercentBox.Text = ClampBurstPercent(_settings.BurstCooldownReductionPercent).ToString("0.##");
            SemiBurstTimingCorrectionBox.Text = ClampBurstTimingCorrectionSeconds(_settings.SemiBurstTimingCorrectionSeconds).ToString("0.##");
            BurstTimingCorrectionBox.Text = ClampBurstTimingCorrectionSeconds(_settings.BurstTimingCorrectionSeconds).ToString("0.##");
            OriginBurstTimingCorrectionBox.Text = ClampBurstTimingCorrectionSeconds(_settings.OriginBurstTimingCorrectionSeconds).ToString("0.##");
            BurstWarningThresholdBox.Text = ClampBurstWarningSeconds(_settings.BurstWarningThresholdSeconds).ToString("0.##");
            BurstMonitorOpacitySlider.Value = ClampBurstMonitorOpacity(_settings.BurstMonitorOpacity);
            BurstMonitorOpacityText.Text = $"{Math.Round(ClampBurstMonitorOpacity(_settings.BurstMonitorOpacity) * 100):0}%";
            BurstMonitorBackgroundOpacitySlider.Value = ClampBurstMonitorBackgroundOpacity(_settings.BurstMonitorBackgroundOpacity);
            BurstMonitorBackgroundOpacityText.Text = $"{Math.Round(ClampBurstMonitorBackgroundOpacity(_settings.BurstMonitorBackgroundOpacity) * 100):0}%";
            UpdateBurstTriggerKeyUi();
            UpdateBurstRoleComboItems();
        }
        finally
        {
            _isUpdatingBurstUi = false;
        }
        UpdateBurstCalibrationStatusUi();
        UpdateBurstMonitorStatusText();
    }

    private int GetBurstTriggerVirtualKey(BurstRole role) => role switch
    {
        BurstRole.SemiBurst => _settings.SemiBurstTriggerVirtualKey,
        BurstRole.Burst => _settings.BurstTriggerVirtualKey,
        BurstRole.OriginBurst => _settings.OriginBurstTriggerVirtualKey,
        _ => 0
    };

    private string GetBurstTriggerKeyName(BurstRole role) => role switch
    {
        BurstRole.SemiBurst => _settings.SemiBurstTriggerKeyName ?? string.Empty,
        BurstRole.Burst => _settings.BurstTriggerKeyName ?? string.Empty,
        BurstRole.OriginBurst => _settings.OriginBurstTriggerKeyName ?? string.Empty,
        _ => string.Empty
    };

    private void SetBurstTriggerKey(BurstRole role, int virtualKey, string keyName)
    {
        virtualKey = Math.Clamp(virtualKey, 0, 255);
        keyName = virtualKey == 0 ? string.Empty : keyName;

        if (virtualKey > 0)
        {
            foreach (var other in new[] { BurstRole.SemiBurst, BurstRole.Burst, BurstRole.OriginBurst })
            {
                if (other == role || GetBurstTriggerVirtualKey(other) != virtualKey) continue;
                switch (other)
                {
                    case BurstRole.SemiBurst:
                        _settings.SemiBurstTriggerVirtualKey = 0;
                        _settings.SemiBurstTriggerKeyName = string.Empty;
                        break;
                    case BurstRole.Burst:
                        _settings.BurstTriggerVirtualKey = 0;
                        _settings.BurstTriggerKeyName = string.Empty;
                        break;
                    case BurstRole.OriginBurst:
                        _settings.OriginBurstTriggerVirtualKey = 0;
                        _settings.OriginBurstTriggerKeyName = string.Empty;
                        break;
                }
                GetBurstRoleState(other).TriggerKeyWasDown = false;
            }
        }

        switch (role)
        {
            case BurstRole.SemiBurst:
                _settings.SemiBurstTriggerVirtualKey = virtualKey;
                _settings.SemiBurstTriggerKeyName = keyName;
                break;
            case BurstRole.Burst:
                _settings.BurstTriggerVirtualKey = virtualKey;
                _settings.BurstTriggerKeyName = keyName;
                break;
            case BurstRole.OriginBurst:
                _settings.OriginBurstTriggerVirtualKey = virtualKey;
                _settings.OriginBurstTriggerKeyName = keyName;
                break;
        }
        GetBurstRoleState(role).TriggerKeyWasDown = false;
        UpdateBurstTriggerKeyUi();
        SaveSettings();
    }

    private void UpdateBurstTriggerKeyUi()
    {
        if (SemiBurstTriggerKeyText == null) return;
        SemiBurstTriggerKeyText.Text = string.IsNullOrWhiteSpace(_settings.SemiBurstTriggerKeyName) ? "미지정" : _settings.SemiBurstTriggerKeyName;
        BurstTriggerKeyText.Text = string.IsNullOrWhiteSpace(_settings.BurstTriggerKeyName) ? "미지정" : _settings.BurstTriggerKeyName;
        OriginBurstTriggerKeyText.Text = string.IsNullOrWhiteSpace(_settings.OriginBurstTriggerKeyName) ? "미지정" : _settings.OriginBurstTriggerKeyName;
    }

    private static bool IsModifierOnlyKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    private void BurstTriggerKeyRegister_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string tag || !Enum.TryParse<BurstRole>(tag, out var role) || role == BurstRole.None) return;
        _awaitingBurstTriggerKeyRole = role;
        BurstTriggerKeyCaptureHintText.Text = $"{role.ToKoreanName()} 키 등록 대기 · 지금 이 창에서 실제 스킬키를 한 번 누르세요. ESC=취소";
        Activate();
        Focus();
        Keyboard.Focus(this);
    }

    private void BurstTriggerKeyClear_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string tag || !Enum.TryParse<BurstRole>(tag, out var role) || role == BurstRole.None) return;
        SetBurstTriggerKey(role, 0, string.Empty);
        BurstTriggerKeyCaptureHintText.Text = $"{role.ToKoreanName()} 키 등록을 해제했습니다.";
    }

    private void UpdateBurstRoleComboItems()
    {
        if (SemiBurstCropCombo == null || BurstCropCombo == null || OriginBurstCropCombo == null) return;
        var previous = _isUpdatingBurstUi;
        _isUpdatingBurstUi = true;
        try
        {
            var options = new List<CropRoleOption> { new(null, "지정 안 함") };
            options.AddRange(_crops.Select(c => new CropRoleOption(c.Id, $"{c.Name} · {c.PipLabel}")));
            SetBurstComboItems(SemiBurstCropCombo, options, BurstRole.SemiBurst);
            SetBurstComboItems(BurstCropCombo, options, BurstRole.Burst);
            SetBurstComboItems(OriginBurstCropCombo, options, BurstRole.OriginBurst);
        }
        finally
        {
            _isUpdatingBurstUi = previous;
        }
    }

    private void SetBurstComboItems(ComboBox combo, List<CropRoleOption> options, BurstRole role)
    {
        var selectedCropId = _crops.FirstOrDefault(c => c.BurstRole == role)?.Id;
        combo.ItemsSource = options;
        combo.SelectedItem = options.FirstOrDefault(o => o.CropId == selectedCropId) ?? options[0];
    }

    private void BurstRoleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || _isApplyingHistory || _isUpdatingBurstUi) return;
        var role = ReferenceEquals(sender, SemiBurstCropCombo)
            ? BurstRole.SemiBurst
            : ReferenceEquals(sender, BurstCropCombo)
                ? BurstRole.Burst
                : BurstRole.OriginBurst;
        if (sender is not ComboBox combo) return;
        var selectedId = (combo.SelectedItem as CropRoleOption)?.CropId;
        AssignBurstRole(role, selectedId);
        ResetBurstRuntimeForRole(role);
        UpdateBurstRoleComboItems();
        UpdateBurstCalibrationStatusUi();
        SaveSettings();
        StatusText.Text = string.IsNullOrWhiteSpace(selectedId)
            ? $"{role.ToKoreanName()} 감지 아이콘 해제"
            : $"{role.ToKoreanName()} 감지 아이콘 지정: {_crops.FirstOrDefault(c => c.Id == selectedId)?.Name}";
    }

    private void AssignBurstRole(BurstRole role, string? cropId)
    {
        foreach (var crop in _crops.Where(c => c.BurstRole == role).ToList())
        {
            crop.BurstRole = BurstRole.None;
        }
        if (string.IsNullOrWhiteSpace(cropId)) return;
        var target = _crops.FirstOrDefault(c => c.Id == cropId);
        if (target == null) return;
        foreach (var crop in _crops.Where(c => c.Id != cropId && c.BurstRole == target.BurstRole).ToList())
        {
            crop.BurstRole = BurstRole.None;
        }
        target.BurstRole = role;
    }

    private void BurstMonitorOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _isApplyingHistory || _isUpdatingBurstUi) return;
        SaveBurstUiToSettings();
        UpdateBurstMonitorVisibility();
        SaveSettings();
    }

    private void BurstMonitorTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _isApplyingHistory || _isUpdatingBurstUi) return;
        SaveBurstUiToSettings();
        SaveSettings();
    }

    private void BurstMonitorTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        SaveBurstUiToSettings();
        SaveSettings();
        e.Handled = true;
    }

    private void SaveBurstUiToSettings()
    {
        _settings.BurstMonitorVisible = BurstMonitorVisibleCheck.IsChecked == true;
        _settings.BurstCooldownReductionSeconds = ReadDoubleBox(BurstCooldownSecondsBox, _settings.BurstCooldownReductionSeconds, 0, 120);
        _settings.BurstCooldownReductionPercent = ReadDoubleBox(BurstCooldownPercentBox, _settings.BurstCooldownReductionPercent, 0, 95);
        _settings.SemiBurstTimingCorrectionSeconds = ReadDoubleBox(SemiBurstTimingCorrectionBox, _settings.SemiBurstTimingCorrectionSeconds, 0, 30);
        _settings.BurstTimingCorrectionSeconds = ReadDoubleBox(BurstTimingCorrectionBox, _settings.BurstTimingCorrectionSeconds, 0, 30);
        _settings.OriginBurstTimingCorrectionSeconds = ReadDoubleBox(OriginBurstTimingCorrectionBox, _settings.OriginBurstTimingCorrectionSeconds, 0, 30);
        _settings.BurstWarningThresholdSeconds = ReadDoubleBox(BurstWarningThresholdBox, _settings.BurstWarningThresholdSeconds, 0, 600);
        _settings.BurstMonitorOpacity = ClampBurstMonitorOpacity(BurstMonitorOpacitySlider.Value);
        _settings.BurstMonitorBackgroundOpacity = ClampBurstMonitorBackgroundOpacity(BurstMonitorBackgroundOpacitySlider.Value);
        BurstCooldownSecondsBox.Text = _settings.BurstCooldownReductionSeconds.ToString("0.##");
        BurstCooldownPercentBox.Text = _settings.BurstCooldownReductionPercent.ToString("0.##");
        SemiBurstTimingCorrectionBox.Text = _settings.SemiBurstTimingCorrectionSeconds.ToString("0.##");
        BurstTimingCorrectionBox.Text = _settings.BurstTimingCorrectionSeconds.ToString("0.##");
        OriginBurstTimingCorrectionBox.Text = _settings.OriginBurstTimingCorrectionSeconds.ToString("0.##");
        BurstWarningThresholdBox.Text = _settings.BurstWarningThresholdSeconds.ToString("0.##");
        BurstMonitorOpacityText.Text = $"{Math.Round(_settings.BurstMonitorOpacity * 100):0}%";
        BurstMonitorBackgroundOpacityText.Text = $"{Math.Round(_settings.BurstMonitorBackgroundOpacity * 100):0}%";
        _burstMonitorWindow?.SetMonitorOpacity(_settings.BurstMonitorOpacity);
        _burstMonitorWindow?.SetBackgroundOpacity(_settings.BurstMonitorBackgroundOpacity);
        UpdateBurstMonitorStatusText();
    }

    private static double ReadDoubleBox(TextBox box, double fallback, double min, double max)
    {
        if (!double.TryParse(box.Text.Trim(), out var value)) value = fallback;
        if (double.IsNaN(value) || double.IsInfinity(value)) value = fallback;
        return Math.Round(Math.Clamp(value, min, max), 2);
    }

    private void BurstMonitorOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BurstMonitorOpacityText != null)
        {
            BurstMonitorOpacityText.Text = $"{Math.Round(ClampBurstMonitorOpacity(e.NewValue) * 100):0}%";
        }
        if (_isInitializing || _isApplyingHistory || _isUpdatingBurstUi) return;
        _settings.BurstMonitorOpacity = ClampBurstMonitorOpacity(e.NewValue);
        _burstMonitorWindow?.SetMonitorOpacity(_settings.BurstMonitorOpacity);
        SaveSettingsThrottled();
    }

    private void BurstMonitorBackgroundOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BurstMonitorBackgroundOpacityText != null)
        {
            BurstMonitorBackgroundOpacityText.Text = $"{Math.Round(ClampBurstMonitorBackgroundOpacity(e.NewValue) * 100):0}%";
        }
        if (_isInitializing || _isApplyingHistory || _isUpdatingBurstUi) return;
        _settings.BurstMonitorBackgroundOpacity = ClampBurstMonitorBackgroundOpacity(e.NewValue);
        _burstMonitorWindow?.SetBackgroundOpacity(_settings.BurstMonitorBackgroundOpacity);
        SaveSettingsThrottled();
    }

    private void ResetBurstMonitor_Click(object sender, RoutedEventArgs e)
    {
        _burstLearningSession = null;
        ResetBurstRuntimeAll();
        UpdateBurstCalibrationStatusUi();
        UpdateBurstMonitorPipRows();
        UpdateBurstMonitorStatusText();
        StatusText.Text = "극딜 감지 상태 초기화 (학습 데이터 유지)";
    }

    private void ResetBurstRuntimeAll()
    {
        _burstStates.Clear();
    }

    private void ResetBurstRuntimeForRole(BurstRole role)
    {
        if (role == BurstRole.None) return;
        _burstStates.Remove(role);
    }

    private void UpdateBurstMonitorStatusText()
    {
        if (BurstMonitorStatusText == null) return;
        var lines = new List<string>();
        foreach (var role in new[] { BurstRole.SemiBurst, BurstRole.Burst, BurstRole.OriginBurst })
        {
            var crop = _crops.FirstOrDefault(c => c.Enabled && c.BurstRole == role);
            if (crop == null)
            {
                lines.Add($"{role.ToKoreanName()}: 미지정");
                continue;
            }

            var state = GetBurstRoleState(role);
            var learned = TryGetCompatibleBurstCalibration(crop, out _, out var reason);
            if (!learned)
            {
                lines.Add($"{role.ToKoreanName()}: {crop.Name} · {reason}");
                continue;
            }

            var detector = state.LastObservedClass switch
            {
                BurstObservedClass.Ready => "READY",
                BurstObservedClass.Cooldown => "COOLDOWN",
                _ => "?"
            };
            var armed = state.ArmedFromReady ? " · 감지 대기" : string.Empty;
            lines.Add($"{role.ToKoreanName()}: {crop.Name} · {detector} {state.LastClassifierConfidence * 100:0}%{armed}");
        }
        BurstMonitorStatusText.Text = string.Join(Environment.NewLine, lines);
    }

    private void UpdateBurstMonitorVisibility()
    {
        if (_settings.BurstMonitorVisible)
        {
            if (_allPipsTemporarilyHidden)
            {
                _burstMonitorWindow?.Hide();
                return;
            }
            EnsureBurstMonitorWindow();
            UpdateBurstMonitorPipRows();
        }
        else
        {
            CloseBurstMonitorWindow();
        }
    }

    private void EnsureBurstMonitorWindow()
    {
        var left = double.IsFinite(_settings.BurstMonitorLeft) ? _settings.BurstMonitorLeft : 980;
        var top = double.IsFinite(_settings.BurstMonitorTop) ? _settings.BurstMonitorTop : 220;

        if (_burstMonitorWindow == null)
        {
            _burstMonitorWindow = new BurstMonitorWindow
            {
                Left = left,
                Top = top,
                Topmost = true
            };
            _burstMonitorWindow.MonitorMoved += BurstMonitor_Moved;
        }
        else
        {
            _burstMonitorWindow.Left = left;
            _burstMonitorWindow.Top = top;
        }

        if (!_burstMonitorWindow.IsVisible) _burstMonitorWindow.Show();
        _burstMonitorWindow.Topmost = _settings.TopMost;
        _burstMonitorWindow.SetMonitorOpacity(_settings.BurstMonitorOpacity);
        _burstMonitorWindow.SetBackgroundOpacity(_settings.BurstMonitorBackgroundOpacity);
        _burstMonitorWindow.SetClickThrough(_settings.ClickThrough);
        ReapplyBurstMonitorTopMost(force: true);

        if (_settings.PipPositionLockedToTarget)
        {
            if (_settings.BurstMonitorTargetOffsetX is null || _settings.BurstMonitorTargetOffsetY is null)
            {
                CaptureBurstMonitorRelativePosition(save: false);
            }
            else
            {
                ApplyBurstMonitorTargetLockedPosition();
            }
        }
    }

    private void CloseBurstMonitorWindow()
    {
        if (_burstMonitorWindow == null) return;
        _burstMonitorWindow.MonitorMoved -= BurstMonitor_Moved;
        _burstMonitorWindow.ForceClose();
        _burstMonitorWindow = null;
    }

    private void BurstMonitor_Moved(BurstMonitorWindow monitor)
    {
        if (_isInitializing || _isApplyingHistory) return;
        _settings.BurstMonitorLeft = Math.Round(monitor.Left, 2);
        _settings.BurstMonitorTop = Math.Round(monitor.Top, 2);
        if (_settings.PipPositionLockedToTarget)
        {
            CaptureBurstMonitorRelativePosition(save: false);
        }
        SaveSettingsThrottled();
    }

    private void CaptureBurstMonitorRelativePosition(bool save)
    {
        if (_burstMonitorWindow == null) return;
        if (!TryGetTargetClientRectForMain(out var rect)) return;

        _settings.BurstMonitorTargetBaselineWidth = rect.Width;
        _settings.BurstMonitorTargetBaselineHeight = rect.Height;
        _settings.BurstMonitorTargetOffsetX = Math.Round(_burstMonitorWindow.Left - rect.X, 2);
        _settings.BurstMonitorTargetOffsetY = Math.Round(_burstMonitorWindow.Top - rect.Y, 2);
        if (save) SaveSettings();
    }

    private void ApplyBurstMonitorTargetLockedPosition()
    {
        if (!_settings.PipPositionLockedToTarget) return;
        if (_burstMonitorWindow == null || !_burstMonitorWindow.IsVisible) return;
        if (_settings.BurstMonitorTargetOffsetX is null || _settings.BurstMonitorTargetOffsetY is null)
        {
            CaptureBurstMonitorRelativePosition(save: false);
            return;
        }
        if (!TryGetTargetClientRectForMain(out var rect)) return;

        var monitorWidth = Math.Max(1, _burstMonitorWindow.ActualWidth > 1 ? _burstMonitorWindow.ActualWidth : _burstMonitorWindow.Width);
        var monitorHeight = Math.Max(1, _burstMonitorWindow.ActualHeight > 1 ? _burstMonitorWindow.ActualHeight : _burstMonitorWindow.Height);
        var desiredLeft = Math.Round(rect.X + _settings.BurstMonitorTargetOffsetX.Value, 2);
        var desiredTop = Math.Round(rect.Y + _settings.BurstMonitorTargetOffsetY.Value, 2);
        desiredLeft = Math.Round(ClampToRange(desiredLeft, rect.X, rect.X + Math.Max(0, rect.Width - monitorWidth)), 2);
        desiredTop = Math.Round(ClampToRange(desiredTop, rect.Y, rect.Y + Math.Max(0, rect.Height - monitorHeight)), 2);

        if (Math.Abs(_burstMonitorWindow.Left - desiredLeft) < 0.5 && Math.Abs(_burstMonitorWindow.Top - desiredTop) < 0.5)
        {
            ReapplyBurstMonitorTopMost(force: false);
            return;
        }

        _burstMonitorWindow.Left = desiredLeft;
        _burstMonitorWindow.Top = desiredTop;
        _settings.BurstMonitorLeft = desiredLeft;
        _settings.BurstMonitorTop = desiredTop;
        ReapplyBurstMonitorTopMost(force: true);
    }

    private void ReapplyBurstMonitorTopMost(bool force)
    {
        if (_burstMonitorWindow == null || !_burstMonitorWindow.IsVisible) return;
        if (!force && (DateTime.UtcNow - _lastBurstMonitorTopmostUtc).TotalMilliseconds < 160) return;
        _lastBurstMonitorTopmostUtc = DateTime.UtcNow;
        _burstMonitorWindow.ForceTopMost();
    }

    private bool IsSelectedTargetForeground()
    {
        if (_target == null || _target.ProcessId <= 0) return false;
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        if (foreground == _target.Hwnd) return true;
        NativeMethods.GetWindowThreadProcessId(foreground, out var foregroundProcessId);
        return foregroundProcessId != 0 && foregroundProcessId == (uint)_target.ProcessId;
    }

    private void TickBurstInputMonitor()
    {
        var targetForeground = IsSelectedTargetForeground();
        var now = DateTime.UtcNow;

        foreach (var role in new[] { BurstRole.SemiBurst, BurstRole.Burst, BurstRole.OriginBurst })
        {
            var state = GetBurstRoleState(role);
            var virtualKey = GetBurstTriggerVirtualKey(role);
            if (virtualKey <= 0)
            {
                state.TriggerKeyWasDown = false;
                continue;
            }

            var isDown = (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
            var risingEdge = isDown && !state.TriggerKeyWasDown;
            state.TriggerKeyWasDown = isDown;
            if (!risingEdge || !targetForeground) continue;

            RegisterBurstInputCandidate(role, now);
        }
    }

    private void RegisterBurstInputCandidate(BurstRole role, DateTime now)
    {
        var crop = _crops.FirstOrDefault(c => c.Enabled && c.BurstRole == role);
        if (crop == null || !TryGetCompatibleBurstCalibration(crop, out _, out _)) return;

        var state = GetBurstRoleState(role);
        var remaining = state.LastUsedUtc is null ? 0 : GetBurstRemainingSeconds(role, state.LastUsedUtc.Value, now);
        state.InputCandidateUtc = now;
        state.InputCandidateExpiresUtc = now.AddSeconds(BurstInputCandidateWindowSeconds);
        state.InputConfirmVotes = 0;
        state.InputBaselineObservedClass = state.LastObservedClass;
        state.InputBaselineConfidence = state.LastClassifierConfidence;
        state.InputBaselineFeatureVector = state.LastFeatureVector?.ToArray();
        state.InputCandidateBaselineReady = state.LastObservedClass == BurstObservedClass.Ready &&
                                            state.LastClassifierConfidence >= BurstInputBaselineReadyMinConfidence;
        state.InputCandidateNearNaturalExpiry = state.LastUsedUtc is null || remaining <= BurstInputNearExpirySeconds;

        SettingsService.Log($"burst_input_candidate_v33 | {role} | key={GetBurstTriggerKeyName(role)} | crop={crop.Name} | remain={remaining:0.00}s | baseline={state.LastObservedClass} | conf={state.LastClassifierConfidence:0.000} | baseline_ready={state.InputCandidateBaselineReady} | near_expiry={state.InputCandidateNearNaturalExpiry}");
    }

    private bool TryConfirmBurstInputCandidate(
        BurstRole role,
        CropItem crop,
        BurstRoleRuntimeState state,
        BurstCalibrationProfile profile,
        BurstClassification classification,
        double[] currentFeatures,
        DateTime now)
    {
        if (state.InputCandidateUtc is null || state.InputCandidateExpiresUtc is null) return false;

        if (now > state.InputCandidateExpiresUtc.Value)
        {
            SettingsService.Log($"burst_input_rejected_v33 | {role} | crop={crop.Name} | reason=timeout | votes={state.InputConfirmVotes} | last={classification.Label} | conf={classification.Confidence:0.000}");
            ClearBurstInputCandidate(state);
            return false;
        }

        var inputJumpRatio = ComputeBurstFeatureJumpRatio(state.InputBaselineFeatureVector, currentFeatures, profile);
        var strongCooldown = classification.Label == BurstObservedClass.Cooldown &&
                             classification.Confidence >= BurstInputCooldownMinConfidence;
        var baselineReadyEvidence = state.InputCandidateBaselineReady;
        var rapidExpiryEvidence = state.InputCandidateNearNaturalExpiry && inputJumpRatio >= BurstInputNearExpiryMinJumpRatio;
        var validTransitionEvidence = baselineReadyEvidence || rapidExpiryEvidence;

        if (strongCooldown && validTransitionEvidence)
        {
            state.InputConfirmVotes++;
            var ageMs = (now - state.InputCandidateUtc.Value).TotalMilliseconds;
            var requiredVotes = baselineReadyEvidence && classification.Confidence >= 0.75 ? 1 : BurstInputConfirmFrames;
            if (state.InputConfirmVotes >= requiredVotes && ageMs >= 8)
            {
                var inputUtc = state.InputCandidateUtc.Value;
                state.LastUsedUtc = inputUtc;
                state.ArmedFromReady = false;
                state.AwaitingVisualReadyRearm = false;
                state.TimerEndedUtc = null;
                state.CooldownVotes = 0;
                state.CooldownCandidateFirstUtc = null;
                state.RapidRecastCandidateFirstUtc = null;
                state.RapidRecastConfirmVotes = 0;
                state.ReadyWhileCoolingFirstUtc = null;
                state.ReadyWhileCoolingVotes = 0;
                var remaining = GetBurstRemainingSeconds(role, inputUtc, now);
                state.Status = remaining < ClampBurstWarningSeconds(_settings.BurstWarningThresholdSeconds)
                    ? BurstRuntimeStatus.Imminent
                    : BurstRuntimeStatus.Cooling;

                SettingsService.Log($"burst_input_confirmed_v33 | {role} | crop={crop.Name} | key={GetBurstTriggerKeyName(role)} | age={ageMs:0}ms | votes={state.InputConfirmVotes} | baseline_ready={baselineReadyEvidence} | near_expiry={state.InputCandidateNearNaturalExpiry} | jump={inputJumpRatio:0.000} | conf={classification.Confidence:0.000} | remain={remaining:0.00}s");
                ClearBurstInputCandidate(state);
                return true;
            }
        }
        else if (classification.Label == BurstObservedClass.Ready)
        {
            // Key was pressed while READY is still visible. Keep the candidate alive; the next
            // few WGC frames may contain the actual cooldown onset.
            state.InputConfirmVotes = 0;
        }
        else if (!strongCooldown)
        {
            state.InputConfirmVotes = Math.Max(0, state.InputConfirmVotes - 1);
        }

        return false;
    }

    private static void ClearBurstInputCandidate(BurstRoleRuntimeState state)
    {
        state.InputCandidateUtc = null;
        state.InputCandidateExpiresUtc = null;
        state.InputConfirmVotes = 0;
        state.InputBaselineObservedClass = BurstObservedClass.Unknown;
        state.InputBaselineConfidence = 0;
        state.InputBaselineFeatureVector = null;
        state.InputCandidateBaselineReady = false;
        state.InputCandidateNearNaturalExpiry = false;
    }

    private void TickBurstMonitorRuntime()
    {
        if (!_settings.BurstMonitorVisible)
        {
            return;
        }

        if (_allPipsTemporarilyHidden)
        {
            _burstMonitorWindow?.Hide();
            return;
        }

        if (_burstMonitorWindow == null || !_burstMonitorWindow.IsVisible)
        {
            EnsureBurstMonitorWindow();
        }

        if (_settings.PipPositionLockedToTarget)
        {
            ApplyBurstMonitorTargetLockedPosition();
        }

        TickBurstLearningSession();
        ProcessBurstRole(BurstRole.SemiBurst);
        ProcessBurstRole(BurstRole.Burst);
        ProcessBurstRole(BurstRole.OriginBurst);
        EvaluateForcedBurstReadyResets();
        UpdateBurstMonitorPipRows();
        UpdateBurstMonitorStatusText();
        UpdateBurstMonitorTimerCadence();
        ReapplyBurstMonitorTopMost(force: false);
    }

    private void UpdateBurstMonitorTimerCadence()
    {
        var now = DateTime.UtcNow;
        var fast = false;
        foreach (var role in new[] { BurstRole.SemiBurst, BurstRole.Burst, BurstRole.OriginBurst })
        {
            var state = GetBurstRoleState(role);
            if (state.LastUsedUtc is not null)
            {
                var remaining = GetBurstRemainingSeconds(role, state.LastUsedUtc.Value, now);
                if (remaining <= BurstFastWindowBeforeReadySeconds)
                {
                    fast = true;
                    break;
                }
            }

            if (state.TimerEndedUtc is not null && (now - state.TimerEndedUtc.Value).TotalSeconds <= BurstRapidRecastGraceSeconds)
            {
                fast = true;
                break;
            }

            if (state.ReadyWhileCoolingFirstUtc is not null || state.RapidRecastCandidateFirstUtc is not null)
            {
                fast = true;
                break;
            }
        }

        var desiredMs = fast ? BurstFastMonitorIntervalMs : BurstNormalMonitorIntervalMs;
        if (Math.Abs(_burstMonitorTimer.Interval.TotalMilliseconds - desiredMs) > 0.5)
        {
            _burstMonitorTimer.Interval = TimeSpan.FromMilliseconds(desiredMs);
        }
    }

    private void ProcessBurstRole(BurstRole role)
    {
        if (role == BurstRole.None) return;
        var crop = _crops.FirstOrDefault(c => c.Enabled && c.BurstRole == role);
        var state = GetBurstRoleState(role);

        if (crop == null)
        {
            state.Reset(BurstRuntimeStatus.Unassigned);
            return;
        }

        if (_burstLearningSession is { } learning && learning.Role == role)
        {
            state.Status = BurstRuntimeStatus.Learning;
            return;
        }

        if (!TryGetCompatibleBurstCalibration(crop, out var profile, out _))
        {
            state.Reset(BurstRuntimeStatus.NeedsTraining);
            return;
        }

        var timerNow = DateTime.UtcNow;
        if (state.LastUsedUtc is not null)
        {
            var timerRemaining = GetBurstRemainingSeconds(role, state.LastUsedUtc.Value, timerNow);
            if (timerRemaining <= 0)
            {
                // v33: visual READY is no longer required before the monitor can display READY.
                // Keep a short high-speed grace window so a direct cooldown-tail -> fresh-cooldown
                // transition can be recognized even when the player recasts before we sampled READY.
                state.LastUsedUtc = null;
                state.ArmedFromReady = false;
                state.AwaitingVisualReadyRearm = true;
                state.TimerEndedUtc = timerNow;
                state.Status = BurstRuntimeStatus.Ready;
                state.ReadyVotes = 0;
                state.CooldownVotes = 0;
                state.CooldownCandidateFirstUtc = null;
                state.RapidRecastCandidateFirstUtc = null;
                state.RapidRecastConfirmVotes = 0;
                state.ReadyWhileCoolingFirstUtc = null;
                state.ReadyWhileCoolingVotes = 0;
                SettingsService.Log($"burst_timer_ready_v33 | {role} | crop={crop.Name} | rapid_recast_grace={BurstRapidRecastGraceSeconds:0.0}s");
                // Deliberately continue into visual classification on the same tick.
            }
        }

        var feature = TryExtractBurstFeature(crop);
        if (feature == null)
        {
            if ((DateTime.UtcNow - state.LastGoodSampleUtc).TotalMilliseconds > 900 && !state.AwaitingVisualReadyRearm)
            {
                state.Status = BurstRuntimeStatus.Unstable;
            }
            return;
        }

        var now = DateTime.UtcNow;
        state.LastGoodSampleUtc = now;
        var classification = ClassifyBurstFeature(feature.Value.Features, profile!);
        var featureJumpRatio = ComputeBurstFeatureJumpRatio(state.LastFeatureVector, feature.Value.Features, profile!);
        state.LastFeatureVector = feature.Value.Features.ToArray();
        state.LastFeatureJumpRatio = featureJumpRatio;
        state.LastClassifierConfidence = classification.Confidence;
        state.LastClassifierScore = classification.NormalizedScore;
        state.LastReadyDistance = classification.ReadyDistance;
        state.LastCooldownDistance = classification.CooldownDistance;
        state.LastObservedClass = classification.Label;

        if (TryConfirmBurstInputCandidate(role, crop, state, profile!, classification, feature.Value.Features, now))
        {
            return;
        }

        if (state.LastUsedUtc is not null)
        {
            var remaining = GetBurstRemainingSeconds(role, state.LastUsedUtc.Value, now);

            // Unlike v30/v31, the visual sensor remains active while the internal timer is running.
            // A sustained, high-confidence READY observation can therefore invalidate a stale timer
            // after a boss-room cooldown reset.
            if (classification.Label == BurstObservedClass.Ready && classification.Confidence >= BurstGlobalResetMinConfidence)
            {
                state.ReadyWhileCoolingFirstUtc ??= now;
                state.ReadyWhileCoolingVotes++;
                if (state.ReadyWhileCoolingVotes == 1)
                {
                    SettingsService.Log($"burst_ready_while_timer_candidate_v33 | {role} | crop={crop.Name} | remain={remaining:0.00}s | conf={classification.Confidence:0.000} | score={classification.NormalizedScore:0.000}");
                }
            }
            else
            {
                state.ReadyWhileCoolingFirstUtc = null;
                state.ReadyWhileCoolingVotes = 0;
            }

            state.Status = remaining < ClampBurstWarningSeconds(_settings.BurstWarningThresholdSeconds)
                ? BurstRuntimeStatus.Imminent
                : BurstRuntimeStatus.Cooling;
            state.ReadyVotes = 0;
            state.CooldownVotes = 0;
            state.CooldownCandidateFirstUtc = null;
            return;
        }

        if (state.AwaitingVisualReadyRearm)
        {
            if (classification.Label == BurstObservedClass.Ready && classification.Confidence >= BurstClassifierMinConfidence)
            {
                state.AwaitingVisualReadyRearm = false;
                state.ArmedFromReady = true;
                state.TimerEndedUtc = null;
                state.ReadyVotes = BurstReadyConfirmFrames;
                state.CooldownVotes = 0;
                state.CooldownCandidateFirstUtc = null;
                state.RapidRecastCandidateFirstUtc = null;
                state.RapidRecastConfirmVotes = 0;
                state.Status = BurstRuntimeStatus.Ready;
                SettingsService.Log($"burst_rearmed_ready_seen_v33 | {role} | crop={crop.Name} | conf={classification.Confidence:0.000}");
                return;
            }

            var inRapidRecastGrace = state.TimerEndedUtc is not null &&
                                     (now - state.TimerEndedUtc.Value).TotalSeconds <= BurstRapidRecastGraceSeconds;
            var strongCooldown = classification.Label == BurstObservedClass.Cooldown &&
                                 classification.Confidence >= BurstRapidRecastMinConfidence &&
                                 classification.NormalizedScore >= BurstRapidRecastMinScore;

            if (inRapidRecastGrace)
            {
                if (state.RapidRecastCandidateFirstUtc is null)
                {
                    // First proof must include a sudden visual jump. This rejects the static tail of
                    // the previous cooldown while still allowing a direct tail -> new-cooldown cast.
                    if (strongCooldown && featureJumpRatio >= BurstRapidRecastMinFeatureJumpRatio)
                    {
                        state.RapidRecastCandidateFirstUtc = now;
                        state.RapidRecastConfirmVotes = 1;
                        SettingsService.Log($"burst_rapid_recast_candidate_v33 | {role} | crop={crop.Name} | jump={featureJumpRatio:0.000} | conf={classification.Confidence:0.000} | score={classification.NormalizedScore:0.000}");
                    }
                }
                else
                {
                    var candidateAgeMs = (now - state.RapidRecastCandidateFirstUtc.Value).TotalMilliseconds;
                    if (strongCooldown && candidateAgeMs <= 320)
                    {
                        state.RapidRecastConfirmVotes++;
                        if (state.RapidRecastConfirmVotes >= 2 && candidateAgeMs >= 25)
                        {
                            state.LastUsedUtc = state.RapidRecastCandidateFirstUtc.Value;
                            state.ArmedFromReady = false;
                            state.AwaitingVisualReadyRearm = false;
                            state.TimerEndedUtc = null;
                            state.CooldownVotes = 0;
                            state.CooldownCandidateFirstUtc = null;
                            state.RapidRecastCandidateFirstUtc = null;
                            state.RapidRecastConfirmVotes = 0;
                            var remaining = GetBurstRemainingSeconds(role, state.LastUsedUtc.Value, now);
                            state.Status = remaining < ClampBurstWarningSeconds(_settings.BurstWarningThresholdSeconds)
                                ? BurstRuntimeStatus.Imminent
                                : BurstRuntimeStatus.Cooling;
                            SettingsService.Log($"burst_rapid_recast_confirmed_v33 | {role} | crop={crop.Name} | remain={remaining:0.00}s | conf={classification.Confidence:0.000}");
                            return;
                        }
                    }
                    else if (candidateAgeMs > 320 || classification.Label == BurstObservedClass.Ready)
                    {
                        state.RapidRecastCandidateFirstUtc = null;
                        state.RapidRecastConfirmVotes = 0;
                    }
                }
            }
            else
            {
                state.RapidRecastCandidateFirstUtc = null;
                state.RapidRecastConfirmVotes = 0;
            }

            // The timer already reached zero, so the user-facing state remains READY. We simply
            // keep the detector internally disarmed until READY is seen or rapid-recast is proven.
            state.Status = BurstRuntimeStatus.Ready;
            return;
        }

        if (classification.Label == BurstObservedClass.Unknown || classification.Confidence < BurstClassifierMinConfidence)
        {
            state.ReadyVotes = Math.Max(0, state.ReadyVotes - 1);
            state.CooldownVotes = Math.Max(0, state.CooldownVotes - 1);
            state.CooldownCandidateFirstUtc = null;
            state.Status = BurstRuntimeStatus.Unstable;
            return;
        }

        if (classification.Label == BurstObservedClass.Ready)
        {
            state.ReadyVotes++;
            state.CooldownVotes = 0;
            state.CooldownCandidateFirstUtc = null;
            if (state.ReadyVotes >= BurstReadyConfirmFrames)
            {
                state.ArmedFromReady = true;
                state.Status = BurstRuntimeStatus.Ready;
            }
            else
            {
                state.Status = BurstRuntimeStatus.Observing;
            }
            return;
        }

        state.ReadyVotes = 0;
        state.CooldownVotes++;
        state.CooldownCandidateFirstUtc ??= now;

        if (!state.ArmedFromReady)
        {
            state.Status = state.CooldownVotes >= BurstUseConfirmFrames
                ? BurstRuntimeStatus.CoolingUnknown
                : BurstRuntimeStatus.Observing;
            return;
        }

        if (state.CooldownVotes < BurstUseConfirmFrames || state.CooldownCandidateFirstUtc is null ||
            (now - state.CooldownCandidateFirstUtc.Value).TotalMilliseconds < 300)
        {
            state.Status = BurstRuntimeStatus.Observing;
            return;
        }

        // Back-date to the first high-confidence COOLDOWN frame so confirmation latency is not
        // added to the displayed timer.
        state.LastUsedUtc = state.CooldownCandidateFirstUtc ?? now;
        state.ArmedFromReady = false;
        state.CooldownVotes = 0;
        state.CooldownCandidateFirstUtc = null;
        state.TimerEndedUtc = null;
        var firstRemaining = GetBurstRemainingSeconds(role, state.LastUsedUtc.Value, now);
        state.Status = firstRemaining < ClampBurstWarningSeconds(_settings.BurstWarningThresholdSeconds)
            ? BurstRuntimeStatus.Imminent
            : BurstRuntimeStatus.Cooling;

        SettingsService.Log($"burst_detected_v33 | {role} | crop={crop.Name} | confidence={classification.Confidence:0.000} | score={classification.NormalizedScore:0.000} | separation={classification.Separation:0.000} | jump={featureJumpRatio:0.000}");
    }

    private static double ComputeBurstFeatureJumpRatio(double[]? previous, double[] current, BurstCalibrationProfile profile)
    {
        if (previous == null || previous.Length == 0 || previous.Length != current.Length ||
            profile.ReadyMean.Length != current.Length || profile.CooldownMean.Length != current.Length)
        {
            return 0;
        }

        double frameDelta = 0;
        double learnedDelta = 0;
        for (var i = 0; i < current.Length; i++)
        {
            frameDelta += Math.Abs(current[i] - previous[i]);
            learnedDelta += Math.Abs(profile.CooldownMean[i] - profile.ReadyMean[i]);
        }

        frameDelta /= current.Length;
        learnedDelta /= current.Length;
        if (learnedDelta < 0.0025) learnedDelta = 0.0025;
        return Math.Clamp(frameDelta / learnedDelta, 0, 10);
    }

    private void EvaluateForcedBurstReadyResets()
    {
        var now = DateTime.UtcNow;
        var roles = new[] { BurstRole.SemiBurst, BurstRole.Burst, BurstRole.OriginBurst };
        var candidates = new List<(BurstRole Role, BurstRoleRuntimeState State, double Remaining, double HeldSeconds)>();

        foreach (var role in roles)
        {
            var state = GetBurstRoleState(role);
            if (state.LastUsedUtc is null || state.ReadyWhileCoolingFirstUtc is null) continue;
            var remaining = GetBurstRemainingSeconds(role, state.LastUsedUtc.Value, now);
            if (remaining <= 2.0) continue; // natural expiry is handled by the timer path, not reset detection.
            var held = (now - state.ReadyWhileCoolingFirstUtc.Value).TotalSeconds;
            candidates.Add((role, state, remaining, held));
        }

        var globalCandidates = candidates
            .Where(c => c.HeldSeconds >= BurstGlobalResetHoldSeconds &&
                        c.State.ReadyWhileCoolingVotes >= 3 &&
                        c.State.LastObservedClass == BurstObservedClass.Ready &&
                        c.State.LastClassifierConfidence >= BurstGlobalResetMinConfidence)
            .ToList();

        if (globalCandidates.Count >= 2)
        {
            var evidenceRoles = string.Join(",", globalCandidates.Select(c => c.Role.ToString()));
            SettingsService.Log($"burst_global_cooldown_reset_confirmed_v33 | roles={evidenceRoles} | count={globalCandidates.Count}");

            foreach (var role in roles)
            {
                var state = GetBurstRoleState(role);
                if (state.LastUsedUtc is null) continue;
                var visuallyConfirmed = globalCandidates.Any(c => c.Role == role);
                ForceBurstReadyState(role, state, visuallyConfirmed, reason: "global_reset");
            }
            return;
        }

        // Single-role fallback is intentionally stricter. This supports users who monitor only one
        // role without letting a brief classifier mistake erase a valid cooldown timer.
        foreach (var candidate in candidates)
        {
            if (candidate.HeldSeconds < BurstForcedReadyHoldSeconds ||
                candidate.State.ReadyWhileCoolingVotes < 5 ||
                candidate.State.LastObservedClass != BurstObservedClass.Ready ||
                candidate.State.LastClassifierConfidence < BurstForcedReadyMinConfidence)
            {
                continue;
            }

            SettingsService.Log($"burst_individual_cooldown_reset_confirmed_v33 | {candidate.Role} | remain={candidate.Remaining:0.00}s | held={candidate.HeldSeconds:0.000}s | conf={candidate.State.LastClassifierConfidence:0.000}");
            ForceBurstReadyState(candidate.Role, candidate.State, visuallyConfirmed: true, reason: "individual_reset");
        }
    }

    private void ForceBurstReadyState(BurstRole role, BurstRoleRuntimeState state, bool visuallyConfirmed, string reason)
    {
        state.LastUsedUtc = null;
        state.Status = BurstRuntimeStatus.Ready;
        state.ReadyVotes = visuallyConfirmed ? BurstReadyConfirmFrames : 0;
        state.CooldownVotes = 0;
        state.CooldownCandidateFirstUtc = null;
        state.ReadyWhileCoolingFirstUtc = null;
        state.ReadyWhileCoolingVotes = 0;
        state.RapidRecastCandidateFirstUtc = null;
        state.RapidRecastConfirmVotes = 0;
        state.TimerEndedUtc = null;
        ClearBurstInputCandidate(state);
        state.ArmedFromReady = visuallyConfirmed;
        state.AwaitingVisualReadyRearm = !visuallyConfirmed;
        SettingsService.Log($"burst_force_ready_v33 | {role} | reason={reason} | visually_confirmed={visuallyConfirmed}");
    }

    private BurstRoleRuntimeState GetBurstRoleState(BurstRole role)
    {
        if (!_burstStates.TryGetValue(role, out var state))
        {
            state = new BurstRoleRuntimeState();
            _burstStates[role] = state;
        }
        return state;
    }

    private bool TryGetCompatibleBurstCalibration(CropItem crop, out BurstCalibrationProfile? profile, out string reason)
    {
        profile = null;
        reason = "학습 안 됨";
        _settings.BurstCalibrationByCropId ??= new Dictionary<string, BurstCalibrationProfile>();
        if (!_settings.BurstCalibrationByCropId.TryGetValue(crop.Id, out var found) || found == null)
        {
            return false;
        }

        if (found.FeatureVersion != BurstFeatureVersion)
        {
            reason = "학습 버전 다름";
            return false;
        }
        if (!string.Equals(found.CropId, crop.Id, StringComparison.Ordinal))
        {
            reason = "크롭 불일치";
            return false;
        }
        if (found.CropX != crop.X || found.CropY != crop.Y || found.CropWidth != crop.Width || found.CropHeight != crop.Height)
        {
            reason = "크롭 영역 변경";
            return false;
        }

        var currentResolution = GetCurrentTargetResolution();
        if (currentResolution is not null && found.TargetSourceWidth > 0 && found.TargetSourceHeight > 0 &&
            (found.TargetSourceWidth != currentResolution.Value.Width || found.TargetSourceHeight != currentResolution.Value.Height))
        {
            reason = $"해상도 불일치 {found.TargetSourceWidth}x{found.TargetSourceHeight}";
            return false;
        }

        if (found.ReadySampleCount < BurstLearningMinimumSamples || found.CooldownSampleCount < BurstLearningMinimumSamples ||
            found.ReadyMean.Length == 0 || found.CooldownMean.Length == 0 ||
            found.ReadyMean.Length != found.CooldownMean.Length ||
            found.ReadyVariance.Length != found.ReadyMean.Length || found.CooldownVariance.Length != found.ReadyMean.Length)
        {
            reason = $"학습 부족 R{found.ReadySampleCount}/C{found.CooldownSampleCount}";
            return false;
        }

        var separation = ComputeBurstCalibrationSeparation(found);
        if (!double.IsFinite(separation) || separation < 0.16)
        {
            reason = $"분리도 낮음 {separation:0.00}";
            return false;
        }

        profile = found;
        reason = $"학습 완료 · 분리도 {separation:0.00}";
        return true;
    }

    private BurstFeatureVector? TryExtractBurstFeature(CropItem crop)
    {
        if (_capture == null || !_capture.IsCapturing) return null;
        var source = _capture.GetLatestCrop(new Int32Rect(crop.X, crop.Y, crop.Width, crop.Height));
        if (source == null) return null;
        return ExtractBurstFeatures(source);
    }

    private static BurstFeatureVector ExtractBurstFeatures(BitmapSource bitmap)
    {
        BitmapSource source = bitmap.Format == PixelFormats.Bgra32
            ? bitmap
            : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var width = Math.Max(1, source.PixelWidth);
        var height = Math.Max(1, source.PixelHeight);
        var stride = width * 4;
        var pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);

        var grid = BurstFeatureGridSize;
        var cellR = new double[grid * grid];
        var cellG = new double[grid * grid];
        var cellB = new double[grid * grid];
        var counts = new int[grid * grid];

        for (var y = 0; y < height; y++)
        {
            var gy = Math.Clamp((int)(y / (double)height * grid), 0, grid - 1);
            var row = y * stride;
            for (var x = 0; x < width; x++)
            {
                var gx = Math.Clamp((int)(x / (double)width * grid), 0, grid - 1);
                var ci = gy * grid + gx;
                var i = row + x * 4;
                cellB[ci] += pixels[i] / 255.0;
                cellG[ci] += pixels[i + 1] / 255.0;
                cellR[ci] += pixels[i + 2] / 255.0;
                counts[ci]++;
            }
        }

        var luma = new double[grid * grid];
        for (var i = 0; i < luma.Length; i++)
        {
            var n = Math.Max(1, counts[i]);
            cellR[i] /= n;
            cellG[i] /= n;
            cellB[i] /= n;
            luma[i] = 0.2126 * cellR[i] + 0.7152 * cellG[i] + 0.0722 * cellB[i];
        }

        // Four channels per cell: RGB means + local structural gradient.
        // This keeps the detector sensitive to the actual learned icon state without relying on any universal brightness threshold.
        var features = new double[grid * grid * 4];
        var fi = 0;
        for (var gy = 0; gy < grid; gy++)
        {
            for (var gx = 0; gx < grid; gx++)
            {
                var ci = gy * grid + gx;
                var right = gy * grid + Math.Min(grid - 1, gx + 1);
                var down = Math.Min(grid - 1, gy + 1) * grid + gx;
                var gradient = Math.Min(1.0, Math.Abs(luma[ci] - luma[right]) + Math.Abs(luma[ci] - luma[down]));
                features[fi++] = cellR[ci];
                features[fi++] = cellG[ci];
                features[fi++] = cellB[ci];
                features[fi++] = gradient;
            }
        }

        return new BurstFeatureVector(features);
    }

    private static BurstClassification ClassifyBurstFeature(double[] features, BurstCalibrationProfile profile)
    {
        if (features.Length == 0 || features.Length != profile.ReadyMean.Length || features.Length != profile.CooldownMean.Length)
        {
            return BurstClassification.Unknown;
        }

        double numerator = 0;
        double halfSeparation = 0;
        double readyDistance = 0;
        double cooldownDistance = 0;

        for (var i = 0; i < features.Length; i++)
        {
            var ready = profile.ReadyMean[i];
            var cooldown = profile.CooldownMean[i];
            var delta = cooldown - ready;
            // Shrinkage floor prevents a perfectly static training pixel from receiving infinite weight.
            var pooledVariance = 0.5 * (Math.Max(0, profile.ReadyVariance[i]) + Math.Max(0, profile.CooldownVariance[i])) + 0.0016;
            var midpoint = 0.5 * (ready + cooldown);
            var weight = delta / pooledVariance;
            numerator += weight * (features[i] - midpoint);
            halfSeparation += 0.5 * delta * delta / pooledVariance;

            var rd = features[i] - ready;
            var cd = features[i] - cooldown;
            readyDistance += rd * rd / pooledVariance;
            cooldownDistance += cd * cd / pooledVariance;
        }

        if (halfSeparation <= 1e-8)
        {
            return BurstClassification.Unknown;
        }

        var normalizedScore = numerator / halfSeparation; // READY mean ~= -1, COOLDOWN mean ~= +1
        readyDistance /= features.Length;
        cooldownDistance /= features.Length;
        var bestDistance = Math.Min(readyDistance, cooldownDistance);
        var distanceMargin = Math.Abs(readyDistance - cooldownDistance) / Math.Max(1e-8, readyDistance + cooldownDistance);
        var directionalConfidence = Math.Clamp((Math.Abs(normalizedScore) - 0.20) / 0.80, 0, 1);
        var marginConfidence = Math.Clamp(distanceMargin / 0.45, 0, 1);
        var inDistributionConfidence = Math.Clamp((3.2 - bestDistance) / 2.4, 0, 1);
        var confidence = Math.Min(directionalConfidence, Math.Min(marginConfidence, inDistributionConfidence));
        var separation = halfSeparation * 2.0 / features.Length;

        var label = confidence < BurstClassifierMinConfidence
            ? BurstObservedClass.Unknown
            : normalizedScore >= 0
                ? BurstObservedClass.Cooldown
                : BurstObservedClass.Ready;

        return new BurstClassification(label, confidence, normalizedScore, separation, readyDistance, cooldownDistance);
    }

    private static double ComputeBurstCalibrationSeparation(BurstCalibrationProfile profile)
    {
        if (profile.ReadyMean == null || profile.CooldownMean == null || profile.ReadyVariance == null || profile.CooldownVariance == null ||
            profile.ReadyMean.Length == 0 || profile.ReadyMean.Length != profile.CooldownMean.Length ||
            profile.ReadyVariance.Length != profile.ReadyMean.Length || profile.CooldownVariance.Length != profile.ReadyMean.Length)
        {
            return 0;
        }

        double sum = 0;
        for (var i = 0; i < profile.ReadyMean.Length; i++)
        {
            var delta = profile.CooldownMean[i] - profile.ReadyMean[i];
            var pooledVariance = 0.5 * (Math.Max(0, profile.ReadyVariance[i]) + Math.Max(0, profile.CooldownVariance[i])) + 0.0016;
            sum += delta * delta / pooledVariance;
        }
        return sum / profile.ReadyMean.Length;
    }

    private void BurstLearn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string tag) return;
        var parts = tag.Split('|');
        if (parts.Length != 2 || !Enum.TryParse<BurstRole>(parts[0], out var role) || !Enum.TryParse<BurstLearnLabel>(parts[1], out var label)) return;
        StartBurstLearning(role, label);
    }

    private void BurstLearnClear_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string tag || !Enum.TryParse<BurstRole>(tag, out var role)) return;
        var crop = _crops.FirstOrDefault(c => c.BurstRole == role);
        if (crop == null)
        {
            StatusText.Text = $"{role.ToKoreanName()} 크롭이 지정되지 않았습니다.";
            return;
        }

        if (_burstLearningSession?.Role == role) _burstLearningSession = null;
        _settings.BurstCalibrationByCropId.Remove(crop.Id);
        ResetBurstRuntimeForRole(role);
        SaveSettings();
        UpdateBurstCalibrationStatusUi();
        UpdateBurstMonitorPipRows();
        StatusText.Text = $"{role.ToKoreanName()} 감지 학습 데이터를 삭제했습니다.";
    }

    private void StartBurstLearning(BurstRole role, BurstLearnLabel label)
    {
        var crop = _crops.FirstOrDefault(c => c.Enabled && c.BurstRole == role);
        if (crop == null)
        {
            StatusText.Text = $"먼저 {role.ToKoreanName()} 스킬 크롭을 지정하세요.";
            return;
        }
        if (_capture == null || !_capture.IsCapturing)
        {
            StatusText.Text = "먼저 메이플 대상 창을 연결하세요.";
            return;
        }

        _settings.BurstCalibrationByCropId ??= new Dictionary<string, BurstCalibrationProfile>();
        if (label == BurstLearnLabel.Cooldown)
        {
            if (!_settings.BurstCalibrationByCropId.TryGetValue(crop.Id, out var existing) || existing.ReadySampleCount < BurstLearningMinimumSamples)
            {
                StatusText.Text = $"{role.ToKoreanName()}: 먼저 스킬이 사용 가능한 상태에서 ‘준비 학습’을 완료하세요.";
                return;
            }
        }

        _burstLearningSession = new BurstLearningSession
        {
            Role = role,
            Label = label,
            CropId = crop.Id,
            StartedUtc = DateTime.UtcNow,
            EndUtc = DateTime.UtcNow + BurstLearningDuration
        };
        ResetBurstRuntimeForRole(role);
        UpdateBurstCalibrationStatusUi();
        StatusText.Text = label == BurstLearnLabel.Ready
            ? $"{role.ToKoreanName()} 준비 상태 학습 중… 스킬을 사용하지 말고 그대로 유지하세요."
            : $"{role.ToKoreanName()} 사용 직후 상태 학습 중… 현재 쿨타임 화면을 그대로 유지하세요.";
    }

    private void TickBurstLearningSession()
    {
        var session = _burstLearningSession;
        if (session == null) return;

        var crop = _crops.FirstOrDefault(c => c.Id == session.CropId && c.Enabled && c.BurstRole == session.Role);
        if (crop == null || _capture == null || !_capture.IsCapturing)
        {
            _burstLearningSession = null;
            UpdateBurstCalibrationStatusUi();
            StatusText.Text = "감지 학습이 중단되었습니다. 대상/크롭 연결을 확인하세요.";
            return;
        }

        var feature = TryExtractBurstFeature(crop);
        if (feature is not null)
        {
            session.Samples.Add(feature.Value.Features);
        }

        var now = DateTime.UtcNow;
        if (now < session.EndUtc)
        {
            UpdateBurstCalibrationStatusUi();
            return;
        }

        _burstLearningSession = null;
        if (session.Samples.Count < BurstLearningMinimumSamples)
        {
            UpdateBurstCalibrationStatusUi();
            StatusText.Text = $"{session.Role.ToKoreanName()} 학습 실패: 유효 프레임 {session.Samples.Count}개. 캡처 연결을 확인하세요.";
            return;
        }

        ComputeBurstBatchStatistics(session.Samples, out var mean, out var variance);
        var currentResolution = GetCurrentTargetResolution();
        if (!_settings.BurstCalibrationByCropId.TryGetValue(crop.Id, out var profile) || profile == null ||
            profile.FeatureVersion != BurstFeatureVersion || profile.CropX != crop.X || profile.CropY != crop.Y || profile.CropWidth != crop.Width || profile.CropHeight != crop.Height ||
            (currentResolution is not null && profile.TargetSourceWidth > 0 && profile.TargetSourceHeight > 0 &&
             (profile.TargetSourceWidth != currentResolution.Value.Width || profile.TargetSourceHeight != currentResolution.Value.Height)))
        {
            profile = new BurstCalibrationProfile();
            _settings.BurstCalibrationByCropId[crop.Id] = profile;
        }

        profile.FeatureVersion = BurstFeatureVersion;
        profile.CropId = crop.Id;
        profile.CropX = crop.X;
        profile.CropY = crop.Y;
        profile.CropWidth = crop.Width;
        profile.CropHeight = crop.Height;
        profile.TargetSourceWidth = currentResolution?.Width ?? 0;
        profile.TargetSourceHeight = currentResolution?.Height ?? 0;

        if (session.Label == BurstLearnLabel.Ready)
        {
            profile.ReadyMean = mean;
            profile.ReadyVariance = variance;
            profile.ReadySampleCount = session.Samples.Count;
            profile.ReadyLearnedUtc = now;
            // READY is the anchor. Re-learning it invalidates an old cooldown class from another visual setup.
            profile.CooldownMean = Array.Empty<double>();
            profile.CooldownVariance = Array.Empty<double>();
            profile.CooldownSampleCount = 0;
            profile.CooldownLearnedUtc = null;
        }
        else
        {
            profile.CooldownMean = mean;
            profile.CooldownVariance = variance;
            profile.CooldownSampleCount = session.Samples.Count;
            profile.CooldownLearnedUtc = now;
        }

        ResetBurstRuntimeForRole(session.Role);
        SaveSettings();
        UpdateBurstCalibrationStatusUi();
        UpdateBurstMonitorPipRows();

        if (session.Label == BurstLearnLabel.Ready)
        {
            StatusText.Text = $"{session.Role.ToKoreanName()} 준비 학습 완료 ({session.Samples.Count}프레임). 이제 스킬 사용 직후 ‘사용 직후 학습’을 실행하세요.";
        }
        else
        {
            var separation = ComputeBurstCalibrationSeparation(profile);
            StatusText.Text = separation >= 0.16
                ? $"{session.Role.ToKoreanName()} 감지 학습 완료 · 분리도 {separation:0.00}"
                : $"{session.Role.ToKoreanName()} 학습 분리도가 낮습니다 ({separation:0.00}). 준비/사용 직후 상태를 다시 학습하세요.";
        }
    }

    private static void ComputeBurstBatchStatistics(List<double[]> samples, out double[] mean, out double[] variance)
    {
        var length = samples.Count == 0 ? 0 : samples[0].Length;
        mean = new double[length];
        variance = new double[length];
        if (length == 0) return;

        var valid = samples.Where(s => s.Length == length).ToList();
        if (valid.Count == 0) return;

        foreach (var sample in valid)
        {
            for (var i = 0; i < length; i++) mean[i] += sample[i];
        }
        for (var i = 0; i < length; i++) mean[i] /= valid.Count;

        if (valid.Count <= 1) return;
        foreach (var sample in valid)
        {
            for (var i = 0; i < length; i++)
            {
                var d = sample[i] - mean[i];
                variance[i] += d * d;
            }
        }
        for (var i = 0; i < length; i++) variance[i] /= valid.Count - 1;
    }

    private void UpdateBurstCalibrationStatusUi()
    {
        if (SemiBurstLearnStatusText == null || BurstLearnStatusText == null || OriginBurstLearnStatusText == null) return;
        SetBurstCalibrationStatusText(BurstRole.SemiBurst, SemiBurstLearnStatusText);
        SetBurstCalibrationStatusText(BurstRole.Burst, BurstLearnStatusText);
        SetBurstCalibrationStatusText(BurstRole.OriginBurst, OriginBurstLearnStatusText);
    }

    private void SetBurstCalibrationStatusText(BurstRole role, TextBlock target)
    {
        if (_burstLearningSession is { } learning && learning.Role == role)
        {
            var remaining = Math.Max(0, (learning.EndUtc - DateTime.UtcNow).TotalSeconds);
            target.Text = $"{(learning.Label == BurstLearnLabel.Ready ? "준비" : "사용 직후")} 학습중 {remaining:0.0}s · {learning.Samples.Count}f";
            target.Foreground = new SolidColorBrush(Color.FromRgb(33, 91, 158));
            return;
        }

        var crop = _crops.FirstOrDefault(c => c.Enabled && c.BurstRole == role);
        if (crop == null)
        {
            target.Text = "크롭 미지정";
            target.Foreground = new SolidColorBrush(Color.FromRgb(102, 119, 128));
            return;
        }

        if (TryGetCompatibleBurstCalibration(crop, out var profile, out var reason))
        {
            target.Text = $"완료 R{profile!.ReadySampleCount}/C{profile.CooldownSampleCount} · {ComputeBurstCalibrationSeparation(profile):0.00}";
            target.Foreground = new SolidColorBrush(Color.FromRgb(27, 116, 68));
            return;
        }

        if (_settings.BurstCalibrationByCropId.TryGetValue(crop.Id, out var partial) && partial != null)
        {
            if (partial.ReadySampleCount >= BurstLearningMinimumSamples && partial.CooldownSampleCount < BurstLearningMinimumSamples)
            {
                target.Text = $"준비 완료 R{partial.ReadySampleCount} · 사용 직후 필요";
            }
            else
            {
                target.Text = reason;
            }
        }
        else
        {
            target.Text = "학습 안 됨";
        }
        target.Foreground = new SolidColorBrush(Color.FromRgb(137, 91, 20));
    }

    private void UpdateBurstMonitorPipRows()
    {
        if (_burstMonitorWindow == null) return;
        UpdateBurstMonitorRoleRow(BurstRole.SemiBurst);
        UpdateBurstMonitorRoleRow(BurstRole.Burst);
        UpdateBurstMonitorRoleRow(BurstRole.OriginBurst);
        _burstMonitorWindow.SetFooter(string.Empty);
    }

    private static string FormatBurstRemainingTime(double remainingSeconds)
    {
        var totalSeconds = Math.Max(0, (int)Math.Ceiling(remainingSeconds));
        if (totalSeconds < 60) return $"{totalSeconds}s";
        var minutes = totalSeconds / 60;
        var seconds = totalSeconds % 60;
        return $"{minutes}m {seconds}s";
    }

    private void UpdateBurstMonitorRoleRow(BurstRole role)
    {
        if (_burstMonitorWindow == null) return;
        var crop = _crops.FirstOrDefault(c => c.Enabled && c.BurstRole == role);
        if (crop == null)
        {
            _burstMonitorWindow.UpdateRole(role, "--", "미지정", BurstMonitorVisualKind.Idle);
            return;
        }

        var state = GetBurstRoleState(role);
        if (state.LastUsedUtc is not null)
        {
            var remaining = GetBurstRemainingSeconds(role, state.LastUsedUtc.Value, DateTime.UtcNow);
            // "임박" is derived only from a confirmed internal timer. Image classification can never set it directly.
            var isImminent = remaining > 0 && remaining < ClampBurstWarningSeconds(_settings.BurstWarningThresholdSeconds);
            var visual = isImminent ? BurstMonitorVisualKind.Imminent : BurstMonitorVisualKind.Cooling;
            var text = isImminent ? "임박" : "쿨중";
            _burstMonitorWindow.UpdateRole(role, FormatBurstRemainingTime(remaining), text, visual);
            return;
        }

        switch (state.Status)
        {
            case BurstRuntimeStatus.Ready:
                _burstMonitorWindow.UpdateRole(role, "OK", "준비", BurstMonitorVisualKind.Ready);
                break;
            case BurstRuntimeStatus.CoolingUnknown:
                _burstMonitorWindow.UpdateRole(role, "--", "쿨중", BurstMonitorVisualKind.Cooling);
                break;
            case BurstRuntimeStatus.Learning:
                _burstMonitorWindow.UpdateRole(role, "--", "학습중", BurstMonitorVisualKind.Idle);
                break;
            case BurstRuntimeStatus.NeedsTraining:
                _burstMonitorWindow.UpdateRole(role, "--", "학습필요", BurstMonitorVisualKind.Idle);
                break;
            case BurstRuntimeStatus.Observing:
                _burstMonitorWindow.UpdateRole(role, "--", "확인중", BurstMonitorVisualKind.Idle);
                break;
            case BurstRuntimeStatus.Unstable:
                _burstMonitorWindow.UpdateRole(role, "--", "불확실", BurstMonitorVisualKind.Unstable);
                break;
            default:
                _burstMonitorWindow.UpdateRole(role, "--", "대기", BurstMonitorVisualKind.Idle);
                break;
        }
    }



    private sealed record PipOption(string Key, string Name);

    private List<PipWindowSnapshot> GetAllPipWindowSnapshots()
    {
        var list = new List<PipWindowSnapshot>();
        if (_overlay != null)
        {
            list.Add(PipWindowSnapshot.FromOverlay(MainPipKey, _overlay, GetPipCropOpacity(MainPipKey), GetPipBackgroundOpacity(MainPipKey)));
        }
        foreach (var pair in _detachedOverlays)
        {
            list.Add(PipWindowSnapshot.FromOverlay(pair.Key, pair.Value, GetPipCropOpacity(pair.Key), GetPipBackgroundOpacity(pair.Key)));
        }
        return list;
    }

    private void ApplyPipWindowSnapshots(IReadOnlyList<PipWindowSnapshot> snapshots)
    {
        foreach (var snapshot in snapshots)
        {
            var overlay = GetOverlayByPipKey(snapshot.Key);
            if (overlay == null) continue;
            overlay.Left = snapshot.Left;
            overlay.Top = snapshot.Top;
            overlay.Width = Math.Max(60, snapshot.Width);
            overlay.Height = Math.Max(40, snapshot.Height);
            overlay.SetTargetLockState(snapshot.TargetOffsetX, snapshot.TargetOffsetY, snapshot.TargetBaselineWidth, snapshot.TargetBaselineHeight);
            SetPipCropOpacity(snapshot.Key, snapshot.CropOpacity, save: false);
            SetPipBackgroundOpacity(snapshot.Key, snapshot.BackgroundOpacity, save: false);
        }
    }


    private sealed class HistorySnapshot
    {
        public List<CropSnapshot> Crops { get; init; } = new();
        public double OverlayLeft { get; init; }
        public double OverlayTop { get; init; }
        public double OverlayWidth { get; init; }
        public double OverlayHeight { get; init; }
        public List<PipWindowSnapshot> PipWindows { get; init; } = new();
        public double Scale { get; init; }
        public double Opacity { get; init; }

        public string Signature => string.Join("|", Crops.Select(c => c.Signature))
            + "~" + string.Join("|", PipWindows.Select(p => p.Signature))
            + $"~{OverlayLeft:0.##},{OverlayTop:0.##},{OverlayWidth:0.##},{OverlayHeight:0.##},{Scale:0.##},{Opacity:0.##}";
    }

    private sealed class DetachLayoutSnapshot
    {
        public DetachLayoutSnapshot(Rect bounds, Dictionary<string, Point> normalizedOffsets, string? sourceGroupId)
        {
            Bounds = bounds;
            NormalizedOffsets = normalizedOffsets;
            SourceGroupId = sourceGroupId;
        }

        public Rect Bounds { get; }
        public Dictionary<string, Point> NormalizedOffsets { get; }
        public string? SourceGroupId { get; }
    }

    private sealed class PipWindowSnapshot
    {
        public string Key { get; init; } = MainPipKey;
        public double Left { get; init; }
        public double Top { get; init; }
        public double Width { get; init; }
        public double Height { get; init; }
        public double CropOpacity { get; init; }
        public double BackgroundOpacity { get; init; }
        public double? TargetOffsetX { get; init; }
        public double? TargetOffsetY { get; init; }
        public int TargetBaselineWidth { get; init; }
        public int TargetBaselineHeight { get; init; }

        public string Signature => $"{Key},{Left:0.##},{Top:0.##},{Width:0.##},{Height:0.##},{CropOpacity:0.##},{BackgroundOpacity:0.##},{TargetOffsetX:0.##},{TargetOffsetY:0.##},{TargetBaselineWidth},{TargetBaselineHeight}";

        public static PipWindowSnapshot FromOverlay(string key, OverlayWindow overlay, double cropOpacity, double backgroundOpacity) => new()
        {
            Key = key,
            Left = overlay.Left,
            Top = overlay.Top,
            Width = overlay.Width,
            Height = overlay.Height,
            CropOpacity = cropOpacity,
            BackgroundOpacity = backgroundOpacity,
            TargetOffsetX = overlay.TargetLockedOffsetX,
            TargetOffsetY = overlay.TargetLockedOffsetY,
            TargetBaselineWidth = overlay.TargetLockBaselineWidth,
            TargetBaselineHeight = overlay.TargetLockBaselineHeight
        };
    }

    private sealed class CropSnapshot
    {
        public string Id { get; init; } = Guid.NewGuid().ToString("N");
        public string Name { get; init; } = "Crop";
        public bool Enabled { get; init; }
        public int X { get; init; }
        public int Y { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public double DisplayX { get; init; }
        public double DisplayY { get; init; }
        public double DisplayWidth { get; init; }
        public double DisplayHeight { get; init; }
        public double PipOffsetX { get; init; }
        public double PipOffsetY { get; init; }
        public CropShape Shape { get; init; }
        public BurstRole BurstRole { get; init; }
        public string BorderColorHex { get; init; } = "#00FF7F";
        public double BorderOpacity { get; init; }
        public double RotationAngle { get; init; }
        public string? DetachedGroupIdRuntime { get; init; }

        public string Signature => string.Join(",",
            Id, Name, Enabled, X, Y, Width, Height,
            DisplayX.ToString("0.##"), DisplayY.ToString("0.##"),
            DisplayWidth.ToString("0.##"), DisplayHeight.ToString("0.##"),
            PipOffsetX.ToString("0.##"), PipOffsetY.ToString("0.##"),
            Shape, BurstRole, BorderColorHex, BorderOpacity.ToString("0.##"), RotationAngle.ToString("0.##"), DetachedGroupIdRuntime ?? "");

        public static CropSnapshot FromCrop(CropItem crop) => new()
        {
            Id = crop.Id,
            Name = crop.Name,
            Enabled = crop.Enabled,
            X = crop.X,
            Y = crop.Y,
            Width = crop.Width,
            Height = crop.Height,
            DisplayX = crop.DisplayX,
            DisplayY = crop.DisplayY,
            DisplayWidth = crop.DisplayWidth,
            DisplayHeight = crop.DisplayHeight,
            PipOffsetX = crop.PipOffsetX,
            PipOffsetY = crop.PipOffsetY,
            Shape = crop.Shape,
            BurstRole = crop.BurstRole,
            BorderColorHex = crop.BorderColorHex,
            BorderOpacity = crop.BorderOpacity,
            RotationAngle = crop.RotationAngle,
            DetachedGroupIdRuntime = crop.DetachedGroupIdRuntime
        };

        public CropItem ToCropItem() => new()
        {
            Id = Id,
            Name = Name,
            Enabled = Enabled,
            X = X,
            Y = Y,
            Width = Width,
            Height = Height,
            DisplayX = DisplayX,
            DisplayY = DisplayY,
            DisplayWidth = DisplayWidth,
            DisplayHeight = DisplayHeight,
            PipOffsetX = PipOffsetX,
            PipOffsetY = PipOffsetY,
            Shape = Shape,
            BurstRole = BurstRole,
            BorderColorHex = BorderColorHex,
            BorderOpacity = BorderOpacity,
            RotationAngle = RotationAngle,
            DetachedGroupIdRuntime = DetachedGroupIdRuntime
        };
    }

    private sealed record CropRoleOption(string? CropId, string Name);

    private readonly record struct BurstFeatureVector(double[] Features);

    private enum BurstLearnLabel
    {
        Ready,
        Cooldown
    }

    private enum BurstObservedClass
    {
        Unknown,
        Ready,
        Cooldown
    }

    private readonly record struct BurstClassification(
        BurstObservedClass Label,
        double Confidence,
        double NormalizedScore,
        double Separation,
        double ReadyDistance,
        double CooldownDistance)
    {
        public static BurstClassification Unknown => new(BurstObservedClass.Unknown, 0, 0, 0, double.PositiveInfinity, double.PositiveInfinity);
    }

    private enum BurstRuntimeStatus
    {
        Unassigned,
        NeedsTraining,
        Learning,
        Observing,
        Ready,
        CoolingUnknown,
        Cooling,
        Imminent,
        Unstable
    }

    private sealed class BurstLearningSession
    {
        public BurstRole Role { get; init; }
        public BurstLearnLabel Label { get; init; }
        public string CropId { get; init; } = string.Empty;
        public DateTime StartedUtc { get; init; }
        public DateTime EndUtc { get; init; }
        public List<double[]> Samples { get; } = new();
    }

    private sealed class BurstRoleRuntimeState
    {
        public bool ArmedFromReady { get; set; }
        public bool AwaitingVisualReadyRearm { get; set; }
        public bool TriggerKeyWasDown { get; set; }
        public DateTime? InputCandidateUtc { get; set; }
        public DateTime? InputCandidateExpiresUtc { get; set; }
        public int InputConfirmVotes { get; set; }
        public BurstObservedClass InputBaselineObservedClass { get; set; } = BurstObservedClass.Unknown;
        public double InputBaselineConfidence { get; set; }
        public double[]? InputBaselineFeatureVector { get; set; }
        public bool InputCandidateBaselineReady { get; set; }
        public bool InputCandidateNearNaturalExpiry { get; set; }
        public int ReadyVotes { get; set; }
        public int CooldownVotes { get; set; }
        public DateTime? CooldownCandidateFirstUtc { get; set; }
        public DateTime? LastUsedUtc { get; set; }
        public DateTime? TimerEndedUtc { get; set; }
        public DateTime? RapidRecastCandidateFirstUtc { get; set; }
        public int RapidRecastConfirmVotes { get; set; }
        public DateTime? ReadyWhileCoolingFirstUtc { get; set; }
        public int ReadyWhileCoolingVotes { get; set; }
        public DateTime LastGoodSampleUtc { get; set; } = DateTime.MinValue;
        public double LastClassifierConfidence { get; set; }
        public double LastClassifierScore { get; set; }
        public double LastReadyDistance { get; set; }
        public double LastCooldownDistance { get; set; }
        public double LastFeatureJumpRatio { get; set; }
        public double[]? LastFeatureVector { get; set; }
        public BurstObservedClass LastObservedClass { get; set; } = BurstObservedClass.Unknown;
        public BurstRuntimeStatus Status { get; set; } = BurstRuntimeStatus.Unassigned;

        public void Reset(BurstRuntimeStatus status)
        {
            ArmedFromReady = false;
            AwaitingVisualReadyRearm = false;
            TriggerKeyWasDown = false;
            InputCandidateUtc = null;
            InputCandidateExpiresUtc = null;
            InputConfirmVotes = 0;
            InputBaselineObservedClass = BurstObservedClass.Unknown;
            InputBaselineConfidence = 0;
            InputBaselineFeatureVector = null;
            InputCandidateBaselineReady = false;
            InputCandidateNearNaturalExpiry = false;
            ReadyVotes = 0;
            CooldownVotes = 0;
            CooldownCandidateFirstUtc = null;
            LastUsedUtc = null;
            TimerEndedUtc = null;
            RapidRecastCandidateFirstUtc = null;
            RapidRecastConfirmVotes = 0;
            ReadyWhileCoolingFirstUtc = null;
            ReadyWhileCoolingVotes = 0;
            LastClassifierConfidence = 0;
            LastClassifierScore = 0;
            LastReadyDistance = 0;
            LastCooldownDistance = 0;
            LastFeatureJumpRatio = 0;
            LastFeatureVector = null;
            LastObservedClass = BurstObservedClass.Unknown;
            Status = status;
        }
    }

    private sealed record GroupVisualLayout(Rect Bounds, Dictionary<string, Rect> VisualBoundsByCropId);

}

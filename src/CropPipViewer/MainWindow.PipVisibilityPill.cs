using System.Windows;
using System.Windows.Controls;

namespace CropPipViewer;

public partial class MainWindow
{
    private PipVisibilityPillWindow? _pipVisibilityPillWindow;
    private bool _isUpdatingPipVisibilityPillUi;
    private bool _filterKeysSuppressedByPipHide;
    private int _pipVisibilityPillSessionBaselineWidth;
    private int _pipVisibilityPillSessionBaselineHeight;
    private DateTime _lastPipVisibilityPillTopmostUtc = DateTime.MinValue;

    private void UpdatePipVisibilityPillUiFromSettings()
    {
        if (PipVisibilityPillVisibleCheck == null || PipHideTurnsOffFilterKeysCheck == null) return;

        _isUpdatingPipVisibilityPillUi = true;
        try
        {
            PipVisibilityPillVisibleCheck.IsChecked = _settings.PipVisibilityPillVisible;
            PipHideTurnsOffFilterKeysCheck.IsChecked = _settings.PipHideTurnsOffFilterKeys;
        }
        finally
        {
            _isUpdatingPipVisibilityPillUi = false;
        }

        UpdatePipVisibilityPillState();
    }

    private void PipVisibilityPillOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _isApplyingHistory || _isUpdatingPipVisibilityPillUi) return;

        var couplingChanged = ReferenceEquals(sender, PipHideTurnsOffFilterKeysCheck);
        _settings.PipVisibilityPillVisible = PipVisibilityPillVisibleCheck.IsChecked == true;
        _settings.PipHideTurnsOffFilterKeys = PipHideTurnsOffFilterKeysCheck.IsChecked == true;

        UpdatePipVisibilityPillVisibility();

        if (couplingChanged && _allPipsTemporarilyHidden)
        {
            if (_settings.PipHideTurnsOffFilterKeys && _settings.FilterKeysEnabled)
            {
                SuppressFilterKeysForPipHide();
            }
            else
            {
                ReleaseFilterKeysPipHideSuppression("PIP 숨김 연동 옵션 해제");
            }
        }

        SaveSettings();
    }

    private void UpdatePipVisibilityPillVisibility()
    {
        if (_settings.PipVisibilityPillVisible)
        {
            EnsurePipVisibilityPillWindow();
        }
        else
        {
            ClosePipVisibilityPillWindow();
        }
    }

    private void EnsurePipVisibilityPillWindow()
    {
        var left = double.IsFinite(_settings.PipVisibilityPillLeft) ? _settings.PipVisibilityPillLeft : 1060;
        var top = double.IsFinite(_settings.PipVisibilityPillTop) ? _settings.PipVisibilityPillTop : 160;

        if (_pipVisibilityPillWindow == null)
        {
            _pipVisibilityPillWindow = new PipVisibilityPillWindow
            {
                Left = left,
                Top = top,
                Topmost = true
            };
            _pipVisibilityPillWindow.ToggleRequested += PipVisibilityPill_ToggleRequested;
            _pipVisibilityPillWindow.PillMoved += PipVisibilityPill_Moved;
            _pipVisibilityPillWindow.PresetMenuRequested += PipVisibilityPill_PresetMenuRequested;
            _pipVisibilityPillWindow.PresetRequested += PipVisibilityPill_PresetRequested;
        }
        else
        {
            _pipVisibilityPillWindow.Left = left;
            _pipVisibilityPillWindow.Top = top;
        }

        ClampPipVisibilityPillToVirtualDesktop();

        if (!_pipVisibilityPillWindow.IsVisible) _pipVisibilityPillWindow.Show();
        _pipVisibilityPillWindow.EnforceFixedSize();
        _pipVisibilityPillWindow.Topmost = true;
        _pipVisibilityPillWindow.SetPipsHidden(_allPipsTemporarilyHidden);
        ReapplyPipVisibilityPillTopMost(force: true);

        if (_settings.PipPositionLockedToTarget)
        {
            if (_settings.PipVisibilityPillTargetOffsetX is null || _settings.PipVisibilityPillTargetOffsetY is null)
            {
                CapturePipVisibilityPillRelativePosition(save: false);
            }
            else
            {
                ApplyPipVisibilityPillTargetLockedPosition();
            }
        }
    }

    private void ClosePipVisibilityPillWindow()
    {
        if (_pipVisibilityPillWindow == null) return;
        _pipVisibilityPillWindow.ToggleRequested -= PipVisibilityPill_ToggleRequested;
        _pipVisibilityPillWindow.PillMoved -= PipVisibilityPill_Moved;
        _pipVisibilityPillWindow.PresetMenuRequested -= PipVisibilityPill_PresetMenuRequested;
        _pipVisibilityPillWindow.PresetRequested -= PipVisibilityPill_PresetRequested;
        _pipVisibilityPillWindow.ForceClose();
        _pipVisibilityPillWindow = null;
    }

    private void PipVisibilityPill_ToggleRequested()
    {
        SetAllPipsTemporarilyHidden(!_allPipsTemporarilyHidden, "PIP 표시/숨김 알약");
    }

    private void PipVisibilityPill_PresetMenuRequested(PipVisibilityPillWindow pill)
    {
        var activeIndex = Math.Clamp(_settings.ActivePresetIndex, 0, PresetSlotCount - 1);
        var items = _settings.Presets
            .Select((preset, index) =>
            {
                var name = string.IsNullOrWhiteSpace(preset.Name) ? $"프리셋 {index + 1}" : preset.Name.Trim();
                return (Index: index, Label: $"P{index + 1} · {name}", IsCurrent: index == activeIndex);
            })
            .Take(PresetSlotCount)
            .ToList();

        pill.ShowPresetMenu(items);
        ReapplyPipVisibilityPillTopMost(force: true);
    }

    private void PipVisibilityPill_PresetRequested(int presetIndex)
    {
        presetIndex = Math.Clamp(presetIndex, 0, PresetSlotCount - 1);
        if (presetIndex == _settings.ActivePresetIndex)
        {
            StatusText.Text = $"현재 프리셋: P{presetIndex + 1} · {_settings.Presets[presetIndex].Name}";
            ReapplyPipVisibilityPillTopMost(force: true);
            return;
        }

        SettingsService.Log($"pip_visibility_pill_preset_request | from={_settings.ActivePresetIndex + 1} | to={presetIndex + 1}");
        SwitchPreset(presetIndex);

        // SwitchPreset may close this pill if the destination preset has the pill disabled.
        // If it remains enabled, refresh its visual/topmost state after the switch.
        if (_pipVisibilityPillWindow != null)
        {
            _pipVisibilityPillWindow.SetPipsHidden(_allPipsTemporarilyHidden);
            ReapplyPipVisibilityPillTopMost(force: true);
        }
    }

    private void PipVisibilityPill_Moved(PipVisibilityPillWindow pill)
    {
        if (_isInitializing || _isApplyingHistory) return;
        _settings.PipVisibilityPillLeft = Math.Round(pill.Left, 2);
        _settings.PipVisibilityPillTop = Math.Round(pill.Top, 2);
        if (_settings.PipPositionLockedToTarget)
        {
            CapturePipVisibilityPillRelativePosition(save: false);
        }
        SaveSettingsThrottled();
    }

    private void UpdatePipVisibilityPillState()
    {
        _pipVisibilityPillWindow?.SetPipsHidden(_allPipsTemporarilyHidden);

        if (ToggleOverlayButton != null)
        {
            ToggleOverlayButton.Content = _allPipsTemporarilyHidden ? "PiP 표시" : "PiP 숨김";
            ToggleOverlayButton.ToolTip = _allPipsTemporarilyHidden
                ? "현재 PIP가 숨겨져 있습니다. 클릭하면 PIP 알약과 동일하게 전체 PIP를 표시합니다."
                : "현재 PIP가 표시 중입니다. 클릭하면 PIP 알약과 동일하게 전체 PIP를 숨깁니다.";
        }
    }

    private void TickPipVisibilityPillRuntime()
    {
        if (_pipVisibilityPillWindow == null || !_pipVisibilityPillWindow.IsVisible) return;

        if (_settings.PipPositionLockedToTarget)
        {
            ApplyPipVisibilityPillTargetLockedPosition();
        }

        ReapplyPipVisibilityPillTopMost(force: false);
    }

    private void CapturePipVisibilityPillRelativePosition(bool save)
    {
        if (_pipVisibilityPillWindow == null) return;
        if (!TryGetTargetClientRectForMain(out var rect)) return;

        _pipVisibilityPillSessionBaselineWidth = rect.Width;
        _pipVisibilityPillSessionBaselineHeight = rect.Height;
        _settings.PipVisibilityPillTargetBaselineWidth = rect.Width;
        _settings.PipVisibilityPillTargetBaselineHeight = rect.Height;
        _settings.PipVisibilityPillTargetOffsetX = Math.Round(_pipVisibilityPillWindow.Left - rect.X, 2);
        _settings.PipVisibilityPillTargetOffsetY = Math.Round(_pipVisibilityPillWindow.Top - rect.Y, 2);

        if (save) SaveSettings();
    }

    private bool IsTargetRectUsableForPipVisibilityPillLock(RectI rect)
    {
        if (_pipVisibilityPillSessionBaselineWidth <= 0 || _pipVisibilityPillSessionBaselineHeight <= 0)
        {
            _pipVisibilityPillSessionBaselineWidth = rect.Width;
            _pipVisibilityPillSessionBaselineHeight = rect.Height;
        }

        if (rect.Width > _pipVisibilityPillSessionBaselineWidth) _pipVisibilityPillSessionBaselineWidth = rect.Width;
        if (rect.Height > _pipVisibilityPillSessionBaselineHeight) _pipVisibilityPillSessionBaselineHeight = rect.Height;
        _settings.PipVisibilityPillTargetBaselineWidth = _pipVisibilityPillSessionBaselineWidth;
        _settings.PipVisibilityPillTargetBaselineHeight = _pipVisibilityPillSessionBaselineHeight;
        return rect.Width > 0 && rect.Height > 0;
    }

    private void ApplyPipVisibilityPillTargetLockedPosition()
    {
        if (!_settings.PipPositionLockedToTarget) return;
        if (_pipVisibilityPillWindow == null || !_pipVisibilityPillWindow.IsVisible) return;

        if (_settings.PipVisibilityPillTargetOffsetX is null || _settings.PipVisibilityPillTargetOffsetY is null)
        {
            CapturePipVisibilityPillRelativePosition(save: false);
            return;
        }

        if (!TryGetTargetClientRectForMain(out var rect)) return;
        if (!IsTargetRectUsableForPipVisibilityPillLock(rect)) return;

        var pillWidth = Math.Max(1, _pipVisibilityPillWindow.ActualWidth > 1 ? _pipVisibilityPillWindow.ActualWidth : _pipVisibilityPillWindow.Width);
        var pillHeight = Math.Max(1, _pipVisibilityPillWindow.ActualHeight > 1 ? _pipVisibilityPillWindow.ActualHeight : _pipVisibilityPillWindow.Height);
        var desiredLeft = Math.Round(rect.X + _settings.PipVisibilityPillTargetOffsetX.Value, 2);
        var desiredTop = Math.Round(rect.Y + _settings.PipVisibilityPillTargetOffsetY.Value, 2);

        // Keep the control pill reachable even after MapleStory resolution/client-size changes.
        desiredLeft = Math.Round(ClampToRange(desiredLeft, rect.X, rect.X + Math.Max(0, rect.Width - pillWidth)), 2);
        desiredTop = Math.Round(ClampToRange(desiredTop, rect.Y, rect.Y + Math.Max(0, rect.Height - pillHeight)), 2);

        if (Math.Abs(_pipVisibilityPillWindow.Left - desiredLeft) < 0.5 &&
            Math.Abs(_pipVisibilityPillWindow.Top - desiredTop) < 0.5)
        {
            ReapplyPipVisibilityPillTopMost(force: false);
            return;
        }

        _pipVisibilityPillWindow.Left = desiredLeft;
        _pipVisibilityPillWindow.Top = desiredTop;
        _settings.PipVisibilityPillLeft = desiredLeft;
        _settings.PipVisibilityPillTop = desiredTop;
        ReapplyPipVisibilityPillTopMost(force: true);
    }

    private void ReapplyPipVisibilityPillTopMost(bool force)
    {
        if (_pipVisibilityPillWindow == null || !_pipVisibilityPillWindow.IsVisible) return;

        var now = DateTime.UtcNow;
        // Keep the same TopMost re-assert cadence as the FilterKeys pill.
        // Both are called from the same 350 ms runtime timer, so using the same
        // 120 ms throttle makes their focus-transition response effectively identical.
        if (!force && (now - _lastPipVisibilityPillTopmostUtc).TotalMilliseconds < 120) return;

        _lastPipVisibilityPillTopmostUtc = now;
        _pipVisibilityPillWindow.ForceTopMost();
    }

    private void ClampPipVisibilityPillToVirtualDesktop()
    {
        if (_pipVisibilityPillWindow == null) return;

        var width = PipVisibilityPillWindow.FixedWidth;
        var height = PipVisibilityPillWindow.FixedHeight;
        var minLeft = SystemParameters.VirtualScreenLeft;
        var minTop = SystemParameters.VirtualScreenTop;
        var maxLeft = minLeft + Math.Max(0, SystemParameters.VirtualScreenWidth - width);
        var maxTop = minTop + Math.Max(0, SystemParameters.VirtualScreenHeight - height);

        var clampedLeft = Math.Round(Math.Clamp(_pipVisibilityPillWindow.Left, minLeft, maxLeft), 2);
        var clampedTop = Math.Round(Math.Clamp(_pipVisibilityPillWindow.Top, minTop, maxTop), 2);

        _pipVisibilityPillWindow.Left = clampedLeft;
        _pipVisibilityPillWindow.Top = clampedTop;
        _settings.PipVisibilityPillLeft = clampedLeft;
        _settings.PipVisibilityPillTop = clampedTop;
    }

    private void SetAllPipsTemporarilyHidden(bool hidden, string reason)
    {
        EnsureOverlay();

        if (hidden)
        {
            _allPipsTemporarilyHidden = true;
            foreach (var overlay in GetAllOverlays().ToList())
            {
                overlay.Hide();
            }
            _burstMonitorWindow?.Hide();

            if (_settings.PipHideTurnsOffFilterKeys && _settings.FilterKeysEnabled)
            {
                SuppressFilterKeysForPipHide();
            }

            StatusText.Text = "모든 PiP 숨김";
        }
        else
        {
            _allPipsTemporarilyHidden = false;

            foreach (var overlay in GetAllOverlays().ToList())
            {
                if (!overlay.IsVisible) overlay.Show();
                if (_target != null && _capture != null) overlay.SetCapture(_capture, _target.Hwnd);
            }

            if (_settings.BurstMonitorVisible)
            {
                EnsureBurstMonitorWindow();
                UpdateBurstMonitorPipRows();
            }

            ReleaseFilterKeysPipHideSuppression("PIP 다시 표시");
            StatusText.Text = "모든 PiP 표시";
        }

        UpdatePipVisibilityPillState();
        ReapplyPipVisibilityPillTopMost(force: true);

        // Respect the independent "메이플 활성 시만 PIP 표시" rule after a manual visibility change.
        ApplyPipFocusVisibilityState();
        SettingsService.Log($"pip_visibility_toggle | hidden={hidden} | reason={reason} | filter_suppressed={_filterKeysSuppressedByPipHide}");
    }

    private void SuppressFilterKeysForPipHide()
    {
        if (_filterKeysSuppressedByPipHide || !_settings.FilterKeysEnabled) return;

        _filterKeysSuppressedByPipHide = true;
        TryTurnOffFilterKeys("PIP 숨김 연동", force: true);
        UpdateFilterKeysRuntime(applyWhenNeeded: false);
        SettingsService.Log("filterkeys_pip_hide_suppressed | configured_enabled=true");
    }

    private void ReleaseFilterKeysPipHideSuppression(string reason)
    {
        if (!_filterKeysSuppressedByPipHide) return;

        _filterKeysSuppressedByPipHide = false;
        UpdateFilterKeysRuntime(applyWhenNeeded: true);
        SettingsService.Log($"filterkeys_pip_hide_restored | configured_enabled={_settings.FilterKeysEnabled} | reason={reason}");
    }

    private void RefreshFilterKeysPipHideSuppressionForCurrentPreset()
    {
        if (_allPipsTemporarilyHidden &&
            _settings.PipHideTurnsOffFilterKeys &&
            _settings.FilterKeysEnabled)
        {
            SuppressFilterKeysForPipHide();
        }
        else
        {
            ReleaseFilterKeysPipHideSuppression("프리셋 전환/상태 동기화");
        }
    }
}

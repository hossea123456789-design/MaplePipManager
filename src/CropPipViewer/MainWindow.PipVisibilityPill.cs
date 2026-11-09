using System.Windows;
using System.Windows.Controls;

namespace CropPipViewer;

public partial class MainWindow
{
    private PipVisibilityPillWindow? _pipVisibilityPillWindow;
    private bool _isUpdatingPipVisibilityPillUi;
    private bool _filterKeysSuppressedByPipHide;
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
    }

    private void ClosePipVisibilityPillWindow()
    {
        if (_pipVisibilityPillWindow == null) return;
        _pipVisibilityPillWindow.ToggleRequested -= PipVisibilityPill_ToggleRequested;
        _pipVisibilityPillWindow.PillMoved -= PipVisibilityPill_Moved;
        _pipVisibilityPillWindow.ForceClose();
        _pipVisibilityPillWindow = null;
    }

    private void PipVisibilityPill_ToggleRequested()
    {
        SetAllPipsTemporarilyHidden(!_allPipsTemporarilyHidden, "PIP 표시/숨김 알약");
    }

    private void PipVisibilityPill_Moved(PipVisibilityPillWindow pill)
    {
        if (_isInitializing || _isApplyingHistory) return;
        _settings.PipVisibilityPillLeft = Math.Round(pill.Left, 2);
        _settings.PipVisibilityPillTop = Math.Round(pill.Top, 2);
        SaveSettingsThrottled();
    }

    private void UpdatePipVisibilityPillState()
    {
        _pipVisibilityPillWindow?.SetPipsHidden(_allPipsTemporarilyHidden);
    }

    private void TickPipVisibilityPillRuntime()
    {
        if (_pipVisibilityPillWindow == null || !_pipVisibilityPillWindow.IsVisible) return;
        ReapplyPipVisibilityPillTopMost(force: false);
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

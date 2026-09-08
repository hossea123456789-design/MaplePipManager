using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace CropPipViewer;

public partial class MainWindow
{
    // Global UI preference (not preset-specific): when enabled, combat PiPs are only
    // visible while the selected MapleStory process or this manager process is foreground.
    private readonly DispatcherTimer _pipFocusVisibilityTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(100)
    };

    private readonly HashSet<string> _pipFocusAutoHiddenPipKeys = new(StringComparer.Ordinal);
    private CheckBox? _pipFocusVisibilityCheck;
    private bool _pipFocusVisibilityInitialized;
    private bool _pipFocusVisibilityEnabled;
    private bool _pipFocusCurrentlyAutoHidden;
    private bool _pipFocusAutoHidBurstMonitor;
    private bool _pipFocusAutoHidFilterKeysPill;

    private static string PipFocusVisibilityPreferencePath =>
        Path.Combine(SettingsService.AppDir, "pip-focus-visibility.txt");

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        InitializePipFocusVisibilityFeature();
    }

    private void InitializePipFocusVisibilityFeature()
    {
        if (_pipFocusVisibilityInitialized) return;
        _pipFocusVisibilityInitialized = true;

        _pipFocusVisibilityEnabled = LoadPipFocusVisibilityPreference();
        AddPipFocusVisibilityOptionToUi();

        _pipFocusVisibilityTimer.Tick += PipFocusVisibilityTimer_Tick;
        _pipFocusVisibilityTimer.Start();
        Closed += (_, _) => _pipFocusVisibilityTimer.Stop();

        ApplyPipFocusVisibilityState();
    }

    private void AddPipFocusVisibilityOptionToUi()
    {
        if (_pipFocusVisibilityCheck != null) return;
        if (TopMostCheck?.Parent is not Panel optionPanel) return;

        _pipFocusVisibilityCheck = new CheckBox
        {
            Content = "메이플 활성 시만 PIP 표시",
            IsChecked = _pipFocusVisibilityEnabled,
            Style = FindResource("OptionCheck") as Style,
            ToolTip = "메이플스토리 또는 Maple PiP Manager가 활성화되어 있을 때만 PIP를 표시합니다.\n" +
                      "Chrome/Discord 등 다른 프로그램으로 전환하면 자동으로 숨깁니다.\n" +
                      "'항상 위' 옵션과 함께 사용하면 메이플에서는 위에 표시되고 다른 앱에서는 보이지 않습니다."
        };

        _pipFocusVisibilityCheck.Checked += PipFocusVisibilityCheck_Changed;
        _pipFocusVisibilityCheck.Unchecked += PipFocusVisibilityCheck_Changed;
        optionPanel.Children.Add(_pipFocusVisibilityCheck);
    }

    private void PipFocusVisibilityCheck_Changed(object sender, RoutedEventArgs e)
    {
        _pipFocusVisibilityEnabled = _pipFocusVisibilityCheck?.IsChecked == true;
        SavePipFocusVisibilityPreference(_pipFocusVisibilityEnabled);

        if (_pipFocusVisibilityEnabled)
        {
            ApplyPipFocusVisibilityState();
            if (StatusText != null) StatusText.Text = "메이플 활성 시만 PIP 표시: ON";
        }
        else
        {
            RestorePipsHiddenByFocusRule();
            if (StatusText != null) StatusText.Text = "메이플 활성 시만 PIP 표시: OFF";
        }

        SettingsService.Log($"pip_focus_visibility | enabled={_pipFocusVisibilityEnabled}");
    }

    private void PipFocusVisibilityTimer_Tick(object? sender, EventArgs e)
    {
        ApplyPipFocusVisibilityState();
    }

    private void ApplyPipFocusVisibilityState()
    {
        if (!_pipFocusVisibilityEnabled)
        {
            RestorePipsHiddenByFocusRule();
            return;
        }

        if (IsPipFocusVisibilityContextAllowed())
        {
            RestorePipsHiddenByFocusRule();
        }
        else
        {
            HidePipsForFocusRule();
        }
    }

    private bool IsPipFocusVisibilityContextAllowed()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;

        NativeMethods.GetWindowThreadProcessId(foreground, out var foregroundPid);

        // Keep PiPs visible while the user edits the manager or directly interacts with a PiP.
        if (foregroundPid == (uint)Environment.ProcessId) return true;

        // Use the selected target process only. Do not fall back to window-title matching here:
        // a browser tab containing words such as "메이플" must not accidentally keep PiPs visible.
        if (_target == null || _target.ProcessId <= 0) return false;
        return foregroundPid == (uint)_target.ProcessId;
    }

    private void HidePipsForFocusRule()
    {
        // Already hidden for this focus transition. Avoid repeated Hide() calls every 100 ms.
        if (_pipFocusCurrentlyAutoHidden) return;

        _pipFocusAutoHiddenPipKeys.Clear();
        foreach (var overlay in GetAllOverlays().ToList())
        {
            if (!overlay.IsVisible) continue;
            _pipFocusAutoHiddenPipKeys.Add(GetPipKey(overlay));
            overlay.Hide();
        }

        _pipFocusAutoHidBurstMonitor = _burstMonitorWindow?.IsVisible == true;
        if (_pipFocusAutoHidBurstMonitor)
        {
            _burstMonitorWindow!.Hide();
        }

        _pipFocusAutoHidFilterKeysPill = _filterKeysPillWindow?.IsVisible == true;
        if (_pipFocusAutoHidFilterKeysPill)
        {
            _filterKeysPillWindow!.Hide();
        }

        _pipFocusCurrentlyAutoHidden = true;
    }

    private void RestorePipsHiddenByFocusRule()
    {
        if (!_pipFocusCurrentlyAutoHidden &&
            _pipFocusAutoHiddenPipKeys.Count == 0 &&
            !_pipFocusAutoHidBurstMonitor &&
            !_pipFocusAutoHidFilterKeysPill)
        {
            return;
        }

        foreach (var key in _pipFocusAutoHiddenPipKeys.ToList())
        {
            var overlay = GetOverlayByPipKey(key);
            if (overlay == null || overlay.IsVisible) continue;

            try
            {
                overlay.Show();
                overlay.Topmost = _settings.TopMost;
            }
            catch (InvalidOperationException)
            {
                // The PiP may have been permanently closed/recreated while hidden.
                // In that case its current runtime state takes precedence.
            }
        }
        _pipFocusAutoHiddenPipKeys.Clear();

        if (_pipFocusAutoHidBurstMonitor)
        {
            if (_burstMonitorWindow == null && _settings.BurstMonitorVisible)
            {
                UpdateBurstMonitorVisibility();
            }

            if (_burstMonitorWindow != null &&
                _settings.BurstMonitorVisible &&
                !_burstMonitorWindow.IsVisible)
            {
                try
                {
                    _burstMonitorWindow.Show();
                    _burstMonitorWindow.ForceTopMost();
                }
                catch (InvalidOperationException)
                {
                }
            }
        }
        _pipFocusAutoHidBurstMonitor = false;

        if (_pipFocusAutoHidFilterKeysPill)
        {
            if (_filterKeysPillWindow == null && _settings.FilterKeysPillVisible)
            {
                UpdateFilterKeysPillVisibility();
            }

            if (_filterKeysPillWindow != null &&
                _settings.FilterKeysPillVisible &&
                !_filterKeysPillWindow.IsVisible)
            {
                try
                {
                    _filterKeysPillWindow.Show();
                    _filterKeysPillWindow.ForceTopMost();
                }
                catch (InvalidOperationException)
                {
                }
            }
        }
        _pipFocusAutoHidFilterKeysPill = false;
        _pipFocusCurrentlyAutoHidden = false;
    }

    private static bool LoadPipFocusVisibilityPreference()
    {
        try
        {
            if (!File.Exists(PipFocusVisibilityPreferencePath)) return false;
            var text = File.ReadAllText(PipFocusVisibilityPreferencePath).Trim();
            if (text == "1") return true;
            if (text == "0") return false;
            return bool.TryParse(text, out var enabled) && enabled;
        }
        catch
        {
            return false;
        }
    }

    private static void SavePipFocusVisibilityPreference(bool enabled)
    {
        try
        {
            Directory.CreateDirectory(SettingsService.AppDir);
            File.WriteAllText(PipFocusVisibilityPreferencePath, enabled ? "1" : "0");
        }
        catch
        {
            // Keep the viewer non-blocking if AppData is temporarily unavailable.
        }
    }
}

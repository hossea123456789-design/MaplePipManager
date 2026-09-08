using System.Windows;

namespace CropPipViewer;

public partial class MainWindow
{
    private bool _removedResizeLinkInitialized;

    // v0.9.1: "창 크기 연동" is intentionally retired.
    // It multiplied DisplayWidth/DisplayHeight when the PiP window was resized. During preset
    // switches the window bounds are restored programmatically, so the same scaling path could
    // mutate freshly loaded preset crop sizes and accumulate distortion across switches.
    // Keep the serialized legacy property only for backward-compatible settings loading, but
    // force it OFF and hide its UI so old settings cannot re-enable the behavior normally.
    private void InitializeRemovedResizeLinkFeature()
    {
        if (_removedResizeLinkInitialized) return;
        _removedResizeLinkInitialized = true;

        _settings.ResizeItemsWithWindow = false;
        if (_settings.Presets != null)
        {
            foreach (var preset in _settings.Presets)
            {
                preset.ResizeItemsWithWindow = false;
            }
        }

        if (ResizeItemsWithWindowCheck != null)
        {
            ResizeItemsWithWindowCheck.IsChecked = false;
            ResizeItemsWithWindowCheck.Visibility = Visibility.Collapsed;
        }

        foreach (var overlay in GetAllOverlays().ToList())
        {
            overlay.SetResizeItemsWithWindow(false);
        }

        SettingsService.Save(_settings);
        SettingsService.Log("legacy_resize_link_disabled | reason=preset_size_distortion");
    }
}

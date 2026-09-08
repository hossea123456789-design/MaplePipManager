using System.Windows.Threading;

namespace CropPipViewer;

public partial class MainWindow
{
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Run after WPF has created the native window and named controls.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            InitializeRemovedResizeLinkFeature();
            InitializeUpdateFeature();
        }), DispatcherPriority.Background);
    }
}

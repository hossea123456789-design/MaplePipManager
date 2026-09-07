using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using D3D11Device = SharpDX.Direct3D11.Device;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;
using WinRT;

namespace CropPipViewer;

public sealed class WgcCaptureManager : IDisposable
{
    private D3D11Device? _d3dDevice;
    private IDirect3DDevice? _winRtDevice;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private BitmapSource? _latestFrame;
    private readonly object _gate = new();
    private int _converting;

    public string SourceName { get; private set; } = "미선택";
    public int SourceWidth { get; private set; }
    public int SourceHeight { get; private set; }
    public bool IsCapturing => _session is not null;
    public IntPtr TargetHwnd { get; private set; } = IntPtr.Zero;

    public event EventHandler? FrameUpdated;

    public async Task<bool> PickAndStartAsync(Window owner)
    {
        var picker = new GraphicsCapturePicker();
        var hwnd = new WindowInteropHelper(owner).Handle;
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var item = await picker.PickSingleItemAsync();
        if (item is null) return false;

        TargetHwnd = IntPtr.Zero;
        Start(item);
        return true;
    }

    public bool StartForWindow(IntPtr hwnd, string? sourceName = null)
    {
        if (hwnd == IntPtr.Zero) return false;

        var item = CreateItemForWindow(hwnd);
        if (item is null) return false;

        Start(item);
        TargetHwnd = hwnd;
        if (!string.IsNullOrWhiteSpace(sourceName))
            SourceName = sourceName;
        return true;
    }

    private static GraphicsCaptureItem? CreateItemForWindow(IntPtr hwnd)
    {
        var factory = WinRT.ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");

        // CsWinRT 2.x returns an ObjectReference from As<T>(); that object does not expose
        // COM methods directly. Convert the activation factory pointer to the COM interop
        // interface and call CreateForWindow through that interface instead.
        var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factory.ThisPtr);

        var iid = GraphicsCaptureItemGuid;
        var itemPtr = interop.CreateForWindow(hwnd, ref iid);
        if (itemPtr == IntPtr.Zero) return null;

        try
        {
            return GraphicsCaptureItem.FromAbi(itemPtr);
        }
        finally
        {
            Marshal.Release(itemPtr);
        }
    }

    private static readonly Guid GraphicsCaptureItemGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, ref Guid iid);
        IntPtr CreateForMonitor(IntPtr monitor, ref Guid iid);
    }

    private void Start(GraphicsCaptureItem item)
    {
        DisposeCaptureOnly();

        _item = item;
        SourceName = string.IsNullOrWhiteSpace(item.DisplayName) ? "선택된 캡처 대상" : item.DisplayName;
        SourceWidth = Math.Max(1, item.Size.Width);
        SourceHeight = Math.Max(1, item.Size.Height);

        _d3dDevice = new D3D11Device(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
        _winRtDevice = CreateDirect3DDevice(_d3dDevice);

        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _winRtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            item.Size);

        _framePool.FrameArrived += OnFrameArrived;
        _session = _framePool.CreateCaptureSession(item);
        try { _session.IsCursorCaptureEnabled = false; } catch { }
        _session.StartCapture();
    }

    private async void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (Interlocked.Exchange(ref _converting, 1) == 1)
        {
            try { using var skipped = sender.TryGetNextFrame(); } catch { }
            return;
        }

        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null) return;

            var contentSize = frame.ContentSize;
            if (contentSize.Width != SourceWidth || contentSize.Height != SourceHeight)
            {
                SourceWidth = Math.Max(1, contentSize.Width);
                SourceHeight = Math.Max(1, contentSize.Height);
                try { _framePool?.Recreate(_winRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, contentSize); } catch { }
            }

            using var softwareBitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface);
            using var converted = SoftwareBitmap.Convert(softwareBitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var source = SoftwareBitmapToBitmapSource(converted);
            source.Freeze();

            lock (_gate)
            {
                _latestFrame = source;
            }
            FrameUpdated?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // The captured window may be resizing/minimized. Keep the process alive.
        }
        finally
        {
            Interlocked.Exchange(ref _converting, 0);
        }
    }

    public BitmapSource? GetLatestFrame()
    {
        lock (_gate)
            return _latestFrame;
    }

    public BitmapSource? GetLatestCrop(Int32Rect crop)
    {
        BitmapSource? frame;
        lock (_gate) frame = _latestFrame;
        if (frame is null) return null;

        var safe = Clamp(crop, frame.PixelWidth, frame.PixelHeight);
        if (safe.Width <= 0 || safe.Height <= 0) return null;

        var cropped = new CroppedBitmap(frame, safe);
        cropped.Freeze();
        return cropped;
    }

    private static Int32Rect Clamp(Int32Rect r, int maxW, int maxH)
    {
        var x = Math.Clamp(r.X, 0, Math.Max(0, maxW - 1));
        var y = Math.Clamp(r.Y, 0, Math.Max(0, maxH - 1));
        var w = Math.Clamp(r.Width, 1, maxW - x);
        var h = Math.Clamp(r.Height, 1, maxH - y);
        return new Int32Rect(x, y, w, h);
    }

    private static BitmapSource SoftwareBitmapToBitmapSource(SoftwareBitmap bitmap)
    {
        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        var buffer = new Windows.Storage.Streams.Buffer((uint)(width * height * 4));
        bitmap.CopyToBuffer(buffer);
        CryptographicBuffer.CopyToByteArray(buffer, out byte[] bytes);

        return BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            bytes,
            width * 4);
    }

    private static IDirect3DDevice CreateDirect3DDevice(D3D11Device d3dDevice)
    {
        using var dxgiDevice = d3dDevice.QueryInterface<SharpDX.DXGI.Device>();
        var hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var pUnknown);
        if (hr != 0)
            Marshal.ThrowExceptionForHR(hr);

        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(pUnknown);
        }
        finally
        {
            Marshal.Release(pUnknown);
        }
    }

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    private void DisposeCaptureOnly()
    {
        try { _session?.Dispose(); } catch { }
        try { _framePool?.Dispose(); } catch { }
        _session = null;
        _framePool = null;
        _item = null;
        TargetHwnd = IntPtr.Zero;
        lock (_gate) _latestFrame = null;
    }

    public void Dispose()
    {
        DisposeCaptureOnly();
        try { _d3dDevice?.Dispose(); } catch { }
        _d3dDevice = null;
        _winRtDevice = null;
    }
}

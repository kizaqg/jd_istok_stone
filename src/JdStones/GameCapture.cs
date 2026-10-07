using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using ApiInformation = Windows.Foundation.Metadata.ApiInformation;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using WinRT;

namespace JdStones;

/// <summary>
/// Фоновый режим: Windows Graphics Capture — картинка окна игры, даже когда оно закрыто
/// другими окнами (но не свёрнуто). Игру не трогает и не заставляет перерисовываться.
/// </summary>
internal sealed class WgcGameCapture : IDisposable
{
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    private readonly GameWindow _window;
    private readonly object _lock = new();
    private readonly IDirect3DDevice _device;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private SizeInt32 _poolSize;
    private Bitmap? _last;
    private bool _disposed;

    public IntPtr WindowHandle => _window.Handle;

    public WgcGameCapture(GameWindow window)
    {
        if (!GraphicsCaptureSession.IsSupported())
            throw new NotSupportedException("Фоновая съёмка окна не поддерживается этой версией Windows.");

        _window = window;
        _device = Direct3D.CreateDevice();
        _item = CreateItemForWindow(window.Handle);
        _poolSize = _item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
        _session = _pool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = false;
        TryDisableBorder(_session);
        _session.StartCapture();
    }

    public Bitmap Capture(Rectangle clientRegion)
    {
        if (!_window.IsAlive) throw new InvalidOperationException("Окно игры закрыто.");
        if (Native.IsIconic(_window.Handle))
            throw new InvalidOperationException("Окно игры свернуто. В фоновом режиме его можно закрыть другими окнами, но не сворачивать.");

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RefreshFrame();
            if (_last == null) throw new InvalidOperationException("Не удалось получить изображение окна игры.");

            // Кадр — это окно целиком (с рамкой). Сдвигаем область на положение клиентской части.
            var frameOrigin = FrameOriginOnScreen();
            var client = _window.ClientScreenRect();
            var rect = new Rectangle(client.X - frameOrigin.X + clientRegion.X, client.Y - frameOrigin.Y + clientRegion.Y,
                clientRegion.Width, clientRegion.Height);
            rect.Intersect(new Rectangle(0, 0, _last.Width, _last.Height));
            if (rect.Width <= 0 || rect.Height <= 0)
                throw new InvalidOperationException("Выбранная область за пределами окна игры. Выберите её заново.");
            return _last.Clone(rect, PixelFormat.Format32bppRgb);
        }
    }

    /// <summary>Забирает самый свежий кадр. Если окно не менялось, новых кадров нет — остаётся прежний.</summary>
    private void RefreshFrame()
    {
        Direct3D11CaptureFrame? latest = null;
        // Сразу после старта первый кадр приходит не мгновенно.
        var deadline = Environment.TickCount64 + (_last == null ? 1000 : 0);
        while (true)
        {
            Direct3D11CaptureFrame? frame;
            while ((frame = _pool.TryGetNextFrame()) != null)
            {
                latest?.Dispose();
                latest = frame;
            }
            if (latest != null || Environment.TickCount64 >= deadline) break;
            Thread.Sleep(15);
        }
        if (latest == null) return;

        using (latest)
        {
            var size = latest.ContentSize;
            // Асинхронную операцию WinRT ждём в фоновом потоке: так не будет взаимной блокировки,
            // если снимок запрошен из потока интерфейса.
            var surface = latest.Surface;
            using var software = Task.Run(() => SoftwareBitmap.CreateCopyFromSurfaceAsync(surface, BitmapAlphaMode.Premultiplied).AsTask())
                .GetAwaiter().GetResult();
            var width = Math.Min(size.Width, software.PixelWidth);
            var height = Math.Min(size.Height, software.PixelHeight);
            _last?.Dispose();
            _last = ToBitmap(software, width, height);

            if (size.Width != _poolSize.Width || size.Height != _poolSize.Height)
            {
                // Окно игры изменило размер — пересоздаём буферы под новый размер.
                _poolSize = size;
                _pool.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
            }
        }
    }

    private Point FrameOriginOnScreen()
    {
        // Кадр совпадает с видимыми границами окна (без невидимой «тени» Windows 10/11).
        if (Native.DwmGetWindowAttribute(_window.Handle, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out var r,
                Marshal.SizeOf<Native.RECT>()) == 0)
            return new Point(r.Left, r.Top);
        Native.GetWindowRect(_window.Handle, out var wr);
        return new Point(wr.Left, wr.Top);
    }

    private static Bitmap ToBitmap(SoftwareBitmap software, int width, int height)
    {
        var stride = software.PixelWidth * 4;
        var bytes = new byte[stride * software.PixelHeight];
        software.CopyToBuffer(bytes.AsBuffer());
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        var data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            for (var y = 0; y < height; y++)
                Marshal.Copy(bytes, y * stride, data.Scan0 + y * data.Stride, width * 4);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return bmp;
    }

    private static void TryDisableBorder(GraphicsCaptureSession session)
    {
        // Windows 11: убираем жёлтую рамку вокруг окна игры.
        try
        {
            if (!ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired")) return;
            var request = Task.Run(() => GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless).AsTask());
            if (!request.Wait(2000)) return;
            session.IsBorderRequired = false;
        }
        catch
        {
            // Рамка останется — на работу не влияет.
        }
    }

    private static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = GraphicsCaptureItemIid;
        var ptr = interop.CreateForWindow(hwnd, ref iid);
        try
        {
            return GraphicsCaptureItem.FromAbi(ptr);
        }
        finally
        {
            Marshal.Release(ptr);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _session.Dispose();
            _pool.Dispose();
            _last?.Dispose();
            _device.Dispose();
        }
    }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [ComVisible(true)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    private static class Direct3D
    {
        private static readonly Guid DxgiDeviceIid = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
        private const int DriverTypeHardware = 1;
        private const int DriverTypeWarp = 5;
        private const uint CreateDeviceBgraSupport = 0x20;
        private const uint SdkVersion = 7;

        [DllImport("d3d11.dll", ExactSpelling = true)]
        private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
            IntPtr featureLevels, uint featureLevelCount, uint sdkVersion, out IntPtr device, out int featureLevel,
            out IntPtr immediateContext);

        [DllImport("d3d11.dll", ExactSpelling = true)]
        private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

        public static IDirect3DDevice CreateDevice()
        {
            var hr = D3D11CreateDevice(IntPtr.Zero, DriverTypeHardware, IntPtr.Zero, CreateDeviceBgraSupport, IntPtr.Zero, 0,
                SdkVersion, out var device, out _, out var context);
            if (hr < 0)
                hr = D3D11CreateDevice(IntPtr.Zero, DriverTypeWarp, IntPtr.Zero, CreateDeviceBgraSupport, IntPtr.Zero, 0,
                    SdkVersion, out device, out _, out context);
            Marshal.ThrowExceptionForHR(hr);
            try
            {
                var iid = DxgiDeviceIid;
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, ref iid, out var dxgi));
                try
                {
                    Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable));
                    try
                    {
                        return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
                    }
                    finally
                    {
                        Marshal.Release(inspectable);
                    }
                }
                finally
                {
                    Marshal.Release(dxgi);
                }
            }
            finally
            {
                if (context != IntPtr.Zero) Marshal.Release(context);
                if (device != IntPtr.Zero) Marshal.Release(device);
            }
        }
    }
}

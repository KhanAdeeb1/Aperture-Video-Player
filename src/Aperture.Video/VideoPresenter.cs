using System.Numerics;
using Aperture.Playback.Mpv;
using Aperture.Video.Angle;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Aperture.Video;

/// <summary>
/// Renders an <see cref="MpvPlaybackEngine"/>'s video into a DXGI composition swap chain on a
/// dedicated thread. The app shows the swap chain in a XAML <c>SwapChainPanel</c> (see
/// <see cref="SwapChainChanged"/>), so Aperture's own controls are composed above the video
/// by XAML itself. Create it before opening any file: libmpv's video output needs the render
/// context to exist when playback starts. Dispose it before the engine.
/// </summary>
public sealed class VideoPresenter : IDisposable
{
    private static readonly FeatureLevel[] FeatureLevels =
        [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];

    private readonly MpvPlaybackEngine _engine;
    private readonly VideoRendererKind _kind;
    private readonly bool _flipY;
    private readonly Action<string, string> _log;
    private readonly AutoResetEvent _wake = new(false);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private readonly object _sizeGate = new();
    private volatile bool _stopping;
    private int _requestedWidth = 64;
    private int _requestedHeight = 64;
    private float _scaleX = 1;
    private float _scaleY = 1;
    private bool _sizeChanged = true;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _immediate;
    private IDXGISwapChain1? _swapChain;
    private IFrameSource? _frames;
    private int _width;
    private int _height;
    private string _description = "Starting";
    private bool _lightweight;

    private VideoPresenter(MpvPlaybackEngine engine, VideoRendererKind kind, bool flipY, Action<string, string> log)
    {
        _engine = engine;
        _kind = kind;
        _flipY = flipY;
        _log = log;
        _thread = new Thread(Run) { IsBackground = true, Name = "Aperture video" };
    }

    /// <summary>
    /// Raised on the render thread with the swap chain's native pointer when it is created or
    /// replaced (after a GPU reset). The pointer stays valid until the next raise or Dispose;
    /// pass it to <c>ISwapChainPanelNative.SetSwapChain</c> on the UI thread, which keeps its
    /// own reference.
    /// </summary>
    public event Action<IntPtr>? SwapChainChanged;

    /// <summary>Completes when the surface can show video; faults if no path could start.</summary>
    public Task Ready => _ready.Task;

    /// <summary>The frame path in use, for the playback information panel.</summary>
    public string Description => _description;

    /// <summary>
    /// Starts the render thread. <paramref name="flipY"/> controls the vertical orientation of
    /// the GPU path's frames (see docs/architecture.md); <paramref name="log"/> receives
    /// (level, message).
    /// </summary>
    public static VideoPresenter Start(MpvPlaybackEngine engine, VideoRendererKind kind, bool flipY, Action<string, string> log)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(log);
        var presenter = new VideoPresenter(engine, kind, flipY, log);
        engine.RendererDescription = () => presenter.Description;
        presenter._thread.Start();
        return presenter;
    }

    /// <summary>
    /// The panel's size in physical pixels and its composition scale. Safe from any thread;
    /// the render thread applies it before the next frame.
    /// </summary>
    public void Resize(int pixelWidth, int pixelHeight, float scaleX, float scaleY)
    {
        lock (_sizeGate)
        {
            _requestedWidth = Math.Clamp(pixelWidth, 1, 16384);
            _requestedHeight = Math.Clamp(pixelHeight, 1, 16384);
            _scaleX = scaleX > 0 ? scaleX : 1;
            _scaleY = scaleY > 0 ? scaleY : 1;
            _sizeChanged = true;
        }

        _wake.Set();
    }

    public void Dispose()
    {
        if (_stopping)
        {
            return;
        }

        _stopping = true;
        _wake.Set();
        if (!_thread.Join(TimeSpan.FromSeconds(5)))
        {
            _log("error", "The video thread did not stop within 5 seconds.");
        }

        _engine.RendererDescription = static () => null;
        _wake.Dispose();
    }

    private void Run()
    {
        try
        {
            StartPipeline();
            _ready.TrySetResult();
            _wake.Set(); // draw the first (black) frame so the panel never shows an uninitialised buffer
            Loop();
        }
        catch (Exception error) when (error is SharpGenException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            _log("error", $"The video surface failed: {error}");
            _description = "Unavailable";
            _ready.TrySetException(error);
        }
        finally
        {
            StopPipeline();
        }
    }

    private void Loop()
    {
        var recoveries = 0;
        while (!_stopping)
        {
            _wake.WaitOne();
            if (_stopping)
            {
                break;
            }

            try
            {
                var redraw = ApplyPendingSize();
                if (_frames!.HasNewFrame() || redraw)
                {
                    DrawAndPresent();
                }
            }
            catch (SharpGenException error) when (IsDeviceLost(error.ResultCode) && recoveries < 3)
            {
                // A driver update, GPU reset or adapter change: rebuild everything once more.
                recoveries++;
                _log("warn", $"The GPU device was lost ({error.ResultCode}); rebuilding the video surface.");
                StopPipeline();
                StartPipeline();
                lock (_sizeGate)
                {
                    _sizeChanged = true;
                }

                _wake.Set();
            }
        }
    }

    private void StartPipeline()
    {
        var adapter = CreateDevice();
        int width, height;
        lock (_sizeGate)
        {
            width = _requestedWidth;
            height = _requestedHeight;
        }

        CreateSwapChain(width, height);

        if (_kind == VideoRendererKind.Automatic)
        {
            try
            {
                _frames = AngleFrameSource.Create(_engine, _device!, _immediate!, adapter, _flipY, () => _wake.Set());
            }
            catch (Exception error) when (error is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException or SharpGenException)
            {
                _log("warn", $"GPU video rendering is unavailable, using the software renderer: {error.Message}");
            }
        }

        _frames ??= SoftwareFrameSource.Create(_engine, _immediate!, adapter, () => _wake.Set());
        _description = _lightweight ? $"{_frames.Description}, fast scaling (no GPU)" : _frames.Description;
        _log("info", $"Video surface ready: {_description}");
        SwapChainChanged?.Invoke(_swapChain!.NativePointer);
    }

    private void StopPipeline()
    {
        _frames?.Dispose();
        _frames = null;
        _swapChain?.Dispose();
        _swapChain = null;
        _immediate?.ClearState();
        _immediate?.Flush();
        _immediate?.Dispose();
        _immediate = null;
        _device?.Dispose();
        _device = null;
    }

    private string CreateDevice()
    {
        var (adapter, software) = CreateDeviceCore();

        // With a real GPU the render thread does little CPU work, and running it above normal
        // keeps frames on time. On WARP every draw is rasterised on the CPU, so the interface
        // must win instead, and libmpv is told to use cheap scalers.
        Thread.CurrentThread.Priority = software ? ThreadPriority.BelowNormal : ThreadPriority.AboveNormal;
        _lightweight = software;
        if (software)
        {
            _engine.UseLightweightScaling();
        }

        return adapter;
    }

    private (string Adapter, bool Software) CreateDeviceCore()
    {
        var flags = DeviceCreationFlags.BgraSupport;
        var result = D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, flags, FeatureLevels, out var device, out var immediate);
        if (result.Failure)
        {
            _log("warn", $"No hardware Direct3D 11 device ({result}); using WARP.");
            D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Warp, flags, FeatureLevels, out device, out immediate).CheckError();
        }

        _device = device!;
        _immediate = immediate!;
        using (var multithread = _device.QueryInterfaceOrNull<ID3D11Multithread>())
        {
            multithread?.SetMultithreadProtected(true);
        }

        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var adapter1 = adapter.QueryInterfaceOrNull<IDXGIAdapter1>();
        var software = adapter1 is not null && (adapter1.Description1.Flags & AdapterFlags.Software) != 0;
        return (adapter.Description.Description.TrimEnd('\0').Trim(), software);
    }

    private void CreateSwapChain(int width, int height)
    {
        using var dxgiDevice = _device!.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();
        var description = new SwapChainDescription1
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = AlphaMode.Ignore,
            Flags = SwapChainFlags.None,
        };
        _swapChain = factory.CreateSwapChainForComposition(_device, description, null);
        _width = width;
        _height = height;
        ApplyScale();
    }

    private bool ApplyPendingSize()
    {
        int width, height;
        lock (_sizeGate)
        {
            if (!_sizeChanged)
            {
                return false;
            }

            _sizeChanged = false;
            width = _requestedWidth;
            height = _requestedHeight;
        }

        if (width != _width || height != _height)
        {
            _swapChain!.ResizeBuffers(2, (uint)width, (uint)height, Format.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();
            _width = width;
            _height = height;
            _frames!.Resize(width, height);
        }

        ApplyScale();
        return true;
    }

    private void ApplyScale()
    {
        float scaleX, scaleY;
        lock (_sizeGate)
        {
            scaleX = _scaleX;
            scaleY = _scaleY;
        }

        // The panel lays the swap chain out in effective pixels; undo the display scale so one
        // swap chain pixel is one screen pixel.
        using var swapChain2 = _swapChain!.QueryInterfaceOrNull<IDXGISwapChain2>();
        if (swapChain2 is not null)
        {
            swapChain2.MatrixTransform = new Matrix3x2(1 / scaleX, 0, 0, 1 / scaleY, 0, 0);
        }
    }

    private void DrawAndPresent()
    {
        using (var backBuffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0))
        {
            _frames!.Render(backBuffer, _width, _height);
        }

        var result = _swapChain.Present(1, PresentFlags.None);
        if (result.Failure)
        {
            throw new SharpGenException(result);
        }

        _frames.ReportSwap();
    }

    private static bool IsDeviceLost(Result result) =>
        result == Vortice.DXGI.ResultCode.DeviceRemoved || result == Vortice.DXGI.ResultCode.DeviceReset || result == Vortice.DXGI.ResultCode.DeviceHung;
}

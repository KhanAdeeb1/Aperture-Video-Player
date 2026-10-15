using System.Runtime.InteropServices;
using Aperture.Playback.Mpv;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Aperture.Video.Angle;

/// <summary>
/// The GPU path: libmpv's OpenGL renderer runs on ANGLE (OpenGL ES implemented on
/// Direct3D 11), sharing Aperture's Direct3D device. Each frame is drawn into a texture
/// wrapped as an EGL pbuffer, then copied into the swap chain's back buffer on the GPU.
/// </summary>
internal sealed unsafe class AngleFrameSource : IFrameSource
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _immediate;
    private readonly bool _flipY;
    private IntPtr _eglDevice;
    private IntPtr _display;
    private IntPtr _config;
    private IntPtr _eglContext;
    private IntPtr _surface;
    private ID3D11Texture2D? _target;
    private MpvRenderContext? _context;
    private int _width;
    private int _height;

    private AngleFrameSource(ID3D11Device device, ID3D11DeviceContext immediate, bool flipY)
    {
        _device = device;
        _immediate = immediate;
        _flipY = flipY;
    }

    public string Description { get; private set; } = "GPU (OpenGL ES via ANGLE on Direct3D 11)";

    /// <summary>
    /// Sets up ANGLE on <paramref name="device"/> and an OpenGL libmpv render context.
    /// Throws with a specific reason when any step fails; the caller falls back to software.
    /// </summary>
    public static AngleFrameSource Create(MpvPlaybackEngine engine, ID3D11Device device, ID3D11DeviceContext immediate, string adapter, bool flipY, Action onUpdate)
    {
        var source = new AngleFrameSource(device, immediate, flipY);
        try
        {
            source.Initialize(engine, adapter, onUpdate);
            return source;
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    public bool HasNewFrame() => _context?.HasNewFrame() == true;

    public void Resize(int width, int height)
    {
        if (width == _width && height == _height && _surface != IntPtr.Zero)
        {
            return;
        }

        var texture = _device.CreateTexture2D(new Texture2DDescription(
            Format.B8G8R8A8_UNorm, (uint)width, (uint)height, arraySize: 1, mipLevels: 1,
            bindFlags: BindFlags.RenderTarget | BindFlags.ShaderResource));
        var attributes = stackalloc int[] { Egl.Width, width, Egl.Height, height, Egl.None };
        var surface = Egl.CreatePbufferFromClientBuffer(_display, Egl.D3DTextureAngle, texture.NativePointer, _config, attributes);
        if (surface == IntPtr.Zero)
        {
            texture.Dispose();
            throw new InvalidOperationException($"ANGLE could not wrap the video texture ({Egl.LastError()}).");
        }

        if (!Egl.MakeCurrent(_display, surface, surface, _eglContext))
        {
            Egl.DestroySurface(_display, surface);
            texture.Dispose();
            throw new InvalidOperationException($"ANGLE could not make the video surface current ({Egl.LastError()}).");
        }

        if (_surface != IntPtr.Zero)
        {
            Egl.DestroySurface(_display, _surface);
        }

        _target?.Dispose();
        _surface = surface;
        _target = texture;
        _width = width;
        _height = height;
    }

    public void Render(ID3D11Texture2D backBuffer, int width, int height)
    {
        Resize(width, height);
        var result = _context!.RenderOpenGl(0, width, height, _flipY);
        if (result < 0)
        {
            throw new InvalidOperationException($"libmpv OpenGL render failed: {MpvRenderContext.Describe(result)}");
        }

        // ANGLE batches commands on the shared immediate context; flush them before the copy.
        Egl.GlFlush();
        _immediate.CopyResource(backBuffer, _target!);
    }

    public void ReportSwap() => _context?.ReportSwap();

    public void Dispose()
    {
        // libmpv must free its OpenGL resources with the context current.
        if (_context is not null)
        {
            if (_display != IntPtr.Zero && _eglContext != IntPtr.Zero)
            {
                Egl.MakeCurrent(_display, _surface, _surface, _eglContext);
            }

            _context.Dispose();
            _context = null;
        }

        if (_display != IntPtr.Zero)
        {
            Egl.MakeCurrent(_display, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (_surface != IntPtr.Zero)
            {
                Egl.DestroySurface(_display, _surface);
                _surface = IntPtr.Zero;
            }

            if (_eglContext != IntPtr.Zero)
            {
                Egl.DestroyContext(_display, _eglContext);
                _eglContext = IntPtr.Zero;
            }

            Egl.Terminate(_display);
            _display = IntPtr.Zero;
        }

        if (_eglDevice != IntPtr.Zero)
        {
            Egl.ReleaseDeviceAngle(_eglDevice);
            _eglDevice = IntPtr.Zero;
        }

        _target?.Dispose();
        _target = null;
    }

    private void Initialize(MpvPlaybackEngine engine, string adapter, Action onUpdate)
    {
        _eglDevice = Egl.CreateDeviceAngle(Egl.D3D11DeviceAngle, _device.NativePointer, IntPtr.Zero);
        if (_eglDevice == IntPtr.Zero)
        {
            throw new InvalidOperationException($"ANGLE could not use the Direct3D device ({Egl.LastError()}).");
        }

        var displayAttributes = stackalloc int[] { Egl.None };
        _display = Egl.GetPlatformDisplayExt(Egl.PlatformDeviceExt, _eglDevice, displayAttributes);
        if (_display == IntPtr.Zero || !Egl.Initialize(_display, out var major, out var minor))
        {
            throw new InvalidOperationException($"ANGLE could not start ({Egl.LastError()}).");
        }

        Egl.BindApi(Egl.OpenGlEsApi);
        if (!TryCreateContext(Egl.OpenGlEs3Bit, 3))
        {
            TryCreateContext(Egl.OpenGlEs2Bit, 2);
        }

        if (_eglContext == IntPtr.Zero)
        {
            throw new InvalidOperationException($"ANGLE has no OpenGL ES 2 or 3 context for this device ({Egl.LastError()}).");
        }

        Resize(16, 16);
        var glVersion = Marshal.PtrToStringUTF8(Egl.GlGetString(Egl.GlVersion)) ?? "OpenGL ES";
        _context = MpvRenderContext.CreateOpenGl(engine, &GetProcAddress, out var error)
            ?? throw new InvalidOperationException($"libmpv could not render with {glVersion}: {error}");
        _context.SetUpdateCallback(onUpdate);
        Description = FormattableString.Invariant($"GPU: {glVersion} on {adapter} (EGL {major}.{minor})");
    }

    private bool TryCreateContext(int renderable, int version)
    {
        var configAttributes = stackalloc int[]
        {
            Egl.RedSize, 8, Egl.GreenSize, 8, Egl.BlueSize, 8, Egl.AlphaSize, 8,
            Egl.DepthSize, 0, Egl.StencilSize, 0,
            Egl.SurfaceType, Egl.PbufferBit, Egl.RenderableType, renderable, Egl.None,
        };
        IntPtr config;
        if (!Egl.ChooseConfig(_display, configAttributes, &config, 1, out var count) || count < 1)
        {
            return false;
        }

        var contextAttributes = stackalloc int[] { Egl.ContextClientVersion, version, Egl.None };
        _eglContext = Egl.CreateContext(_display, config, IntPtr.Zero, contextAttributes);
        if (_eglContext == IntPtr.Zero)
        {
            return false;
        }

        _config = config;
        return true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static IntPtr GetProcAddress(IntPtr context, byte* name) => Egl.GetProcAddress(name);
}

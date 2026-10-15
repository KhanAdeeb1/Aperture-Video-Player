using System.Runtime.InteropServices;
using Aperture.Playback.Mpv.Interop;

namespace Aperture.Playback.Mpv;

/// <summary>
/// A libmpv render context: the engine draws video frames into a target the caller owns
/// (an OpenGL framebuffer, or a block of memory for the software renderer). Every method,
/// including <see cref="Dispose"/>, must be called on the caller's render thread, with the
/// OpenGL context current for the OpenGL kind. Dispose it before the engine.
/// </summary>
public sealed unsafe class MpvRenderContext : IDisposable
{
    private static readonly IntPtr SoftwareFormat = Marshal.StringToCoTaskMemUTF8("bgr0");

    private readonly MpvPlaybackEngine _engine;
    private GCHandle _self;
    private Action? _onUpdate;
    private IntPtr _context;

    private MpvRenderContext(MpvPlaybackEngine engine, IntPtr context, bool isOpenGl)
    {
        _engine = engine;
        _context = context;
        IsOpenGl = isOpenGl;
        engine.RenderContextCreated();
    }

    /// <summary>True for the OpenGL kind, false for the software renderer.</summary>
    public bool IsOpenGl { get; }

    /// <summary>
    /// Creates an OpenGL render context. <paramref name="getProcAddress"/> resolves OpenGL
    /// function names for the context that is current on this thread. Returns null with a
    /// reason in <paramref name="error"/> when libmpv cannot use that context.
    /// </summary>
    public static MpvRenderContext? CreateOpenGl(MpvPlaybackEngine engine, delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr> getProcAddress, out string? error)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var api = Marshal.StringToCoTaskMemUTF8("opengl");
        try
        {
            var init = new MpvOpenGlInitParams { GetProcAddress = getProcAddress, GetProcAddressContext = IntPtr.Zero };
            var advanced = 0;
            var parameters = stackalloc MpvRenderParam[]
            {
                new(MpvRenderParamType.ApiType, api),
                new(MpvRenderParamType.OpenGlInitParams, (IntPtr)(&init)),
                new(MpvRenderParamType.AdvancedControl, (IntPtr)(&advanced)),
                new(MpvRenderParamType.Invalid, IntPtr.Zero),
            };
            return Create(engine, parameters, isOpenGl: true, out error);
        }
        finally
        {
            Marshal.FreeCoTaskMem(api);
        }
    }

    /// <summary>Creates a software render context (frames are drawn by the CPU into memory).</summary>
    public static MpvRenderContext? CreateSoftware(MpvPlaybackEngine engine, out string? error)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var api = Marshal.StringToCoTaskMemUTF8("sw");
        try
        {
            var parameters = stackalloc MpvRenderParam[]
            {
                new(MpvRenderParamType.ApiType, api),
                new(MpvRenderParamType.Invalid, IntPtr.Zero),
            };
            return Create(engine, parameters, isOpenGl: false, out error);
        }
        finally
        {
            Marshal.FreeCoTaskMem(api);
        }
    }

    /// <summary>
    /// Calls <paramref name="onUpdate"/> (on a libmpv thread) whenever a new frame is ready or
    /// the picture must be redrawn. The callback must only signal the render thread.
    /// </summary>
    public void SetUpdateCallback(Action onUpdate)
    {
        ArgumentNullException.ThrowIfNull(onUpdate);
        _onUpdate = onUpdate;
        if (!_self.IsAllocated)
        {
            _self = GCHandle.Alloc(this);
        }

        MpvNative.RenderContextSetUpdateCallback(_context, &OnUpdate, GCHandle.ToIntPtr(_self));
    }

    /// <summary>True when libmpv has a new frame to draw (call after each update callback).</summary>
    public bool HasNewFrame() => _context != IntPtr.Zero && (MpvNative.RenderContextUpdate(_context) & MpvNative.RenderUpdateFrame) != 0;

    /// <summary>Draws the current frame into OpenGL framebuffer <paramref name="framebuffer"/>.</summary>
    public int RenderOpenGl(int framebuffer, int width, int height, bool flipY)
    {
        var fbo = new MpvOpenGlFbo { Fbo = framebuffer, Width = width, Height = height, InternalFormat = 0 };
        var flip = flipY ? 1 : 0;
        var parameters = stackalloc MpvRenderParam[]
        {
            new(MpvRenderParamType.OpenGlFbo, (IntPtr)(&fbo)),
            new(MpvRenderParamType.FlipY, (IntPtr)(&flip)),
            new(MpvRenderParamType.Invalid, IntPtr.Zero),
        };
        return MpvNative.RenderContextRender(_context, parameters);
    }

    /// <summary>
    /// Draws the current frame as 32-bit BGRX pixels into <paramref name="pixels"/>
    /// (<paramref name="stride"/> bytes per row; both should be multiples of 64).
    /// </summary>
    public int RenderSoftware(int width, int height, nuint stride, IntPtr pixels)
    {
        var size = stackalloc int[] { width, height };
        var parameters = stackalloc MpvRenderParam[]
        {
            new(MpvRenderParamType.SoftwareSize, (IntPtr)size),
            new(MpvRenderParamType.SoftwareFormat, SoftwareFormat),
            new(MpvRenderParamType.SoftwareStride, (IntPtr)(&stride)),
            new(MpvRenderParamType.SoftwarePointer, pixels),
            new(MpvRenderParamType.Invalid, IntPtr.Zero),
        };
        return MpvNative.RenderContextRender(_context, parameters);
    }

    /// <summary>Tells libmpv a frame was presented, for its frame timing.</summary>
    public void ReportSwap()
    {
        if (_context != IntPtr.Zero)
        {
            MpvNative.RenderContextReportSwap(_context);
        }
    }

    public void Dispose()
    {
        if (_context == IntPtr.Zero)
        {
            return;
        }

        MpvNative.RenderContextFree(_context);
        _context = IntPtr.Zero;
        if (_self.IsAllocated)
        {
            _self.Free();
        }

        _engine.RenderContextFreed();
    }

    /// <summary>libmpv's text for a render result code.</summary>
    public static string Describe(int result) => MpvNative.Describe(result);

    private static MpvRenderContext? Create(MpvPlaybackEngine engine, MpvRenderParam* parameters, bool isOpenGl, out string? error)
    {
        var result = MpvNative.RenderContextCreate(out var context, engine.Handle, parameters);
        if (result < 0)
        {
            error = MpvNative.Describe(result);
            return null;
        }

        error = null;
        return new MpvRenderContext(engine, context, isOpenGl);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnUpdate(IntPtr state)
    {
        if (GCHandle.FromIntPtr(state).Target is MpvRenderContext { _onUpdate: { } onUpdate })
        {
            onUpdate();
        }
    }
}

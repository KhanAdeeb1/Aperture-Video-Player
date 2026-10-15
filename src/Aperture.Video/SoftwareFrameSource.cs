using System.Runtime.InteropServices;
using Aperture.Playback.Mpv;
using Vortice.Direct3D11;

namespace Aperture.Video;

/// <summary>
/// The fallback path: libmpv's software renderer draws each frame into memory, which is
/// uploaded to the back buffer. Works on any Direct3D 11 device (including WARP) but scales
/// and converts colour on the CPU, so large windows and 4K video cost noticeably more.
/// </summary>
internal sealed unsafe class SoftwareFrameSource : IFrameSource
{
    private readonly MpvRenderContext _context;
    private readonly ID3D11DeviceContext _immediate;
    private void* _pixels;
    private nuint _stride;
    private int _width;
    private int _height;

    private SoftwareFrameSource(MpvRenderContext context, ID3D11DeviceContext immediate, string adapter)
    {
        _context = context;
        _immediate = immediate;
        Description = $"Software (libmpv CPU renderer, presented on {adapter})";
    }

    public string Description { get; }

    public static SoftwareFrameSource Create(MpvPlaybackEngine engine, ID3D11DeviceContext immediate, string adapter, Action onUpdate)
    {
        var context = MpvRenderContext.CreateSoftware(engine, out var error)
            ?? throw new InvalidOperationException($"libmpv's software renderer could not start: {error}");
        context.SetUpdateCallback(onUpdate);
        return new SoftwareFrameSource(context, immediate, adapter);
    }

    public bool HasNewFrame() => _context.HasNewFrame();

    public void Resize(int width, int height)
    {
        if (width == _width && height == _height && _pixels is not null)
        {
            return;
        }

        Free();
        _width = width;
        _height = height;
        _stride = (nuint)((width * 4 + 63) & ~63);
        _pixels = NativeMemory.AlignedAlloc(_stride * (nuint)height, 64);
    }

    public void Render(ID3D11Texture2D backBuffer, int width, int height)
    {
        Resize(width, height);
        var result = _context.RenderSoftware(width, height, _stride, (IntPtr)_pixels);
        if (result < 0)
        {
            throw new InvalidOperationException($"libmpv software render failed: {MpvRenderContext.Describe(result)}");
        }

        _immediate.UpdateSubresource(backBuffer, 0, null, (IntPtr)_pixels, (uint)_stride, 0);
    }

    public void ReportSwap() => _context.ReportSwap();

    public void Dispose()
    {
        _context.Dispose();
        Free();
    }

    private void Free()
    {
        if (_pixels is not null)
        {
            NativeMemory.AlignedFree(_pixels);
            _pixels = null;
        }
    }
}

using Vortice.Direct3D11;

namespace Aperture.Video;

/// <summary>Draws libmpv frames into the swap chain's back buffer. Used only on the render thread.</summary>
internal interface IFrameSource : IDisposable
{
    /// <summary>"GPU: OpenGL ES 3.0 (ANGLE) on Direct3D 11" and similar, for diagnostics.</summary>
    string Description { get; }

    /// <summary>True when libmpv has a new frame since the last call.</summary>
    bool HasNewFrame();

    /// <summary>The target size changed; reallocate whatever depends on it.</summary>
    void Resize(int width, int height);

    /// <summary>Draws the current frame into <paramref name="backBuffer"/>.</summary>
    void Render(ID3D11Texture2D backBuffer, int width, int height);

    /// <summary>Tells libmpv the frame was presented.</summary>
    void ReportSwap();
}

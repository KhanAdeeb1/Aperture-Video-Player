using System.Runtime.InteropServices;
using Aperture.App.Services;
using Aperture.Video;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Aperture.App.Video;

/// <summary>
/// Connects a <see cref="VideoPresenter"/> to a XAML <see cref="SwapChainPanel"/>: hands the
/// presenter's swap chain to the panel (through <c>ISwapChainPanelNative</c>) and reports the
/// panel's size and display scale back to the presenter.
/// </summary>
internal sealed class SwapChainPanelBridge
{
    // ISwapChainPanelNative from microsoft.ui.xaml.media.dxinterop.h (Windows App SDK).
    private static readonly Guid SwapChainPanelNativeId = new("63aad0b8-7c24-40ff-85a8-640d944cc325");

    private readonly SwapChainPanel _panel;
    private readonly VideoPresenter _presenter;
    private readonly DispatcherQueue _dispatcher;

    public SwapChainPanelBridge(SwapChainPanel panel, VideoPresenter presenter)
    {
        _panel = panel;
        _presenter = presenter;
        _dispatcher = panel.DispatcherQueue;
        presenter.SwapChainChanged += OnSwapChainChanged;
        panel.SizeChanged += (_, _) => ReportSize();
        panel.CompositionScaleChanged += (_, _) => ReportSize();
        panel.Loaded += (_, _) => ReportSize();
    }

    private void ReportSize()
    {
        var scaleX = _panel.CompositionScaleX;
        var scaleY = _panel.CompositionScaleY;
        var width = (int)Math.Round(_panel.ActualWidth * scaleX);
        var height = (int)Math.Round(_panel.ActualHeight * scaleY);
        if (width > 0 && height > 0)
        {
            _presenter.Resize(width, height, scaleX, scaleY);
        }
    }

    private void OnSwapChainChanged(IntPtr swapChain)
    {
        // Raised on the render thread; the panel may only be touched on the UI thread. The
        // presenter keeps the swap chain alive until it raises again or is disposed, and
        // SetSwapChain takes the panel's own reference before this returns.
        using var done = new ManualResetEventSlim(false);
        if (!_dispatcher.TryEnqueue(() =>
        {
            try
            {
                SetSwapChain(swapChain);
                ReportSize();
            }
            finally
            {
                done.Set();
            }
        }))
        {
            return;
        }

        done.Wait(TimeSpan.FromSeconds(5));
    }

    private unsafe void SetSwapChain(IntPtr swapChain)
    {
        var unknown = ((WinRT.IWinRTObject)_panel).NativeObject.ThisPtr;
        var result = Marshal.QueryInterface(unknown, in SwapChainPanelNativeId, out var native);
        if (result < 0)
        {
            AppLog.Error($"The video panel does not support swap chains (0x{result:X8})");
            return;
        }

        try
        {
            // ISwapChainPanelNative::SetSwapChain is the first method after IUnknown's three.
            var setSwapChain = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)(*(IntPtr**)native)[3];
            result = setSwapChain(native, swapChain);
            if (result < 0)
            {
                AppLog.Error($"The video panel refused the swap chain (0x{result:X8})");
            }
        }
        finally
        {
            Marshal.Release(native);
        }
    }

    /// <summary>Detaches the swap chain (before the presenter is disposed).</summary>
    public void Detach()
    {
        _presenter.SwapChainChanged -= OnSwapChainChanged;
        if (_dispatcher.HasThreadAccess)
        {
            SetSwapChain(IntPtr.Zero);
        }
    }
}

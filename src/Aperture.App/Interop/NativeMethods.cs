using System.Runtime.InteropServices;

namespace Aperture.App.Interop;

internal static partial class NativeMethods
{
    /// <summary>Win32 ShowCursor: adjusts the cursor display counter of the calling thread.</summary>
    [LibraryImport("user32.dll", EntryPoint = "ShowCursor")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int ShowCursor([MarshalAs(UnmanagedType.Bool)] bool show);

    /// <summary>Win32 GetDpiForWindow: the window's DPI (96 at 100 % scaling).</summary>
    [LibraryImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial uint GetDpiForWindow(nint hwnd);
}

using System.Runtime.InteropServices;

namespace Aperture.Video.Angle;

/// <summary>
/// The EGL entry points of ANGLE as built by the Avalonia project (<c>av_libglesv2.dll</c>,
/// which exports EGL with an "EGL_" prefix and OpenGL ES with a "GL_" prefix).
/// </summary>
internal static unsafe partial class Egl
{
    public const string Library = "av_libglesv2";

    public const int None = 0x3038;
    public const int Success = 0x3000;
    public const int RedSize = 0x3024;
    public const int GreenSize = 0x3023;
    public const int BlueSize = 0x3022;
    public const int AlphaSize = 0x3021;
    public const int DepthSize = 0x3025;
    public const int StencilSize = 0x3026;
    public const int SurfaceType = 0x3033;
    public const int PbufferBit = 0x0001;
    public const int RenderableType = 0x3040;
    public const int OpenGlEs2Bit = 0x0004;
    public const int OpenGlEs3Bit = 0x0040;
    public const int Width = 0x3057;
    public const int Height = 0x3056;
    public const int OpenGlEsApi = 0x30A0;
    public const int ContextClientVersion = 0x3098;
    public const int Vendor = 0x3053;
    public const int Version = 0x3054;
    public const int PlatformDeviceExt = 0x313F;
    public const int D3D11DeviceAngle = 0x33A1;
    public const int D3DTextureAngle = 0x33A3;

    public const int GlVersion = 0x1F02;
    public const int GlRenderer = 0x1F01;

    [LibraryImport(Library, EntryPoint = "EGL_CreateDeviceANGLE")]
    public static partial IntPtr CreateDeviceAngle(int deviceType, IntPtr nativeDevice, IntPtr attributes);

    [LibraryImport(Library, EntryPoint = "EGL_ReleaseDeviceANGLE")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReleaseDeviceAngle(IntPtr device);

    [LibraryImport(Library, EntryPoint = "EGL_GetPlatformDisplayEXT")]
    public static partial IntPtr GetPlatformDisplayExt(int platform, IntPtr nativeDisplay, int* attributes);

    [LibraryImport(Library, EntryPoint = "EGL_Initialize")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Initialize(IntPtr display, out int major, out int minor);

    [LibraryImport(Library, EntryPoint = "EGL_Terminate")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Terminate(IntPtr display);

    [LibraryImport(Library, EntryPoint = "EGL_BindAPI")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BindApi(int api);

    [LibraryImport(Library, EntryPoint = "EGL_ChooseConfig")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ChooseConfig(IntPtr display, int* attributes, IntPtr* configs, int configSize, out int count);

    [LibraryImport(Library, EntryPoint = "EGL_CreateContext")]
    public static partial IntPtr CreateContext(IntPtr display, IntPtr config, IntPtr shareContext, int* attributes);

    [LibraryImport(Library, EntryPoint = "EGL_DestroyContext")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyContext(IntPtr display, IntPtr context);

    [LibraryImport(Library, EntryPoint = "EGL_CreatePbufferFromClientBuffer")]
    public static partial IntPtr CreatePbufferFromClientBuffer(IntPtr display, int bufferType, IntPtr buffer, IntPtr config, int* attributes);

    [LibraryImport(Library, EntryPoint = "EGL_DestroySurface")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroySurface(IntPtr display, IntPtr surface);

    [LibraryImport(Library, EntryPoint = "EGL_MakeCurrent")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool MakeCurrent(IntPtr display, IntPtr draw, IntPtr read, IntPtr context);

    [LibraryImport(Library, EntryPoint = "EGL_GetError")]
    public static partial int GetError();

    [LibraryImport(Library, EntryPoint = "EGL_QueryString")]
    public static partial IntPtr QueryString(IntPtr display, int name);

    [LibraryImport(Library, EntryPoint = "EGL_GetProcAddress")]
    public static partial IntPtr GetProcAddress(byte* name);

    [LibraryImport(Library, EntryPoint = "GL_Flush")]
    public static partial void GlFlush();

    [LibraryImport(Library, EntryPoint = "GL_GetString")]
    public static partial IntPtr GlGetString(int name);

    /// <summary>The last EGL error as hex, for messages.</summary>
    public static string LastError() => FormattableString.Invariant($"EGL error 0x{GetError():X4}");
}

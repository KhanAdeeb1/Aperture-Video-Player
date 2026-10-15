using System.Runtime.InteropServices;

namespace Aperture.Playback.Mpv.Interop;

// Declarations follow libmpv's include/mpv/client.h and render.h (client API 2.x).
// Only the functions Aperture uses are declared.

internal enum MpvFormat
{
    None = 0,
    String = 1,
    OsdString = 2,
    Flag = 3,
    Int64 = 4,
    Double = 5,
    Node = 6,
}

internal enum MpvEventId
{
    None = 0,
    Shutdown = 1,
    LogMessage = 2,
    GetPropertyReply = 3,
    SetPropertyReply = 4,
    CommandReply = 5,
    StartFile = 6,
    EndFile = 7,
    FileLoaded = 8,
    Idle = 11,
    Tick = 14,
    ClientMessage = 16,
    VideoReconfig = 17,
    AudioReconfig = 18,
    Seek = 20,
    PlaybackRestart = 21,
    PropertyChange = 22,
    QueueOverflow = 24,
    Hook = 25,
}

internal enum MpvEndFileReason
{
    Eof = 0,
    Stop = 2,
    Quit = 3,
    Error = 4,
    Redirect = 5,
}

/// <summary>libmpv error codes (mpv_error).</summary>
internal static class MpvError
{
    public const int Success = 0;
    public const int PropertyUnavailable = -10;
    public const int Command = -12;
    public const int LoadingFailed = -13;
    public const int AudioOutputInitFailed = -14;
    public const int VideoOutputInitFailed = -15;
    public const int NothingToPlay = -16;
    public const int UnknownFormat = -17;
    public const int Unsupported = -18;
    public const int Generic = -20;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEvent
{
    public MpvEventId EventId;
    public int Error;
    public ulong ReplyUserData;
    public IntPtr Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventProperty
{
    public IntPtr Name;
    public MpvFormat Format;
    public IntPtr Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventLogMessage
{
    public IntPtr Prefix;
    public IntPtr Level;
    public IntPtr Text;
    public int LogLevel;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventStartFile
{
    public long PlaylistEntryId;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventEndFile
{
    public MpvEndFileReason Reason;
    public int Error;
    public long PlaylistEntryId;
    public long PlaylistInsertId;
    public int PlaylistInsertNumEntries;
}

internal enum MpvRenderParamType
{
    Invalid = 0,
    ApiType = 1,
    OpenGlInitParams = 2,
    OpenGlFbo = 3,
    FlipY = 4,
    AdvancedControl = 10,
    BlockForTargetTime = 12,
    SoftwareSize = 17,
    SoftwareFormat = 18,
    SoftwareStride = 19,
    SoftwarePointer = 20,
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvRenderParam
{
    public MpvRenderParamType Type;
    public IntPtr Data;

    public MpvRenderParam(MpvRenderParamType type, IntPtr data)
    {
        Type = type;
        Data = data;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MpvOpenGlInitParams
{
    public delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr> GetProcAddress;
    public IntPtr GetProcAddressContext;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvOpenGlFbo
{
    public int Fbo;
    public int Width;
    public int Height;
    public int InternalFormat;
}

internal static unsafe partial class MpvNative
{
    /// <summary>The logical library name; <see cref="MpvLibrary"/> maps it to the real file per platform.</summary>
    public const string Library = "mpv";

    /// <summary>MPV_RENDER_UPDATE_FRAME.</summary>
    public const ulong RenderUpdateFrame = 1;

    [LibraryImport(Library, EntryPoint = "mpv_client_api_version")]
    public static partial ulong ClientApiVersion();

    [LibraryImport(Library, EntryPoint = "mpv_error_string")]
    public static partial IntPtr ErrorString(int error);

    [LibraryImport(Library, EntryPoint = "mpv_create")]
    public static partial IntPtr Create();

    [LibraryImport(Library, EntryPoint = "mpv_initialize")]
    public static partial int Initialize(IntPtr handle);

    [LibraryImport(Library, EntryPoint = "mpv_terminate_destroy")]
    public static partial void TerminateDestroy(IntPtr handle);

    [LibraryImport(Library, EntryPoint = "mpv_set_option_string", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int SetOptionString(IntPtr handle, string name, string data);

    [LibraryImport(Library, EntryPoint = "mpv_request_log_messages", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int RequestLogMessages(IntPtr handle, string minLevel);

    [LibraryImport(Library, EntryPoint = "mpv_command_async")]
    public static partial int CommandAsync(IntPtr handle, ulong replyUserData, IntPtr* args);

    [LibraryImport(Library, EntryPoint = "mpv_command")]
    public static partial int Command(IntPtr handle, IntPtr* args);

    [LibraryImport(Library, EntryPoint = "mpv_set_property_async", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int SetPropertyAsync(IntPtr handle, ulong replyUserData, string name, MpvFormat format, void* data);

    [LibraryImport(Library, EntryPoint = "mpv_set_property_string", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int SetPropertyString(IntPtr handle, string name, string data);

    [LibraryImport(Library, EntryPoint = "mpv_get_property_string", StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr GetPropertyString(IntPtr handle, string name);

    [LibraryImport(Library, EntryPoint = "mpv_get_property", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int GetProperty(IntPtr handle, string name, MpvFormat format, void* data);

    [LibraryImport(Library, EntryPoint = "mpv_observe_property", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int ObserveProperty(IntPtr handle, ulong replyUserData, string name, MpvFormat format);

    [LibraryImport(Library, EntryPoint = "mpv_free")]
    public static partial void Free(IntPtr data);

    [LibraryImport(Library, EntryPoint = "mpv_wait_event")]
    public static partial MpvEvent* WaitEvent(IntPtr handle, double timeout);

    [LibraryImport(Library, EntryPoint = "mpv_wakeup")]
    public static partial void Wakeup(IntPtr handle);

    [LibraryImport(Library, EntryPoint = "mpv_render_context_create")]
    public static partial int RenderContextCreate(out IntPtr context, IntPtr handle, MpvRenderParam* parameters);

    [LibraryImport(Library, EntryPoint = "mpv_render_context_set_update_callback")]
    public static partial void RenderContextSetUpdateCallback(IntPtr context, delegate* unmanaged[Cdecl]<IntPtr, void> callback, IntPtr callbackContext);

    [LibraryImport(Library, EntryPoint = "mpv_render_context_update")]
    public static partial ulong RenderContextUpdate(IntPtr context);

    [LibraryImport(Library, EntryPoint = "mpv_render_context_render")]
    public static partial int RenderContextRender(IntPtr context, MpvRenderParam* parameters);

    [LibraryImport(Library, EntryPoint = "mpv_render_context_report_swap")]
    public static partial void RenderContextReportSwap(IntPtr context);

    [LibraryImport(Library, EntryPoint = "mpv_render_context_free")]
    public static partial void RenderContextFree(IntPtr context);

    /// <summary>A UTF-8 C string from libmpv as a managed string (null stays null).</summary>
    public static string? Utf8(IntPtr text) => text == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(text);

    /// <summary>libmpv's own description of an error code.</summary>
    public static string Describe(int error) => Utf8(ErrorString(error)) ?? FormattableString.Invariant($"error {error}");
}

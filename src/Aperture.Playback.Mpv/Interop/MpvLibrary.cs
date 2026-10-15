using System.Reflection;
using System.Runtime.InteropServices;

namespace Aperture.Playback.Mpv.Interop;

/// <summary>
/// Finds and loads the native libmpv library. On Windows it is <c>libmpv-2.dll</c> next to
/// the app (fetched by <c>tools/Get-Libmpv.ps1</c>); elsewhere it is the system's libmpv.
/// </summary>
public static class MpvLibrary
{
    /// <summary>The libmpv client API major version this code is written against.</summary>
    public const int RequiredApiMajor = 2;

    private static readonly object Gate = new();
    private static IntPtr _handle;
    private static string? _loadedFrom;

    /// <summary>The file libmpv was loaded from, once loaded.</summary>
    public static string? LoadedFrom => _loadedFrom;

    /// <summary>The loaded library's client API version, as "major.minor".</summary>
    public static string ApiVersion
    {
        get
        {
            EnsureLoaded();
            var version = MpvNative.ClientApiVersion();
            return FormattableString.Invariant($"{version >> 16}.{version & 0xFFFF}");
        }
    }

    /// <summary>
    /// Loads libmpv if it is not loaded yet. Throws <see cref="MpvUnavailableException"/>
    /// with a plain explanation when the library is missing or too old.
    /// </summary>
    public static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (_handle != IntPtr.Zero)
            {
                return;
            }

            foreach (var candidate in Candidates())
            {
                if (NativeLibrary.TryLoad(candidate, typeof(MpvLibrary).Assembly, DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.SafeDirectories, out var handle)
                    || NativeLibrary.TryLoad(candidate, out handle))
                {
                    _handle = handle;
                    _loadedFrom = candidate;
                    break;
                }
            }

            if (_handle == IntPtr.Zero)
            {
                throw new MpvUnavailableException(
                    $"The libmpv library was not found (looked for {string.Join(", ", Candidates())}).");
            }

            NativeLibrary.SetDllImportResolver(typeof(MpvLibrary).Assembly, Resolve);

            var major = MpvNative.ClientApiVersion() >> 16;
            if (major != RequiredApiMajor)
            {
                throw new MpvUnavailableException(
                    FormattableString.Invariant($"libmpv client API {major}.x was found; Aperture needs {RequiredApiMajor}.x."));
            }
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath) =>
        libraryName == MpvNative.Library ? _handle : IntPtr.Zero;

    private static IEnumerable<string> Candidates()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return "libmpv-2.dll";
            yield return "mpv-2.dll";
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "libmpv.2.dylib";
            yield return "libmpv.dylib";
        }
        else
        {
            yield return "libmpv.so.2";
            yield return "libmpv.so";
        }
    }
}

/// <summary>The native playback library is missing or incompatible.</summary>
public sealed class MpvUnavailableException : Exception
{
    public MpvUnavailableException()
    {
    }

    public MpvUnavailableException(string message)
        : base(message)
    {
    }

    public MpvUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

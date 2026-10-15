using System.Runtime.InteropServices;
using System.Text;
using Aperture.Core.Settings;

namespace Aperture.App.Interop;

/// <summary>
/// Keeps secrets (an online service's API key the user typed in) in Windows Credential
/// Manager for the signed-in user, never in Aperture's database or log. Entries are named
/// "Aperture/&lt;name&gt;" and can be seen and removed in Control Panel › Credential Manager.
/// </summary>
internal sealed unsafe partial class WindowsCredentialStore : ISecretStore
{
    private const uint GenericType = 1;
    private const uint PersistLocalMachine = 2;
    private const int NotFound = 1168;

    public string? Read(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!CredRead(Target(name), GenericType, 0, out var handle))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != NotFound)
            {
                Services.AppLog.Warning($"Could not read a saved key from Credential Manager (error {error}).");
            }

            return null;
        }

        try
        {
            var credential = (Credential*)handle;
            if (credential->CredentialBlob == 0 || credential->CredentialBlobSize == 0)
            {
                return null;
            }

            return Encoding.Unicode.GetString((byte*)credential->CredentialBlob, (int)credential->CredentialBlobSize);
        }
        finally
        {
            CredFree(handle);
        }
    }

    public void Write(string name, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var target = Target(name);
        if (string.IsNullOrEmpty(value))
        {
            if (!CredDelete(target, GenericType, 0))
            {
                var error = Marshal.GetLastPInvokeError();
                if (error != NotFound)
                {
                    Services.AppLog.Warning($"Could not remove a saved key from Credential Manager (error {error}).");
                }
            }

            return;
        }

        var blob = Encoding.Unicode.GetBytes(value);
        fixed (char* targetName = target)
        fixed (char* userName = "Aperture")
        fixed (byte* data = blob)
        {
            var credential = new Credential
            {
                Type = GenericType,
                TargetName = (nint)targetName,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = (nint)data,
                Persist = PersistLocalMachine,
                UserName = (nint)userName,
            };
            if (!CredWrite(&credential, 0))
            {
                Services.AppLog.Warning($"Could not save a key to Credential Manager (error {Marshal.GetLastPInvokeError()}).");
            }
        }

        Array.Clear(blob);
    }

    private static string Target(string name) => "Aperture/" + name;

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public nint TargetName;
        public nint Comment;
        public uint LastWrittenLow;
        public uint LastWrittenHigh;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public nint TargetAlias;
        public nint UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredRead(string target, uint type, uint flags, out nint credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWrite(Credential* credential, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDelete(string target, uint type, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void CredFree(nint buffer);
}

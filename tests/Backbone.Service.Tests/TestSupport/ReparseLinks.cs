using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Backbone.Service.Tests.TestSupport;

/// <summary>
/// Puntos de reanálisis de prueba sin elevar: <i>junctions</i> con cualquier destino NT (<c>FSCTL_SET_REPARSE_POINT</c> con
/// <c>IO_REPARSE_TAG_MOUNT_POINT</c> sobre una carpeta vacía propia, como haría un atacante sin privilegios) y enlaces
/// simbólicos de carpeta (<c>CreateSymbolicLinkW</c>, que sin elevar requiere el modo de desarrollador).
/// </summary>
internal static partial class ReparseLinks
{
    private const uint GenericWrite = 0x4000_0000;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x0200_0000;
    private const uint FileFlagOpenReparsePoint = 0x0020_0000;
    private const uint FsctlSetReparsePoint = 0x0009_00A4;
    private const uint IoReparseTagMountPoint = 0xA000_0003;
    private const uint SymbolicLinkFlagDirectory = 0x1;
    private const uint SymbolicLinkFlagAllowUnprivilegedCreate = 0x2;

    /// <summary>Crea <paramref name="link"/> como <i>junction</i> con <paramref name="substituteName"/> tal cual (p. ej. <c>\RPC Control\x</c>).</summary>
    public static void CreateMountPoint(string link, string substituteName)
    {
        Directory.CreateDirectory(link);
        using var handle = CreateFile(link, GenericWrite, 0, 0, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, 0);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var substitute = MemoryMarshal.AsBytes(substituteName.AsSpan());
        const string print = "";
        // MountPointReparseBuffer: SubstituteNameOffset, SubstituteNameLength, PrintNameOffset, PrintNameLength, PathBuffer.
        var pathBufferLength = substitute.Length + 2 + (print.Length * 2) + 2;
        var dataLength = 8 + pathBufferLength;
        var buffer = new byte[8 + dataLength];
        BitConverter.TryWriteBytes(buffer.AsSpan(0), IoReparseTagMountPoint);
        BitConverter.TryWriteBytes(buffer.AsSpan(4), (ushort)dataLength);
        BitConverter.TryWriteBytes(buffer.AsSpan(8), (ushort)0);
        BitConverter.TryWriteBytes(buffer.AsSpan(10), (ushort)substitute.Length);
        BitConverter.TryWriteBytes(buffer.AsSpan(12), (ushort)(substitute.Length + 2));
        BitConverter.TryWriteBytes(buffer.AsSpan(14), (ushort)0);
        substitute.CopyTo(buffer.AsSpan(16));
        if (!DeviceIoControl(handle, FsctlSetReparsePoint, buffer, (uint)buffer.Length, 0, 0, out _, 0))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    /// <summary>Enlace simbólico de carpeta; <see langword="false"/> si no se puede crear sin elevar (sin modo de desarrollador).</summary>
    public static bool TryCreateDirectorySymlink(string link, string target) =>
        CreateSymbolicLink(link, target, SymbolicLinkFlagDirectory | SymbolicLinkFlagAllowUnprivilegedCreate);

    /// <summary><c>Volume{guid}\</c> del volumen de <paramref name="path"/>, o <see langword="null"/>.</summary>
    public static string? VolumeGuidRoot(string path)
    {
        var buffer = new char[64];
        if (!GetVolumeNameForVolumeMountPoint(System.IO.Path.GetPathRoot(path) ?? @"C:\", buffer, (uint)buffer.Length))
        {
            return null;
        }

        var name = new string(buffer, 0, Array.IndexOf(buffer, '\0'));
        return name.StartsWith(@"\\?\", StringComparison.Ordinal) ? name[4..] : null;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumeNameForVolumeMountPoint(string mountPoint, [Out] char[] volumeName, uint length);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string fileName, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint code, byte[] inBuffer, uint inSize, nint outBuffer, uint outSize, out uint returned, nint overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateSymbolicLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool CreateSymbolicLink(string link, string target, uint flags);
}

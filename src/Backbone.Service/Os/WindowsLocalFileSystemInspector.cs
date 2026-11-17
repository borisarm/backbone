using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static Backbone.Service.Os.NativeMethods;

namespace Backbone.Service.Os;

/// <summary>
/// <see cref="ILocalFileSystemInspector"/> sobre la API de Windows: todo por handle (<c>CreateFile</c> con
/// <c>FILE_FLAG_BACKUP_SEMANTICS</c>), con rutas largas (<c>\\?\</c>, RNF-09).
/// </summary>
internal sealed class WindowsLocalFileSystemInspector : ILocalFileSystemInspector
{
    private const string LongPrefix = @"\\?\";
    private const string LongUncPrefix = @"\\?\UNC\";
    private const string VolumeGuidPrefix = @"\\?\Volume{";
    private const int MaxPathPlusOne = 261;

    public LocalFolderInfo InspectFolder(string path)
    {
        if (!TryGetLocalFullPath(path, out var fullPath))
        {
            return new LocalFolderInfo(LocalFolderStatus.InvalidPath);
        }

        // Una letra de unidad asignada a un recurso de red no se abre: no se accede a la red desde la validación.
        switch (GetDriveType(fullPath[..3]))
        {
            case DriveRemote:
                return new LocalFolderInfo(LocalFolderStatus.Remote);
            case DriveNoRootDir:
                return new LocalFolderInfo(LocalFolderStatus.NotFound);
        }

        using var handle = OpenFollowing(fullPath, FileReadAttributes, out var error);
        if (handle is null)
        {
            return new LocalFolderInfo(error is ErrorFileNotFound or ErrorPathNotFound ? LocalFolderStatus.NotFound : LocalFolderStatus.Unreadable);
        }

        if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfo, out FileAttributeTagInformation tag, (uint)Marshal.SizeOf<FileAttributeTagInformation>()))
        {
            return new LocalFolderInfo(LocalFolderStatus.Unreadable);
        }

        if ((tag.FileAttributes & FileAttributeDirectory) == 0)
        {
            return new LocalFolderInfo(LocalFolderStatus.NotADirectory);
        }

        if (!TryGetFinalPath(handle, FileNameNormalizedVolumeNameDos, out var dosPath))
        {
            return new LocalFolderInfo(LocalFolderStatus.Unreadable);
        }

        var finalPath = StripLongPrefix(dosPath);
        if (finalPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            // Un enlace que lleva a un recurso de red.
            return new LocalFolderInfo(LocalFolderStatus.Ok, finalPath, string.Empty, LocalVolumeKind.Remote);
        }

        var volume = LocalVolumeKind.Other;
        if (TryGetFinalPath(handle, FileNameNormalizedVolumeNameGuid, out var guidPath) && TryGetVolumeRoot(guidPath, out var volumeRoot))
        {
            volume = GetDriveType(volumeRoot) switch
            {
                DriveFixed => LocalVolumeKind.Fixed,
                DriveRemote => LocalVolumeKind.Remote,
                DriveRemovable => LocalVolumeKind.Removable,
                _ => LocalVolumeKind.Other,
            };
        }

        var fileSystem = new char[MaxPathPlusOne];
        var fileSystemName = GetVolumeInformationByHandle(handle, 0, 0, 0, 0, 0, fileSystem, (uint)fileSystem.Length)
            ? new string(fileSystem, 0, Array.IndexOf(fileSystem, '\0') is var end and >= 0 ? end : fileSystem.Length)
            : string.Empty;

        return new LocalFolderInfo(LocalFolderStatus.Ok, finalPath, fileSystemName, volume);
    }

    public string GetFinalDirectoryPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        using var handle = OpenFollowing(fullPath, FileReadAttributes, out var error)
            ?? throw new IOException($"The folder '{fullPath}' could not be opened.", new Win32Exception(error));
        if (!TryGetFinalPath(handle, FileNameNormalizedVolumeNameDos, out var dosPath))
        {
            throw new IOException($"The final path of '{fullPath}' could not be obtained.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        return StripLongPrefix(dosPath);
    }

    public RealDirectoryScan FindRealDirectories(string directory, string namePrefix, int maxNames)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxNames);
        var fullPath = Path.GetFullPath(directory);
        using var handle = OpenFollowing(fullPath, FileListDirectory | Synchronize, out var error)
            ?? throw new IOException($"The folder '{fullPath}' could not be opened.", new Win32Exception(error));

        var names = new List<string>();
        var total = 0;
        var buffer = new byte[64 * 1024];
        var infoClass = FileFullDirectoryRestartInfo;
        while (true)
        {
            if (!GetFileInformationByHandleExBuffer(handle, infoClass, buffer, (uint)buffer.Length))
            {
                var code = Marshal.GetLastPInvokeError();
                if (code == ErrorNoMoreFiles)
                {
                    break;
                }

                throw new IOException($"The folder '{fullPath}' could not be enumerated.", new Win32Exception(code));
            }

            infoClass = FileFullDirectoryInfo;
            foreach (var name in ReadNames(buffer))
            {
                if (name is "." or ".." || !name.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (IsRealDirectoryOrUnknown(Path.Join(fullPath, name)))
                {
                    total++;
                    if (names.Count < maxNames)
                    {
                        names.Add(name);
                    }
                }
            }
        }

        return new RealDirectoryScan(names, total);
    }

    /// <summary>
    /// <c>FILE_FULL_DIR_INFO</c>: <c>NextEntryOffset</c> (0), atributos (56), <c>FileNameLength</c> en bytes (60) y nombre
    /// (68).
    /// </summary>
    private static List<string> ReadNames(byte[] buffer)
    {
        var names = new List<string>();
        var offset = 0;
        while (true)
        {
            var span = buffer.AsSpan(offset);
            var next = BinaryPrimitives.ReadUInt32LittleEndian(span);
            var nameBytes = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[60..]);
            names.Add(new string(MemoryMarshal.Cast<byte, char>(span.Slice(68, nameBytes))));
            if (next == 0)
            {
                return names;
            }

            offset += (int)next;
        }
    }

    /// <summary>
    /// Abre la entrada <b>sin seguir</b> puntos de reanálisis y comprueba sobre ese handle que es una carpeta real (M-B). Si
    /// no se puede abrir o consultar, se cuenta como carpeta (falla cerrado: como mucho se excluye algo de más).
    /// </summary>
    private static bool IsRealDirectoryOrUnknown(string path)
    {
        using var handle = CreateFile(
            LongPath(path),
            FileReadAttributes,
            FileShareRead | FileShareWrite | FileShareDelete,
            0,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            0);
        if (handle.IsInvalid)
        {
            // Desaparecida entre la enumeración y la apertura: ya no está.
            var error = Marshal.GetLastPInvokeError();
            return error is not ErrorFileNotFound and not ErrorPathNotFound;
        }

        if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfo, out FileAttributeTagInformation tag, (uint)Marshal.SizeOf<FileAttributeTagInformation>()))
        {
            return true;
        }

        return (tag.FileAttributes & FileAttributeReparsePoint) == 0 && (tag.FileAttributes & FileAttributeDirectory) != 0;
    }

    private static SafeFileHandle? OpenFollowing(string fullPath, uint access, out int error)
    {
        var handle = CreateFile(
            LongPath(fullPath),
            access,
            FileShareRead | FileShareWrite | FileShareDelete,
            0,
            OpenExisting,
            FileFlagBackupSemantics,
            0);
        if (handle.IsInvalid)
        {
            error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            return null;
        }

        error = ErrorSuccess;
        return handle;
    }

    private static bool TryGetFinalPath(SafeFileHandle handle, uint flags, out string path)
    {
        var buffer = new char[512];
        while (true)
        {
            var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, flags);
            if (length == 0)
            {
                path = string.Empty;
                return false;
            }

            if (length < buffer.Length)
            {
                path = new string(buffer, 0, (int)length);
                return true;
            }

            buffer = new char[length];
        }
    }

    private static bool TryGetVolumeRoot(string guidPath, out string root)
    {
        // \\?\Volume{xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}\ : 11 + 36 + 2 caracteres.
        const int RootLength = 49;
        if (guidPath.Length >= RootLength && guidPath.StartsWith(VolumeGuidPrefix, StringComparison.OrdinalIgnoreCase) && guidPath[RootLength - 1] == '\\')
        {
            root = guidPath[..RootLength];
            return true;
        }

        root = string.Empty;
        return false;
    }

    /// <summary>Ruta local absoluta con letra de unidad (<c>X:\…</c>), normalizada; nunca UNC ni <c>\\?\</c>/<c>\\.\</c>.</summary>
    internal static bool TryGetLocalFullPath(string path, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrEmpty(path) || path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/')
            || path.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        return fullPath.Length >= 3 && fullPath[1] == ':' && fullPath[2] == '\\';
    }

    private static string LongPath(string fullPath) =>
        fullPath.StartsWith(@"\\", StringComparison.Ordinal) ? fullPath : LongPrefix + fullPath;

    internal static string StripLongPrefix(string path)
    {
        if (path.StartsWith(LongUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[LongUncPrefix.Length..];
        }

        return path.StartsWith(LongPrefix, StringComparison.Ordinal) ? path[LongPrefix.Length..] : path;
    }
}

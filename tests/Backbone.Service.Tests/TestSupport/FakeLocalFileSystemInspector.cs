using System.Collections.Concurrent;
using Backbone.Service.Os;

namespace Backbone.Service.Tests.TestSupport;

/// <summary>Tipo de volumen simulado (en el real lo decide <c>FileFsDeviceInformation</c> de la raíz).</summary>
internal enum FakeVolumeKind
{
    Fixed = 0,
    Remote = 1,
    Removable = 2,
    Other = 3,
}

/// <summary>
/// Doble de <see cref="ILocalFileSystemInspector"/>: carpetas declaradas con su ruta final, sistema de archivos y volumen;
/// cualquier otra ruta no existe. <c>%ProgramData%</c> y sus carpetas apartadas también se declaran.
/// </summary>
internal sealed class FakeLocalFileSystemInspector : ILocalFileSystemInspector, IDisposable
{
    private readonly ConcurrentDictionary<string, LocalFolderInfo> _folders = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _finalDirectories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Carpetas apartadas reales que devuelve <see cref="FindRealDirectories"/> (sin filtrar por prefijo).</summary>
    public List<string> QuarantinedFolders { get; } = [];

    /// <summary>Si no es nulo, <see cref="FindRealDirectories"/> lanza esta excepción.</summary>
    public Exception? EnumerationFailure { get; set; }

    /// <summary>Número de veces que se ha calculado la lista de carpetas apartadas.</summary>
    public int EnumerationCount => _enumerationCount;

    /// <summary>Entradas que <see cref="FindRealDirectories"/> ha "abierto" (comprueba que para en la 101).</summary>
    public int OpenedEntries => _openedEntries;

    /// <summary>Rutas que ha recibido <see cref="ResolveFolder"/>, en orden.</summary>
    public ConcurrentQueue<string> Resolved { get; } = new();

    /// <summary>
    /// Rutas cuya resolución se queda colgada dentro de <see cref="SourceIoGuard.Run{T}"/> hasta que el guardián la cancela
    /// (plazo o llamada), o para siempre si <see cref="IgnoreCancellation"/>.
    /// </summary>
    public ConcurrentDictionary<string, bool> Hanging { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Las operaciones colgadas no atienden la cancelación (controlador que no vuelve): se liberan con <see cref="ReleaseHung"/>.</summary>
    public bool IgnoreCancellation { get; set; }

    /// <summary>Se señala cada vez que una resolución entra en una operación colgada.</summary>
    public SemaphoreSlim HangEntered { get; } = new(0);

    private readonly ManualResetEventSlim _releaseHung = new(false);
    private int _enumerationCount;
    private int _openedEntries;
    private int _concurrent;
    private int _maxConcurrent;

    /// <summary>Máximo de resoluciones simultáneas observadas.</summary>
    public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

    /// <summary>Declara una carpeta NTFS en un disco fijo; <paramref name="finalPath"/> por defecto es la propia ruta.</summary>
    public FakeLocalFileSystemInspector AddFolder(string path, string? finalPath = null, string fileSystem = "NTFS", FakeVolumeKind volume = FakeVolumeKind.Fixed)
    {
        _folders[path] = volume == FakeVolumeKind.Fixed
            ? new LocalFolderInfo(LocalFolderStatus.Ok, finalPath ?? path, fileSystem)
            : new LocalFolderInfo(LocalFolderStatus.NotLocal, Detail: "fake: not a fixed disk");
        return this;
    }

    public FakeLocalFileSystemInspector AddStatus(string path, LocalFolderStatus status)
    {
        _folders[path] = new LocalFolderInfo(status, Detail: "fake");
        return this;
    }

    /// <summary>Ruta final de una carpeta (<c>%ProgramData%</c>) con resolución correcta.</summary>
    public FakeLocalFileSystemInspector MapFinalDirectory(string path, string finalPath)
    {
        _finalDirectories[path] = finalPath;
        return this;
    }

    public void Dispose()
    {
        _releaseHung.Set();
        _releaseHung.Dispose();
        HangEntered.Dispose();
    }

    /// <summary>Libera las operaciones colgadas que no atienden la cancelación.</summary>
    public void ReleaseHung() => _releaseHung.Set();

    public LocalFolderInfo ResolveFolder(string normalizedPath, SourceIoGuard guard)
    {
        Resolved.Enqueue(normalizedPath);
        var now = Interlocked.Increment(ref _concurrent);
        try
        {
            int seen;
            while (now > (seen = Volatile.Read(ref _maxConcurrent)) && Interlocked.CompareExchange(ref _maxConcurrent, now, seen) != seen)
            {
            }

            if (Hanging.ContainsKey(normalizedPath))
            {
                guard.Run(token =>
                {
                    HangEntered.Release();
                    if (IgnoreCancellation)
                    {
                        _releaseHung.Wait(CancellationToken.None);
                    }
                    else
                    {
                        token.WaitHandle.WaitOne();
                    }

                    return 0;
                });
            }
            else
            {
                guard.Run(_ => 0);
            }

            if (_folders.TryGetValue(normalizedPath, out var info))
            {
                return info;
            }

            if (_finalDirectories.TryGetValue(normalizedPath, out var final))
            {
                return new LocalFolderInfo(LocalFolderStatus.Ok, final, "NTFS");
            }

            return new LocalFolderInfo(LocalFolderStatus.NotFound, Detail: "fake: not declared");
        }
        finally
        {
            Interlocked.Decrement(ref _concurrent);
        }
    }

    public RealDirectoryScan FindRealDirectories(string directory, string namePrefix, int maxNames, SourceIoGuard guard)
    {
        Interlocked.Increment(ref _enumerationCount);
        if (EnumerationFailure is { } failure)
        {
            throw failure;
        }

        var names = new List<string>();
        var found = 0;
        foreach (var name in QuarantinedFolders.Where(n => n.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase)))
        {
            Interlocked.Increment(ref _openedEntries);
            guard.Run(_ => 0);
            found++;
            if (found > maxNames)
            {
                return new RealDirectoryScan(names, LimitExceeded: true);
            }

            names.Add(name);
        }

        return new RealDirectoryScan(names, LimitExceeded: false);
    }
}

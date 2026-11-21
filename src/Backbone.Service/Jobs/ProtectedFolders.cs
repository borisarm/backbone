using System.ComponentModel;
using Backbone.Service.Hosting;
using Backbone.Service.Os;
using Microsoft.Extensions.Logging;

namespace Backbone.Service.Jobs;

/// <summary>Relación entre la ruta final de un origen y las carpetas protegidas (§8.3, ADR-16).</summary>
internal enum SourceProtectionKind
{
    /// <summary>El origen no tiene nada que ver con las carpetas protegidas.</summary>
    None = 0,

    /// <summary>El origen es una carpeta protegida o está dentro de ella: <c>SourceNotAllowed</c>.</summary>
    InsideProtectedFolder = 1,

    /// <summary>
    /// El origen contiene carpetas protegidas, que se excluyen siempre (<see cref="SourceProtection.MandatoryExclusions"/>):
    /// aviso <c>SourceContainsDataFolder</c>.
    /// </summary>
    ContainsProtectedFolders = 2,

    /// <summary>
    /// El origen contiene <c>%ProgramData%</c> pero la lista de carpetas apartadas no se ha podido completar (más de
    /// <see cref="ProtectedFolderResolver.MaxQuarantinedFolders"/>, o <c>%ProgramData%</c> no se ha podido enumerar): falla
    /// cerrado con <c>SourceNotAllowed</c> en la validación y <c>ValidationFailed</c> en la ejecución (M-B).
    /// </summary>
    ProtectedFoldersUnknown = 3,
}

/// <summary>Resultado de <see cref="ProtectedFolderSet.Classify"/>.</summary>
/// <param name="Kind">Relación.</param>
/// <param name="ProtectedPath">
/// Ruta que lo causa, para <c>ValidationIssue.Value</c>: la carpeta protegida (dentro), la carpeta de datos (contiene) o
/// <c>%ProgramData%</c> (lista incompleta). Vacía con <see cref="SourceProtectionKind.None"/>.
/// </param>
/// <param name="MandatoryExclusions">
/// Con <see cref="SourceProtectionKind.ContainsProtectedFolders"/>: ruta relativa al origen (separador <c>\</c>) de cada
/// carpeta protegida que contiene (<c>ProgramData\Backbone</c>, <c>ProgramData\Backbone.untrusted-…</c>), para
/// <c>BackupSource.MandatoryExclusions</c> (§8.3, capa 2).
/// </param>
internal sealed record SourceProtection(SourceProtectionKind Kind, string ProtectedPath, IReadOnlyList<string> MandatoryExclusions)
{
    public static SourceProtection None { get; } = new(SourceProtectionKind.None, string.Empty, []);

    /// <summary>El origen no se puede copiar (error <c>SourceNotAllowed</c>).</summary>
    public bool IsNotAllowed => Kind is SourceProtectionKind.InsideProtectedFolder or SourceProtectionKind.ProtectedFoldersUnknown;
}

/// <summary>
/// Carpetas protegidas en un momento dado (§8.3 "Carpetas protegidas"): la carpeta de datos <c>PD\Backbone</c> y las
/// carpetas apartadas reales <c>PD\Backbone.untrusted-*</c>, siendo <c>PD</c> la ruta final de <c>%ProgramData%</c>. Se
/// calcula de nuevo en cada validación y al empezar cada ejecución (<see cref="ProtectedFolderResolver.Resolve"/>).
/// </summary>
/// <param name="ProgramDataPath">Ruta final de <c>%ProgramData%</c> (sin <c>\\?\</c>), para los mensajes.</param>
/// <param name="ProgramDataGuidPath">
/// Ruta final de <c>%ProgramData%</c> en la forma <c>VOLUME_NAME_GUID</c> (<c>\\?\Volume{guid}\ProgramData</c>): la que se
/// compara con la ruta final <c>VOLUME_NAME_GUID</c> de cada origen (I-5). Vacía si no se ha podido resolver.
/// </param>
/// <param name="DataFolderName">Nombre de la carpeta de datos (<c>Backbone</c>).</param>
/// <param name="QuarantinedFolderNames">Nombres de las carpetas apartadas reales (como mucho 100).</param>
/// <param name="IsComplete">
/// <see langword="false"/> si hay más carpetas apartadas de las admitidas o no se ha podido enumerar <c>PD</c>: los orígenes
/// que contienen <c>PD</c> fallan cerrados.
/// </param>
internal sealed record ProtectedFolderSet(
    string ProgramDataPath,
    string ProgramDataGuidPath,
    string DataFolderName,
    IReadOnlyList<string> QuarantinedFolderNames,
    bool IsComplete)
{
    /// <summary>Prefijo de las carpetas apartadas (ADR-13): <c>Backbone.untrusted-</c>.</summary>
    public string QuarantinePrefix => DataFolderName + DataDirectoryGuard.QuarantineSuffix;

    /// <summary><c>PD\Backbone</c>.</summary>
    public string DataFolderPath => Path.Join(ProgramDataPath, DataFolderName);

    /// <summary>
    /// Clasifica la <b>ruta final</b> de un origen (sin puntos de reanálisis, como la de
    /// <see cref="ILocalFileSystemInspector.ResolveFolder"/>), comparando por segmentos completos sin distinguir mayúsculas.
    /// </summary>
    /// <param name="sourceGuidPath">
    /// Ruta final del origen en la forma <c>VOLUME_NAME_GUID</c> (<see cref="LocalFolderInfo.GuidPath"/>): se compara con
    /// <see cref="ProgramDataGuidPath"/>, nunca una letra con un GUID (I-5, §8.3 punto 2).
    /// </param>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Dentro: la ruta empieza por <c>PD</c> y el segmento siguiente es la carpeta de datos o empieza por el prefijo de
    /// las apartadas. Como la ruta final no contiene puntos de reanálisis, ese segmento es una carpeta real situada en
    /// <c>PD</c>: la ubicación manda, nunca el destino de un enlace (un <i>junction</i> <c>PD\Backbone.untrusted-x</c> hacia
    /// otra carpeta no aparece en la ruta final de esa carpeta). Esta regla no depende de la lista, así que vale también
    /// con más de 100 apartadas.</item>
    /// <item>Contiene: <c>PD</c> es la ruta del origen o está dentro de ella. Si la lista está incompleta, falla cerrado.</item>
    /// </list>
    /// </remarks>
    public SourceProtection Classify(string sourceGuidPath)
    {
        ArgumentNullException.ThrowIfNull(sourceGuidPath);
        var source = Segments(sourceGuidPath);
        var programData = Segments(ProgramDataGuidPath);
        if (source.Length == 0)
        {
            return SourceProtection.None;
        }

        if (programData.Length == 0)
        {
            // Sin la ruta de volumen de PD no se puede saber si el origen la contiene: falla cerrado.
            return new SourceProtection(SourceProtectionKind.ProtectedFoldersUnknown, ProgramDataPath, []);
        }

        if (source.Length > programData.Length && StartsWith(source, programData))
        {
            var child = source[programData.Length];
            if (string.Equals(child, DataFolderName, StringComparison.OrdinalIgnoreCase)
                || child.StartsWith(QuarantinePrefix, StringComparison.OrdinalIgnoreCase))
            {
                return new SourceProtection(SourceProtectionKind.InsideProtectedFolder, Path.Join(ProgramDataPath, child), []);
            }

            return SourceProtection.None;
        }

        if (!StartsWith(programData, source))
        {
            return SourceProtection.None;
        }

        if (!IsComplete)
        {
            return new SourceProtection(SourceProtectionKind.ProtectedFoldersUnknown, ProgramDataPath, []);
        }

        var relativeProgramData = string.Join('\\', programData[source.Length..]);
        var exclusions = new List<string>(1 + QuarantinedFolderNames.Count);
        foreach (var name in (IEnumerable<string>)[DataFolderName, .. QuarantinedFolderNames])
        {
            exclusions.Add(relativeProgramData.Length == 0 ? name : relativeProgramData + "\\" + name);
        }

        return new SourceProtection(SourceProtectionKind.ContainsProtectedFolders, DataFolderPath, exclusions);
    }

    private static string[] Segments(string path) =>
        WindowsLocalFileSystemInspector.StripLongPrefix(path).Split('\\', StringSplitOptions.RemoveEmptyEntries);

    private static bool StartsWith(string[] path, string[] prefix)
    {
        if (prefix.Length > path.Length)
        {
            return false;
        }

        for (var i = 0; i < prefix.Length; i++)
        {
            if (!string.Equals(path[i], prefix[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Calcula las carpetas protegidas (§8.3, ADR-16, M-B). Lo usan la validación de trabajos (<c>ValidateJobAsync</c>,
/// <c>SaveJobAsync</c>) y el <c>JobRunner</c> al empezar cada ejecución, que repite <see cref="ProtectedFolderSet.Classify"/>
/// con la ruta final de cada origen y pasa <see cref="SourceProtection.MandatoryExclusions"/> al motor.
/// </summary>
/// <remarks>
/// <c>PD</c> es la ruta final de la carpeta que contiene la carpeta de datos (<c>%ProgramData%</c> en producción; una
/// carpeta temporal en las pruebas), obtenida con la misma resolución controlada que los orígenes
/// (<see cref="ILocalFileSystemInspector.ResolveFolder"/>, ADR-20): ningún código del servicio deja que el sistema siga un
/// enlace al abrir una ruta. Se llama desde el hilo de trabajo de la validación (<see cref="SourceValidationRunner"/>).
/// </remarks>
internal sealed partial class ProtectedFolderResolver(ServicePaths paths, ILocalFileSystemInspector fileSystem, ILogger<ProtectedFolderResolver> logger)
{
    /// <summary>Máximo de carpetas apartadas; con más, los orígenes que contienen <c>PD</c> fallan cerrados (M-B).</summary>
    public const int MaxQuarantinedFolders = 100;

    /// <exception cref="OperationCanceledException">Se ha cancelado la llamada.</exception>
    public ProtectedFolderSet Resolve(SourceIoGuard guard)
    {
        ArgumentNullException.ThrowIfNull(guard);
        var dataFolderName = Path.GetFileName(paths.Root);
        var parent = Path.GetDirectoryName(paths.Root) ?? paths.Root;

        string programData;
        string programDataGuid;
        try
        {
            if (SourcePath.TryNormalize(parent, out var normalized) != SourcePathCheck.Ok)
            {
                throw new IOException($"The folder '{parent}' is not a normalized local path.");
            }

            var folder = fileSystem.ResolveFolder(normalized, guard);
            if (folder.Status != LocalFolderStatus.Ok)
            {
                throw new IOException($"The folder '{parent}' could not be resolved ({folder.Status}: {folder.Detail}).");
            }

            programData = folder.FinalPath;
            programDataGuid = folder.GuidPath;
            if (programDataGuid.Length == 0)
            {
                throw new IOException($"The volume path of '{parent}' could not be obtained.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            LogProgramDataUnavailable(logger, ex, parent);
            return new ProtectedFolderSet(parent, string.Empty, dataFolderName, [], IsComplete: false);
        }

        var prefix = dataFolderName + DataDirectoryGuard.QuarantineSuffix;
        RealDirectoryScan scan;
        try
        {
            scan = fileSystem.FindRealDirectories(programData, prefix, MaxQuarantinedFolders, guard);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            LogProgramDataUnavailable(logger, ex, programData);
            return new ProtectedFolderSet(programData, programDataGuid, dataFolderName, [], IsComplete: false);
        }

        if (scan.LimitExceeded)
        {
            LogTooManyQuarantined(logger, MaxQuarantinedFolders, Path.Join(programData, prefix + "*"));
            return new ProtectedFolderSet(programData, programDataGuid, dataFolderName, [], IsComplete: false);
        }

        return new ProtectedFolderSet(programData, programDataGuid, dataFolderName, scan.Names, IsComplete: true);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se ha podido enumerar {ProgramData} para calcular las carpetas protegidas (§8.3); los orígenes que la contienen se rechazan.")]
    private static partial void LogProgramDataUnavailable(ILogger logger, Exception exception, string programData);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Hay más de {Max} carpetas apartadas: los orígenes que contienen %ProgramData% se rechazan hasta que un administrador revise {Pattern} (§8.3, R11).")]
    private static partial void LogTooManyQuarantined(ILogger logger, int max, string pattern);
}

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Backbone.Contracts;
using Backbone.Service.Ipc;
using Microsoft.Extensions.Logging;

namespace Backbone.Service.Persistence;

/// <summary>Lista actual de trabajos (solo lectura) y aviso de sus cambios.</summary>
internal interface IJobList
{
    /// <summary>Trabajos guardados, en el orden de <c>jobs.json</c>.</summary>
    ImmutableArray<JobDefinition> Jobs { get; }

    /// <summary>La lista ha cambiado (después de persistirla). Lo escuchan el estado (<c>StatusChanged</c>) y el scheduler.</summary>
    event EventHandler? Changed;
}

/// <summary>
/// <c>config\jobs.json</c> (§8.2, HU-07): lee y escribe la lista de trabajos con escritura atómica y notifica los cambios.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Acceso por handle con <see cref="TrustedFile"/> (ADR-14). La escritura es atómica: temporal con descriptor
/// explícito, volcado a disco (<c>FlushFileBuffers</c>) y renombrado por handle que reemplaza el destino (HU-07 criterio 5:
/// matar el servicio a mitad deja el archivo anterior completo o el nuevo completo, nunca uno a medias).</item>
/// <item>Si al arrancar <c>jobs.json</c> se rechaza (punto de reanálisis, carpeta, enlace duro o propietario no fiable),
/// <see cref="LoadAsync"/> lanza <see cref="UntrustedFileException"/> sin leer nada y el servicio no arranca (1002), igual que
/// con <c>settings.json</c>.</item>
/// <item>Si está dañado (JSON no válido o más de <see cref="MaxFileBytes"/>), la lista queda vacía y, antes de la
/// siguiente escritura, el archivo se aparta por handle como <c>jobs.json.bad</c> (no se borra ni se vuelve a leer).</item>
/// <item>Las entradas que no son un trabajo utilizable (nulas, sin identificador, con identificador repetido o fuera de los
/// límites de <see cref="ContractLimits"/>) se ignoran con aviso; las demás se cargan tal cual, sin volver a comprobar los
/// orígenes (pueden no estar disponibles en el arranque; lo comprueba cada ejecución).</item>
/// <item>Los temporales que haya dejado una escritura interrumpida (<c>.jobs.json.*.tmp</c>) se borran al cargar.</item>
/// <item>Una escritura que produciría un archivo de más de <see cref="MaxFileBytes"/> se rechaza (<c>LimitExceeded</c>),
/// para no escribir nunca algo que no se podría volver a leer.</item>
/// </list>
/// </remarks>
internal sealed partial class JobStore(string jobsPath, TrustedFile files, ILogger<JobStore> logger, int maxFileBytes = JobStore.MaxFileBytes)
    : IJobList, IDisposable
{
    /// <summary>Tamaño máximo de <c>jobs.json</c> (las pruebas pueden indicar otro en el constructor).</summary>
    internal const int MaxFileBytes = 64 * 1024 * 1024;

    /// <summary>Versión del esquema de <c>jobs.json</c>.</summary>
    internal const int SchemaVersion = 1;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        // Los nulos se escriben: en un trabajo tienen significado (p. ej. MinBatteryPercent = null, sin condición) y, si se
        // omitieran, al leer tomarían el valor por defecto del DTO.
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private JobsDocument _document = new();
    private bool _loadedFileWasCorrupt;
    private ImmutableArray<JobDefinition> _jobs = [];

    public event EventHandler? Changed;

    public string JobsPath { get; } = Path.GetFullPath(jobsPath);

    public ImmutableArray<JobDefinition> Jobs => _jobs;

    public void Dispose() => _writeLock.Dispose();

    /// <summary>Busca un trabajo por identificador.</summary>
    public JobDefinition? Find(Guid id)
    {
        foreach (var job in _jobs)
        {
            if (job.Id == id)
            {
                return job;
            }
        }

        return null;
    }

    /// <summary>Lee <c>jobs.json</c>. Solo se llama al arrancar, tras comprobar la carpeta de datos.</summary>
    /// <exception cref="UntrustedFileException">El archivo o su carpeta se han rechazado (§8.2): no se ha leído nada.</exception>
    public async Task LoadAsync(CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _document = await ReadDocumentAsync(ct).ConfigureAwait(false);
            _jobs = Normalize(_document.Jobs);
            RemoveLeftoverTemporaries();
            LogLoaded(logger, _jobs.Length);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Cambia la lista de forma serializada: <paramref name="change"/> recibe la lista actual (bajo el bloqueo de escritura,
    /// así que puede comprobar reglas que dependen de los demás trabajos sin carreras) y devuelve la nueva y un resultado.
    /// Si la lista no cambia, no se escribe nada. <see cref="Changed"/> se emite después de persistir.
    /// </summary>
    /// <exception cref="UntrustedFileException">El <c>jobs.json</c> existente se rechaza: no se escribe nada.</exception>
    /// <exception cref="BackboneException">La función la lanza (se propaga sin escribir), o el archivo superaría el límite.</exception>
    public async Task<T> UpdateAsync<T>(Func<ImmutableArray<JobDefinition>, (ImmutableArray<JobDefinition> Jobs, T Result)> change, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);
        bool changed;
        T result;
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            (var updated, result) = change(_jobs);
            changed = !updated.SequenceEqual(_jobs);
            if (changed)
            {
                var document = _document with { SchemaVersion = SchemaVersion, Jobs = [.. updated] };
                await WriteDocumentAsync(document, ct).ConfigureAwait(false);
                _document = document;
                _jobs = updated;
            }
        }
        finally
        {
            _writeLock.Release();
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return result;
    }

    private async Task<JobsDocument> ReadDocumentAsync(CancellationToken ct)
    {
        byte[]? bytes;
        try
        {
            bytes = await files.ReadAllBytesAsync(JobsPath, maxFileBytes, ct).ConfigureAwait(false);
        }
        catch (UntrustedFileException ex)
        {
            LogRejected(logger, ex.FilePath ?? JobsPath, ex.Reason);
            throw;
        }
        catch (InvalidDataException ex)
        {
            return Corrupt(ex);
        }

        if (bytes is null)
        {
            return new JobsDocument();
        }

        try
        {
            return JsonSerializer.Deserialize<JobsDocument>(bytes, JsonOptions) ?? Corrupt(new JsonException("null document"));
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            return Corrupt(ex);
        }
    }

    private JobsDocument Corrupt(Exception ex)
    {
        LogCorrupt(logger, JobsPath, ex.GetType().Name);
        _loadedFileWasCorrupt = true;
        return new JobsDocument();
    }

    private async Task WriteDocumentAsync(JobsDocument document, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (bytes.Length > maxFileBytes)
        {
            throw BackboneException.ValidationFailed(string.Empty, ValidationIssueCode.LimitExceeded);
        }

        try
        {
            if (_loadedFileWasCorrupt)
            {
                // Se aparta el archivo dañado por handle (validado como en la lectura), sin volver a leerlo.
                files.MoveReplacing(JobsPath, JobsPath + ".bad");
                _loadedFileWasCorrupt = false;
            }

            await files.WriteAllBytesAsync(JobsPath, bytes, ct).ConfigureAwait(false);
        }
        catch (UntrustedFileException ex)
        {
            LogRejected(logger, ex.FilePath ?? JobsPath, ex.Reason);
            throw;
        }
    }

    private ImmutableArray<JobDefinition> Normalize(IReadOnlyList<JobDefinition?>? jobs)
    {
        if (jobs is null)
        {
            return [];
        }

        var result = ImmutableArray.CreateBuilder<JobDefinition>();
        var ids = new HashSet<Guid>();
        for (var i = 0; i < jobs.Count; i++)
        {
            var job = jobs[i];
            if (job is null || job.Id == Guid.Empty || !ids.Add(job.Id) || !WithinLimits(job))
            {
                LogIgnoredEntry(logger, i);
                continue;
            }

            result.Add(job);
        }

        return result.ToImmutable();
    }

    private static bool WithinLimits(JobDefinition job)
    {
        try
        {
            IpcInputValidator.Job(job);
            return true;
        }
        catch (BackboneException)
        {
            return false;
        }
    }

    /// <summary>Temporales de una escritura interrumpida (<c>.jobs.json.&lt;guid&gt;.tmp</c>, <see cref="TrustedFile"/>).</summary>
    private void RemoveLeftoverTemporaries()
    {
        var directory = Path.GetDirectoryName(JobsPath);
        if (directory is null)
        {
            return;
        }

        IEnumerable<string> leftovers;
        try
        {
            leftovers = Directory.EnumerateFiles(directory, $".{Path.GetFileName(JobsPath)}.*.tmp").ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var leftover in leftovers)
        {
            try
            {
                files.DeleteIfExists(leftover);
            }
            catch (Exception ex) when (ex is UntrustedFileException or IOException or UnauthorizedAccessException)
            {
                LogLeftoverNotRemoved(logger, leftover, ex.GetType().Name);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Trabajos leídos: {Count}.")]
    private static partial void LogLoaded(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Path} no es válido ({ErrorType}): se arranca sin trabajos y se apartará como jobs.json.bad antes de la siguiente escritura.")]
    private static partial void LogCorrupt(ILogger logger, string path, string errorType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Se rechaza {Path} ({Reason}): no se lee ni se reemplaza (§8.2).")]
    private static partial void LogRejected(ILogger logger, string path, UntrustedFileReason reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Se ignora la entrada {Index} de jobs.json: no es un trabajo válido (sin identificador, repetido o fuera de límites).")]
    private static partial void LogIgnoredEntry(ILogger logger, int index);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se ha podido borrar el temporal {Path} ({ErrorType}).")]
    private static partial void LogLeftoverNotRemoved(ILogger logger, string path, string errorType);
}

/// <summary>Contenido de <c>jobs.json</c> (camelCase). Las propiedades desconocidas de primer nivel se conservan al reescribirlo.</summary>
internal sealed record JobsDocument
{
    /// <summary>Versión del esquema del archivo.</summary>
    public int SchemaVersion { get; init; } = JobStore.SchemaVersion;

    /// <summary>Trabajos.</summary>
    public IReadOnlyList<JobDefinition?> Jobs { get; init; } = [];

    /// <summary>Resto de propiedades (versiones más nuevas).</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

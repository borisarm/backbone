using System.Collections.Immutable;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Backbone.Service.Persistence;

/// <summary>Lista actual de usuarios autorizados (§7.3): solo SID, como en <c>settings.json → authorizedUsers</c>.</summary>
internal interface IAuthorizedUserList
{
    /// <summary>SID autorizados, normalizados (<see cref="SecurityIdentifier.Value"/>).</summary>
    ImmutableArray<string> Sids { get; }

    /// <summary>La lista ha cambiado (después de persistirla).</summary>
    event EventHandler? Changed;

    bool Contains(SecurityIdentifier sid);
}

/// <summary>
/// <c>config\settings.json</c> (§8.2). En HT-03 gestiona <c>authorizedUsers</c>; conserva intactas el resto de propiedades
/// (las añaden otras historias o el instalador) mediante <see cref="SettingsDocument.Extra"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Se lee después de comprobar la carpeta de datos (HT-03 I-1).</item>
/// <item>Si el archivo no existe, la lista está vacía. Si está dañado, también (falla cerrado) y se conserva una copia
/// <c>settings.json.bad</c> antes de la siguiente escritura.</item>
/// <item>Solo se aceptan SID de cuenta (<see cref="IsAccountSid"/>); el resto se ignora con aviso.</item>
/// </list>
/// </remarks>
internal sealed partial class SettingsStore(string settingsPath, ILogger<SettingsStore> logger) : IAuthorizedUserList, IDisposable
{
    /// <summary>Tamaño máximo que se lee de <c>settings.json</c>.</summary>
    internal const int MaxFileBytes = 1024 * 1024;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private SettingsDocument _document = new();
    private bool _loadedFileWasCorrupt;
    private ImmutableArray<string> _sids = [];

    public event EventHandler? Changed;

    public string SettingsPath { get; } = settingsPath;

    public ImmutableArray<string> Sids => _sids;

    public bool Contains(SecurityIdentifier sid) => _sids.Contains(sid.Value, StringComparer.OrdinalIgnoreCase);

    public void Dispose() => _writeLock.Dispose();

    /// <summary>Lee <c>settings.json</c>. Solo se llama al arrancar, tras comprobar la carpeta de datos.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _document = await ReadDocumentAsync(ct).ConfigureAwait(false);
            _sids = Normalize(_document.AuthorizedUsers);
            LogLoaded(logger, _sids.Length);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Añade un SID (idempotente). Devuelve <see langword="true"/> si la lista ha cambiado.</summary>
    public Task<bool> AddAuthorizedUserAsync(SecurityIdentifier sid, CancellationToken ct) =>
        UpdateAsync(list => list.Contains(sid.Value, StringComparer.OrdinalIgnoreCase) ? list : list.Add(sid.Value), ct);

    /// <summary>Quita un SID (idempotente). Devuelve <see langword="true"/> si la lista ha cambiado.</summary>
    public Task<bool> RemoveAuthorizedUserAsync(SecurityIdentifier sid, CancellationToken ct) =>
        UpdateAsync(list => list.RemoveAll(s => string.Equals(s, sid.Value, StringComparison.OrdinalIgnoreCase)), ct);

    internal static bool IsAccountSid(SecurityIdentifier sid) =>
        // Cuentas locales y de dominio (S-1-5-21-…) y de Microsoft Entra ID (S-1-12-1-…). Excluye SID bien conocidos,
        // grupos integrados (S-1-5-32-…) y cuentas de servicio (S-1-5-80-…).
        sid.Value.StartsWith("S-1-5-21-", StringComparison.Ordinal) || sid.Value.StartsWith("S-1-12-1-", StringComparison.Ordinal);

    private async Task<bool> UpdateAsync(Func<ImmutableArray<string>, ImmutableArray<string>> change, CancellationToken ct)
    {
        bool changed;
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var updated = change(_sids);
            changed = !updated.SequenceEqual(_sids, StringComparer.OrdinalIgnoreCase);
            if (changed)
            {
                var document = _document with { AuthorizedUsers = [.. updated] };
                await WriteDocumentAsync(document, ct).ConfigureAwait(false);
                _document = document;
                _sids = updated;
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

        return changed;
    }

    private async Task<SettingsDocument> ReadDocumentAsync(CancellationToken ct)
    {
        if (!File.Exists(SettingsPath))
        {
            return new SettingsDocument();
        }

        try
        {
            var info = new FileInfo(SettingsPath);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length > MaxFileBytes)
            {
                throw new InvalidDataException("settings.json is a reparse point or too large.");
            }

            var bytes = await File.ReadAllBytesAsync(SettingsPath, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<SettingsDocument>(bytes, JsonOptions) ?? new SettingsDocument();
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException)
        {
            // Falla cerrado: sin usuarios autorizados (solo SYSTEM y administradores elevados pueden conectar).
            LogCorrupt(logger, ex.GetType().Name);
            _loadedFileWasCorrupt = true;
            return new SettingsDocument();
        }
    }

    private async Task WriteDocumentAsync(SettingsDocument document, CancellationToken ct)
    {
        if (_loadedFileWasCorrupt && File.Exists(SettingsPath))
        {
            File.Copy(SettingsPath, SettingsPath + ".bad", overwrite: true);
            _loadedFileWasCorrupt = false;
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        await AtomicFile.WriteAllBytesAsync(SettingsPath, bytes, ct).ConfigureAwait(false);
    }

    private ImmutableArray<string> Normalize(IReadOnlyList<string?>? values)
    {
        var result = ImmutableArray.CreateBuilder<string>();
        for (var i = 0; i < (values?.Count ?? 0); i++)
        {
            var value = values![i];
            SecurityIdentifier? sid = null;
            try
            {
                sid = value is null ? null : new SecurityIdentifier(value);
            }
            catch (ArgumentException)
            {
            }

            if (sid is null || !IsAccountSid(sid))
            {
                LogIgnoredEntry(logger, i);
                continue;
            }

            if (!result.Contains(sid.Value, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(sid.Value);
            }
        }

        return result.ToImmutable();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Ajustes leídos: {Count} usuarios autorizados.")]
    private static partial void LogLoaded(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "settings.json no es válido ({ErrorType}); se usa una lista de usuarios autorizados vacía.")]
    private static partial void LogCorrupt(ILogger logger, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Se ignora la entrada {Index} de authorizedUsers: no es un SID de cuenta de usuario.")]
    private static partial void LogIgnoredEntry(ILogger logger, int index);
}

/// <summary>Contenido de <c>settings.json</c> (camelCase). Las propiedades desconocidas se conservan al reescribirlo.</summary>
internal sealed record SettingsDocument
{
    /// <summary>Versión del esquema del archivo.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>SID de los usuarios autorizados (§7.3).</summary>
    public IReadOnlyList<string?> AuthorizedUsers { get; init; } = [];

    /// <summary>Resto de propiedades (otras historias, versiones más nuevas).</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

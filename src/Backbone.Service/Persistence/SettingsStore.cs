using System.Collections.Immutable;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Backbone.Service.Os;
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
/// <item>Se lee después de comprobar la carpeta de datos (HT-03 I-1), por handle y sin seguir enlaces con
/// <see cref="TrustedFile"/> (§8.2, ADR-14). Si se rechaza (punto de reanálisis, carpeta, enlace duro o propietario que
/// no es SYSTEM ni Administradores), <see cref="LoadAsync"/> lanza <see cref="UntrustedFileException"/> sin leer nada y el
/// servicio no arranca (1002).</item>
/// <item>Si el archivo no existe, la lista está vacía. Si está dañado (JSON no válido o más de
/// <see cref="MaxFileBytes"/>), también (falla cerrado) y antes de la siguiente escritura se aparta, renombrándolo por
/// handle, como <c>settings.json.bad</c>.</item>
/// <item>Escritura atómica con <see cref="TrustedFile.WriteAllBytesAsync"/>. Si el <c>settings.json</c> existente se
/// rechaza al escribir (solo puede ocurrir si se ha sustituido después del arranque), la escritura no se hace, la lista en
/// memoria no cambia y la operación falla con <see cref="UntrustedFileException"/>; el siguiente arranque lo rechaza
/// (1002).</item>
/// <item>Solo se aceptan SID de cuenta (<see cref="IsAccountSid"/>); el resto se ignora con aviso. Si hay directorio de
/// cuentas, también se ignoran los SID que el sistema resuelve como algo distinto de una cuenta de usuario (un grupo de
/// dominio o local tiene el mismo prefijo); los que no se pueden resolver (controlador de dominio inaccesible, cuenta
/// borrada) se conservan, porque solo pueden autorizar a un token cuyo usuario sea exactamente ese SID.</item>
/// </list>
/// </remarks>
internal sealed partial class SettingsStore(
    string settingsPath,
    TrustedFile files,
    ILogger<SettingsStore> logger,
    IAccountDirectory? accounts = null)
    : IAuthorizedUserList, IDisposable
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
    /// <exception cref="UntrustedFileException">El archivo se ha rechazado (§8.2): no se ha leído nada.</exception>
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
        byte[]? bytes;
        try
        {
            bytes = await files.ReadAllBytesAsync(SettingsPath, MaxFileBytes, ct).ConfigureAwait(false);
        }
        catch (UntrustedFileException ex)
        {
            LogRejected(logger, ex.FilePath ?? SettingsPath, ex.Reason);
            throw;
        }
        catch (InvalidDataException ex)
        {
            return Corrupt(ex);
        }

        if (bytes is null)
        {
            return new SettingsDocument();
        }

        try
        {
            return JsonSerializer.Deserialize<SettingsDocument>(bytes, JsonOptions) ?? new SettingsDocument();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return Corrupt(ex);
        }
    }

    /// <summary>Falla cerrado: sin usuarios autorizados (solo SYSTEM y administradores elevados pueden conectar).</summary>
    private SettingsDocument Corrupt(Exception ex)
    {
        LogCorrupt(logger, ex.GetType().Name);
        _loadedFileWasCorrupt = true;
        return new SettingsDocument();
    }

    private async Task WriteDocumentAsync(SettingsDocument document, CancellationToken ct)
    {
        try
        {
            if (_loadedFileWasCorrupt)
            {
                // Se aparta el archivo dañado por handle (validado como en la lectura), sin volver a leerlo.
                files.MoveReplacing(SettingsPath, SettingsPath + ".bad");
                _loadedFileWasCorrupt = false;
            }

            var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
            await files.WriteAllBytesAsync(SettingsPath, bytes, ct).ConfigureAwait(false);
        }
        catch (UntrustedFileException ex)
        {
            LogRejected(logger, ex.FilePath ?? SettingsPath, ex.Reason);
            throw;
        }
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

            if (sid is null || !IsAccountSid(sid) || IsResolvedAsNonUser(sid))
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

    private bool IsResolvedAsNonUser(SecurityIdentifier sid)
    {
        if (accounts is null)
        {
            return false;
        }

        try
        {
            return accounts.LookupBySid(sid) is { Kind: not AccountKind.User };
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Ajustes leídos: {Count} usuarios autorizados.")]
    private static partial void LogLoaded(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "settings.json no es válido ({ErrorType}); se usa una lista de usuarios autorizados vacía.")]
    private static partial void LogCorrupt(ILogger logger, string errorType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Se rechaza {Path} ({Reason}): no se lee ni se reemplaza (§8.2).")]
    private static partial void LogRejected(ILogger logger, string path, UntrustedFileReason reason);

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

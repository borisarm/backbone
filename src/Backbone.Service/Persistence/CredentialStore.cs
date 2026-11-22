using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Backbone.Contracts;
using Backbone.Service.Os;
using Microsoft.Extensions.Logging;

namespace Backbone.Service.Persistence;

/// <summary>
/// Credenciales del NAS por recurso (<c>\\servidor\recurso</c>, RF-60), cifradas en reposo (ADR-07). Las usan las
/// operaciones IPC (guardar, consultar sin contraseña, borrar) y la conexión al recurso (<c>IShareConnector</c>, HU-05) en
/// las ejecuciones, también las programadas sin sesión iniciada.
/// </summary>
/// <remarks>
/// <paramref name="shareRoot"/> es siempre la raíz <b>normalizada</b> de <see cref="UncPaths"/> (minúsculas, sin barra
/// final); otra cosa es un error de programación (<see cref="ArgumentException"/>).
/// </remarks>
internal interface ICredentialStore
{
    /// <summary>Guarda (o sustituye) la credencial del recurso.</summary>
    /// <returns>Lo guardado, <b>sin contraseña</b>.</returns>
    /// <exception cref="UntrustedFileException">El archivo existente o la carpeta se rechazan (§8.2): no se escribe nada.</exception>
    Task<CredentialInfo> SaveAsync(string shareRoot, CredentialInput credential, CancellationToken ct);

    /// <summary>Usuario y fecha de la credencial guardada, sin contraseña (RF-62); <see langword="null"/> si no hay ninguna utilizable.</summary>
    /// <exception cref="UntrustedFileException">El archivo se rechaza (§8.2): no se lee.</exception>
    Task<CredentialInfo?> GetInfoAsync(string shareRoot, CancellationToken ct);

    /// <summary>
    /// Credencial completa para conectar con el recurso; <see langword="null"/> si no hay ninguna utilizable (el llamante
    /// responde <see cref="BackboneErrorCode.CredentialRequired"/>: nunca se conecta sin credencial explícita). El llamante
    /// la desecha en cuanto ha conectado, lo que pone a cero la contraseña.
    /// </summary>
    /// <exception cref="UntrustedFileException">El archivo se rechaza (§8.2): no se lee.</exception>
    Task<NasCredential?> GetAsync(string shareRoot, CancellationToken ct);

    /// <summary>Borra la credencial del recurso (idempotente).</summary>
    /// <returns><see langword="true"/> si existía.</returns>
    /// <exception cref="UntrustedFileException">El archivo se rechaza (§8.2): no se borra.</exception>
    Task<bool> DeleteAsync(string shareRoot, CancellationToken ct);
}

/// <summary>
/// Credencial descifrada para conectar con un recurso. La contraseña vive en un array fijado en memoria que
/// <see cref="Dispose"/> pone a cero; nunca se expone como <see cref="string"/> ni aparece en <see cref="ToString"/>.
/// </summary>
internal sealed class NasCredential : IDisposable
{
    private readonly int _passwordLength;
    private char[]? _password;

    /// <param name="password">Array fijado con la contraseña seguida de un NUL; pasa a ser propiedad de esta instancia.</param>
    /// <param name="isExplicit">
    /// La credencial no está guardada: la ha indicado el cliente (la de "Probar conexión"). Nunca comparte una conexión ajena
    /// (§8.2 "Conexiones al NAS", revisión final de seguridad de HU-05, M-1).
    /// </param>
    internal NasCredential(string shareRoot, string userName, DateTimeOffset updatedUtc, char[] password, int passwordLength, bool isExplicit)
    {
        if (passwordLength < 0 || passwordLength >= password.Length || password[passwordLength] != '\0')
        {
            throw new ArgumentException("The password buffer must be NUL-terminated.", nameof(password));
        }

        ShareRoot = shareRoot;
        UserName = userName;
        UpdatedUtc = updatedUtc;
        _password = password;
        _passwordLength = passwordLength;
        IsExplicit = isExplicit;
    }

    public string ShareRoot { get; }

    /// <summary>
    /// La credencial la ha indicado el cliente y no está guardada: su identidad no es la de ninguna credencial guardada, así que
    /// el gestor de conexiones nunca la comparte con otra conexión (M-1).
    /// </summary>
    public bool IsExplicit { get; }

    public string UserName { get; }

    public DateTimeOffset UpdatedUtc { get; }

    /// <summary>Contraseña (sin el NUL final).</summary>
    /// <exception cref="ObjectDisposedException">Ya se ha desechado.</exception>
    public ReadOnlySpan<char> Password => Buffer.AsSpan(0, _passwordLength);

    /// <summary>
    /// Contraseña con el NUL final, para pasarla a la API de Windows (<c>WNetAddConnection2</c>, HU-05) sin copiarla a un
    /// <see cref="string"/>. El array está fijado: su dirección no cambia mientras la instancia no se deseche.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Ya se ha desechado.</exception>
    public ReadOnlySpan<char> PasswordWithNullTerminator => Buffer.AsSpan(0, _passwordLength + 1);

    private char[] Buffer => _password ?? throw new ObjectDisposedException(nameof(NasCredential));

    public void Dispose()
    {
        if (_password is { } password)
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(password.AsSpan()));
            _password = null;
        }
    }

    /// <summary>Sin usuario ni contraseña; la raíz, saneada (<see cref="UntrustedText"/>), por si acaba en un log.</summary>
    public override string ToString() => $"NasCredential {{ ShareRoot = {UntrustedText.Describe(ShareRoot)}, UserName = ***, Password = *** }}";
}

/// <summary>Por qué un archivo de credenciales de propietario fiable no se puede usar.</summary>
internal enum CredentialFileProblem
{
    /// <summary>Mide más de <see cref="CredentialStore.MaxFileBytes"/>.</summary>
    TooLarge = 0,

    /// <summary>No empieza por la cabecera de Backbone o es de una versión desconocida.</summary>
    UnknownFormat = 1,

    /// <summary>DPAPI no lo descifra (otro equipo, otra entropía, datos alterados).</summary>
    NotDecryptable = 2,

    /// <summary>Se descifra pero el contenido no es válido o es de otro recurso.</summary>
    InvalidContent = 3,
}

/// <summary>
/// <c>credentials\&lt;sha256 de la raíz del recurso&gt;.bin</c> (§8.2, §8.3, ADR-07, RF-60, RF-62, RNF-06).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Formato</b>: cabecera en claro <c>BBCRED</c> + versión (1) y, detrás, un blob de DPAPI con ámbito
/// <b>equipo</b> (<see cref="DpapiMachineProtector"/>) y entropía propia (<see cref="EntropyPrefix"/> + la raíz del recurso,
/// de modo que el archivo de un recurso no se descifra como el de otro). Dentro del blob: versión, raíz del recurso,
/// usuario, fecha de actualización (UTC) y contraseña, en UTF-8 con longitudes. Nada del contenido (ni el usuario) queda en
/// claro.</item>
/// <item><b>Acceso</b>: siempre por <see cref="TrustedFile"/> (ADR-14): la carpeta <c>credentials\</c> la crea
/// <see cref="DataDirectoryGuard"/> al arrancar y aquí no se crea; lectura, escritura atómica (con la DACL protegida de
/// RNF-07) y borrado por handle.</item>
/// <item><b>Archivo rechazado</b> por <see cref="TrustedFile"/> (punto de reanálisis, carpeta, enlace duro, propietario no
/// fiable): no se lee, ni se sustituye, ni se borra; se registra el error y la operación falla (en IPC,
/// <see cref="BackboneErrorCode.Internal"/>; en una ejecución, fallida). Igual que <c>settings.json</c> después del
/// arranque (§8.2): solo un administrador puede haberlo dejado así (<c>credentials\</c> hereda la DACL de la raíz y el
/// arranque rechaza el contenido de propietario no fiable, evento 1002), así que se falla cerrado y se deja para que lo
/// revise.</item>
/// <item><b>Archivo de propietario fiable que no se puede usar</b> (<see cref="CredentialFileProblem"/>: p. ej. copiado de
/// otro equipo, que DPAPI con ámbito equipo no descifra): se registra un aviso, se trata como si no hubiera credencial
/// (<see cref="BackboneErrorCode.CredentialRequired"/> al conectar) y se sustituye al guardar una nueva. No se aparta como
/// <c>.bad</c>: su contenido no sirve para revisarlo y es material cifrado de una contraseña.</item>
/// <item><b>Secretos</b>: la contraseña en claro solo existe en arrays fijados que se ponen a cero tras cifrar o descifrar
/// (y en el <see cref="string"/> de <see cref="CredentialInput"/> recibido por IPC, que no se puede borrar). Nunca se
/// registra ni va en excepciones. Los registros solo llevan el nombre del archivo (un hash): ni el usuario ni la raíz del
/// recurso, que llegan como argumentos IPC (§7.3: "ni rutas ni datos de credenciales"). La raíz saneada con
/// <see cref="UntrustedText"/> solo se registra al usarla para conectar (<see cref="Nas.ShareCredentialProvider"/>).</item>
/// </list>
/// </remarks>
internal sealed partial class CredentialStore(
    string credentialsDirectory,
    TrustedFile files,
    IDataProtector protector,
    TimeProvider timeProvider,
    ILogger<CredentialStore> logger)
    : ICredentialStore, IDisposable
{
    /// <summary>Tamaño máximo que se lee de un archivo de credenciales.</summary>
    internal const int MaxFileBytes = 64 * 1024;

    /// <summary>Versión del formato del archivo y del contenido cifrado.</summary>
    internal const byte FormatVersion = 1;

    /// <summary>Codificación estricta: nunca sustituye caracteres (las entradas ya se han validado).</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>Cabecera en claro del archivo (seguida de <see cref="FormatVersion"/>).</summary>
    internal static ReadOnlySpan<byte> FileMagic => "BBCRED"u8;

    /// <summary>Entropía propia de DPAPI (ADR-07), seguida de la raíz del recurso en UTF-8.</summary>
    internal static ReadOnlySpan<byte> EntropyPrefix => "Backbone.CredentialStore.v1\0"u8;

    private static int HeaderLength => FileMagic.Length + 1;

    public string CredentialsDirectory { get; } = Path.GetFullPath(credentialsDirectory);

    /// <summary>
    /// Nombre del archivo de un recurso: SHA-256 en hexadecimal (minúsculas) de la raíz normalizada en UTF-8 <b>estricto</b>
    /// (una suplente suelta lanza en lugar de convertirse en U+FFFD y compartir archivo con otra raíz).
    /// </summary>
    /// <exception cref="EncoderFallbackException">La raíz tiene suplentes sueltas.</exception>
    internal static string FileNameFor(string shareRoot) =>
        Convert.ToHexStringLower(SHA256.HashData(StrictUtf8.GetBytes(shareRoot))) + ".bin";

    /// <summary>Entropía de DPAPI para un recurso (raíz en UTF-8 estricto, como <see cref="FileNameFor"/>).</summary>
    /// <exception cref="EncoderFallbackException">La raíz tiene suplentes sueltas.</exception>
    internal static byte[] EntropyFor(string shareRoot)
    {
        var root = StrictUtf8.GetBytes(shareRoot);
        var entropy = new byte[EntropyPrefix.Length + root.Length];
        EntropyPrefix.CopyTo(entropy);
        root.CopyTo(entropy.AsSpan(EntropyPrefix.Length));
        return entropy;
    }

    /// <summary>
    /// Texto que se puede guardar: sin suplentes sueltas ni NUL y, si <paramref name="allowControl"/> es
    /// <see langword="false"/>, sin caracteres de control. No revela qué carácter falla.
    /// </summary>
    internal static bool IsStorable(string value, bool allowControl)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    i++;
                    continue;
                }

                return false;
            }

            if (char.IsLowSurrogate(c) || c == '\0' || (!allowControl && char.IsControl(c)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Raíz normalizada del recurso de <paramref name="destination"/> (§7.5, A-3) para guardar o usar su credencial: reglas de
    /// <see cref="UncPaths.TryParse"/> con los nombres y direcciones del propio equipo (<see cref="LocalMachine"/>), y sin
    /// suplentes sueltas ni NUL en todo el valor (<see cref="IsStorable"/>; <see cref="UncPaths"/> no las rechaza y no se
    /// pueden codificar en UTF-8 estricto). La comparten las operaciones IPC y <see cref="Nas.ShareCredentialProvider"/>.
    /// </summary>
    /// <param name="destination">Destino UNC (<c>\\servidor\recurso[\ruta]</c>) o raíz del recurso.</param>
    /// <param name="shareRoot">Raíz normalizada, si es válido.</param>
    /// <param name="issue">Motivo del rechazo, si no lo es.</param>
    internal static bool TryNormalizeShareRoot(string? destination, out string shareRoot, out ValidationIssueCode issue)
    {
        if (!UncPaths.TryParse(destination, out shareRoot, out _, out issue, LocalMachine.Names(), LocalMachine.Addresses()))
        {
            return false;
        }

        if (destination is null || !IsStorable(destination, allowControl: false))
        {
            shareRoot = string.Empty;
            issue = ValidationIssueCode.DestinationNotAllowed;
            return false;
        }

        return true;
    }

    public string PathFor(string shareRoot) => Path.Combine(CredentialsDirectory, FileNameFor(RequireNormalized(shareRoot)));

    public void Dispose() => _writeLock.Dispose();

    public async Task<CredentialInfo> SaveAsync(string shareRoot, CredentialInput credential, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credential);
        var path = PathFor(shareRoot);
        var userName = credential.UserName;
        if (userName.Length is 0 or > ContractLimits.MaxAccountNameLength || string.IsNullOrWhiteSpace(userName)
            || credential.Password.Length > ContractLimits.MaxPasswordLength
            || !IsStorable(userName, allowControl: false) || !IsStorable(credential.Password, allowControl: true))
        {
            // Sin valores: el mensaje puede acabar en un log.
            throw new ArgumentException("The credential is not storable.", nameof(credential));
        }

        var updatedUtc = timeProvider.GetUtcNow().ToUniversalTime();
        var content = Encrypt(shareRoot, userName, credential.Password, updatedUtc);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await files.WriteAllBytesAsync(path, content, ct).ConfigureAwait(false);
        }
        catch (UntrustedFileException ex)
        {
            LogRejected(logger, ex.FilePath ?? path, ex.Reason);
            throw;
        }
        finally
        {
            _writeLock.Release();
        }

        var fileName = FileNameFor(shareRoot);
        LogSaved(logger, fileName);

        return new CredentialInfo { ShareRoot = shareRoot, UserName = userName, UpdatedUtc = updatedUtc };
    }

    public async Task<CredentialInfo?> GetInfoAsync(string shareRoot, CancellationToken ct)
    {
        using var credential = await ReadAsync(shareRoot, includePassword: false, ct).ConfigureAwait(false);
        return credential is null
            ? null
            : new CredentialInfo { ShareRoot = credential.ShareRoot, UserName = credential.UserName, UpdatedUtc = credential.UpdatedUtc };
    }

    public Task<NasCredential?> GetAsync(string shareRoot, CancellationToken ct) => ReadAsync(shareRoot, includePassword: true, ct);

    public async Task<bool> DeleteAsync(string shareRoot, CancellationToken ct)
    {
        var path = PathFor(shareRoot);
        bool deleted;
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            deleted = files.DeleteIfExists(path);
        }
        catch (UntrustedFileException ex)
        {
            LogRejected(logger, ex.FilePath ?? path, ex.Reason);
            throw;
        }
        finally
        {
            _writeLock.Release();
        }

        if (deleted)
        {
            var fileName = FileNameFor(shareRoot);
            LogDeleted(logger, fileName);
        }

        return deleted;
    }

    private static string RequireNormalized(string shareRoot)
    {
        if (!UncPaths.TryParse(shareRoot, out var root, out var normalized, out _)
            || !IsStorable(shareRoot, allowControl: false)
            || !string.Equals(root, shareRoot, StringComparison.Ordinal)
            || !string.Equals(normalized, shareRoot, StringComparison.Ordinal))
        {
            throw new ArgumentException("The share root must be normalized with UncPaths.", nameof(shareRoot));
        }

        return shareRoot;
    }

    /// <summary>Contenido del archivo: cabecera + blob DPAPI. El texto en claro se pone a cero antes de volver.</summary>
    private byte[] Encrypt(string shareRoot, string userName, string password, DateTimeOffset updatedUtc)
    {
        var rootLength = StrictUtf8.GetByteCount(shareRoot);
        var userLength = StrictUtf8.GetByteCount(userName);
        var passwordLength = StrictUtf8.GetByteCount(password);
        var size = 1 + (2 + rootLength) + (2 + userLength) + 8 + (2 + passwordLength);

        // Fijado: el GC no deja copias del texto en claro al compactar.
        var plaintext = GC.AllocateArray<byte>(size, pinned: true);
        try
        {
            var span = plaintext.AsSpan();
            span[0] = FormatVersion;
            var offset = 1;
            offset = WriteString(span, offset, shareRoot, rootLength);
            offset = WriteString(span, offset, userName, userLength);
            BinaryPrimitives.WriteInt64LittleEndian(span[offset..], updatedUtc.UtcTicks);
            offset += 8;
            WriteString(span, offset, password, passwordLength);

            var blob = protector.Protect(plaintext, EntropyFor(shareRoot));
            var content = new byte[HeaderLength + blob.Length];
            FileMagic.CopyTo(content);
            content[FileMagic.Length] = FormatVersion;
            blob.CopyTo(content.AsSpan(HeaderLength));
            return content;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static int WriteString(Span<byte> span, int offset, string value, int byteCount)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(span[offset..], checked((ushort)byteCount));
        offset += 2;
        StrictUtf8.GetBytes(value, span.Slice(offset, byteCount));
        return offset + byteCount;
    }

    private async Task<NasCredential?> ReadAsync(string shareRoot, bool includePassword, CancellationToken ct)
    {
        var path = PathFor(shareRoot);
        byte[]? content;
        try
        {
            content = await files.ReadAllBytesAsync(path, MaxFileBytes, ct).ConfigureAwait(false);
        }
        catch (UntrustedFileException ex)
        {
            LogRejected(logger, ex.FilePath ?? path, ex.Reason);
            throw;
        }
        catch (InvalidDataException)
        {
            return Unusable(shareRoot, CredentialFileProblem.TooLarge);
        }

        if (content is null)
        {
            return null;
        }

        if (content.Length <= HeaderLength || !content.AsSpan(0, FileMagic.Length).SequenceEqual(FileMagic) || content[FileMagic.Length] != FormatVersion)
        {
            return Unusable(shareRoot, CredentialFileProblem.UnknownFormat);
        }

        byte[] plaintext;
        try
        {
            plaintext = protector.Unprotect(content.AsSpan(HeaderLength), EntropyFor(shareRoot));
        }
        catch (CryptographicException)
        {
            return Unusable(shareRoot, CredentialFileProblem.NotDecryptable);
        }

        try
        {
            return TryParse(plaintext, shareRoot, includePassword) ?? Unusable(shareRoot, CredentialFileProblem.InvalidContent);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private NasCredential? Unusable(string shareRoot, CredentialFileProblem problem)
    {
        var fileName = FileNameFor(shareRoot);
        LogUnusable(logger, fileName, problem);
        return null;
    }

    /// <summary>Interpreta el contenido descifrado; <see langword="null"/> si no es válido o es de otro recurso.</summary>
    private static NasCredential? TryParse(ReadOnlySpan<byte> plaintext, string shareRoot, bool includePassword)
    {
        try
        {
            if (plaintext.Length < 1 || plaintext[0] != FormatVersion)
            {
                return null;
            }

            var offset = 1;
            if (!TryReadSlice(plaintext, ref offset, out var root) || !TryReadSlice(plaintext, ref offset, out var user)
                || plaintext.Length - offset < 8)
            {
                return null;
            }

            var ticks = BinaryPrimitives.ReadInt64LittleEndian(plaintext[offset..]);
            offset += 8;
            if (!TryReadSlice(plaintext, ref offset, out var password) || offset != plaintext.Length
                || ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks)
            {
                return null;
            }

            var storedRoot = StrictUtf8.GetString(plaintext.Slice(root.Start, root.Length));
            var userName = StrictUtf8.GetString(plaintext.Slice(user.Start, user.Length));
            if (!string.Equals(storedRoot, shareRoot, StringComparison.Ordinal)
                || userName.Length is 0 or > ContractLimits.MaxAccountNameLength)
            {
                return null;
            }

            var passwordBytes = plaintext.Slice(password.Start, password.Length);
            var passwordChars = includePassword ? StrictUtf8.GetCharCount(passwordBytes) : 0;
            if (passwordChars > ContractLimits.MaxPasswordLength)
            {
                return null;
            }

            // Fijado y con NUL final; si no se pide la contraseña, queda vacío.
            var buffer = GC.AllocateArray<char>(passwordChars + 1, pinned: true);
            if (includePassword)
            {
                StrictUtf8.GetChars(passwordBytes, buffer);
            }

            return new NasCredential(shareRoot, userName, new DateTimeOffset(ticks, TimeSpan.Zero), buffer, passwordChars, isExplicit: false);
        }
        catch (DecoderFallbackException)
        {
            // UTF-8 no válido. La excepción no se propaga: su mensaje incluye los bytes.
            return null;
        }
    }

    private static bool TryReadSlice(ReadOnlySpan<byte> data, ref int offset, out (int Start, int Length) slice)
    {
        slice = default;
        if (data.Length - offset < 2)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
        offset += 2;
        if (data.Length - offset < length)
        {
            return false;
        }

        slice = (offset, length);
        offset += length;
        return true;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Credencial guardada en la carpeta de credenciales: {FileName}.")]
    private static partial void LogSaved(ILogger logger, string fileName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Credencial borrada de la carpeta de credenciales: {FileName}.")]
    private static partial void LogDeleted(ILogger logger, string fileName);

    [LoggerMessage(Level = LogLevel.Error, Message = "Se rechaza el archivo de credenciales {Path} ({Reason}): no se lee, ni se sustituye, ni se borra (§8.2).")]
    private static partial void LogRejected(ILogger logger, string path, UntrustedFileReason reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "La credencial guardada en {FileName} (carpeta de credenciales) no se puede usar ({Problem}); se trata como si no hubiera ninguna y se sustituirá al guardar otra.")]
    private static partial void LogUnusable(ILogger logger, string fileName, CredentialFileProblem problem);
}

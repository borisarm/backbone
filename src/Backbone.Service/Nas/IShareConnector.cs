using Backbone.Contracts;
using Backbone.Service.Persistence;

namespace Backbone.Service.Nas;

/// <summary>
/// Conexión al recurso SMB del NAS (§8.2 <c>ShareConnector</c>): <c>WNetAddConnection2</c> sin letra de unidad con una
/// credencial explícita y <c>WNetCancelConnection2</c> al terminar. Toda la integración con el SO para llegar al NAS pasa
/// por aquí, para poder simularla en pruebas.
/// </summary>
internal interface IShareConnector
{
    /// <summary>
    /// Conecta con el recurso <see cref="NasCredential.ShareRoot"/> usando el usuario y la contraseña de
    /// <paramref name="credential"/>. La credencial sigue siendo del llamante, que no la desecha hasta que esta tarea termina
    /// (la API de Windows lee la contraseña durante la llamada).
    /// </summary>
    /// <remarks>
    /// La llamada al sistema no se puede interrumpir: <paramref name="ct"/> solo evita empezarla. El plazo y el abandono los
    /// gestiona el llamante, que se encarga de desechar la conexión si la tarea termina bien después de abandonarla.
    /// </remarks>
    /// <exception cref="ShareException">El sistema rechaza la conexión; <see cref="ShareException.Code"/> dice por qué.</exception>
    Task<IShareConnection> ConnectAsync(NasCredential credential, CancellationToken ct);

    /// <summary>
    /// Cierra a la fuerza la conexión con <paramref name="shareRoot"/> (<c>WNetCancelConnection2</c> con <c>fForce</c>), aunque
    /// tenga archivos abiertos. Solo para la parada del servicio (<see cref="ShareConnectionShutdown"/>).
    /// </summary>
    Task ForceDisconnectAsync(string shareRoot, CancellationToken ct);
}

/// <summary>
/// Conexión abierta con un recurso. <see cref="IAsyncDisposable.DisposeAsync"/> la cierra (idempotente). Las rutas son UNC
/// completas dentro de <see cref="ShareRoot"/>.
/// </summary>
/// <remarks>
/// Cada operación falla con <see cref="ShareException"/>. Como en <see cref="IShareConnector.ConnectAsync"/>, la E/S de red
/// no siempre se puede interrumpir: el llamante pone el plazo y no desecha la conexión hasta que terminan las operaciones
/// pendientes.
/// </remarks>
internal interface IShareConnection : IAsyncDisposable
{
    /// <summary>Raíz normalizada del recurso, <c>\\servidor\recurso</c>.</summary>
    string ShareRoot { get; }

    /// <summary>
    /// Tras <see cref="IAsyncDisposable.DisposeAsync"/>: la desconexión (<c>WNetCancelConnection2</c> sin forzar) ha fallado y la
    /// conexión puede seguir viva en el sistema (p. ej. <c>ERROR_OPEN_FILES</c>). <see cref="ShareConnectionManager"/> lo guarda
    /// para reintentarla a la fuerza y para cerrarla en la parada del servicio.
    /// </summary>
    bool DisconnectFailed { get; }

    /// <summary>
    /// Indica si el sistema sigue teniendo el registro de uso (la conexión con credencial explícita) de <see cref="ShareRoot"/>.
    /// Sin él, la E/S saldría con la cuenta del equipo (§7.5 A-3): p. ej. si una desconexión pendiente lo ha borrado (§8.2 B-1).
    /// Quien va a escribir lo comprueba antes de la primera E/S.
    /// </summary>
    Task<bool> HasUseRecordAsync(CancellationToken ct);

    /// <summary>Indica si <paramref name="path"/> existe y es una carpeta; un error cuenta como "no".</summary>
    Task<bool> DirectoryExistsAsync(string path, CancellationToken ct);

    /// <summary>
    /// Ruta final de la carpeta abierta (<c>GetFinalPathNameByHandle</c>, sin normalizar), p. ej.
    /// <c>\?\UNC\servidor\recurso\carpeta</c>; <see langword="null"/> si el redirector no la sabe dar.
    /// </summary>
    Task<string?> GetFinalPathAsync(string path, CancellationToken ct);

    /// <summary>Lee el contenido de la carpeta (comprobación de lectura, RF-61).</summary>
    Task ListDirectoryAsync(string path, CancellationToken ct);

    /// <summary>Espacio libre (disponible para el usuario de la conexión) y total del volumen del recurso.</summary>
    Task<ShareSpace> GetSpaceAsync(string path, CancellationToken ct);

    /// <summary>Dialecto SMB negociado (p. ej. <c>3.1.1</c>) o <see langword="null"/> si el protocolo no es SMB.</summary>
    Task<string?> GetSmbDialectAsync(string path, CancellationToken ct);

    /// <summary>
    /// Crea <paramref name="path"/> (falla si ya existe), escribe <paramref name="content"/>, lo vuelve a leer y comprueba que
    /// coincide. El archivo se crea con borrado al cerrar (<c>FILE_FLAG_DELETE_ON_CLOSE</c>), así que, si el servidor lo
    /// respeta, desaparece al terminar aunque algo falle; el llamante comprueba después que ya no está.
    /// </summary>
    /// <param name="path">Archivo de prueba.</param>
    /// <param name="content">Contenido.</param>
    /// <param name="finalRoot">
    /// Ruta final (<c>GetFinalPathNameByHandle</c>, sin normalizar, p. ej. <c>\\?\UNC\servidor\recurso</c>) de la raíz conectada.
    /// <b>Sobre el handle ya abierto del archivo y antes de escribir nada</b> (§8.2 "Conexiones al NAS", B-3), se obtiene su
    /// ruta final y tiene que estar dentro de <paramref name="finalRoot"/>; si no se puede obtener o está fuera, falla con
    /// <see cref="BackboneErrorCode.AccessDenied"/> sin haber escrito.
    /// </param>
    /// <param name="ct">Cancelación.</param>
    Task WriteProbeFileAsync(string path, ReadOnlyMemory<byte> content, string finalRoot, CancellationToken ct);

    /// <summary>Indica si existe el archivo <paramref name="path"/>.</summary>
    Task<bool> FileExistsAsync(string path, CancellationToken ct);

    /// <summary>Borra el archivo <paramref name="path"/>; no es un error que no exista.</summary>
    Task DeleteFileAsync(string path, CancellationToken ct);
}

/// <summary>Espacio del volumen de un recurso, en bytes.</summary>
internal readonly record struct ShareSpace(long FreeBytes, long TotalBytes);

/// <summary>
/// Fallo de una operación sobre el recurso, traducido a <see cref="BackboneErrorCode"/> (<see cref="ShareErrors"/>).
/// </summary>
/// <remarks>
/// El mensaje solo lleva el código Win32: nunca la ruta, el nombre del recurso, el usuario ni textos del servidor.
/// </remarks>
internal sealed class ShareException : Exception
{
    public ShareException()
        : this(BackboneErrorCode.NasUnreachable, 0)
    {
    }

    public ShareException(string message)
        : base(message)
    {
        Code = BackboneErrorCode.NasUnreachable;
    }

    public ShareException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = BackboneErrorCode.NasUnreachable;
    }

    public ShareException(BackboneErrorCode code, int nativeErrorCode)
        : base($"Share operation failed with Win32 error {nativeErrorCode}.")
    {
        Code = code;
        NativeErrorCode = nativeErrorCode;
    }

    /// <summary>Motivo, para <see cref="ConnectionTestResult.ErrorCode"/> o el error de una ejecución.</summary>
    public BackboneErrorCode Code { get; }

    /// <summary>Código Win32 original (0 si no lo hay), para el log.</summary>
    public int NativeErrorCode { get; }
}

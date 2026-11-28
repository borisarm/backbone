namespace Backbone.Service.Os;

/// <summary>
/// Una instantánea VSS de un volumen (§8.2 <c>VssSnapshotter</c>, RF-22).
/// </summary>
/// <param name="Volume">Volumen de origen en la forma <c>\\?\Volume{guid}\</c> (la de <c>VOLUME_NAME_GUID</c>).</param>
/// <param name="DevicePath">
/// Raíz de la instantánea: <c>\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy&lt;n&gt;</c> (sin barra final). Es la raíz que
/// admite <see cref="Sources.SourceRoots"/> ("Raíces de dispositivo", B-2) y se compone con la ruta relativa del origen.
/// </param>
/// <param name="CreatedUtc">Instante de creación que da VSS; el mismo para todas las instantáneas de un conjunto.</param>
internal sealed record VolumeSnapshot(string Volume, string DevicePath, DateTimeOffset CreatedUtc)
{
    /// <summary>Nombre del dispositivo (<c>HarddiskVolumeShadowCopy&lt;n&gt;</c>), el último segmento de <see cref="DevicePath"/>.</summary>
    public string DeviceName => DevicePath[(DevicePath.LastIndexOf('\\') + 1)..];
}

/// <summary>
/// Las instantáneas creadas para una ejecución, en un único conjunto de VSS (mismo instante, RF-22, HU-15 criterio 5). Al
/// desecharla se eliminan, también si la ejecución falla, se cancela o el servicio se detiene.
/// </summary>
internal interface IVolumeSnapshotSet : IAsyncDisposable
{
    /// <summary>Una instantánea por volumen pedido.</summary>
    IReadOnlyList<VolumeSnapshot> Snapshots { get; }
}

/// <summary>
/// Crea y elimina instantáneas VSS (§8.2). Implementación real: <see cref="VssSnapshotter"/>; las pruebas usan un doble.
/// </summary>
internal interface IVolumeSnapshotter
{
    /// <summary>
    /// Crea <b>un único conjunto</b> de instantáneas, una por volumen (todas del mismo instante). Si no puede (VSS parado, sin
    /// espacio, volumen no admitido, escritores en mal estado…) lanza <see cref="VolumeSnapshotException"/> sin dejar nada
    /// creado: el <c>JobRunner</c> copia entonces en vivo (RF-22, evento 4000).
    /// </summary>
    /// <param name="volumes">
    /// Volúmenes en la forma <c>\\?\Volume{guid}\</c>, sin repetir (se compara sin distinguir mayúsculas) y al menos uno.
    /// </param>
    /// <param name="cancellationToken">Cancelación: no deja ninguna instantánea creada.</param>
    /// <exception cref="VolumeSnapshotException">VSS no ha podido crear el conjunto.</exception>
    /// <exception cref="OperationCanceledException">Se canceló.</exception>
    Task<IVolumeSnapshotSet> CreateAsync(IReadOnlyCollection<string> volumes, CancellationToken cancellationToken);

    /// <summary>
    /// Elimina las instantáneas que Backbone creó en una ejecución anterior del servicio y no llegó a eliminar (HU-15 criterio
    /// 3). <b>Solo</b> las registradas por Backbone; nunca las de otro programa. No lanza por fallos de VSS (los registra y
    /// reintenta en el siguiente arranque); devuelve cuántas ha eliminado.
    /// </summary>
    Task<int> DeleteOrphansAsync(CancellationToken cancellationToken);
}

/// <summary>VSS no ha podido crear las instantáneas (RF-22); la copia sigue en vivo.</summary>
internal sealed class VolumeSnapshotException : Exception
{
    public VolumeSnapshotException()
    {
    }

    public VolumeSnapshotException(string message)
        : base(message)
    {
    }

    public VolumeSnapshotException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public VolumeSnapshotException(string message, int hresult, Exception? innerException = null)
        : base(message, innerException)
    {
        HResult = hresult;
    }
}

using System.Runtime.InteropServices;

namespace Backbone.Service.Os;

/// <summary>Un método de VSS ha fallado (<see cref="Exception.HResult"/> es el <c>HRESULT</c>).</summary>
internal sealed class VssException : Exception
{
    public VssException()
    {
    }

    public VssException(string message)
        : base(message)
    {
    }

    public VssException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public VssException(int hresult, string operation)
        : base($"VSS {operation} failed (HRESULT 0x{unchecked((uint)hresult):X8}).")
    {
        HResult = hresult;
        Operation = operation;
    }

    /// <summary>Método de VSS que falló.</summary>
    public string Operation { get; } = string.Empty;

    /// <summary>El objeto (instantánea, conjunto) ya no existe: para quien lo elimina, es lo mismo que haberlo eliminado.</summary>
    public bool IsObjectNotFound => HResult == NativeMethods.Vss.VssEObjectNotFound;
}

/// <summary>Propiedades de una instantánea (<c>VSS_SNAPSHOT_PROP</c>).</summary>
/// <param name="SnapshotId">Identificador de la instantánea.</param>
/// <param name="SnapshotSetId">Identificador del conjunto.</param>
/// <param name="OriginalVolume">Volumen de origen (<c>\\?\Volume{guid}\</c>).</param>
/// <param name="DeviceObject">Dispositivo de la instantánea tal como lo da VSS (<c>\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN</c>).</param>
/// <param name="Attributes"><c>VSS_VOLUME_SNAPSHOT_ATTRIBUTES</c> (contexto con el que se creó).</param>
/// <param name="CreatedUtc">Instante de creación.</param>
internal sealed record VssSnapshotProperties(
    Guid SnapshotId,
    Guid SnapshotSetId,
    string OriginalVolume,
    string DeviceObject,
    int Attributes,
    DateTimeOffset CreatedUtc);

/// <summary>
/// Un objeto <c>IVssBackupComponents</c> de VSS: sirve para <b>una</b> copia o consulta y se suelta con
/// <see cref="IDisposable.Dispose"/> (que, en el contexto de copia, elimina también las instantáneas que no se hubieran
/// eliminado). Todas las llamadas se hacen desde el mismo hilo (el de <see cref="VssSnapshotter"/>). Implementación real:
/// <see cref="VssBackupComponents"/>; las pruebas usan un doble.
/// </summary>
internal interface IVssBackup : IDisposable
{
    /// <summary><c>SetContext</c>.</summary>
    void SetContext(int context);

    /// <summary><c>SetBackupState(false, false, VSS_BT_COPY, false)</c>: copia sin seleccionar componentes que no altera el historial de los escritores.</summary>
    void SetBackupState();

    /// <summary><c>GatherWriterMetadata</c> y espera.</summary>
    void GatherWriterMetadata(CancellationToken cancellationToken);

    /// <summary><c>StartSnapshotSet</c>: crea un conjunto vacío y devuelve su identificador.</summary>
    Guid StartSnapshotSet();

    /// <summary><c>AddToSnapshotSet</c> con el proveedor predeterminado: devuelve el identificador de la instantánea (aún no existe).</summary>
    Guid AddToSnapshotSet(string volume);

    /// <summary><c>PrepareForBackup</c> y espera.</summary>
    void PrepareForBackup(CancellationToken cancellationToken);

    /// <summary><c>DoSnapshotSet</c> y espera: crea todas las instantáneas del conjunto a la vez.</summary>
    void DoSnapshotSet(CancellationToken cancellationToken);

    /// <summary><c>GetSnapshotProperties</c>.</summary>
    /// <exception cref="VssException">No existe (<see cref="VssException.IsObjectNotFound"/>) o falla.</exception>
    VssSnapshotProperties GetSnapshotProperties(Guid snapshotId);

    /// <summary><c>BackupComplete</c> y espera.</summary>
    void BackupComplete(CancellationToken cancellationToken);

    /// <summary><c>AbortBackup</c>.</summary>
    void AbortBackup();

    /// <summary><c>DeleteSnapshots</c> de un conjunto (<paramref name="isSet"/>) o de una instantánea; devuelve cuántas eliminó.</summary>
    /// <exception cref="VssException">No existe (<see cref="VssException.IsObjectNotFound"/>) o falla.</exception>
    int DeleteSnapshots(Guid id, bool isSet);
}

/// <summary>Crea objetos <see cref="IVssBackup"/> (<c>CreateVssBackupComponents</c> + <c>InitializeForBackup</c>).</summary>
internal interface IVssBackupFactory
{
    IVssBackup Create();
}

/// <inheritdoc cref="IVssBackupFactory"/>
internal sealed class VssBackupFactory : IVssBackupFactory
{
    public IVssBackup Create() => VssBackupComponents.Create();
}

/// <summary>
/// <see cref="IVssBackup"/> real: llama a <c>IVssBackupComponents</c> por su tabla de métodos, porque la interfaz no tiene
/// proyección .NET. Posiciones (después de los tres de <c>IUnknown</c>) tomadas del orden de declaración en <c>vsbackup.h</c>.
/// </summary>
/// <remarks>
/// Los <c>VSS_ID</c> (<c>GUID</c>, 16 bytes) se pasan por valor: el CLR sigue la ABI de la plataforma (en x64 se pasa un puntero
/// a una copia, como hace el compilador de C++). <c>bool</c> de C++ es un byte; <c>BOOL</c>, un entero. Cada <c>IVssAsync</c>
/// se espera con sondeo corto para poder atender la cancelación (<c>Cancel</c>) y se suelta siempre.
/// </remarks>
internal sealed unsafe class VssBackupComponents : IVssBackup
{
    // Posiciones en la tabla de métodos de IVssBackupComponents (vsbackup.h): IUnknown ocupa 0 a 2.
    private const int InitializeForBackupSlot = 5;
    private const int SetBackupStateSlot = 6;
    private const int GatherWriterMetadataSlot = 9;
    private const int PrepareForBackupSlot = 14;
    private const int AbortBackupSlot = 15;
    private const int BackupCompleteSlot = 27;
    private const int SetContextSlot = 35;
    private const int StartSnapshotSetSlot = 36;
    private const int AddToSnapshotSetSlot = 37;
    private const int DoSnapshotSetSlot = 38;
    private const int DeleteSnapshotsSlot = 39;
    private const int GetSnapshotPropertiesSlot = 42;

    // IVssAsync: IUnknown ocupa 0 a 2.
    private const int AsyncCancelSlot = 3;
    private const int AsyncWaitSlot = 4;
    private const int AsyncQueryStatusSlot = 5;

    /// <summary>Cuánto espera cada vuelta del sondeo de <c>IVssAsync</c> (ms): acota lo que tarda en notarse una cancelación.</summary>
    private const uint PollMilliseconds = 250;

    private nint _self;

    private VssBackupComponents(nint self) => _self = self;

    /// <summary>Crea el objeto y lo inicializa para una copia.</summary>
    public static VssBackupComponents Create()
    {
        Check(NativeMethods.Vss.CreateVssBackupComponentsInternal(out var pointer), "CreateVssBackupComponents");
        var backup = new VssBackupComponents(pointer);
        try
        {
            // InitializeForBackup(BSTR bstrXML = NULL): documento de componentes vacío.
            Check(((delegate* unmanaged[MemberFunction]<nint, nint, int>)Slot(pointer, InitializeForBackupSlot))(pointer, 0), "InitializeForBackup");
            return backup;
        }
        catch
        {
            backup.Dispose();
            throw;
        }
    }

    public void SetContext(int context) =>
        Check(((delegate* unmanaged[MemberFunction]<nint, int, int>)Slot(Self, SetContextSlot))(Self, context), "SetContext");

    public void SetBackupState() =>
        Check(
            ((delegate* unmanaged[MemberFunction]<nint, byte, byte, int, byte, int>)Slot(Self, SetBackupStateSlot))(Self, 0, 0, NativeMethods.Vss.BackupTypeCopy, 0),
            "SetBackupState");

    public void GatherWriterMetadata(CancellationToken cancellationToken)
    {
        nint operation;
        Check(((delegate* unmanaged[MemberFunction]<nint, nint*, int>)Slot(Self, GatherWriterMetadataSlot))(Self, &operation), "GatherWriterMetadata");
        FinishOperation(operation, "GatherWriterMetadata", cancellationToken);
    }

    public Guid StartSnapshotSet()
    {
        Guid id;
        Check(((delegate* unmanaged[MemberFunction]<nint, Guid*, int>)Slot(Self, StartSnapshotSetSlot))(Self, &id), "StartSnapshotSet");
        return id;
    }

    public Guid AddToSnapshotSet(string volume)
    {
        ArgumentException.ThrowIfNullOrEmpty(volume);
        Guid id;
        fixed (char* name = volume)
        {
            // ProviderId = GUID_NULL: el proveedor predeterminado del sistema.
            Check(((delegate* unmanaged[MemberFunction]<nint, char*, Guid, Guid*, int>)Slot(Self, AddToSnapshotSetSlot))(Self, name, Guid.Empty, &id), "AddToSnapshotSet");
        }

        return id;
    }

    public void PrepareForBackup(CancellationToken cancellationToken)
    {
        nint operation;
        Check(((delegate* unmanaged[MemberFunction]<nint, nint*, int>)Slot(Self, PrepareForBackupSlot))(Self, &operation), "PrepareForBackup");
        FinishOperation(operation, "PrepareForBackup", cancellationToken);
    }

    public void DoSnapshotSet(CancellationToken cancellationToken)
    {
        nint operation;
        Check(((delegate* unmanaged[MemberFunction]<nint, nint*, int>)Slot(Self, DoSnapshotSetSlot))(Self, &operation), "DoSnapshotSet");
        FinishOperation(operation, "DoSnapshotSet", cancellationToken);
    }

    public VssSnapshotProperties GetSnapshotProperties(Guid snapshotId)
    {
        NativeMethods.Vss.SnapshotProp prop = default;
        Check(((delegate* unmanaged[MemberFunction]<nint, Guid, NativeMethods.Vss.SnapshotProp*, int>)Slot(Self, GetSnapshotPropertiesSlot))(Self, snapshotId, &prop), "GetSnapshotProperties");
        try
        {
            return new VssSnapshotProperties(
                prop.SnapshotId,
                prop.SnapshotSetId,
                Marshal.PtrToStringUni(prop.OriginalVolumeName) ?? string.Empty,
                Marshal.PtrToStringUni(prop.SnapshotDeviceObject) ?? string.Empty,
                prop.SnapshotAttributes,
                DateTimeOffset.FromFileTime(prop.CreationTimestamp).ToUniversalTime());
        }
        finally
        {
            NativeMethods.Vss.VssFreeSnapshotPropertiesInternal(ref prop);
        }
    }

    public void BackupComplete(CancellationToken cancellationToken)
    {
        nint operation;
        Check(((delegate* unmanaged[MemberFunction]<nint, nint*, int>)Slot(Self, BackupCompleteSlot))(Self, &operation), "BackupComplete");
        FinishOperation(operation, "BackupComplete", cancellationToken);
    }

    public void AbortBackup() =>
        Check(((delegate* unmanaged[MemberFunction]<nint, int>)Slot(Self, AbortBackupSlot))(Self), "AbortBackup");

    public int DeleteSnapshots(Guid id, bool isSet)
    {
        int deleted;
        Guid notDeleted;
        var type = isSet ? NativeMethods.Vss.ObjectSnapshotSet : NativeMethods.Vss.ObjectSnapshot;

        // bForceDelete = TRUE: la instantánea puede tener handles abiertos (p. ej. de un hilo de lectura abandonado, R12).
        Check(
            ((delegate* unmanaged[MemberFunction]<nint, Guid, int, int, int*, Guid*, int>)Slot(Self, DeleteSnapshotsSlot))(Self, id, type, 1, &deleted, &notDeleted),
            "DeleteSnapshots");
        return deleted;
    }

    public void Dispose()
    {
        var self = Interlocked.Exchange(ref _self, 0);
        if (self != 0)
        {
            _ = ((delegate* unmanaged[MemberFunction]<nint, uint>)Slot(self, 2))(self);
        }
    }

    private nint Self => _self != 0 ? _self : throw new ObjectDisposedException(nameof(VssBackupComponents));

    private static void* Slot(nint self, int index) => (*(void***)self)[index];

    private static void Check(int hresult, string operation)
    {
        if (hresult < 0)
        {
            throw new VssException(hresult, operation);
        }
    }

    /// <summary>
    /// Espera una operación asíncrona de VSS con sondeo corto y la suelta siempre. Si se cancela, llama a <c>Cancel</c> y lanza
    /// <see cref="OperationCanceledException"/>; si termina con error, lanza <see cref="VssException"/> con ese <c>HRESULT</c>.
    /// </summary>
    private static void FinishOperation(nint asyncOperation, string operation, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _ = ((delegate* unmanaged[MemberFunction]<nint, int>)Slot(asyncOperation, AsyncCancelSlot))(asyncOperation);
                    throw new OperationCanceledException(cancellationToken);
                }

                // El resultado de Wait no importa: si la operación falló, QueryStatus da el mismo error.
                _ = ((delegate* unmanaged[MemberFunction]<nint, uint, int>)Slot(asyncOperation, AsyncWaitSlot))(asyncOperation, PollMilliseconds);

                int status;
                Check(((delegate* unmanaged[MemberFunction]<nint, int*, int*, int>)Slot(asyncOperation, AsyncQueryStatusSlot))(asyncOperation, &status, null), operation);
                if (status == NativeMethods.Vss.VssSAsyncPending)
                {
                    continue;
                }

                if (status == NativeMethods.Vss.VssSAsyncFinished)
                {
                    return;
                }

                if (status == NativeMethods.Vss.VssSAsyncCancelled)
                {
                    throw new OperationCanceledException($"VSS {operation} was cancelled.");
                }

                throw new VssException(status, operation);
            }
        }
        finally
        {
            _ = ((delegate* unmanaged[MemberFunction]<nint, uint>)Slot(asyncOperation, 2))(asyncOperation);
        }
    }
}

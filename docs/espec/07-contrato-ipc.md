## 7. Contrato IPC

### 7.1 Transporte

- Named pipe `Backbone.Service.v1` (`\\.\pipe\Backbone.Service.v1`), dúplex, asíncrono, mensajes con `HeaderDelimitedMessageHandler` y `SystemTextJsonFormatter`.
- Una conexión por instancia de la app; el servidor admite hasta 8 conexiones simultáneas, y como mucho 4 por SID para que un usuario no agote las instancias (B-4). Una conexión que supera el límite de su SID se rechaza con `NotAuthorized` (cierre ordenado, B-2) y **sigue contando** en ese SID mientras espera a cerrarse, para que una cuenta no pueda ocupar las instancias globales reconectando una y otra vez.
- Versión: `GetServiceInfoAsync` devuelve la versión del contrato; la app muestra un aviso si no coincide en la versión mayor.
- Constantes en `Backbone.Contracts`: `BackboneIpc.PipeName` (`Backbone.Service.v1`; el sufijo es la versión mayor del contrato), `BackboneIpc.MaxConnections` (8), `BackboneIpc.MaxRunProgressEventsPerSecond` (4), `BackboneIpc.MaxMessageBytes` (4 MiB) y `ContractVersion` (`Value = "1.4"`, `Major`, `Minor`, `IsCompatibleWith`). Los límites de las entradas están en `ContractLimits` (§7.5).
- `ContractVersion` es semántica: un cambio incompatible (quitar o renombrar un método, propiedad o valor de enumerado, cambiar un tipo o un significado) sube la **mayor** y el nombre del pipe; añadir métodos, propiedades opcionales o valores de enumerado sube la **menor**.
- Formato (ADR-10): servicio y app crean `new SystemTextJsonFormatter()` **sin cambiar** `JsonSerializerOptions`. Los nombres de propiedad viajan tal cual (PascalCase), los enumerados como número, las propiedades desconocidas se ignoran y las ausentes toman su valor por defecto. La profundidad máxima es la de System.Text.Json por defecto (64, `ContractLimits.MaxJsonDepth`).
- Tamaño de mensaje: ambos extremos cortan la conexión si **reciben** un mensaje que supera `BackboneIpc.MaxMessageBytes` (4 MiB), sin llegar a deserializarlo (el límite se aplica a la cabecera `Content-Length` antes de reservar memoria). Por eso el historial, la exploración de snapshots y la búsqueda se paginan. Al **enviar**, el servidor no deja salir nada por encima del límite: una respuesta que lo superaría se sustituye por un error `Internal` (§7.4) con identificador de correlación, que va al log junto con el tamaño; una notificación (evento) que lo superaría se descarta y se registra.
- Límites por conexión en el servidor (B-2):
  - **Identificación**: si el primer mensaje del cliente no llega en **5 s** desde la conexión, el servidor la cierra. Hasta ese mensaje no se puede suplantar al cliente ni conocer su identidad, y una conexión sin identificar ocupa una de las instancias del pipe.
  - **Peticiones simultáneas**: como máximo **16** peticiones leídas y aún sin respuesta por conexión. La petición número 17 no se atiende: el servidor **cierra la conexión** (no responde con un error). Las notificaciones del cliente no cuentan.
  - **Cierre ordenado tras un rechazo**: cuando el servidor responde `NotAuthorized` a una conexión que **aún no estaba autorizada** (rechazo desde el estado pendiente: identidad no establecida, nivel de suplantación insuficiente, SID fuera de la lista o no administrador, o demasiadas conexiones del SID, B-4), la conexión pasa a estado **rechazado** y no se cierra en ese momento:
    - toda llamada posterior recibe `NotAuthorized`;
    - no recibe eventos;
    - no puede pasar a autorizada, aunque entretanto el usuario se añada a `authorizedUsers` (tiene que abrir una conexión nueva);
    - se cierra cuando la cierra el cliente o, como mucho, a los **2 s** (`RejectionLinger`), plazo menor que el de identificación (5 s) para que una conexión rechazada nunca ocupe una instancia más que una sin identificar.

    Motivo: si el servidor cerrase el pipe justo después de escribir la respuesta, el cliente podría perderla y ver "conexión perdida" (y mostrar "Reconectando") en vez de "no autorizado" (§9.1).
  - La revocación de una conexión **ya autorizada** no sigue este cierre ordenado: se cierra de inmediato (§7.3).
  - **Mensaje único**: todo rechazo (cierre ordenado, incluido el exceso de conexiones del SID) y toda revocación (§7.3) usan el mismo error `NotAuthorized` con el mismo texto, `"The caller is not authorized to use the service."`, sin revelar el motivo. El motivo concreto solo va al log del servicio.
  - **Sin tope global de conexiones sin identificar** (decisión, backbone-service): se descarta reservar instancias o limitar cuántas de las 8 pueden estar sin identificar o rechazadas. Con 8 instancias, cualquier tope (3 o 4) bloquearía también a clientes legítimos nuevos, que empiezan sin identificar, y no añade protección real: las conexiones sin identificar ya están acotadas por el plazo de identificación de 5 s y por la ACL del pipe (solo administradores y cuentas de `authorizedUsers`), y las rechazadas por los 2 s de `RejectionLinger`.
- **Servidor auténtico (A-1)**:
  - El servidor crea la primera instancia del pipe con `PipeOptions.FirstPipeInstance` (`FILE_FLAG_FIRST_PIPE_INSTANCE`). Si falla porque ya existe un pipe con ese nombre, el servicio **no arranca**, lo registra en el Visor de eventos (1002, §8.5) y deja que el SCM aplique la recuperación.
  - El cliente, nada más conectar y **antes de enviar ningún mensaje**, comprueba, en este orden, sobre el handle de su extremo del pipe:
    1. **Propietario del pipe**: el propietario del descriptor de seguridad del pipe (`GetSecurityInfo` con `SE_KERNEL_OBJECT` y `OWNER_SECURITY_INFORMATION`) es `S-1-5-18` (LocalSystem) o `S-1-5-32-544` (Administradores). Un proceso sin privilegios no puede crear un objeto con esos propietarios.
    2. **Sesión**: `GetNamedPipeServerSessionId` devuelve 0 (los servicios corren en la sesión 0; la app y cualquier proceso de usuario, en otra).
    3. **Proceso del servicio**: lee el PID del servidor (`GetNamedPipeServerProcessId`) y consulta al Administrador de control de servicios (SCM), abriendo el servicio solo con `SERVICE_QUERY_STATUS | SERVICE_QUERY_CONFIG`:
       - el servicio `BackboneService` está instalado y **en ejecución** (`QueryServiceStatusEx`, `SC_STATUS_PROCESS_INFO`, `SERVICE_RUNNING` y PID distinto de 0);
       - su tipo es `SERVICE_WIN32_OWN_PROCESS` (proceso propio, no compartido con otros servicios);
       - el PID del servidor del pipe **coincide** con el PID del servicio según el SCM;
       - la cuenta configurada del servicio (`QueryServiceConfig`, `lpServiceStartName`) es `LocalSystem` o `NT AUTHORITY\SYSTEM` (sin distinguir mayúsculas).
    4. **PID estable**: vuelve a leer `GetNamedPipeServerProcessId` después de consultar al SCM y debe coincidir con el leído en el paso 3, para cerrar la ventana en que el proceso pudiera cambiar entre medias.

    Si alguna falla (o falla una de las llamadas al sistema), cierra la conexión sin haber escrito nada y la trata como "servicio no disponible" (§9.1, §9.3). Como el servidor solo puede suplantar después de haber leído un mensaje del cliente, esta comprobación previa impide que un servidor falso suplante al usuario.
  - Motivo de comprobar la cuenta en el SCM y no en el token (ADR-12): una app sin elevar no puede abrir el token de un proceso que corre como SYSTEM (su DACL solo concede acceso a SYSTEM y Administradores con el grupo habilitado), así que leer `S-1-5-18` del token fallaría siempre en el caso normal. La configuración del servicio sí la pueden leer los usuarios autenticados, y solo un administrador puede cambiarla o sustituir el proceso del servicio; por eso PID del SCM + cuenta configurada da la misma garantía frente a un usuario sin privilegios que intente ocupar el nombre del pipe. El propietario del pipe y la sesión 0 son defensa en profundidad: un pipe falso creado por un usuario sin privilegios falla ya en el paso 1.

### 7.2 Interfaz (`Backbone.Contracts.IBackboneService`)

```csharp
public interface IBackboneService
{
    // Información y estado
    Task<ServiceInfo> GetServiceInfoAsync(CancellationToken ct = default);   // versión, contrato, identidad y elevación del llamante
    Task<ServiceStatus> GetStatusAsync(CancellationToken ct = default);      // estado global, pausa, ejecución actual, cola

    // Trabajos
    Task<IReadOnlyList<JobDefinition>> GetJobsAsync(CancellationToken ct = default);
    Task<JobDefinition> SaveJobAsync(JobDefinition job, CancellationToken ct = default);   // crea si Id == Guid.Empty; emite StatusChanged
    Task DeleteJobAsync(Guid jobId, CancellationToken ct = default);                       // JobAlreadyRunning si está en ejecución o en cola; emite StatusChanged
    Task<ValidationResult> ValidateJobAsync(JobDefinition job, CancellationToken ct = default);

    // Ejecución
    Task RunNowAsync(Guid jobId, CancellationToken ct = default);
    Task CancelRunAsync(Guid jobId, CancellationToken ct = default);          // idempotente: sin ejecución, no hace nada
    Task SetPauseAsync(PauseRequest request, CancellationToken ct = default); // F2

    // NAS y credenciales
    Task SaveCredentialAsync(string shareRoot, CredentialInput credential, CancellationToken ct = default);
    Task<CredentialInfo?> GetCredentialInfoAsync(string shareRoot, CancellationToken ct = default); // sin contraseña
    Task DeleteCredentialAsync(string shareRoot, CancellationToken ct = default);
    Task<ConnectionTestResult> TestConnectionAsync(string destination, CredentialInput? credential, CancellationToken ct = default);
    Task<StorageInfo> GetStorageInfoAsync(Guid jobId, CancellationToken ct = default);           // F2

    // Historial
    Task<HistoryPage> GetHistoryAsync(HistoryQuery query, CancellationToken ct = default);       // página + TotalCount
    Task<RunDetail> GetRunDetailAsync(long runId, CancellationToken ct = default);

    // Restauración
    Task<IReadOnlyList<SnapshotInfo>> GetSnapshotsAsync(Guid jobId, CancellationToken ct = default);   // todos los del repositorio del trabajo
    Task<SnapshotItemPage> BrowseSnapshotAsync(Guid jobId, string snapshotId, string path, int offset, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<SnapshotItem>> SearchSnapshotAsync(Guid jobId, string snapshotId, string text, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<FileVersion>> GetFileVersionsAsync(Guid jobId, string path, CancellationToken ct = default); // F2
    Task<Guid> StartRestoreAsync(RestoreRequest request, CancellationToken ct = default);        // la restauración pertenece al SID que la inicia
    Task ResolveRestoreConflictAsync(Guid restoreId, ConflictResolution resolution, bool applyToAll, CancellationToken ct = default); // solo su SID
    Task CancelRestoreAsync(Guid restoreId, CancellationToken ct = default);                     // solo su SID

    // Mantenimiento (F2)
    Task<RetentionResult> ApplyRetentionAsync(Guid jobId, bool dryRun, CancellationToken ct = default); // con dryRun, qué se borraría (HU-28.2)
    Task StartPruneAsync(Guid jobId, CancellationToken ct = default);                 // resultado con RunCompleted (RunKind.Prune)
    Task StartCheckAsync(Guid jobId, bool full, CancellationToken ct = default);      // resultado con RunCompleted (RunKind.QuickCheck/FullCheck)

    // Ajustes y usuarios autorizados
    Task<ServiceSettings> GetSettingsAsync(CancellationToken ct = default);           // periodo de gracia, días sin copia, notificaciones
    Task SaveSettingsAsync(ServiceSettings settings, CancellationToken ct = default);
    Task<IReadOnlyList<AuthorizedUser>> GetAuthorizedUsersAsync(CancellationToken ct = default);   // SID + nombre resuelto
    Task<AuthorizedUser> AddAuthorizedUserAsync(string account, CancellationToken ct = default);  // cliente elevado; solo cuentas de usuario administradoras
    Task RemoveAuthorizedUserAsync(string sid, CancellationToken ct = default);                   // cliente elevado; si no, NotAuthorized
    Task<DiagnosticsExportResult> ExportDiagnosticsAsync(DiagnosticsExportRequest request, CancellationToken ct = default); // F2; §8.7

    // Eventos push (StreamJsonRpc los envía como notificaciones)
    event EventHandler<StatusChangedEvent> StatusChanged;
    event EventHandler<RunProgressEvent> RunProgress;          // como máximo 4 por segundo
    event EventHandler<RunCompletedEvent> RunCompleted;
    event EventHandler<RestoreProgressEvent> RestoreProgress;  // solo a las conexiones del SID dueño
    event EventHandler<RestoreConflictEvent> RestoreConflict;  // solo a las conexiones del SID dueño; espera ResolveRestoreConflictAsync
}
```

Todos los DTO son `record` inmutables en `Backbone.Contracts`. Las fechas viajan como `DateTimeOffset` UTC. Los métodos F2/F3 forman parte del contrato 1.0; mientras su fase no esté implementada, el servicio responde `NotSupported` (§7.4). Los campos de cada DTO, los errores de cada método y sus límites están documentados (XML doc) en `Backbone.Contracts`; las reglas que no se deducen de los tipos están en §7.5.

### 7.3 Seguridad del canal

**Modelo "solo cuentas administradoras" (C-1).** Solo pueden gestionar Backbone cuentas de **usuario** que sean miembros del grupo local Administradores. `authorizedUsers` es la lista de esas cuentas que pueden usar la app **sin elevar**.

- ACL del pipe: SYSTEM y `BUILTIN\Administrators` control total; cada SID de `settings.json → authorizedUsers` lectura/escritura. Con UAC, el token sin elevar de un administrador tiene el grupo Administradores como "solo denegar", así que la ACE del grupo no le da acceso; se lo da la ACE de su SID. Por eso **la app funciona sin elevar** para los usuarios de la lista.
- `AddAuthorizedUserAsync` y `RemoveAuthorizedUserAsync` exigen cliente **elevado** (si no → `NotAuthorized`) y actualizan la ACL del pipe al aplicarse. `ServiceInfo.Caller.IsElevated` indica a la app si puede ofrecer la gestión. Consultar la lista (`GetAuthorizedUsersAsync`) lo puede hacer cualquier usuario autorizado.
- `AddAuthorizedUserAsync` solo acepta cuentas cualificadas: `EQUIPO\usuario`, `DOMINIO\usuario` o un SID. Se rechazan con `ValidationFailed`:
  - `AccountNotFound` si no se resuelven.
  - `AccountNotUser` si no son de tipo usuario (`SidTypeUser`) o el nombre no está cualificado: grupos, Everyone, Usuarios autenticados, SID bien conocidos o de servicio.
  - `AccountNotAdministrator` si la cuenta no es miembro del grupo local Administradores, directamente o mediante un grupo anidado.
  Devuelve el SID y el nombre resuelto.
- **Revalidación**: en cada conexión, y en cada llamada, el servicio comprueba que el SID del cliente está en la lista actual y sigue siendo miembro de Administradores (pertenencia actual consultada al sistema, no solo la del token). Una cuenta que deja de ser administradora o que se retira de la lista pierde el acceso; el servicio cierra sus conexiones abiertas.
- El instalador añade el SID del usuario que instala **solo si** es miembro de Administradores (con "ejecutar como otro usuario", el que instala puede no serlo); si no, no añade a nadie.
- Requisitos de implementación del servidor (M-7, B-3, B-4):
  - La autorización se calcula **en cada llamada** contra la lista actual, no solo al conectar.
  - La elevación se calcula del token del cliente (grupo Administradores habilitado), nunca de lo que diga el cliente.
  - La conexión se rechaza si el nivel de suplantación del cliente es `Anonymous` o `Identification`: hace falta `Impersonation` para §8.6 y §8.7.
  - Al quitar un SID de la lista, se cierran sus conexiones.
  - **Revocación frente a rechazo**: si una conexión **ya autorizada** deja de estarlo (su SID se quita de la lista o deja de ser administrador), la conexión se **cierra de inmediato** (se libera el `JsonRpc` y se cancela lo que esté en curso en esa conexión). Por eso no se garantiza la respuesta: la llamada que detecta la revocación recibe `NotAuthorized` o ve la conexión cerrada; al reconectar, la nueva conexión recibe `NotAuthorized` (mismo mensaje único que el rechazo, §7.1). La app solo mira el código de error, nunca el texto. Una conexión que nunca llegó a autorizarse se rechaza con el cierre ordenado de §7.1 (B-2): queda rechazada hasta que el cliente cierra o pasan 2 s, y no puede pasar a autorizada aunque se añada su SID.
  - Límite de 4 conexiones por SID (§7.1); las rechazadas por superarlo cuentan en el SID hasta que se cierran.
  - Cada operación de escritura se audita en el log con el SID, el nombre de la cuenta y el nombre del método, **sin argumentos**; ni rutas ni datos de credenciales.
- **Permiso de cada método** (lectura, escritura o escritura con cliente elevado). Hoy la diferencia entre lectura y escritura es la auditoría, porque todas las cuentas autorizadas son administradoras (C-1). Pero la tabla es también la frontera de permisos: un permiso reducido, si alguna vez lo hay, solo dará acceso a los métodos de lectura. Cuenta como escritura todo método que cambia estado **o que hace que SYSTEM abra rutas o conecte a destinos que indica el cliente**:

  | Permiso | Métodos |
  |---|---|
  | Lectura | `GetServiceInfoAsync`, `GetStatusAsync`, `GetJobsAsync`, `GetCredentialInfoAsync`, `GetStorageInfoAsync`, `GetHistoryAsync`, `GetRunDetailAsync`, `GetSnapshotsAsync`, `BrowseSnapshotAsync`, `SearchSnapshotAsync`, `GetFileVersionsAsync`, `GetSettingsAsync`, `GetAuthorizedUsersAsync` |
  | Escritura | `SaveJobAsync`, `DeleteJobAsync`, **`ValidateJobAsync`**, `RunNowAsync`, `CancelRunAsync`, `SetPauseAsync`, `SaveCredentialAsync`, `DeleteCredentialAsync`, `TestConnectionAsync`, `StartRestoreAsync`, `ResolveRestoreConflictAsync`, `CancelRestoreAsync`, `ApplyRetentionAsync`, `StartPruneAsync`, `StartCheckAsync`, `SaveSettingsAsync`, `ExportDiagnosticsAsync` |
  | Escritura, cliente elevado | `AddAuthorizedUserAsync`, `RemoveAuthorizedUserAsync` |

  **`ValidateJobAsync` es de escritura** (revisión de seguridad de HU-07, ALTA-1 e I-1; decisión del coordinador), como `TestConnectionAsync`: abre como SYSTEM las rutas de origen que indica el cliente, así que sirve para sondear como SYSTEM si existen rutas que el usuario no puede ver, y antes de ADR-20 hacía que SYSTEM siguiera enlaces hacia la red. Quien solo pudiera leer tampoco podría guardar el trabajo, así que no pierde nada. La clasificación no está en el contrato (Backbone.Contracts solo lo documenta en el XML de cada método); la aplica el servidor.
- **Propiedad de las restauraciones (A-2)**: cada restauración pertenece al SID que la inició. `ResolveRestoreConflictAsync` y `CancelRestoreAsync` desde otro SID → `NotAuthorized`. `RestoreProgress` y `RestoreConflict` solo se envían a las conexiones de ese SID. Para no perder eventos emitidos antes de que llegue la respuesta, el cliente puede proponer `RestoreRequest.RestoreId`; si está en uso → `ValidationFailed` con `IdAlreadyInUse`.

### 7.4 Errores

Errores de negocio como `RemoteInvocationException` con `ErrorCode` del enumerado `BackboneErrorCode`:

`ValidationFailed`, `JobNotFound`, `JobAlreadyRunning`, `NasUnreachable`, `CredentialsRejected`, `ShareNotFound`, `AccessDenied`, `RepositoryLocked`, `RepositoryIncompatible`, `RepositoryCorrupt`, `SnapshotNotFound`, `RestoreTargetNotWritable`, `Interrupted` (suspensión o apagado durante la ejecución), `NotAuthorized`, `NotSupported` (método de una fase aún no implementada), `Internal`, `CredentialRequired` (no hay credencial guardada para el recurso), `SnapshotTooLarge` (la copia tiene demasiados archivos y carpetas para un solo snapshot, §6.3; contrato 1.2), `NasBusy` (el servidor está en uso por otra conexión del servicio con otra credencial, o hay demasiados rechazos de credencial seguidos; no se intenta conectar y la respuesta no depende de si la credencial es correcta, §8.2 "Conexiones al NAS"; contrato 1.3).

La app traduce cada código a un mensaje localizado con una acción sugerida (`.resw`, es-ES y en-US). El de `SnapshotTooLarge` habla del **número de archivos y carpetas** del trabajo, no del volumen de datos (el límite de 16 GiB es el del listado, no el del contenido), y sugiere dividirlo:

- es-ES: "El trabajo tiene demasiados archivos y carpetas para una sola copia (más de 5 000 000). Divídelo en varios trabajos con menos carpetas cada uno."
- en-US: "This job has too many files and folders for a single backup (more than 5,000,000). Split it into several jobs, each with fewer folders."

Los códigos de validación (`ValidationIssueCode`, §7.5) también se traducen así. El de `ProtectedFoldersUnavailable` (contrato 1.4, §8.3 "`PD` desconocida") no habla de carpetas apartadas, a diferencia del de `SourceNotAllowed` por exceso de carpetas apartadas, porque no lo arregla revisarlas:

- es-ES: "No se ha podido comprobar la carpeta de datos de Backbone en %ProgramData%; por seguridad no se copia ningún origen. Vuelve a intentarlo y, si se repite, revisa el registro del servicio."
- en-US: "Backbone could not check its data folder in %ProgramData%, so no source is backed up, for safety. Try again and, if it happens again, check the service log."

Transporte del error:

- Valores numéricos fijos, en este orden, de `1000` (`ValidationFailed`) a `1018` (`NasBusy`); fuera del intervalo [-32768, -32000] reservado por JSON-RPC. No se reutilizan ni se reordenan.
- El servicio lanza `LocalRpcException` con `ErrorCode = (int)code` (`BackboneErrors.ToJsonRpcErrorCode`) y `ErrorData = BackboneErrorData { Code, Message, Validation }`.
  - `Message` es un detalle técnico en inglés para el log, sin secretos, trazas ni contenido de archivos; la app no lo muestra.
  - Con `ValidationFailed`, `Validation` lleva el `ValidationResult`.
- **Excepciones no de negocio (M-5)**: toda excepción que no sea un error de negocio se convierte en `Internal`, con un `Message` genérico (`"Internal error. Correlation id: <guid>"`). El mismo identificador va al log del servicio junto con la excepción completa.
  - Nunca se envía `ex.ToString()`, el tipo, la traza ni mensajes internos.
  - El servidor sobrescribe la creación de detalles de error de `JsonRpc` (o envuelve el objetivo RPC) para que ninguna excepción salga con el `CommonErrorData` por defecto, que incluye la traza.
  - `ExceptionStrategy` nunca es `ISerializable`.
- La app recibe `RemoteInvocationException`: identifica el código con `BackboneErrors.TryGetErrorCode(ex.ErrorCode, …)` y, para obtener `BackboneErrorData` en `ex.DeserializedErrorData`, su `JsonRpc` sobrescribe `GetErrorDetailsDataType` y devuelve `typeof(BackboneErrorData)` cuando el código es de Backbone.
- Un fallo de "Probar conexión" no es una excepción: es un `ConnectionTestResult` con `Success = false` y `ErrorCode`.

### 7.5 Convenciones del contrato

- **DTO**: `sealed record` sin herencia, constructor sin parámetros y propiedades `init` con valor por defecto (`string.Empty`, `[]`, `new()`); sin `required`, para que un campo nuevo sea compatible hacia atrás. Las propiedades no anulables normalizan un `null` explícito recibido a su valor vacío (`init => field = value ?? …`), así que el servicio nunca ve `null` donde el tipo no lo admite (F-1). Colecciones `IReadOnlyList<T>`; fechas `DateTimeOffset` en UTC; identificadores `Guid` (salvo `runId`, `long`, y el identificador de snapshot, texto de §6.3). Las horas del día de una programación son `TimeOnly` en **hora local** del equipo (no son instantes).
- **Enumerados (M-4)**: valores numéricos explícitos y estables, con un valor 0 definido (salvo `BackboneErrorCode`). En las **entradas**, el servicio rechaza valores no definidos (y bits fuera de los definidos en los enumerados de indicadores) con `ValidationFailed` / `ValueOutOfRange`. La tolerancia a valores desconocidos (llegan como número no definido) es solo de la app al leer salidas de un servicio más nuevo.
- **Fechas de entrada (B-1)**: el servicio normaliza a UTC (`ToUniversalTime()`) todo `DateTimeOffset` recibido (`PauseRequest.UntilUtc`, `HistoryQuery.FromUtc`/`ToUtc`); las salidas siempre llevan desplazamiento 0.
- **Contraseñas (M-1)**: solo viajan de la app al servicio dentro de `CredentialInput` (`SaveCredentialAsync`, `TestConnectionAsync`), cuyo `ToString()` la oculta; ningún parámetro de la interfaz es una contraseña suelta y ningún DTO de salida lleva contraseña.
- **Límites (M-2)**: constantes en `ContractLimits`. El servicio rechaza lo que las supera con `ValidationFailed` / `LimitExceeded`.

  | Entrada | Límite |
  |---|---|
  | Nombre del trabajo | 128 caracteres |
  | Alias | 64 caracteres y segmento válido |
  | Orígenes | 32 |
  | Exclusiones | 256 patrones de hasta 512 caracteres; coste total de la lista ≤ 131 584 (§4.1) |
  | Horas de una programación diaria | 24 |
  | Rutas locales | 32 767 caracteres |
  | Destino UNC | 1 024 caracteres |
  | Rutas de snapshot | 32 767 caracteres, segmentos de 255 |
  | `RestoreRequest.Paths` | de 1 a 1 000 |
  | Página (historial, exploración, búsqueda) | de 1 a 1 000 elementos; `Offset` ≥ 0 |
  | Texto de búsqueda | de 1 a 256 caracteres, literal y sin comodines |
  | `HistoryQuery` | `FromUtc` ≤ `ToUtc` |
  | Cuentas y usuario del NAS | 256 caracteres |
  | Contraseña del NAS | 256 caracteres |

  Los patrones *glob* de exclusión se evalúan con un algoritmo **sin retroceso**, de coste polinómico y acotado por el presupuesto de coste de la lista (131 584, `ExclusionMatcher.MaxListCost`; no se traducen a expresiones regulares con *backtracking*). Su semántica, su coste y su validación están en §4.1 ("Patrones de exclusión").
- **Rutas de snapshot (M-3)**: separador `/`, prefijadas por el alias, sin `/` inicial ni final (`Documentos/Facturas/enero.pdf`). En `BrowseSnapshotAsync` la cadena vacía es la raíz, que contiene una carpeta por alias. `SnapshotPaths` (utilidad pura de Contracts) valida:
  - El identificador de snapshot (`IsValidSnapshotId`, formato de §6.3).
  - Cada segmento (`IsValidSegment`): no vacío; distinto de `.` y `..`; sin `\ / : * ? " < > |` ni caracteres de control; sin punto ni espacio final; sin nombres de dispositivo reservados, tampoco con extensión (`CON`, `NUL.txt`, `COM1`, `CONIN$`, `CONOUT$`…); como mucho 255 caracteres; sin suplentes sueltas (UTF-16 válido; contrato 1.2). Como `UncPaths.TryParse` valida con ella los segmentos del destino, un recurso o una ruta con una suplente suelta se rechaza con `DestinationNotAllowed`.
  - La ruta completa (`IsValidPath`).
  El alias de un origen cumple las mismas reglas de segmento. El motor omite, con aviso `PathInvalid`, los archivos de origen cuyo nombre no las cumple.
- **Destino UNC (A-3)**: `UncPaths.TryParse` (utilidad pura de Contracts) valida el destino y calcula la raíz del recurso `\\servidor\recurso` en minúsculas, con la que se guardan las credenciales; la app la usa en lugar de duplicar la regla.
  - Se rechazan con `DestinationNotAllowed`:
    - Las rutas `\\?\…`, `\\?\UNC\…`, `\\.\…` y `\??\…`.
    - El *loopback* (`localhost`, `*.localhost`, `127.0.0.0/8` en cualquier notación IPv4 que acepte `IPAddress.TryParse`, como `2130706433`, `0x7f.1` o `127.1`, y `0.0.0.0`) y los nombres `*.ipv6-literal.net`.
    - El propio equipo: el servicio pasa sus nombres NetBIOS y DNS y las direcciones de **todas** sus interfaces locales, incluidas las de enlace local `169.254.0.0/16`. Una IP literal se compara con esas direcciones, y un nombre se resuelve y se rechaza si apunta a una de ellas.
    - **Resolución del nombre** (HU-05, decisión del coordinador): la hace `LocalMachine.ResolvesToSelfAsync` del servicio, con un tope de **4 s** dentro del plazo total de 11 s de "Probar conexión" (HU-05, criterio 3: menos de 15 s). Si alguna dirección resuelta es del propio equipo (*loopback*, `Any`, `IPv6Any`, IPv4 mapeadas en IPv6 de cualquiera de ellas, o una dirección de una interfaz local) → `ValidationFailed` con `DestinationNotAllowed`, sin conectar. Si la resolución falla o vence el tope, no se rechaza por esto: decide la conexión (normalmente `NasUnreachable`). La validación de trabajos (`ValidateJobAsync`, `SaveJobAsync`, HU-07) usa el mismo método. Riesgo residual de *DNS rebinding*: R14 (§13).
    - Los recursos administrativos (`C$`, `ADMIN$`, `IPC$`).
    - Los segmentos no válidos.
  - El servidor solo admite letras y dígitos ASCII, `-`, `_` y `.`. Un nombre internacional va en punycode (`xn--…`). Un punto inicial o final no se normaliza: se rechaza con `DestinationNotUnc`. Las IP IPv6 entre corchetes tampoco se admiten.
  - **El servicio nunca conecta al NAS sin una credencial explícita**: sin credencial guardada ni indicada, `TestConnectionAsync` devuelve `CredentialRequired` sin intentar conectar, y las ejecuciones y `SaveJobAsync` fallan con `CredentialRequired` (nunca se usa la cuenta del equipo).
- **Textos no confiables (§6.1)**: `UntrustedText.Describe` (utilidad pura de Contracts, fuera del cable IPC) recorta a 200 puntos de código con `…` y escapa como `\uXXXX` `Cc`, `Cf`, suplentes sueltas, U+2028 y U+2029; `null` da `(null)`. Servicio y app la usan en lugar de duplicar la regla; Core mantiene su `Untrusted.Describe` interno con el mismo comportamiento.
- **Validación**: `ValidationIssue.Field` usa los nombres de propiedad del DTO validado (`JobDefinition`, `ServiceSettings`…) con índices desde 0 (`Name`, `Sources[1].Alias`, `Schedule.IntervalHours`); vacío si afecta a la entrada entera o a un parámetro simple. `ValidationIssue.Code` es un enumerado que la app traduce; `ValidationResult.Warnings` no impide guardar (p. ej. repositorio de formato superior).
  - **Carpeta de datos (ADR-16, contrato 1.2)**: `SourceNotAllowed` (26, error, `Field` = `Sources[i].Path`) si un origen es `%ProgramData%\Backbone` o una carpeta apartada `%ProgramData%\Backbone.untrusted-<guid>`, o está dentro de ellas; `SourceContainsDataFolder` (27, aviso, mismo `Field`) si un origen contiene alguna y por tanto se excluirá siempre (§8.3).
  - **Orígenes (ADR-20, contrato 1.3)**, todos errores en `Sources[i].Path`. Se comprueban en este orden y cada origen da como mucho uno de ellos:
    - `SourcePathInvalid` (29): la ruta no cumple "Ruta de origen" (abajo).
    - `SourceNotLocal` (5): ruta UNC o de dispositivo, unidad de red o extraíble, o un enlace cuyo destino no es un volumen local (no se abre).
    - `SourceNotFound` (3): un componente no existe (`STATUS_OBJECT_NAME_NOT_FOUND`, `STATUS_OBJECT_PATH_NOT_FOUND`), o el último es un archivo y no una carpeta.
    - `SourceUnreadable` (28): no se puede comprobar. Puede ser acceso denegado, error de E/S, plazo vencido o más de 63 saltos. También que un componente sea un punto de reanálisis que no es sustituto de nombre y no se atraviesa (ADR-19: carpeta solo en la nube o no poblada, etiqueta fuera de la lista blanca), o que la ruta final no coincida con la resuelta. Antes del contrato 1.3, el acceso denegado se notificaba como `SourceNotFound`.
    - `SourceNotNtfs` (4) y `SourceNotAllowed` (26), sobre la ruta resuelta.

    Un cliente 1.2 ve los códigos 28 y 29 como valor no definido y muestra el mensaje genérico de origen no válido.
  - **Carpetas protegidas desconocidas (contrato 1.4; cierre de HU-08 servicio, decisión del usuario, R16)**: `ProtectedFoldersUnavailable` (30, error, `Field` = `Sources[i].Path`, `Value` nulo) cuando el servicio no ha podido obtener la ruta `VOLUME_NAME_GUID` de `%ProgramData%`, o la del origen ha venido vacía, y por eso no puede decidir si el origen contiene la carpeta de datos (§8.3 "Carpetas protegidas", `PD` desconocida). Se da en lugar de `SourceNotAllowed`, en el último paso de la lista anterior. `SourceNotAllowed` sigue cubriendo el origen dentro de una carpeta protegida y el exceso de carpetas apartadas. Un cliente 1.3 ve el 30 como valor no definido y muestra el mensaje genérico de origen no válido.
  - **Exclusiones demasiado complejas** (§4.1): `ExclusionPatternInvalid` en `Exclusions`, **sin índice** y con `Value` nulo, cuando la lista entera supera el presupuesto de coste. Un patrón no válido lo da en `Exclusions[i]`. No hace falta un código propio: la app distingue los dos casos por `Field` y muestra para la lista entera un texto que pide quitar o simplificar patrones con texto entre dos `*`.
- **Ruta de origen (revisión de seguridad de HU-07, BAJA-1)**: `JobSource.Path` es una ruta local **completa con letra de unidad** (`X:\…`). El servicio la normaliza y la valida **sin** la normalización de Win32 (`GetFullPathName` quita puntos y espacios finales y acepta flujos alternativos, así que `C:\docs.` abriría `C:\docs`):
  - Se aceptan `/` como separador (se convierte en `\`), la letra en minúsculas (se pasa a mayúsculas) y una `\` final (se quita, salvo en la raíz `X:\`).
  - Se rechaza con `SourcePathInvalid`:
    - una ruta relativa o sin `\` tras la letra (`C:docs`);
    - un `:` fuera de la posición 1 (`C:\ProgramData::$INDEX_ALLOCATION`, `C:\a:b`);
    - segmentos vacíos (`C:\a\\b`), `.` o `..`;
    - cualquier segmento que no cumpla `SnapshotPaths.IsValidSegment` (§7.5 M-3): punto o espacio final (`docs.`, `docs `), caracteres no válidos o de control, nombres de dispositivo reservados, más de 255 caracteres o suplentes sueltas.
  - Las rutas `\\servidor\…`, `\\?\…`, `\\.\…` y `\??\…` siguen dando `SourceNotLocal`.
  - **Lo que se guarda** en `jobs.json` (y devuelve `SaveJobAsync`) es esta ruta normalizada, no la escrita por el cliente ni la ruta final resuelta. Cada ejecución la vuelve a resolver con ADR-20, así que abre exactamente lo que se validó y detecta un enlace plantado después. La app muestra la ruta devuelta.
- **Valores por defecto** de un trabajo nuevo en `JobDefaults` (exclusiones de RF-04, retención 3/7/4/12/0, batería 30 %, uso medido y NAS accesible activados), compartidos por app y servicio.
- **Trabajos y estado**:
  - `SaveJobAsync` y `DeleteJobAsync` emiten `StatusChanged`.
  - `DeleteJobAsync` de un trabajo en ejecución o en cola → `JobAlreadyRunning` (hay que cancelarlo antes).
  - `CancelRunAsync` sin ejecución en curso ni en cola no hace nada (idempotente); solo falla con `JobNotFound`.
  - **Errores de `SaveJobAsync` con destino** (HU-06, RF-63; detalle en §8.2 "Preparación del repositorio"): además de los de validación, `CredentialRequired` (sin credencial guardada para la raíz), `NasBusy` (1018; hay otra preparación en curso con ese servidor o se alcanzó el límite de 4 en el servicio), `NasUnreachable` (red caída, plazo de 30 s agotado o sin registro de uso de la conexión; no se escribe nada), `AccessDenied`, `RepositoryCorrupt` (`repo.json` dañado) y `ValidationFailed` con `ValidationIssueCode.DestinationNotRepository` (13) en `Destination` cuando hay archivos ajenos. Un repositorio de formato superior **no** es un error: el trabajo se guarda con `RepositoryState = ReadOnlyNewerFormat`.
  - `JobStatus.RepositoryState` (`RepositoryState`) vale `Unknown` (0) hasta que `SaveJobAsync` o una ejecución examinan el repositorio, y `ReadOnlyNewerFormat` (2) cuando es de formato superior (HU-06.4). Se guarda **solo en memoria** y es **orientativo**: tras reiniciar el servicio vuelve a `Unknown`, y el servicio nunca lo usa para autorizar escrituras (§8.2).
- **Avisos**: `RunIssueCode.WarningsTruncated` (contrato 1.1) resume los avisos que no caben en el snapshot (§6.3) y puede aparecer también entre las incidencias de la ejecución; su `Message` indica cuántos se han descartado. `RunIssueCode.SourceRootUnreadable` (13, contrato 1.2) indica que no se pudo recorrer la raíz de un origen y no se ha actualizado nada de él (`Path` = alias, causa en `Message`; §8.4 "Memoria del motor", R11); la app lo muestra con un texto propio y un cliente 1.1 lo ve como `Other`.
- **Ejecuciones**: `RunRecord.Kind` distingue copia (`Backup`) de mantenimiento F2 (`Retention`, `Prune`, `QuickCheck`, `FullCheck`), que comparten historial, `RunProgress` y `RunCompleted`. `ServiceStatus.CurrentRun` es el último `RunProgressEvent` de la ejecución en curso, para el cliente que conecta a mitad.
- **Historial**: `GetHistoryAsync` devuelve `HistoryPage { Items, TotalCount }`. `HistoryQuery` pagina con `Offset` y `Limit` (por defecto 100, máximo 1000), del más reciente al más antiguo.
- **Snapshots**:
  - `GetSnapshotsAsync` devuelve **todos** los snapshots del repositorio del trabajo, también los creados por una instalación anterior o con otro nombre de trabajo (HU-06.2). `SnapshotInfo.JobName` es el nombre guardado en cada snapshot.
  - `BrowseSnapshotAsync` pagina con `offset` y `limit` (máximo 1000) y devuelve `SnapshotItemPage { Items, TotalCount }`, primero carpetas y después archivos, por nombre (ordinal, sin distinguir mayúsculas).
- **Pausa**: la app convierte "1 h", "4 h" y "hasta mañana" (mañana a las 06:00 en su hora local) en `PauseRequest.UntilUtc`; `null` es indefinida.
- **Ajustes** (`ServiceSettings`):
  - `MissedRunGracePeriod`: RF-11; 5 min, de 0 a 24 h.
  - `OutdatedAfterDays`: RF-50/RF-52 y evento 7000; 3, de 1 a 365.
  - `Notifications`: `OnWarnings` activado, `OnSuccess` desactivado, `OnOutdated` activado y `OnLowSpace` activado. La ejecución fallida se notifica siempre.
  - No incluyen la pausa, los usuarios autorizados ni el inicio con Windows (HKCU, lo gestiona la app). Valores por defecto en `ServiceSettingsDefaults`.
- **Usuarios autorizados** (`AuthorizedUser`): `Sid` es la identidad (lo único que guarda `settings.json`); `AccountName` lo resuelve el servicio al responder y es `null` si la cuenta ya no existe. Reglas de alta en §7.3. Añadir uno que ya está o quitar uno que no está no son errores.
- **Restauración**:
  - Hay como mucho un `RestoreConflictEvent` pendiente por restauración; por eso `ResolveRestoreConflictAsync` no lleva identificador de conflicto.
  - `RestoreProgressEvent` se emite al menos una vez con estado final. Solo ese lleva el resumen: `Items` (resultado de cada ruta seleccionada) e `Issues` (archivos omitidos o fallidos, que el servicio puede truncar con `IssuesTruncated`).
  - Errores de `StartRestoreAsync`: `ValidationFailed`, `JobNotFound`, `SnapshotNotFound`, `RestoreTargetNotWritable`, `CredentialRequired`, `NasUnreachable`, `CredentialsRejected`, `RepositoryIncompatible`, `RepositoryLocked`.

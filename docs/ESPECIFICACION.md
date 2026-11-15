# Backbone — Especificación funcional y técnica

| | |
|---|---|
| **Producto** | Backbone: copias de seguridad automáticas de Windows 11 a un NAS (OpenMediaVault vía SMB) |
| **Versión del documento** | 1.0 — 2026-11-15 |
| **Estado** | Aprobado para desarrollo (MVP) |
| **Historias de usuario** | [HISTORIAS.md](HISTORIAS.md) |

---

## 1. Visión

Backbone es un servicio de Windows que copia automáticamente carpetas del PC a una carpeta compartida de un NAS, guardando **versiones** de los archivos sin duplicar espacio, y una aplicación WinUI 3 para gestionarlo: configurar trabajos, ver el estado y **restaurar** archivos de cualquier fecha.

Principios:

1. **La restauración es el producto.** Una copia que no se puede restaurar fácilmente no vale nada.
2. **Nunca corromper el repositorio.** Un corte de luz, de red o un cierre forzado a mitad de copia debe dejar el repositorio válido.
3. **Integrado con Windows.** Usa los servicios del sistema (SCM, VSS, DPAPI, Visor de eventos, notificaciones, gestión de energía y de red) en lugar de reinventarlos.
4. **Silencioso cuando todo va bien, insistente cuando algo falla.**

### 1.1 Alcance

- Un PC con Windows 11 (22H2 o posterior, x64 y ARM64) y uno o varios usuarios locales.
- Destino: carpeta compartida SMB (OpenMediaVault u otro NAS/servidor SMB 2.1+).
- Origen: carpetas en volúmenes NTFS locales.

### 1.2 Fuera de alcance (v1)

- Copia de la imagen completa del sistema o *bare-metal restore*.
- Destinos en la nube (S3, OneDrive…), FTP o SSH.
- Gestión centralizada de varios PC.
- Volúmenes FAT/exFAT, unidades extraíbles como origen.
- Copia de permisos NTFS (ACL) y flujos alternativos (ADS). Se guardan contenido, fecha de modificación y atributos básicos.

---

## 2. Glosario

| Término | Definición |
|---|---|
| **Trabajo** (*job*) | Configuración de una copia: orígenes, destino, exclusiones, programación y retención. |
| **Origen** | Carpeta local incluida en un trabajo. Tiene un **alias** único dentro del trabajo (p. ej. `Documentos`). |
| **Destino** | Ruta UNC de la carpeta del repositorio en el NAS, p. ej. `\\192.168.1.50\Backups\PC-Salon`. |
| **Repositorio** | Estructura de carpetas en el destino con objetos y snapshots (§6). Un repositorio por trabajo. |
| **Objeto** | Contenido de un archivo, guardado una sola vez y nombrado por su hash SHA-256. |
| **Snapshot** | Manifiesto que describe el estado de todos los archivos del trabajo en un instante. |
| **Ejecución** (*run*) | Cada vez que se lanza un trabajo. Una ejecución correcta produce un snapshot. |
| **Retención** | Reglas que deciden qué snapshots se conservan. |
| **Limpieza** (*prune*) | Borrado de objetos que ningún snapshot referencia. |
| **Verificación** (*check*) | Comprobación de que todos los objetos referenciados existen y son íntegros. |
| **Usuario autorizado** | Cuenta de Windows con permiso para gestionar Backbone mediante la app. |

---

## 3. Arquitectura

```
┌───────────────────────────────┐                 ┌─────────────────────────────────────┐
│ Backbone.App (WinUI 3)        │                 │ Backbone.Service (Worker, .NET 10)  │
│ sesión del usuario            │  named pipe     │ sesión 0, LocalSystem               │
│ · icono en bandeja            │  JSON-RPC       │ · Scheduler                         │
│ · panel / trabajos / restaurar│◄──────────────► │ · JobRunner → Backbone.Core         │
│ · historial / ajustes         │  (eventos push) │ · IPC server (ACL)                  │
│ · notificaciones toast        │                 │ · OS: VSS, SMB, DPAPI, EventLog,    │
└───────────────────────────────┘                 │   energía, red                      │
                                                  │ · Almacén: config JSON + SQLite     │
                                                  └──────────────────┬──────────────────┘
                                                                     │ SMB (WNetAddConnection2)
                                                                     ▼
                                                    \\NAS\Backups\<trabajo>\  (repositorio §6)
```

El servicio hace todo el trabajo. La app solo es una interfaz: si se cierra, las copias continúan. Un servicio en la sesión 0 no puede mostrar interfaz, de ahí la separación.

### 3.1 Solución

```
Backbone.slnx
├── src/
│   ├── Backbone.Contracts/   net10.0          DTO, interfaz IPC, códigos de error. Sin dependencias de Windows.
│   ├── Backbone.Core/        net10.0          Motor: repositorio, backup, restore, retención, prune, check, exclusiones, programación.
│   ├── Backbone.Service/     net10.0-windows  Host del servicio, scheduler, servidor IPC, integración con el SO, persistencia.
│   └── Backbone.App/         net10.0-windows10.0.22621.0  WinUI 3 sin empaquetar (WindowsPackageType=None).
├── installer/
│   └── Backbone.Installer/   WiX v5 (MSI)
├── tests/
│   ├── Backbone.Core.Tests/          xUnit, sin red ni privilegios
│   ├── Backbone.Service.Tests/       xUnit, scheduler e IPC con dobles de prueba
│   ├── Backbone.App.Tests/           xUnit, view models y cliente IPC (sin UI real)
│   ├── Backbone.Architecture.Tests/  reglas de dependencia entre proyectos
│   └── Backbone.IntegrationTests/    requieren admin / recurso SMB local; marcados con [Trait("Category","Integration")]
└── docs/
```

Reglas de dependencia: `App → Contracts`; `Service → Core → Contracts`. **La app nunca referencia Core ni Service.** Core no conoce Windows (salvo `System.IO`): todo lo específico del SO vive en Service detrás de interfaces. `Backbone.Architecture.Tests` comprueba estas reglas leyendo los `.csproj` de `src/` (cierre transitivo de `ProjectReference`) y, para Contracts y Core, también las referencias de los ensamblados compilados.

Compilación: SDK fijado en `global.json` (10.0.x); propiedades comunes en `Directory.Build.props` y versiones de NuGet en `Directory.Packages.props`. La app, al ser autocontenida, necesita una arquitectura concreta: con `-p:Platform=x64|ARM64` (o `-r win-x64|win-arm64`) se compila para esa arquitectura y, si no se indica, para la de la máquina, de modo que `dotnet build Backbone.slnx` funciona sin opciones.

### 3.2 Decisiones de arquitectura (ADR)

| ID | Decisión | Motivo |
|---|---|---|
| ADR-01 | .NET 10 LTS en todos los proyectos. | Soporte largo; `UseWindowsService()` oficial. |
| ADR-02 | WinUI 3 sin empaquetar + Windows App SDK (última versión estable 1.x), autocontenido. | Aspecto nativo de Windows 11; sin empaquetar evita restricciones de MSIX con servicios. |
| ADR-03 | IPC con **StreamJsonRpc** sobre `NamedPipeServerStream` con `PipeSecurity`, formateador `SystemTextJsonFormatter`. | Contrato definido como interfaz C# compartida, sin generación de código; admite eventos del servidor al cliente (progreso) de forma nativa. Sustituye al gRPC planteado inicialmente. |
| ADR-04 | Repositorio propio direccionado por contenido (§6), un objeto por archivo en formato v1. | Deduplicación y versiones sobre SMB plano, sin software en el NAS. *Chunking* y compresión en formato v2 (fase 3). |
| ADR-05 | El servicio corre como **LocalSystem**. | VSS y lectura de todos los archivos de usuario requieren privilegios; la cuenta de máquina no accede al NAS, por eso se conecta con credenciales explícitas. |
| ADR-06 | Configuración en JSON y el historial en SQLite (`Microsoft.Data.Sqlite`), en `%ProgramData%\Backbone`. | JSON legible y fácil de respaldar; SQLite para consultas de historial. |
| ADR-07 | Credenciales cifradas con DPAPI, ámbito `LocalMachine` + entropía propia, en archivos con ACL restringida a SYSTEM y Administradores. | Nunca en texto plano; solo el servicio de esa máquina las descifra. |
| ADR-08 | `TimeProvider` inyectado en todo el código que dependa del reloj. | Pruebas deterministas del scheduler y la retención. |
| ADR-09 | Instalador WiX v5 (MSI). | Registra el servicio, su recuperación, el origen del Visor de eventos y las ACL. |

---

## 4. Requisitos funcionales

Prioridad: **MVP** = fase 1, **F2** = fase 2 (uso diario), **F3** = fase 3 (pulido y rendimiento).

### 4.1 Trabajos

| ID | Requisito | Prioridad |
|---|---|---|
| RF-01 | Crear, editar, duplicar y eliminar trabajos. Al eliminar, el repositorio del NAS **no** se borra; se pregunta aparte. | MVP |
| RF-02 | Un trabajo tiene: nombre, uno o más orígenes (ruta + alias), destino UNC, exclusiones, programación, retención, condiciones y activado/desactivado. | MVP |
| RF-03 | Validación: orígenes existen y son NTFS local; alias únicos y válidos como nombre de carpeta; destino es UNC `\\servidor\recurso[\ruta]`; un destino no puede estar dentro de un origen ni ser usado por otro trabajo. | MVP |
| RF-04 | Exclusiones con patrones *glob* (`*.tmp`, `**/node_modules/**`, `Documentos/Privado/**`). Exclusiones por defecto: `desktop.ini`, `thumbs.db`, `~$*`, `*.tmp`, `$RECYCLE.BIN`, `System Volume Information`. | MVP |
| RF-05 | Exclusión por tamaño máximo de archivo (opcional). | F2 |
| RF-06 | No se siguen puntos de reanálisis (*junctions*, enlaces simbólicos): se registran como omitidos. Los marcadores de OneDrive "solo en la nube" no se descargan; se omiten con aviso. | MVP |

### 4.2 Programación y condiciones

| ID | Requisito | Prioridad |
|---|---|---|
| RF-10 | Tipos de programación: **Manual**; **Diaria** (una o más horas, días de la semana seleccionables); **Intervalo** (cada N horas, 1–24). | MVP |
| RF-11 | Recuperación de ejecuciones perdidas: si el equipo estaba apagado o suspendido a la hora prevista, el trabajo se ejecuta al arrancar el servicio o al reanudar, tras un periodo de gracia de 5 minutos (configurable). | MVP |
| RF-12 | Pausa global: ninguna ejecución programada se lanza mientras esté activa. Duración: 1 h, 4 h, hasta mañana, indefinida. "Copiar ahora" sigue disponible. | F2 |
| RF-13 | Condiciones por trabajo: no ejecutar con batería por debajo del X % (por defecto 30 %), no ejecutar en conexiones de uso medido, solo si el NAS es accesible. | F2 |
| RF-14 | Despertar el equipo para la ejecución programada (opcional, por trabajo) mediante una tarea del Programador de tareas con `WakeToRun`, que el servicio crea y mantiene. | F3 |
| RF-15 | Solo se ejecuta un trabajo a la vez; los demás quedan en cola en orden de llegada. | MVP |

### 4.3 Ejecución de copias

| ID | Requisito | Prioridad |
|---|---|---|
| RF-20 | "Copiar ahora" lanza un trabajo inmediatamente (o lo encola). | MVP |
| RF-21 | Cancelar una ejecución en curso. La cancelación no produce snapshot y no deja el repositorio inconsistente. | MVP |
| RF-22 | Uso de **VSS**: se crea una instantánea de cada volumen de origen, se lee desde ella y se elimina al terminar (también si la ejecución falla o se cancela). Si VSS no está disponible, se copia en vivo, se marca la ejecución "con avisos" y se registra el motivo. | MVP |
| RF-23 | Detección incremental: un archivo cuyo tamaño, fecha de modificación y atributos coinciden con el último snapshot no se vuelve a leer. | MVP |
| RF-24 | Detección incremental mediante **USN Change Journal** cuando está disponible (fallback al recorrido completo si el diario se ha reiniciado o desbordado). | F3 |
| RF-25 | Archivos que no se pueden leer (bloqueados sin VSS, sin permiso, ruta inválida) se omiten, se listan en la ejecución y esta termina "con avisos". El snapshot se genera igualmente y conserva la versión anterior de esos archivos si existía. | MVP |
| RF-26 | Reintentos ante errores transitorios de red (3 intentos, espera exponencial 2/8/30 s). Si el NAS desaparece durante más de 2 minutos, la ejecución falla de forma controlada. | MVP |
| RF-27 | Evitar la suspensión del sistema mientras dura una ejecución (`SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED)`). | MVP |
| RF-28 | Límite de ancho de banda configurable (MB/s) por trabajo. | F3 |
| RF-29 | Prioridad baja de E/S y CPU durante las copias (`THREAD_MODE_BACKGROUND_BEGIN`). | F2 |

### 4.4 Restauración

| ID | Requisito | Prioridad |
|---|---|---|
| RF-30 | Listar los snapshots de un trabajo con fecha, número de archivos, tamaño y estado. | MVP |
| RF-31 | Explorar un snapshot como un árbol de carpetas, con búsqueda por nombre. | MVP |
| RF-32 | Restaurar archivos o carpetas seleccionados a su ubicación original o a otra carpeta. Ante conflicto: **preguntar**, **sobrescribir**, **conservar ambos** (`nombre (restaurado 2026-11-15).ext`) u **omitir**; con opción "aplicar a todos". | MVP |
| RF-33 | La restauración conserva la fecha de modificación y los atributos guardados y verifica el hash de cada archivo restaurado. | MVP |
| RF-34 | Antes de restaurar, el servicio comprueba **suplantando al usuario que lo pide** que este tiene permiso de escritura en la carpeta de destino (§8.6). | MVP |
| RF-35 | Ver todas las versiones de un archivo concreto (en qué snapshots cambió) y restaurar cualquiera. | F2 |
| RF-36 | Integración con el Explorador: menú contextual "Backbone → Ver versiones anteriores" sobre archivos y carpetas incluidos en un trabajo. | F3 |

### 4.5 Retención y mantenimiento

| ID | Requisito | Prioridad |
|---|---|---|
| RF-40 | Retención por trabajo: conservar los últimos N, diarios D, semanales S, mensuales M y anuales A. Por defecto 3/7/4/12/0. Se aplica tras cada ejecución correcta. | F2 |
| RF-41 | Limpieza (*prune*) de objetos no referenciados, semanal por defecto y a petición. | F2 |
| RF-42 | Verificación: rápida (todos los objetos referenciados existen y tienen el tamaño esperado) tras cada limpieza; completa (relee y recalcula hashes) mensual y a petición. | F2 |
| RF-43 | Mostrar el espacio libre y total del recurso del NAS y el tamaño del repositorio. Aviso si el espacio libre baja del 10 % o de 20 GB. | F2 |
| RF-44 | Snapshots marcados como **protegidos** por el usuario, que la retención nunca elimina. | F3 |

### 4.6 Supervisión y notificaciones

| ID | Requisito | Prioridad |
|---|---|---|
| RF-50 | Icono en la bandeja con estado: correcto (verde), con avisos o desactualizado (amarillo), error o servicio detenido (rojo), copiando (animado). Menú: Copiar todo ahora, Pausar, Abrir Backbone, Salir. | MVP |
| RF-51 | Panel de inicio: estado general, por trabajo última ejecución, próxima ejecución y resultado, progreso en vivo (archivos, bytes, velocidad, archivo actual, tiempo estimado). | MVP |
| RF-52 | Notificaciones toast: ejecución fallida (siempre); con avisos (configurable); correcta (desactivado por defecto); sin copia correcta en X días (por defecto 3); espacio bajo en el NAS. | MVP (fallida) / F2 (resto) |
| RF-53 | Historial de ejecuciones filtrable por trabajo y estado, con detalle: duración, archivos nuevos/modificados/omitidos, bytes transferidos y lista de avisos y errores. Se conserva 1 año. | MVP |
| RF-54 | Escritura en el **Visor de eventos** (registro Aplicación, origen `Backbone`) con los identificadores de §8.5. | MVP |
| RF-55 | Exportar el registro y el diagnóstico (configuración sin secretos + últimos logs) a un ZIP. | F2 |

### 4.7 Conexión al NAS y credenciales

| ID | Requisito | Prioridad |
|---|---|---|
| RF-60 | Configurar por servidor (`\\servidor\recurso`): usuario y contraseña. Una credencial puede compartirse entre trabajos con el mismo recurso. | MVP |
| RF-61 | "Probar conexión": conecta, comprueba lectura, escritura y borrado de un archivo de prueba, informa del espacio libre y del dialecto SMB. Mensajes de error específicos: servidor inaccesible, credenciales rechazadas, recurso inexistente, sin permiso de escritura. | MVP |
| RF-62 | La contraseña nunca se devuelve a la app ni se escribe en logs; la app solo sabe si existe una credencial guardada. | MVP |
| RF-63 | Inicializar el repositorio en el destino si no existe; si existe, validar su formato y adoptarlo (permite reinstalar Windows y seguir con el mismo repositorio). | MVP |

### 4.8 Seguridad del repositorio

| ID | Requisito | Prioridad |
|---|---|---|
| RF-70 | Cifrado opcional del repositorio (AES-256-GCM, clave derivada de una contraseña del repositorio con Argon2id o PBKDF2-SHA256 ≥ 600 000 iteraciones). Se elige al crear el repositorio; aviso explícito de que perder la contraseña implica perder las copias. | F3 |

### 4.9 Primer uso

| ID | Requisito | Prioridad |
|---|---|---|
| RF-80 | Asistente al primer arranque de la app: conectar con el NAS → elegir carpetas (sugerencias: Documentos, Escritorio, Imágenes) → programación (sugerida: diaria 13:00 y al arrancar si se perdió) → primera copia. | F2 |

---

## 5. Requisitos no funcionales

| ID | Categoría | Requisito |
|---|---|---|
| RNF-01 | Fiabilidad | Matar el proceso del servicio o cortar la red en cualquier momento de una ejecución **nunca** deja un snapshot que referencie objetos inexistentes ni objetos con contenido que no coincide con su nombre. Comprobado con pruebas de inyección de fallos. |
| RNF-02 | Fiabilidad | El servicio se recupera solo: acciones de recuperación del SCM "reiniciar" a los 1, 1 y 5 minutos; contador a cero tras 1 día. |
| RNF-03 | Rendimiento | Ejecución incremental sin cambios sobre 100 000 archivos: < 60 s sin USN (F1), < 10 s con USN (F3). |
| RNF-04 | Rendimiento | Primera copia por Ethernet de 1 Gb: ≥ 80 % del rendimiento de `robocopy` sobre el mismo conjunto. |
| RNF-05 | Recursos | Servicio en reposo: CPU ≈ 0 %, memoria < 60 MB. En ejecución con 500 000 archivos: < 400 MB. |
| RNF-06 | Seguridad | Ninguna contraseña en texto plano en disco, logs, IPC de salida ni volcados de diagnóstico. |
| RNF-07 | Seguridad | `%ProgramData%\Backbone` con ACL: SYSTEM y Administradores control total; usuarios sin acceso. |
| RNF-08 | Seguridad | El canal IPC solo admite a SYSTEM, Administradores y usuarios autorizados (§7.3). Toda entrada recibida por IPC se valida en el servicio. |
| RNF-09 | Compatibilidad | Windows 11 22H2+ x64 y ARM64. SMB 2.1+. Rutas largas (> 260 caracteres) admitidas en origen y destino. |
| RNF-10 | Accesibilidad | Navegable entera con teclado; nombres accesibles para Narrador en todos los controles; compatible con temas de alto contraste; escalado hasta 200 %. |
| RNF-11 | Localización | Interfaz en español (es-ES) y en inglés (en-US) mediante archivos `.resw`; ninguna cadena visible escrita en el código. Fechas y tamaños con la cultura del usuario. |
| RNF-12 | Observabilidad | Logs estructurados con `Microsoft.Extensions.Logging` en `%ProgramData%\Backbone\logs`, rotación diaria, 14 días, máx. 10 MB por archivo. |
| RNF-13 | Mantenibilidad | Análisis de código .NET a nivel `latest-recommended`, `Nullable` activado, advertencias como errores en CI. Cobertura de Backbone.Core ≥ 85 %. |
| RNF-14 | Compatibilidad hacia delante | El repositorio lleva versión de formato; una versión de Backbone nunca escribe en un repositorio de formato superior al que entiende (solo lectura y aviso). |

---

## 6. Formato del repositorio (v1)

### 6.1 Estructura

```
<destino>\
├── repo.json                      configuración del repositorio
├── objects\ab\ab12…ef             contenido de archivos; nombre = SHA-256 en hex minúsculas (64 caracteres)
├── snapshots\<id>.json            manifiestos de snapshots completados
├── locks\<host>-<pid>.lock        bloqueos (§6.4)
└── tmp\                           escritura temporal; puede vaciarse en cualquier momento sin bloqueos activos
```

### 6.2 `repo.json`

```json
{
  "formatVersion": 1,
  "repositoryId": "8f6c2e1a-…",
  "createdUtc": "2026-11-15T02:00:00Z",
  "createdBy": "Backbone 1.0.0 en PC-SALON",
  "hash": "sha256",
  "compression": "none",
  "encryption": "none"
}
```

### 6.3 Snapshot `snapshots/<id>.json`

`id` = `yyyyMMddTHHmmssZ-<4 hex aleatorios>`, ordenable por fecha.

```json
{
  "formatVersion": 1,
  "id": "20261115T020000Z-a1b2",
  "jobId": "1b2c…",
  "jobName": "Documentos",
  "host": "PC-SALON",
  "startedUtc": "2026-11-15T02:00:00Z",
  "completedUtc": "2026-11-15T02:03:12Z",
  "parentId": "20261114T020000Z-77fe",
  "status": "completed | completedWithWarnings",
  "usedVss": true,
  "sources": [ { "alias": "Documentos", "path": "C:\\Users\\ana\\Documents" } ],
  "stats": { "files": 12034, "bytes": 5480123456, "newFiles": 12, "changedFiles": 3, "newObjects": 14, "newBytes": 10485760, "skipped": 1 },
  "warnings": [ { "path": "Documentos/bloqueado.pst", "code": "FileLocked", "message": "…" } ],
  "entries": [
    { "p": "Documentos/Facturas/2026/enero.pdf", "h": "ab12…ef", "s": 182733, "m": "2026-01-31T10:22:01.1234567Z", "a": 32 }
  ]
}
```

- `p`: ruta relativa con `/` como separador, prefijada por el alias del origen. Única dentro del snapshot.
- `h`: hash del contenido; `s`: tamaño en bytes; `m`: última modificación UTC (precisión de 100 ns); `a`: `FileAttributes` relevantes (ReadOnly, Hidden, System, Archive).
- Las carpetas vacías se representan con una entrada `{ "p": "…/", "d": true }`.
- Los snapshots son **inmutables**: nunca se modifican después de escribirse.

### 6.4 Reglas de escritura y concurrencia

1. Todo archivo se escribe primero en `tmp\<guid>.tmp` y se renombra al nombre final (operación atómica dentro del mismo recurso SMB). Si el destino ya existe, se descarta el temporal.
2. Un objeto se escribe calculando el SHA-256 **mientras se copia**. Si el hash final no coincide con el calculado al inspeccionar el archivo (el archivo cambió durante la copia sin VSS), se usa el hash real y se registra el cambio.
3. El snapshot se escribe **solo después** de que todos sus objetos existen. Lo que no tiene snapshot no existe a efectos de restauración.
4. Bloqueos: una ejecución de copia crea `locks\<host>-<pid>.lock` (JSON con host, pid, tipo `shared`, `heartbeatUtc` actualizado cada 60 s). La limpieza necesita un bloqueo `exclusive` y que no haya otros bloqueos vigentes. Un bloqueo sin latido en 15 minutos se considera abandonado y puede borrarse.
5. La limpieza borra primero los snapshots que expiran y después los objetos no referenciados **con más de 24 h de antigüedad** (protege objetos de una ejecución en curso en otro equipo).
6. `tmp\` se vacía al empezar una limpieza.

### 6.5 Evolución (v2, F3)

Objetos troceados (*content-defined chunking*, FastCDC, tamaño medio 1 MiB) empaquetados en *packs* de ~16 MiB con índice, compresión zstd y cifrado opcional. Un repositorio v1 se puede seguir leyendo; la migración v1 → v2 es explícita.

---

## 7. Contrato IPC

### 7.1 Transporte

- Named pipe `Backbone.Service.v1` (`\\.\pipe\Backbone.Service.v1`), dúplex, asíncrono, mensajes con `HeaderDelimitedMessageHandler` y `SystemTextJsonFormatter`.
- Una conexión por instancia de la app; el servidor admite hasta 8 conexiones simultáneas.
- Versión: `GetServiceInfoAsync` devuelve la versión del contrato; la app muestra un aviso si no coincide en la versión mayor.

### 7.2 Interfaz (`Backbone.Contracts.IBackboneService`)

```csharp
public interface IBackboneService
{
    // Información y estado
    Task<ServiceInfo> GetServiceInfoAsync(CancellationToken ct = default);   // versión, contrato, autorizaciones del llamante
    Task<ServiceStatus> GetStatusAsync(CancellationToken ct = default);      // estado global, pausa, ejecución actual, cola

    // Trabajos
    Task<IReadOnlyList<JobDefinition>> GetJobsAsync(CancellationToken ct = default);
    Task<JobDefinition> SaveJobAsync(JobDefinition job, CancellationToken ct = default);   // crea si Id == Guid.Empty
    Task DeleteJobAsync(Guid jobId, CancellationToken ct = default);
    Task<ValidationResult> ValidateJobAsync(JobDefinition job, CancellationToken ct = default);

    // Ejecución
    Task RunNowAsync(Guid jobId, CancellationToken ct = default);
    Task CancelRunAsync(Guid jobId, CancellationToken ct = default);
    Task SetPauseAsync(PauseRequest request, CancellationToken ct = default);

    // NAS y credenciales
    Task SaveCredentialAsync(string shareRoot, string userName, string password, CancellationToken ct = default);
    Task<CredentialInfo?> GetCredentialInfoAsync(string shareRoot, CancellationToken ct = default); // sin contraseña
    Task DeleteCredentialAsync(string shareRoot, CancellationToken ct = default);
    Task<ConnectionTestResult> TestConnectionAsync(string destination, CredentialInput? credential, CancellationToken ct = default);
    Task<StorageInfo> GetStorageInfoAsync(Guid jobId, CancellationToken ct = default);

    // Historial
    Task<IReadOnlyList<RunRecord>> GetHistoryAsync(HistoryQuery query, CancellationToken ct = default);
    Task<RunDetail> GetRunDetailAsync(long runId, CancellationToken ct = default);

    // Restauración
    Task<IReadOnlyList<SnapshotInfo>> GetSnapshotsAsync(Guid jobId, CancellationToken ct = default);
    Task<IReadOnlyList<SnapshotItem>> BrowseSnapshotAsync(Guid jobId, string snapshotId, string path, CancellationToken ct = default);
    Task<IReadOnlyList<SnapshotItem>> SearchSnapshotAsync(Guid jobId, string snapshotId, string text, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<FileVersion>> GetFileVersionsAsync(Guid jobId, string path, CancellationToken ct = default); // F2
    Task<Guid> StartRestoreAsync(RestoreRequest request, CancellationToken ct = default);
    Task ResolveRestoreConflictAsync(Guid restoreId, ConflictResolution resolution, bool applyToAll, CancellationToken ct = default);
    Task CancelRestoreAsync(Guid restoreId, CancellationToken ct = default);

    // Mantenimiento (F2)
    Task ApplyRetentionAsync(Guid jobId, bool dryRun, CancellationToken ct = default);
    Task StartPruneAsync(Guid jobId, CancellationToken ct = default);
    Task StartCheckAsync(Guid jobId, bool full, CancellationToken ct = default);

    // Eventos push (StreamJsonRpc los envía como notificaciones)
    event EventHandler<StatusChangedEvent> StatusChanged;
    event EventHandler<RunProgressEvent> RunProgress;          // como máximo 4 por segundo
    event EventHandler<RunCompletedEvent> RunCompleted;
    event EventHandler<RestoreProgressEvent> RestoreProgress;
    event EventHandler<RestoreConflictEvent> RestoreConflict;  // espera ResolveRestoreConflictAsync
}
```

Todos los DTO son `record` inmutables en `Backbone.Contracts`. Las fechas viajan como `DateTimeOffset` UTC.

### 7.3 Seguridad del canal

- ACL del pipe: SYSTEM y `BUILTIN\Administrators` control total; cada SID de `settings.json → authorizedUsers` lectura/escritura. El instalador añade el SID del usuario que instala.
- Como con UAC el token sin elevar de un administrador tiene el grupo Administradores deshabilitado, **la app funciona sin elevar** gracias a que el usuario está en `authorizedUsers`.
- Gestionar la lista de usuarios autorizados solo es posible desde el instalador o la app **elevada** (comprobación del token del cliente en el servidor).
- Cada llamada identifica al cliente (`GetImpersonationUserName` / `RunAsClient`) y lo registra en el log de auditoría para operaciones de escritura.

### 7.4 Errores

Errores de negocio como `RemoteInvocationException` con `ErrorCode` del enumerado `BackboneErrorCode`:

`ValidationFailed`, `JobNotFound`, `JobAlreadyRunning`, `NasUnreachable`, `CredentialsRejected`, `ShareNotFound`, `AccessDenied`, `RepositoryLocked`, `RepositoryIncompatible`, `RepositoryCorrupt`, `SnapshotNotFound`, `RestoreTargetNotWritable`, `Interrupted` (suspensión o apagado durante la ejecución), `NotAuthorized`, `NotSupported` (método de una fase aún no implementada), `Internal`.

La app traduce cada código a un mensaje localizado con una acción sugerida.

---

## 8. Servicio de Windows

### 8.1 Registro

| Propiedad | Valor |
|---|---|
| Nombre | `BackboneService` |
| Nombre para mostrar | Backbone — Copias de seguridad |
| Cuenta | LocalSystem |
| Inicio | Automático (inicio retrasado) |
| Recuperación | Reiniciar a 1 min, 1 min, 5 min; reiniciar contador tras 1 día |
| Dependencias | `LanmanWorkstation` |
| Apagado | Señal de cancelación a la ejecución en curso; se espera hasta 20 s (`HostOptions.ShutdownTimeout`) |

El ejecutable funciona también como aplicación de consola (`--console`) para depurar sin instalar.

### 8.2 Componentes internos

| Componente | Responsabilidad |
|---|---|
| `JobStore` | Lee y escribe `config\jobs.json` con escritura atómica; notifica cambios. |
| `SettingsStore` | `config\settings.json`: usuarios autorizados, pausa, preferencias de notificación, periodo de gracia. |
| `CredentialStore` | Cifra y descifra con DPAPI (`ProtectedData`, `DataProtectionScope.LocalMachine`); un archivo por servidor en `credentials\`. |
| `HistoryStore` | SQLite `data\history.db`: tablas `runs`, `run_issues`; migraciones versionadas; purga de más de 1 año. |
| `Scheduler` | Calcula la próxima ejecución de cada trabajo con `TimeProvider`; cola FIFO de un solo ejecutor; recuperación de ejecuciones perdidas; escucha cambios de configuración, de energía y de red. |
| `JobRunner` | Orquesta una ejecución: condiciones → conexión SMB → bloqueo → VSS → `BackupEngine` → snapshot → retención → historial → eventos. |
| `ShareConnector` | `WNetAddConnection2` sin letra de unidad con las credenciales guardadas; `WNetCancelConnection2` al final; traduce códigos Win32 a `BackboneErrorCode`. |
| `VssSnapshotter` | Crea y elimina instantáneas VSS (WMI `Win32_ShadowCopy` o API COM de VSS); traduce rutas del origen a la ruta del dispositivo de la instantánea. Garantiza la eliminación con `finally` y limpia al arrancar instantáneas huérfanas creadas por Backbone. |
| `PowerManager` | `SetThreadExecutionState`; estado de batería; eventos de suspensión y reanudación (`SystemEvents` / `RegisterPowerSettingNotification`). |
| `NetworkMonitor` | Cambios de red y coste de la conexión (Network List Manager / `NetworkInformation`). |
| `EventLogWriter` | Escribe en el registro Aplicación con el origen `Backbone`. |
| `IpcServer` | Bucle de aceptación de pipes, `PipeSecurity`, una instancia de `JsonRpc` por cliente, difusión de eventos. |

### 8.3 Rutas

```
%ProgramData%\Backbone\
├── config\jobs.json, settings.json
├── credentials\<hash-del-servidor>.bin
├── data\history.db
└── logs\backbone-YYYYMMDD.log
```

### 8.4 Ciclo de una ejecución

1. Comprobar condiciones (RF-13). Si no se cumplen → ejecución `Skipped` con motivo, se reintenta en el siguiente punto de programación.
2. Conectar con el recurso; inicializar o validar el repositorio (RF-63).
3. Adquirir bloqueo `shared`.
4. Crear instantáneas VSS de los volúmenes de origen (RF-22).
5. Recorrer orígenes aplicando exclusiones; comparar con el último snapshot (RF-23); subir objetos nuevos (§6.4).
6. Escribir el snapshot.
7. Aplicar retención (F2).
8. Liberar bloqueo, eliminar instantáneas, desconectar el recurso, restaurar el estado de energía (siempre, en `finally`).
9. Guardar en historial, escribir en el Visor de eventos, emitir `RunCompleted`.

Estados de ejecución: `Queued`, `Running`, `Completed`, `CompletedWithWarnings`, `Failed`, `Cancelled`, `Skipped`.

### 8.5 Identificadores del Visor de eventos

| ID | Nivel | Evento |
|---|---|---|
| 1000 | Información | Servicio iniciado (versión) |
| 1001 | Información | Servicio detenido |
| 2000 | Información | Copia completada (trabajo, archivos, bytes, duración) |
| 2001 | Advertencia | Copia completada con avisos |
| 2002 | Error | Copia fallida (código, mensaje) |
| 2003 | Información | Copia cancelada por el usuario |
| 3000 | Error | NAS inaccesible |
| 3001 | Error | Credenciales rechazadas por el NAS |
| 3002 | Advertencia | Espacio bajo en el NAS |
| 4000 | Advertencia | VSS no disponible; copia en vivo |
| 5000 | Información | Retención y limpieza aplicadas |
| 6000 | Error | Verificación de integridad fallida |
| 7000 | Advertencia | Sin copia correcta en X días |
| 8000 | Información | Restauración completada |

### 8.6 Restauración segura

El servicio corre como SYSTEM y podría escribir en cualquier sitio. Para evitar que un usuario autorizado use la restauración para escribir donde no debe:

1. El servicio suplanta al cliente (`NamedPipeServerStream.RunAsClient`) y comprueba que puede crear y borrar un archivo de prueba en la carpeta de destino.
2. Si no puede → `RestoreTargetNotWritable`.
3. Los archivos restaurados heredan las ACL de la carpeta de destino.
4. Se rechazan destinos dentro de `%WINDIR%`, `%ProgramFiles%`, `%ProgramFiles(x86)%` y `%ProgramData%\Backbone`.

---

## 9. Aplicación WinUI 3

### 9.1 Generalidades

- Sin empaquetar, una sola instancia (`AppInstance.FindOrRegisterForKey`); una segunda ejecución trae al frente la existente.
- Se inicia con la sesión (clave `HKCU\…\Run` creada por el instalador, desactivable en Ajustes) minimizada en la bandeja.
- Cerrar la ventana la oculta en la bandeja; "Salir" en el menú de la bandeja cierra la app (no el servicio).
- Fondo Mica, tema claro/oscuro del sistema, iconos de Segoe Fluent Icons.
- Patrón MVVM con `CommunityToolkit.Mvvm`; inyección de dependencias con `Microsoft.Extensions.DependencyInjection`.
- Icono de bandeja con `H.NotifyIcon.WinUI`; notificaciones con `Microsoft.Windows.AppNotifications`.
- Cliente IPC con reconexión automática (espera 1 s, 2 s, 5 s, 10 s, luego cada 10 s) e indicador "reconectando".

### 9.2 Navegación (`NavigationView`)

| Página | Contenido |
|---|---|
| **Inicio** | Tarjeta de estado general; una tarjeta por trabajo (estado, última y próxima ejecución, botón Copiar ahora / Cancelar, progreso en vivo); espacio del NAS (F2); avisos activos. |
| **Trabajos** | Lista de trabajos; editor con secciones: General (nombre, activado), Orígenes (añadir con `FolderPicker`, alias), Destino (UNC, credenciales, Probar conexión), Exclusiones (lista editable con vista previa de cuántos archivos excluye), Programación, Retención (F2), Condiciones (F2). Validación en línea mediante `ValidateJobAsync`. |
| **Restaurar** | Selector de trabajo → lista de snapshots (línea de tiempo agrupada por día) → explorador del snapshot (`TreeView` + lista con nombre, tamaño, fecha) con búsqueda → selección múltiple → diálogo de restauración (original / otra carpeta, política de conflictos) → progreso y resumen. |
| **Historial** | Lista virtualizada de ejecuciones con filtros; panel de detalle con avisos y errores; botón "Abrir en el Visor de eventos". |
| **Ajustes** | Pausa, notificaciones, inicio con Windows, periodo de gracia, usuarios autorizados (requiere elevar), exportar diagnóstico (F2), acerca de (versiones de app, servicio y contrato). |

### 9.3 Estados especiales

- **Servicio no instalado o detenido**: banner rojo persistente con "Iniciar servicio" (lanza `sc start` elevado mediante `runas`).
- **Sin autorización**: pantalla explicativa con el comando para añadirse como usuario autorizado.
- **Versión de contrato incompatible**: aviso con la versión de cada parte.
- **Sin trabajos**: estado vacío con botón al asistente de primer uso.

---

## 10. Instalador (WiX v5)

- MSI por arquitectura (x64, ARM64), per-machine, requiere elevación.
- Instala servicio y app en `%ProgramFiles%\Backbone\`.
- `ServiceInstall` + `ServiceConfig` (inicio retrasado, recuperación), `ServiceControl` para arrancar al instalar y detener al desinstalar o actualizar.
- Crea el origen del Visor de eventos `Backbone`.
- Crea `%ProgramData%\Backbone` con las ACL de RNF-07.
- Añade el SID del usuario que instala a `authorizedUsers`.
- Acceso directo en el menú Inicio; entrada `HKCU\…\Run` opcional (casilla, activada por defecto).
- Actualizaciones mayores (`MajorUpgrade`) que conservan la configuración, el historial y las credenciales.
- Desinstalación: casilla "Eliminar también configuración e historial" (desactivada por defecto). **Nunca** toca repositorios del NAS.
- Firma de código (Authenticode) del MSI y los ejecutables en CI cuando haya certificado disponible.

---

## 11. Estrategia de pruebas

| Nivel | Proyecto | Qué cubre |
|---|---|---|
| Unitarias | `Backbone.Core.Tests` | Formato del repositorio, backup incremental, deduplicación, restauración, exclusiones, retención (tabla de casos con fechas fijas), programación, escrituras atómicas. Almacenamiento abstraído (`IRepositoryStorage`) para inyectar fallos en cualquier operación. |
| Unitarias | `Backbone.Service.Tests` | Scheduler con `FakeTimeProvider` (ejecuciones perdidas, intervalos, cambios de configuración), cola, validación de entradas IPC, `JobStore`/`HistoryStore` sobre carpetas temporales. |
| Integración | `Backbone.IntegrationTests` | Servidor y cliente IPC reales sobre un pipe con nombre aleatorio; ejecución completa contra un recurso SMB local (`New-SmbShare` sobre carpeta temporal, requiere admin); VSS real (requiere admin). |
| Fallos | `Backbone.Core.Tests` | Para cada punto de escritura: fallo simulado → el repositorio supera `check` y la siguiente ejecución termina bien (RNF-01). |
| Manual | `docs/PRUEBAS-MANUALES.md` (a crear) | Suspensión e hibernación durante copia, desconexión del cable, reinicio del NAS, apagado de Windows en mitad de una copia, UI con Narrador y alto contraste. |

Cada historia de usuario lista sus criterios de aceptación; cada criterio debe tener al menos una prueba automática o un caso manual documentado.

---

## 12. Fases

| Fase | Objetivo | Historias |
|---|---|---|
| **F1 — MVP** | Copias programadas fiables con VSS, restauración básica, app con bandeja y panel, instalador. | Ver columna "Fase = MVP" en [HISTORIAS.md](HISTORIAS.md) |
| **F2 — Uso diario** | Retención, limpieza y verificación, condiciones de red y energía, notificaciones completas, versiones de un archivo, asistente, diagnóstico. | Fase = F2 |
| **F3 — Pulido y rendimiento** | USN Journal, formato v2 (chunking, compresión, cifrado), ancho de banda, integración con el Explorador, despertar el equipo. | Fase = F3 |

## 13. Riesgos y cuestiones abiertas

| # | Riesgo / cuestión | Mitigación |
|---|---|---|
| R1 | La API de VSS desde .NET es incómoda (COM); WMI `Win32_ShadowCopy.Create` puede comportarse distinto entre ediciones. | Encapsular tras `IVolumeSnapshotter`; prototipo en la primera historia de VSS; fallback a copia en vivo. |
| R2 | Renombrar sobre SMB en OMV/Samba: atomicidad y comportamiento con antivirus del NAS. | Pruebas de integración contra Samba real antes de cerrar el MVP. |
| R3 | Manifiestos JSON grandes (500 000 archivos ≈ 80 MB). | Lectura/escritura en *streaming* con `Utf8JsonReader`/`Utf8JsonWriter`; comprimir manifiestos (gzip) a partir de v2. |
| R4 | Archivos de OneDrive "solo en la nube". | Detectar `FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS` / `RECALL_ON_OPEN` y omitir sin desencadenar la descarga. |
| R5 | Firma de código: sin certificado, SmartScreen avisará. | Documentar; obtener certificado antes de distribuir fuera del entorno propio. |
| Q1 | ¿Varias máquinas en el mismo repositorio? | v1: no se soporta oficialmente (un repositorio por trabajo y equipo), aunque el formato ya lo tolera. |

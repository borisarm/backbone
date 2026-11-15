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
| **Usuario autorizado** | Cuenta de usuario de Windows, miembro del grupo local Administradores, que puede gestionar Backbone desde la app sin elevar (§7.3). |

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
│   ├── Backbone.Contracts.Tests/     contrato IPC: ida y vuelta de cada DTO, convenciones, errores y eventos con StreamJsonRpc en memoria
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
| ADR-10 | Serialización IPC con las opciones **por defecto** de `SystemTextJsonFormatter` (nombres de propiedad tal cual, enumerados como número con valores explícitos y estables); DTO como `record` sellados con propiedades `init` opcionales y valores por defecto, sin `required` ni herencia (§7.5). `Backbone.Contracts` no depende de ningún paquete. | Nada que configurar igual en ambos extremos; añadir campos o valores de enumerado no rompe a un extremo más antiguo (versión menor). |
| ADR-11 | El contenido del repositorio es **no confiable**: límites explícitos de tamaño, número y profundidad en los manifiestos (§6.3), ningún punto de reanálisis dentro del repositorio (→ corrupto), almacenamiento solo sobre destinos UNC que no sean el propio equipo (§6.1) y textos saneados antes de registrarlos o mostrarlos (también toda la categoría Unicode `Cf`, y también en el servicio). Los nombres no direccionables de `locks\` cuentan como bloqueo exclusivo ilegible (latido = fecha de modificación; dejan de bloquear al quedar abandonados) y los de `tmp\` se ignoran; Backbone no los borra (R8). Un latido en el futuro más allá de la tolerancia de reloj es anómalo y no alarga el bloqueo (§6.4.4). El escritor recorta los avisos (`WarningsTruncated`) para no publicar nunca un manifiesto que el lector rechace. | Otro equipo o cualquiera con escritura en el recurso del NAS puede manipularlo; un manifiesto hostil no debe agotar memoria ni registros, ni un *junction* llevar escrituras o borrados fuera del repositorio. Límites holgados (5 000 000 de entradas = 10 × RNF-05) para no rechazar copias legítimas. |

---

## 4. Requisitos funcionales

Prioridad: **MVP** = fase 1, **F2** = fase 2 (uso diario), **F3** = fase 3 (pulido y rendimiento).

### 4.1 Trabajos

| ID | Requisito | Prioridad |
|---|---|---|
| RF-01 | Crear, editar, duplicar y eliminar trabajos. Al eliminar, el repositorio del NAS **no** se borra; se pregunta aparte. | MVP |
| RF-02 | Un trabajo tiene: nombre, uno o más orígenes (ruta + alias), destino UNC, exclusiones, programación, retención, condiciones y activado/desactivado. | MVP |
| RF-03 | Validación: orígenes existen y son NTFS local; alias únicos y válidos como nombre de carpeta; destino es UNC `\\servidor\recurso[\ruta]`; un destino no puede estar dentro de un origen ni ser usado por otro trabajo; se rechazan como destino el propio equipo o *loopback*, las rutas `\\?\` y `\\.\` y los recursos administrativos (`C$`, `ADMIN$`, `IPC$`) (§7.5). Límites de longitud y número en §7.5. | MVP |
| RF-04 | Exclusiones con patrones *glob* (`*.tmp`, `**/node_modules/**`, `Documentos/Privado/**`). Exclusiones por defecto: `desktop.ini`, `thumbs.db`, `~$*`, `*.tmp`, `$RECYCLE.BIN`, `System Volume Information`. | MVP |
| RF-05 | Exclusión por tamaño máximo de archivo (opcional). | F2 |
| RF-06 | No se siguen puntos de reanálisis (*junctions*, enlaces simbólicos): se registran como omitidos. Los marcadores de OneDrive "solo en la nube" no se descargan; se omiten con aviso. | MVP |

### 4.2 Programación y condiciones

| ID | Requisito | Prioridad |
|---|---|---|
| RF-10 | Tipos de programación: **Manual**; **Diaria** (una o más horas, días de la semana seleccionables); **Intervalo** (cada N horas, 1–24). | MVP |
| RF-11 | Recuperación de ejecuciones perdidas: si el equipo estaba apagado o suspendido a la hora prevista, el trabajo se ejecuta al arrancar el servicio o al reanudar, tras un periodo de gracia de 5 minutos (configurable). | MVP |
| RF-12 | Pausa global: ninguna ejecución programada se lanza mientras esté activa. Duración: 1 h, 4 h, hasta mañana (termina mañana a las 06:00 en hora local del usuario; lo calcula la app), indefinida. "Copiar ahora" sigue disponible. | F2 |
| RF-13 | Condiciones por trabajo: no ejecutar con batería por debajo del X % (por defecto 30 %), no ejecutar en conexiones de uso medido, solo si el NAS es accesible. Las tres están **activadas por defecto**; con el NAS inaccesible, la ejecución queda `Skipped` (no `Failed`). | F2 |
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
| RF-32 | Restaurar archivos o carpetas seleccionados a su ubicación original o a otra carpeta. A otra carpeta, cada elemento seleccionado se coloca directamente en ella con su subárbol, como al copiar y pegar en Windows (`Documentos/Fotos/2024` → `D:\Restaurado\2024\…`). Ante conflicto: **preguntar**, **sobrescribir**, **conservar ambos** (`nombre (restaurado 2026-11-15).ext`) u **omitir**; con opción "aplicar a todos". | MVP |
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
| RF-52 | Notificaciones toast: ejecución fallida (siempre); con avisos (configurable, activado por defecto); correcta (desactivado por defecto); sin copia correcta en X días (por defecto 3; activado); espacio bajo en el NAS (activado). Preferencias en `settings.json`, gestionadas con `GetSettingsAsync`/`SaveSettingsAsync`. | MVP (fallida) / F2 (resto) |
| RF-53 | Historial de ejecuciones filtrable por trabajo y estado, con detalle: duración, archivos nuevos/modificados/omitidos, bytes transferidos y lista de avisos y errores. Se conserva 1 año. | MVP |
| RF-54 | Escritura en el **Visor de eventos** (registro Aplicación, origen `Backbone`) con los identificadores de §8.5. | MVP |
| RF-55 | Exportar el registro y el diagnóstico (configuración sin secretos + últimos logs) a un ZIP. El servicio lo escribe en la ruta que elige el usuario, suplantándolo (§8.7). | F2 |

### 4.7 Conexión al NAS y credenciales

| ID | Requisito | Prioridad |
|---|---|---|
| RF-60 | Configurar por servidor (`\\servidor\recurso`): usuario y contraseña. Una credencial puede compartirse entre trabajos con el mismo recurso. El servicio **nunca** conecta al NAS sin una credencial explícita (`CredentialRequired`). | MVP |
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
| RNF-08 | Seguridad | El canal IPC solo admite a SYSTEM, Administradores y usuarios autorizados, que son siempre cuentas administradoras (§7.3); el cliente verifica que el servidor es el servicio (§7.1). Toda entrada recibida por IPC se valida en el servicio con los límites de §7.5. |
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

- Los nombres de carpeta son exactamente los de arriba, en minúsculas. `objects\ab\` toma los dos primeros caracteres del hash y se crea cuando se escribe el primer objeto con ese prefijo.
- Estado de un destino al examinarlo (sin escribir nada):
  - **Inexistente**: la carpeta no existe.
  - **Vacío**: sin archivos, o solo con lo que deja una inicialización interrumpida (las carpetas de arriba, vacías salvo `tmp\`, que puede tener temporales). Se puede inicializar.
  - **No es un repositorio**: hay otros archivos y no hay `repo.json`. No se inicializa encima.
  - **Compatible**, **incompatible** o **corrupto**, según `repo.json` (§6.2).
- Un destino con `repo.json` en el que falta cualquiera de las cuatro carpetas (`objects`, `snapshots`, `locks`, `tmp`) es **corrupto**. No se recrean automáticamente: puede indicar que se apunta a un recurso equivocado o que se han borrado datos.
- **Solo destinos UNC**: el almacenamiento del repositorio (`FileSystemRepositoryStorage`) solo se crea sobre un destino que cumple `UncPaths.TryParse` (§7.5): nada de rutas locales, `\\?\`, `\\.\` o `\??\`, *loopback* ni recursos administrativos (`C$`, `ADMIN$`, `IPC$`). El servicio comprueba además, antes de crearlo, que el destino no apunta al propio equipo (sus nombres, sus direcciones y la resolución del nombre, §7.5). Las pruebas usan una variante explícita para carpetas locales (`ForLocalTesting`) que el servicio no usa nunca.
- **Puntos de reanálisis**: Backbone no sigue ninguno dentro del repositorio. Antes de cada operación se comprueba la raíz, cada carpeta intermedia (`objects\`, `objects\ab\`, `snapshots\`, `locks\`, `tmp\`) y el propio elemento si existe; al enumerar una carpeta, también cada elemento que contiene. Un punto de reanálisis (*junction*, enlace simbólico u otro) en cualquiera de ellos hace que el repositorio se trate como **corrupto** (`Corrupt`) y la operación falle sin tocar nada: un repositorio manipulado no puede hacer que una copia escriba, ni que una limpieza borre, fuera de él. Queda una ventana entre la comprobación y la operación (R7).
- **Contenido no confiable**: lo que se lee del repositorio lo puede haber escrito otro equipo o cualquiera con acceso de escritura al recurso. Además de los límites de §6.2–§6.4, los textos de `repo.json`, de la cabecera de los snapshots y de los bloqueos (`createdBy`, `host`, `jobName`, `jobId`, `sources[].path`, los avisos, y cualquier valor leído que aparezca en un mensaje de error) se **sanean antes de registrarlos o mostrarlos**:
  - En logs, mensajes de excepción y eventos se recortan a **200 caracteres** (con `…` detrás) y se escriben como `\uXXXX` los caracteres de control (categoría Unicode `Cc`), las suplentes sueltas, U+2028, U+2029 y **toda la categoría Unicode `Cf` (formato)**: controles bidireccionales (U+200E, U+200F, U+061C, U+202A–U+202E, U+2066–U+2069), espacios y uniones de anchura cero (U+200B–U+200D, U+2060), BOM (U+FEFF), guion blando (U+00AD), etiquetas (U+E0000–U+E007F) y el resto. La categoría se decide por punto de código (`Rune.GetUnicodeCategory`), así que un carácter `Cf` fuera del plano básico se escribe como sus dos unidades UTF-16 (`\uXXXX\uXXXX`). Así un repositorio hostil no puede inundar los registros, falsear líneas, ocultar caracteres ni reordenar visualmente el texto. En Core lo hace `Untrusted.Describe`.
  - El saneado se aplica igual en el **servicio** cuando registra o reenvía estos textos (logs, Visor de eventos, mensajes de error por IPC). Si el servicio reutiliza el helper de Core o tiene uno propio compartido se decide en la primera historia del servicio que los registre (HU-04/HU-05); el comportamiento es el de esta sección.
  - La app los muestra siempre como texto plano (nunca como marcado ni como enlace o ruta que se abre), con los mismos caracteres (controles, suplentes sueltas, U+2028, U+2029 y `Cf`) sustituidos.
- **Nombres no direccionables en `tmp\` y `locks\`**: el NAS puede servir nombres que Backbone no puede abrir ni borrar por ruta de Windows (p. ej. `nul.lock` o `a:b.tmp` creados en el NAS y servidos por Samba sin *mangling*). En `tmp\` se **ignoran**; en `locks\` cada uno cuenta como **bloqueo ilegible** (`exclusive`, con la fecha de modificación del archivo como latido, §6.4.4): impide copias y limpiezas mientras no esté abandonado (15 minutos sin cambiar su fecha de modificación) y después deja de bloquear. Backbone no los borra nunca, ni vigentes ni abandonados (tampoco la limpieza ni la adquisición exclusiva); hay que quitarlos a mano en el NAS (R8).

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

- JSON UTF-8 sin BOM, con sangría. Como mucho 64 KiB (igual que los bloqueos); si es mayor, el repositorio es corrupto.
- `formatVersion` se comprueba **antes que ningún otro campo**: mayor que la versión que entiende Backbone → **incompatible** (RNF-14), aunque el resto del archivo no se entienda; ausente, no entera o menor que 1 → corrupto.
- `hash` distinto de `sha256`, o `compression`/`encryption` distintos de `none` → incompatible (características de un formato posterior).
- `repositoryId` es un GUID con guiones (formato `D`); `createdUtc` sigue las reglas de fechas de §6.3. Si falta algún campo o no es válido → corrupto.
- `createdBy` es texto libre y no confiable (§6.1); el UTF-16 no válido se sustituye por U+FFFD.
- **Inicialización**: se crean las carpetas de §6.1 y `repo.json` se publica **el último**, de forma atómica y sin sobrescribir (§6.4.1). Hasta que existe, el destino no es un repositorio y una inicialización interrumpida se puede repetir. Si otro proceso lo publicó antes, la inicialización falla como "ya inicializado".

### 6.3 Snapshot `snapshots/<id>.json`

`id` = `yyyyMMddTHHmmssZ-<4 hex aleatorios>`, ordenable por fecha con comparación ordinal.

- La fecha del identificador es `startedUtc` truncada al segundo. Los 4 hexadecimales van en minúsculas. Solo se admite la forma canónica (fecha válida, 21 caracteres).
- El identificador se elige **libre antes de empezar** (se comprueba que no existe `snapshots\<id>.json`; si existe, se genera otro sufijo). Si al publicar ya existe un manifiesto con ese nombre, no se sobrescribe y la copia falla con `SnapshotIdConflict`.
- Los archivos de `snapshots\` cuyo nombre no es `<id>.json` con un identificador válido se ignoran al listar.

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

El ejemplo está sangrado para leerlo; el archivo real es compacto (ver "Serialización").

**Cabecera**

- **Orden (regla, no solo ejemplo)**: todos los campos de cabecera van **antes** de `entries`, y `entries` es el último campo que escribe Backbone. Así se lee la cabecera (listar snapshots) sin recorrer las entradas. El lector no busca cabecera después de `entries`: lo que haya detrás se descarta, y si falta un campo obligatorio antes de `entries` el manifiesto es corrupto.
- `id` debe coincidir con el nombre del archivo.
- `jobId`: no vacío (el escritor pone el GUID del trabajo en formato `D`). `jobName`: el nombre del trabajo al hacer la copia. `host`: nombre del equipo, que cumple las reglas de segmento (abajo). Los tres son texto no confiable (§6.1).
- `parentId` siempre está presente: `null` explícito en el primer snapshot del trabajo.
- `status`: `completed` o `completedWithWarnings`; cualquier otro valor → corrupto.
- `sources`: al menos uno; cada `alias` cumple las reglas de segmento (abajo) y los alias son únicos **sin distinguir mayúsculas**. `path` es la ruta local del origen al hacer la copia; es texto no confiable y, si se usa como ubicación original al restaurar, se valida como cualquier carpeta de destino (§8.6).
- `stats.files` y `stats.bytes` los calcula el escritor a partir de las entradas; al terminar de leer las entradas se comprueba que coinciden (discrepancia → corrupto). Los demás contadores no pueden ser negativos.
- `warnings` (opcional; ausente equivale a ninguno): cada aviso tiene `path` (ruta del snapshot a la que se refiere, o vacía si no se refiere a un archivo, como `VssUnavailable`), `code` y `message`. `code` es el nombre de un valor de `RunIssueCode` (§7.2); quien lo muestre trata un código que no conoce como `Other`. Los textos con UTF-16 no válido se guardan con U+FFFD en su lugar.
- `formatVersion` mayor que la que entiende Backbone → incompatible. Los campos desconocidos (de la cabecera, de las entradas o de objetos anidados) se ignoran: dentro de v1 se pueden añadir campos opcionales sin romper lectores anteriores. Cuentan para los límites (abajo) igual que los conocidos.

**Límites**

Protegen al lector frente a un manifiesto hostil o dañado (memoria, tiempo, registros). Las longitudes de texto se miden en caracteres UTF-16 (`string.Length`); los tamaños, en bytes del archivo. Superar cualquiera al leer → **corrupto**.

| Elemento | Límite |
|---|---|
| Manifiesto completo | 16 GiB |
| Cabecera: todo lo anterior a `entries` (desde la `{` inicial hasta `"entries":[`), incluidas las propiedades desconocidas | 16 MiB |
| Unidad de cabecera: el valor completo de una propiedad simple o desconocida, `stats`, un elemento de `sources` o de `warnings`, y cada propiedad posterior a `entries` | 256 KiB |
| Una entrada (el objeto completo) | 1 MiB |
| Profundidad de anidamiento JSON | 16 |
| Entradas (archivos más carpetas vacías) | 5 000 000 (10 × RNF-05) |
| `sources` | 32 (`ContractLimits.MaxSources`) |
| `warnings` | 10 000 |
| `id`, `parentId`, `status` | 64 caracteres (además, su formato) |
| `jobId` | 128 caracteres |
| `jobName` | 128 caracteres (`ContractLimits.MaxJobNameLength`) |
| `host` | segmento válido (255 caracteres) |
| `sources[].alias` | 64 caracteres y segmento válido |
| `sources[].path` | 32 767 caracteres |
| `warnings[].path` / `code` / `message` | 32 767 / 64 / 2 048 caracteres |
| `p` | 32 767 caracteres, segmentos de 255 (`SnapshotPaths`, §7.5) |

- La unidad de cabecera está dimensionada para el mayor elemento válido: un aviso con los tres textos al máximo (32 767 + 64 + 2 048 caracteres) ocupa como mucho unos 210 KB aunque cada carácter se escape como `\uXXXX` (6 bytes). Con 32 orígenes al máximo (≈ 6,3 MB) queda sitio en la cabecera para miles de avisos de tamaño normal.
- Lo que hay detrás de `entries` se descarta (ver "Orden"), pero sigue sujeto a la unidad de cabecera, a la profundidad y al tamaño total.

**El escritor nunca publica un manifiesto que el lector rechace**:

- Los textos de un aviso que superan su límite se **recortan** a él (sin partir un par suplente).
- Si hay como mucho 10 000 avisos y caben todos en la cabecera, se guardan todos. Si no, se guardan en orden los primeros que quepan, como mucho 9 999 y mientras la cabecera, contando el aviso que se añade, no pase de 16 MiB − 256 KiB (sitio reservado para el resumen); los demás se descartan y se añade al final un **aviso resumen** `{ "path": "", "code": "WarningsTruncated", "message": "<N> avisos más no se han guardado en el snapshot." }`, con `N` el número de avisos descartados. Así nunca hay más de 10 000 avisos ni la cabecera supera su presupuesto.
- Los demás campos de cabecera que superan su límite (`jobId`, `jobName`, `host`, `sources`) no pueden venir de un trabajo válido (§7.5): el escritor los rechaza al empezar la copia.
- Si la copia llega a más de 5 000 000 de entradas o el manifiesto superaría 16 GiB, la copia **falla** con un mensaje que indica el límite superado y no se publica snapshot (el anterior sigue siendo el último restaurable). Por ahora Core lo señala con `InvalidOperationException`; su traducción a `BackboneErrorCode`/`RunIssueCode` (y si hace falta un código nuevo) se decide en HU-13.

**Entradas**

- `p`: ruta relativa con `/` como separador, prefijada por el alias de un origen que figura en `sources`. Un archivo tiene al menos dos segmentos (`alias/nombre`).
  - **Única** dentro del snapshot con comparación **ordinal** (distingue mayúsculas): `A.txt` y `a.txt` (posibles en carpetas con distinción de mayúsculas) son dos entradas. Si al restaurar dos entradas solo difieren en mayúsculas y el destino no las distingue, la segunda se trata como **conflicto** (RF-32).
  - **Reglas de segmento**: cada segmento cumple `SnapshotPaths.IsValidSegment` (§7.5): no vacío; distinto de `.` y `..`; sin `\ / : * ? " < > |` ni caracteres de control; sin punto ni espacio final; sin nombres de dispositivo reservados, tampoco con extensión; como mucho 255 caracteres. Además, el texto debe ser **UTF-16 válido** (sin suplentes sueltas, que NTFS admite pero UTF-8 no puede representar).
  - Un archivo o carpeta de origen cuyo nombre no cumple estas reglas **no se copia**: la copia lo omite con aviso `PathInvalid` (HU-13). Un manifiesto con una ruta que no las cumple es **corrupto**, porque restaurarla podría salir de la carpeta de destino.
- `h`: hash del contenido (64 hexadecimales en minúsculas); `s`: tamaño en bytes (≥ 0); `m`: última modificación UTC (precisión de 100 ns); `a`: `FileAttributes` relevantes (ReadOnly = 1, Hidden = 2, System = 4, Archive = 32). Un bit fuera de esos cuatro → corrupto.
- Las carpetas vacías se representan con una entrada `{ "p": "…/", "d": true }`, sin `h`, `s`, `m` ni `a`. `"<alias>/"` representa un origen vacío.
- Los snapshots son **inmutables**: nunca se modifican después de escribirse.

**Serialización**

- UTF-8 sin BOM. Los caracteres no ASCII se escriben sin escapar (nombres legibles); comillas, `\` y caracteres de control se escapan siempre.
- Manifiesto **compacto**: la cabecera ocupa la primera línea y cada entrada va en su propia línea (`…"entries":[⏎{…},⏎{…}⏎]}`). Orden de campos en una entrada: `p`, `h`, `s`, `m`, `a` (o `p`, `d`).
- Fechas en ISO 8601 UTC con `Z`:
  - `m`: **siempre con 7 decimales** (`2026-01-31T10:22:01.0000000Z`).
  - Fechas de cabecera (`startedUtc`, `completedUtc`), `createdUtc` y `heartbeatUtc`: sin decimales si son un segundo exacto; si no, con 7 decimales.
  - Al leer se acepta cualquier fecha ISO 8601 con desfase y se convierte a UTC.

**Integridad**: el manifiesto no lleva suma de comprobación propia. El lector detecta los errores de estructura (JSON, rutas, hashes mal formados, estadísticas), pero no un `h` o un `s` alterados con un valor bien formado: eso solo lo detecta la verificación completa (HU-26), cuando el objeto no existe o no coincide en tamaño, y ni siquiera ella si `h` pasa a apuntar a otro objeto existente del mismo tamaño. Ver R6.

### 6.4 Reglas de escritura y concurrencia

1. Todo archivo (objetos, manifiestos, `repo.json` y bloqueos) se escribe primero en `tmp\<guid>.tmp`, se vacía al almacenamiento y se renombra al nombre final (operación atómica dentro del mismo recurso SMB). Objetos, manifiestos y `repo.json` se renombran **sin sobrescribir**: si el destino ya existe, se descarta el temporal. Los bloqueos sí se sobrescriben (latido). Si algo falla, el temporal se borra; si no se puede, lo recoge la limpieza (regla 6).
2. Un objeto se escribe calculando el SHA-256 **mientras se copia**, sobre los mismos bytes que se escriben. Si el hash final no coincide con el calculado al inspeccionar el archivo (el archivo cambió durante la copia sin VSS), se usa el hash real y se registra el cambio. Si el objeto ya existe, el temporal se descarta (deduplicación).
3. **Orden de publicación**:
   - Inicialización: las carpetas primero y `repo.json` el último (§6.2).
   - Copia: el snapshot se publica **solo después** de que todos sus objetos existen. Las entradas se van escribiendo en un temporal de `tmp\`; al terminar se compone el manifiesto definitivo en otro temporal (cabecera con las estadísticas ya conocidas y, detrás, las entradas), se **renueva el bloqueo** confirmando que sigue siendo propio y, justo después, se renombra a `snapshots\<id>.json`. Si el bloqueo se ha perdido, no se publica nada.
   - Lo que no tiene snapshot no existe a efectos de restauración.
4. **Bloqueos**:
   - Archivo `locks\<host>-<pid>.lock`, donde `host` es el nombre del equipo (debe cumplir las reglas de segmento) y `pid` el identificador del proceso. Un proceso tiene como mucho un bloqueo por repositorio. Contenido (JSON con sangría):
     ```json
     { "host": "PC-SALON", "pid": 4242, "type": "shared", "heartbeatUtc": "2026-11-15T02:00:00Z" }
     ```
   - `type`: `shared` (copia; compatible con otras copias) o `exclusive` (limpieza; incompatible con cualquier otro bloqueo vigente).
   - El archivo ocupa como mucho 64 KiB. Su contenido es no confiable (§6.1): `host` se sanea antes de registrarlo o de mostrarlo en el mensaje de "repositorio bloqueado".
   - **Latido de cada bloqueo**: su `heartbeatUtc` si el archivo es legible. Un archivo ilegible (contenido no válido, más de 64 KiB, error de lectura) se trata como `exclusive` con la **fecha de modificación** del archivo como latido. Un archivo de `locks\` con un nombre que Backbone no puede direccionar (§6.1) cuenta como ilegible igual que los demás; la única diferencia es que **no se borra nunca**, tampoco abandonado (R8).
   - **Vigencia** (regla sin estado; `ahora` es el reloj del equipo que comprueba, `T` = `StaleAfter` = **15 minutos**, que hace también de tolerancia de reloj, y `m` la fecha de modificación del archivo):
     1. `L` = el latido del punto anterior.
     2. Si `L > ahora + T`, el latido es **anómalo** (en el futuro más allá de la tolerancia): se usa `L = m` si `m ≤ ahora + T`; si `m` también es anómala, el bloqueo está **abandonado**.
     3. Con `L` no anómalo: **vigente** si `ahora − L < T`; si no, **abandonado**. Un latido en el futuro dentro de la tolerancia es vigente.

     Consecuencias: sin escrituras nuevas, ningún bloqueo bloquea más de `2 × T` (30 min) desde la primera vez que se ve, y uno con todas sus fechas en el futuro lejano no bloquea nunca; quien solo tiene escritura en el recurso no puede bloquear el repositorio indefinidamente sin seguir escribiendo. Sin guardar estado entre comprobaciones no se puede acotar a `T` sin rechazar también latidos de equipos con un desfase menor que `T`. Contrapartida: un titular legítimo con el reloj adelantado más de `T` respecto al que comprueba (y con una `m` también futura) se ve abandonado; es el riesgo de desfase de Q1 en el sentido contrario.
   - **Adquisición**: (a) se **publica el bloqueo propio** (sobrescribiendo: un archivo con el mismo nombre es de un proceso anterior que ya no existe); (b) después se **examinan los demás**; si hay alguno vigente incompatible, se borra el propio y se falla con "repositorio bloqueado"; (c) **solo la adquisición exclusiva borra los bloqueos abandonados**. Si dos procesos compiten, al menos uno ve al otro: en el peor caso fallan los dos, nunca entran los dos.
   - **Latido** cada **60 s**, reescribiendo `heartbeatUtc`. Si el archivo propio ha desaparecido (otro proceso lo dio por abandonado), no se recrea: el bloqueo se da por perdido. Los errores transitorios se reintentan en el siguiente latido.
   - El titular da su bloqueo por **perdido a los 13 minutos** sin un latido correcto (15 min − 2 latidos), antes de que otro pueda considerarlo abandonado, y deja de escribir en el repositorio.
   - Al terminar, el titular borra su archivo. Si no puede, caduca solo a los 15 minutos.
5. La limpieza borra primero los snapshots que expiran y después los objetos no referenciados **con más de 24 h de antigüedad** (protege objetos de una ejecución en curso en otro equipo).
6. `tmp\` se vacía al empezar una limpieza; los temporales con nombres no direccionables se ignoran y se quedan (§6.1).

### 6.5 Evolución (v2, F3)

Objetos troceados (*content-defined chunking*, FastCDC, tamaño medio 1 MiB) empaquetados en *packs* de ~16 MiB con índice, compresión zstd y cifrado opcional. Un repositorio v1 se puede seguir leyendo; la migración v1 → v2 es explícita.

---

## 7. Contrato IPC

### 7.1 Transporte

- Named pipe `Backbone.Service.v1` (`\\.\pipe\Backbone.Service.v1`), dúplex, asíncrono, mensajes con `HeaderDelimitedMessageHandler` y `SystemTextJsonFormatter`.
- Una conexión por instancia de la app; el servidor admite hasta 8 conexiones simultáneas, y como mucho 4 por SID para que un usuario no agote las instancias (B-4).
- Versión: `GetServiceInfoAsync` devuelve la versión del contrato; la app muestra un aviso si no coincide en la versión mayor.
- Constantes en `Backbone.Contracts`: `BackboneIpc.PipeName` (`Backbone.Service.v1`; el sufijo es la versión mayor del contrato), `BackboneIpc.MaxConnections` (8), `BackboneIpc.MaxRunProgressEventsPerSecond` (4), `BackboneIpc.MaxMessageBytes` (4 MiB) y `ContractVersion` (`Value = "1.1"`, `Major`, `Minor`, `IsCompatibleWith`). Los límites de las entradas están en `ContractLimits` (§7.5).
- `ContractVersion` es semántica: un cambio incompatible (quitar o renombrar un método, propiedad o valor de enumerado, cambiar un tipo o un significado) sube la **mayor** y el nombre del pipe; añadir métodos, propiedades opcionales o valores de enumerado sube la **menor**.
- Formato (ADR-10): servicio y app crean `new SystemTextJsonFormatter()` **sin cambiar** `JsonSerializerOptions`. Los nombres de propiedad viajan tal cual (PascalCase), los enumerados como número, las propiedades desconocidas se ignoran y las ausentes toman su valor por defecto. La profundidad máxima es la de System.Text.Json por defecto (64, `ContractLimits.MaxJsonDepth`).
- Tamaño de mensaje: ambos extremos cortan la conexión si un mensaje supera `BackboneIpc.MaxMessageBytes` (4 MiB), sin llegar a deserializarlo. Por eso el historial, la exploración de snapshots y la búsqueda se paginan.
- **Servidor auténtico (A-1)**:
  - El servidor crea la primera instancia del pipe con `PipeOptions.FirstPipeInstance` (`FILE_FLAG_FIRST_PIPE_INSTANCE`). Si falla porque ya existe un pipe con ese nombre, el servicio **no arranca**, lo registra en el Visor de eventos y deja que el SCM aplique la recuperación.
  - El cliente, nada más conectar y **antes de enviar ningún mensaje**, obtiene el proceso servidor con `GetNamedPipeServerProcessId` y comprueba que es el proceso del servicio `BackboneService` (PID según el SCM, `QueryServiceStatusEx`) y que su token es `S-1-5-18` (LocalSystem). Si no, cierra la conexión y muestra el estado "servicio no disponible". Como el servidor solo puede suplantar después de haber leído un mensaje del cliente, esta comprobación previa impide que un servidor falso suplante al usuario.

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
  - Límite de 4 conexiones por SID (§7.1).
  - Cada operación de escritura se audita en el log con el SID, el nombre de la cuenta y el nombre del método, **sin argumentos**; ni rutas ni datos de credenciales.
- **Propiedad de las restauraciones (A-2)**: cada restauración pertenece al SID que la inició. `ResolveRestoreConflictAsync` y `CancelRestoreAsync` desde otro SID → `NotAuthorized`. `RestoreProgress` y `RestoreConflict` solo se envían a las conexiones de ese SID. Para no perder eventos emitidos antes de que llegue la respuesta, el cliente puede proponer `RestoreRequest.RestoreId`; si está en uso → `ValidationFailed` con `IdAlreadyInUse`.

### 7.4 Errores

Errores de negocio como `RemoteInvocationException` con `ErrorCode` del enumerado `BackboneErrorCode`:

`ValidationFailed`, `JobNotFound`, `JobAlreadyRunning`, `NasUnreachable`, `CredentialsRejected`, `ShareNotFound`, `AccessDenied`, `RepositoryLocked`, `RepositoryIncompatible`, `RepositoryCorrupt`, `SnapshotNotFound`, `RestoreTargetNotWritable`, `Interrupted` (suspensión o apagado durante la ejecución), `NotAuthorized`, `NotSupported` (método de una fase aún no implementada), `Internal`, `CredentialRequired` (no hay credencial guardada para el recurso).

La app traduce cada código a un mensaje localizado con una acción sugerida.

Transporte del error:

- Valores numéricos fijos, en este orden, de `1000` (`ValidationFailed`) a `1016` (`CredentialRequired`); fuera del intervalo [-32768, -32000] reservado por JSON-RPC. No se reutilizan ni se reordenan.
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
  | Exclusiones | 256 patrones de hasta 512 caracteres |
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

  Los patrones *glob* de exclusión se evalúan con un algoritmo **lineal, sin retroceso** (no se traducen a expresiones regulares con *backtracking*).
- **Rutas de snapshot (M-3)**: separador `/`, prefijadas por el alias, sin `/` inicial ni final (`Documentos/Facturas/enero.pdf`). En `BrowseSnapshotAsync` la cadena vacía es la raíz, que contiene una carpeta por alias. `SnapshotPaths` (utilidad pura de Contracts) valida:
  - El identificador de snapshot (`IsValidSnapshotId`, formato de §6.3).
  - Cada segmento (`IsValidSegment`): no vacío; distinto de `.` y `..`; sin `\ / : * ? " < > |` ni caracteres de control; sin punto ni espacio final; sin nombres de dispositivo reservados, tampoco con extensión (`CON`, `NUL.txt`, `COM1`, `CONIN$`, `CONOUT$`…); como mucho 255 caracteres.
  - La ruta completa (`IsValidPath`).
  El alias de un origen cumple las mismas reglas de segmento. El motor omite, con aviso `PathInvalid`, los archivos de origen cuyo nombre no las cumple.
- **Destino UNC (A-3)**: `UncPaths.TryParse` (utilidad pura de Contracts) valida el destino y calcula la raíz del recurso `\\servidor\recurso` en minúsculas, con la que se guardan las credenciales; la app la usa en lugar de duplicar la regla.
  - Se rechazan con `DestinationNotAllowed`:
    - Las rutas `\\?\…`, `\\?\UNC\…`, `\\.\…` y `\??\…`.
    - El *loopback* (`localhost`, `*.localhost`, `127.0.0.0/8` en cualquier notación IPv4 que acepte `IPAddress.TryParse`, como `2130706433`, `0x7f.1` o `127.1`, y `0.0.0.0`) y los nombres `*.ipv6-literal.net`.
    - El propio equipo: el servicio pasa sus nombres NetBIOS y DNS y las direcciones de **todas** sus interfaces locales, incluidas las de enlace local `169.254.0.0/16`. Una IP literal se compara con esas direcciones, y un nombre se resuelve y se rechaza si apunta a una de ellas.
    - Los recursos administrativos (`C$`, `ADMIN$`, `IPC$`).
    - Los segmentos no válidos.
  - El servidor solo admite letras y dígitos ASCII, `-`, `_` y `.`. Un nombre internacional va en punycode (`xn--…`). Un punto inicial o final no se normaliza: se rechaza con `DestinationNotUnc`. Las IP IPv6 entre corchetes tampoco se admiten.
  - **El servicio nunca conecta al NAS sin una credencial explícita**: sin credencial guardada ni indicada, `TestConnectionAsync` devuelve `CredentialRequired` sin intentar conectar, y las ejecuciones y `SaveJobAsync` fallan con `CredentialRequired` (nunca se usa la cuenta del equipo).
- **Validación**: `ValidationIssue.Field` usa los nombres de propiedad del DTO validado (`JobDefinition`, `ServiceSettings`…) con índices desde 0 (`Name`, `Sources[1].Alias`, `Schedule.IntervalHours`); vacío si afecta a la entrada entera o a un parámetro simple. `ValidationIssue.Code` es un enumerado que la app traduce; `ValidationResult.Warnings` no impide guardar (p. ej. repositorio de formato superior).
- **Valores por defecto** de un trabajo nuevo en `JobDefaults` (exclusiones de RF-04, retención 3/7/4/12/0, batería 30 %, uso medido y NAS accesible activados), compartidos por app y servicio.
- **Trabajos y estado**:
  - `SaveJobAsync` y `DeleteJobAsync` emiten `StatusChanged`.
  - `DeleteJobAsync` de un trabajo en ejecución o en cola → `JobAlreadyRunning` (hay que cancelarlo antes).
  - `CancelRunAsync` sin ejecución en curso ni en cola no hace nada (idempotente); solo falla con `JobNotFound`.
  - `JobStatus.RepositoryState` indica `ReadOnlyNewerFormat` cuando el repositorio es de formato superior (HU-06.4).
- **Avisos**: `RunIssueCode.WarningsTruncated` (contrato 1.1) resume los avisos que no caben en el snapshot (§6.3) y puede aparecer también entre las incidencias de la ejecución; su `Message` indica cuántos se han descartado.
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

El servicio corre como SYSTEM y podría escribir en cualquier sitio. Para que un usuario autorizado no pueda usar la restauración para escribir donde él mismo no puede, ni redirigirla con enlaces (C-2), **cada carpeta y archivo de destino se crea o se abre con el token del usuario y SYSTEM solo escribe a través de ese handle**:

1. **Validación de la petición**:
   - La carpeta de destino (o la ubicación original de cada origen) es una ruta local completamente cualificada (`Path.IsPathFullyQualified`, letra de unidad de un volumen local), no UNC ni `\\?\` o `\\.\`.
   - Las rutas del snapshot cumplen `SnapshotPaths` (§7.5).
   - Cada nombre que se va a crear es un segmento válido.
2. **Handles bajo suplantación**: para cada carpeta y cada archivo de destino, el servicio suplanta al cliente (`NamedPipeServerStream.RunAsClient`) **solo durante la apertura**, de forma síncrona y mínima:
   - Abre o crea el objeto con `CreateFile` (relativo al handle de la carpeta padre cuando sea posible) con `FILE_FLAG_OPEN_REPARSE_POINT` (y `FILE_FLAG_BACKUP_SEMANTICS` para carpetas).
   - Si el objeto abierto es un punto de reanálisis (*junction*, enlace simbólico, montaje), **no se sigue**: el elemento falla con `RestoreTargetNotWritable`.
   - Revierte la suplantación. SYSTEM escribe el contenido, la fecha y los atributos **por ese handle**.
3. **Antes de copiar nada**, el servicio abre así la carpeta raíz de destino. Si el usuario no puede crear en ella → `RestoreTargetNotWritable` y no se escribe nada.
4. **Defensa adicional**: la ruta final de cada carpeta abierta (`GetFinalPathNameByHandle`, no la ruta pedida) se compara con `%WINDIR%`, `%ProgramFiles%`, `%ProgramFiles(x86)%` y `%ProgramData%\Backbone`. Si está dentro de alguna → `RestoreTargetNotWritable`. Esta lista no sustituye a los puntos 2 y 3.
5. **Archivos completos o nada**: cada archivo se escribe en un temporal creado igual que en el punto 2 en la misma carpeta, se verifica su hash (RF-33) y se renombra por handle (`SetFileInformationByHandle`, bajo suplantación) al nombre final según la política de conflicto. Un hash erróneo borra el temporal y marca el archivo como fallido.
6. Los objetos creados con el token del usuario tienen a ese usuario como propietario y heredan las ACL de la carpeta de destino.
7. La restauración pertenece al SID que la inicia (§7.3).

### 8.7 Exportación segura del diagnóstico (F2)

El servicio (SYSTEM) no devuelve el ZIP por el canal: lo escribe en la ruta que el usuario elige en la app (`DiagnosticsExportRequest.TargetPath`). Se descarta enviar el contenido por IPC porque exigiría multiplexar flujos o trocear en mensajes y mantener estado de transferencia, y el tamaño superaría `BackboneIpc.MaxMessageBytes`. Escribir suplantando reutiliza el mecanismo de §8.6. Reglas (M-8):

1. **Ruta**:
   - Completamente cualificada (`Path.IsPathFullyQualified`) con letra de unidad de un volumen local fijo.
   - No UNC, ni `\\?\`, `\\.\` o `\??\`.
   - Cada segmento cumple las reglas de `SnapshotPaths.IsValidSegment`, en particular sin `:`, que abriría un flujo alternativo (ADS), sin nombres reservados aunque lleven extensión (`NUL.zip`) y sin puntos ni espacios finales.
   - La extensión es `.zip` sin distinguir mayúsculas y el archivo no existe.
   - Si no se cumple → `ValidationFailed` con `TargetPathNotAllowed`.
2. **Creación**: suplantando al cliente solo durante la llamada, de forma síncrona y mínima, el servicio crea el archivo con `CreateFile(CREATE_NEW, FILE_FLAG_OPEN_REPARSE_POINT)` y revierte la suplantación.
   - Si el usuario no puede crearlo → `AccessDenied`.
   - La ruta final de la carpeta padre (`GetFinalPathNameByHandle`) no puede estar en las carpetas protegidas de §8.6.4; si lo está, se cierra y borra el archivo y se responde `ValidationFailed` con `TargetPathNotAllowed`.
3. **Escritura**: SYSTEM genera el ZIP en memoria o en `%ProgramData%\Backbone` y lo escribe **por el handle**; nunca vuelve a abrir la ruta por nombre.
4. **Contenido** (HU-23): configuración sin credenciales, logs de 14 días, últimas 100 ejecuciones y versiones. Nunca contraseñas ni el contenido de `credentials\` (RNF-06).

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
- Añade el SID del usuario que instala a `authorizedUsers` solo si es miembro de Administradores (§7.3).
- Acceso directo en el menú Inicio; entrada `HKCU\…\Run` opcional (casilla, activada por defecto).
- Actualizaciones mayores (`MajorUpgrade`) que conservan la configuración, el historial y las credenciales.
- Desinstalación: casilla "Eliminar también configuración e historial" (desactivada por defecto). **Nunca** toca repositorios del NAS.
- Firma de código (Authenticode) del MSI y los ejecutables en CI cuando haya certificado disponible.

---

## 11. Estrategia de pruebas

| Nivel | Proyecto | Qué cubre |
|---|---|---|
| Unitarias | `Backbone.Core.Tests` | Formato del repositorio, backup incremental, deduplicación, restauración, exclusiones, retención (tabla de casos con fechas fijas), programación, escrituras atómicas. Almacenamiento abstraído (`IRepositoryStorage`) para inyectar fallos en cualquier operación. |
| Contrato | `Backbone.Contracts.Tests` | Ida y vuelta con `System.Text.Json` de cada DTO (descubiertos por reflexión) con las opciones de `SystemTextJsonFormatter`, convenciones de §7.5, códigos de error y transporte de errores y eventos con StreamJsonRpc en memoria. |
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
| R6 | El manifiesto no lleva suma de comprobación (§6.3): un `h` o un `s` alterados con un valor bien formado pasan la lectura y solo los detecta la verificación completa (HU-26); un `h` cambiado por el de otro objeto existente del mismo tamaño no lo detecta nadie. | v1: se acepta (los manifiestos son inmutables y se publican de forma atómica). v2: añadir una suma del manifiesto (p. ej. SHA-256 de las entradas en la cabecera o en un archivo aparte), comprobada al leer. |
| R7 | Puntos de reanálisis en el repositorio (§6.1): la comprobación y la operación no son atómicas (TOCTOU), así que quien puede escribir en el recurso del NAS podría cambiar una carpeta por un *junction* entre ambas. | v1: se acepta; ese atacante ya puede destruir el repositorio. Valorar abrir con `FILE_FLAG_OPEN_REPARSE_POINT` y comprobar el identificador del archivo abierto. |
| R8 | Nombres no direccionables en `tmp\` o `locks\` (§6.1): Samba sin *mangling* puede servir nombres que Windows no puede abrir por ruta (`nul.lock`, `a:b.tmp`). Un bloqueo así impide copias y limpiezas mientras su fecha de modificación tenga menos de 15 minutos (§6.4.4) y después deja de bloquear, pero se queda; un temporal así ocupa espacio sin que la limpieza lo quite. | v1: limitación conocida; Backbone no los borra nunca y hay que quitarlos a mano en el NAS. Valorar borrarlos por identificador de archivo o con rutas `\\?\` en una versión posterior. |
| Q1 | ¿Varias máquinas en el mismo repositorio? | v1: no se soporta oficialmente (un repositorio por trabajo y equipo), aunque el formato ya lo tolera. Riesgo si se usa: el abandono de un bloqueo (§6.4.4) se decide con el reloj del equipo que comprueba frente al `heartbeatUtc` que el titular escribió con el suyo; si el reloj del que comprueba va adelantado unos 14 minutos o más, una limpieza podría borrar un bloqueo vivo (el titular no se daría cuenta hasta su siguiente latido). En el caso contrario (titular adelantado más de 15 minutos) su latido es anómalo: se sustituye por la fecha de modificación del archivo o, si también es futura, el bloqueo cuenta como abandonado (§6.4.4). Mitigación: equipos sincronizados por NTP; en v2, valorar comparar siempre con la fecha de modificación que asigna el NAS. |

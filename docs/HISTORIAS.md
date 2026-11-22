# Backbone — Historias de usuario

Referencia: [ESPECIFICACION.md](ESPECIFICACION.md). Cada historia enlaza los requisitos (RF/RNF) que cubre.

**Formato de cada historia**

- **Fase**: MVP, F2 o F3 (§12 de la especificación).
- **Puntos**: estimación relativa (1, 2, 3, 5, 8, 13).
- **Depende de**: historias que deben estar terminadas antes.
- **Agente**: subagente de Claude Code responsable (ver [CLAUDE.md](../CLAUDE.md)). Entre paréntesis, los que colaboran.
- **Criterios de aceptación** en formato Dado / Cuando / Entonces. Cada criterio necesita una prueba automática o un caso manual en `docs/PRUEBAS-MANUALES.md`.

**Personas**

- **Ana**, usuaria doméstica: quiere que sus documentos y fotos estén a salvo sin pensar en ello.
- **Luis**, usuario técnico: administra el NAS, quiere control, diagnósticos y que nada falle en silencio.

**Definición de terminado** (aplica a todas)

1. Todos los criterios de aceptación cubiertos por pruebas que pasan (o caso manual documentado y ejecutado).
2. `dotnet build -warnaserror` y `dotnet test` sin fallos.
3. Cadenas de interfaz en `.resw` es-ES y en-US.
4. Revisado por `backbone-security-review` si toca IPC, credenciales, ACL, restauración o rutas de archivo.
5. Historia marcada como hecha en la tabla de seguimiento (§ final).

---

## Épica T — Fundamentos técnicos

### HT-01 · Estructura de la solución y CI
Como equipo de desarrollo, quiero la solución creada con todos los proyectos y una integración continua, para que cada historia se construya y pruebe de la misma forma.

- **Fase** MVP · **Puntos** 3 · **Depende de** — · **Agente** backbone-architect (backbone-installer)
- **Criterios**
  1. Dado un clon limpio, cuando se ejecuta `dotnet build Backbone.slnx -c Release`, entonces compilan todos los proyectos de §3.1 sin advertencias.
  2. `Directory.Build.props` fija `net10.0`, `Nullable`, `ImplicitUsings`, `AnalysisLevel=latest-recommended`, `TreatWarningsAsErrors` en CI.
  3. `Directory.Packages.props` centraliza las versiones de paquetes NuGet.
  4. Un flujo de GitHub Actions en `windows-latest` compila, ejecuta las pruebas no marcadas como `Integration` y publica la cobertura de Backbone.Core.
  5. Las reglas de dependencia de §3.1 se comprueban con una prueba de arquitectura (la app no referencia Core ni Service).

### HT-02 · Contratos IPC y DTO
Como equipo, quiero la interfaz `IBackboneService` y todos sus DTO definidos, para que servicio y app se desarrollen en paralelo contra el mismo contrato.

- **Fase** MVP · **Puntos** 3 · **Depende de** HT-01 · **Agente** backbone-architect
- **Criterios**
  1. `Backbone.Contracts` contiene la interfaz de §7.2, los DTO como `record` y `BackboneErrorCode` (§7.4).
  2. Una prueba serializa y deserializa cada DTO con `System.Text.Json` sin pérdida.
  3. `ContractVersion` es una constante semántica (`1.0`).
  4. Los métodos marcados F2/F3 existen en la interfaz; el servicio puede devolver `NotSupported` hasta que se implementen.

### HT-03 · Canal IPC seguro (servidor y cliente)
Como equipo, quiero un canal named pipe con StreamJsonRpc y control de acceso, para que la app hable con el servicio de forma segura.

- **Fase** MVP · **Puntos** 5 · **Depende de** HT-02 · **Agente** backbone-service (backbone-app, backbone-security-review)
- **Criterios**
  1. Dado el servicio en ejecución, cuando un usuario autorizado conecta, entonces `GetServiceInfoAsync` devuelve versión y contrato.
  2. Dado un usuario no autorizado y sin elevar, cuando intenta conectar, entonces la conexión se rechaza con acceso denegado.
  3. Los eventos `StatusChanged` y `RunProgress` llegan a todos los clientes conectados; `RunProgress` no supera 4 mensajes por segundo.
  4. Si la app se desconecta de forma abrupta, el servidor libera los recursos de esa conexión sin afectar a otras.
  5. Cliente con reconexión automática según §9.1.
  6. Prueba de integración con servidor y cliente reales sobre un pipe de nombre aleatorio.
- **Requisitos de seguridad (revisión HT-01)**
  - I-1: al arrancar, el servicio comprueba `%ProgramData%\Backbone` (no es punto de reanálisis, propietario Administradores/SYSTEM, DACL protegida según RNF-07) antes de leer `authorizedUsers` o la configuración, con la regla de §8.3 (ADR-13): todo sobre handles; una raíz de propietario no fiable o que no se puede abrir nunca se corrige en el sitio, se aparta a `Backbone.untrusted-<guid>` y se crea otra con el descriptor en la creación (evento 1003); si no se puede apartar o el contenido no es fiable, el servicio no arranca (evento 1002).
  - I-5: al añadir `AddWindowsService`, `ContentRootPath = AppContext.BaseDirectory` y `Environment = Production` fijos; no se carga configuración (appsettings, variables de entorno, rutas) desde ubicaciones escribibles por usuarios.

### HT-04 · Motor: formato del repositorio
Como equipo, quiero leer y escribir repositorios en formato v1 con escrituras atómicas y bloqueos, para tener una base fiable para copiar y restaurar.

- **Fase** MVP · **Puntos** 8 · **Depende de** HT-01 · **Agente** backbone-core (backbone-qa)
- **Criterios**
  1. `BackupRepository.InitializeAsync` crea la estructura de §6.1 y `repo.json` (§6.2); `OpenAsync` valida el formato y rechaza versiones superiores con `RepositoryIncompatible` (RNF-14).
  2. Toda escritura pasa por `tmp\` + renombrado (§6.4.1). Prueba de inyección de fallos: un fallo en cualquier punto no deja archivos incompletos en `objects\` ni en `snapshots\`.
  3. Snapshots leídos y escritos en *streaming* (`Utf8JsonReader`/`Utf8JsonWriter`); un manifiesto de 500 000 entradas se escribe y lee con < 400 MB de memoria (prueba de rendimiento opcional `[Trait("Category","Perf")]`).
  4. Bloqueos `shared`/`exclusive` con latido y caducidad a los 15 minutos (§6.4.4).
  5. Todo el acceso al disco pasa por `IRepositoryStorage` con una implementación de sistema de archivos y otra en memoria para pruebas.

---

## Épica E1 — Instalación y primer uso

### HU-01 · Instalar Backbone
Como Ana, quiero instalar Backbone con un instalador normal de Windows, para empezar a usarlo sin configurar nada a mano.

- **Fase** MVP · **Puntos** 8 · **Depende de** HT-03 · **Agente** backbone-installer (backbone-security-review) · **Requisitos** §10, RNF-02, RNF-07
- **Criterios**
  1. Dado un Windows 11 limpio, cuando ejecuto el MSI y acepto la elevación, entonces el servicio `BackboneService` queda instalado, en ejecución, con inicio automático retrasado y las acciones de recuperación de RNF-02.
  2. El origen `Backbone` existe en el registro Aplicación del Visor de eventos.
  3. `%ProgramData%\Backbone` existe con las ACL de RNF-07.
  4. Si mi usuario es administrador, figura en `authorizedUsers` y la app, abierta sin elevar, conecta con el servicio (§7.3).
  5. La app arranca minimizada en la bandeja al iniciar sesión (si dejé marcada la casilla).
  6. Hay MSI para x64 y ARM64.
- **Requisitos de seguridad (revisión HT-01)**
  - I-1: `%ProgramData%\Backbone` puede existir antes de instalar, creado por un usuario o como *junction*. El MSI aplica la misma regla que el servicio (§8.3, ADR-13), sobre handles y sin seguir enlaces:
    - Si es un punto de reanálisis (o no es una carpeta), lo rechaza y no toca el destino.
    - Si no existe, la crea con `CreateDirectoryW` y el descriptor de RNF-07 en la propia creación (nunca crear y después cambiar la ACL).
    - Si existe con propietario que no es SYSTEM ni Administradores, o no se puede abrir, **nunca toma la propiedad ni cambia la DACL en el sitio**: la aparta renombrándola a `Backbone.untrusted-<guid>` (sin borrar nada) y crea otra como en el caso anterior. Si no se puede apartar, la instalación falla con un mensaje claro y deshace los cambios.
    - Si existe con propietario fiable y DACL distinta, aplica la DACL protegida (sin herencia) de RNF-07 por handle (riesgo aceptado R9, §13).
  - I-2: ninguna acción personalizada se ejecuta en reparación si puede alterar ACL o datos; las elevadas son `deferred` sin suplantación. `INSTALLFOLDER` cuelga de `ProgramFiles64Folder`.
  - I-3: decidir de forma explícita si x64 y ARM64 comparten `UpgradeCode`; la versión es única y se pasa al `.wixproj` como `$(Version)` (la de `Directory.Build.props`).
  - I-4: la entrada `HKCU\…\Run` la crea la app en la sesión del usuario (casilla del primer arranque o Ajustes), no el MSI per-machine.
  - B-6: los artefactos de CI no son distribuibles; la publicación de MSI firmados va por un flujo aparte.
  - Endurecimiento (revisión del `SourceFileSystem` de HU-08, ADR-21; lista fijada en el cierre de HU-08 servicio, decisión del coordinador): el servicio se instala con `SERVICE_CONFIG_REQUIRED_PRIVILEGES_INFO` con exactamente `SeBackupPrivilege`, `SeImpersonatePrivilege` y `SeChangeNotifyPrivilege`, ninguno más (§10). La prueba de aceptación comprueba:
    - con `sc qprivs BackboneService`, que la lista es exactamente esa;
    - que el servicio arranca con esos privilegios ya limitados;
    - que, tras borrar `%ProgramData%\Backbone` con el servicio parado, el servicio la vuelve a crear al arrancar con Administradores como propietario y la DACL de RNF-07, y que corrige una raíz de propietario fiable con otra DACL, todo sin `SeRestorePrivilege` (§8.3).
  - Nota (revisión de HT-03, I-6): las bibliotecas nativas de terceros que carga el servicio (p. ej. `e_sqlite3.dll` de `Microsoft.Data.Sqlite`) se cargan desde la carpeta de instalación; su integridad depende de que `INSTALLFOLDER` (bajo `ProgramFiles64Folder`) **no sea escribible por usuarios**. El MSI no debe añadir ACL que lo permitan y la prueba de aceptación lo comprueba.

### HU-02 · Actualizar y desinstalar
Como Luis, quiero actualizar a una versión nueva sin perder la configuración y poder desinstalar limpiamente, para mantener el sistema ordenado.

- **Fase** MVP · **Puntos** 3 · **Depende de** HU-01 · **Agente** backbone-installer
- **Criterios**
  1. Dado Backbone instalado con trabajos, cuando instalo una versión superior, entonces se conservan trabajos, historial y credenciales, y el servicio vuelve a arrancar.
  2. Si hay una copia en curso al actualizar, se cancela de forma controlada (sin corromper el repositorio) antes de detener el servicio.
  3. Al desinstalar con la casilla desmarcada, `%ProgramData%\Backbone` se conserva; marcada, se elimina.
  4. Ninguna desinstalación modifica el repositorio del NAS.
- **Requisitos de seguridad (revisión HT-01)**
  - I-3: `MajorUpgrade` según el `UpgradeCode` por arquitectura decidido en HU-01; la versión del MSI sale de `$(Version)` para que cada actualización la suba.

### HU-03 · Asistente de primer uso
Como Ana, quiero que la primera vez que abro Backbone me guíe paso a paso, para tener la primera copia hecha en pocos minutos.

- **Fase** F2 · **Puntos** 5 · **Depende de** HU-05, HU-07, HU-09 · **Agente** backbone-app · **Requisitos** RF-80
- **Criterios**
  1. Dado que no hay trabajos, cuando abro la app, entonces aparece el asistente con los pasos: NAS → carpetas → programación → resumen.
  2. En el paso NAS no puedo continuar hasta que "Probar conexión" sea correcta.
  3. En el paso carpetas se proponen Documentos, Escritorio e Imágenes con su tamaño estimado.
  4. Al terminar se guarda el trabajo y se lanza la primera copia, mostrando su progreso.
  5. Puedo cerrar el asistente en cualquier paso y retomarlo desde el estado vacío de Inicio.

---

## Épica E2 — Conexión al NAS

### HU-04 · Guardar las credenciales del NAS
Como Ana, quiero escribir una vez el usuario y la contraseña del NAS, para que las copias funcionen aunque no haya iniciado sesión.

- **Fase** MVP · **Puntos** 5 · **Depende de** HT-03 · **Agente** backbone-service (backbone-app, backbone-security-review) · **Requisitos** RF-60, RF-62, ADR-07, RNF-06
- **Criterios**
  1. Dado un recurso `\\nas\Backups`, cuando guardo usuario y contraseña, entonces se crea un archivo en `credentials\` cifrado con DPAPI de ámbito equipo (`CryptProtectData` con `CRYPTPROTECT_LOCAL_MACHINE`, ADR-07) y entropía propia.
  2. `GetCredentialInfoAsync` devuelve el usuario y la fecha de actualización, nunca la contraseña.
  3. Una búsqueda de la contraseña en `%ProgramData%\Backbone` (logs, config, historial) no encuentra coincidencias.
  4. El campo de contraseña de la app usa `PasswordBox` y se vacía tras guardar.
  5. Con la sesión cerrada, una ejecución programada se conecta con esas credenciales.
- **Decidido (revisión de seguridad de HU-04)**:
  - INFORMATIVA-5: el archivo es `credentials\<hash-del-recurso>.bin`, con el SHA-256 en hexadecimal en minúsculas de la raíz del recurso normalizada (`\\servidor\recurso` en minúsculas, `UncPaths.TryParse`), una credencial por recurso (§8.3, RF-60).
  - MEDIA-1: como la única protección de `credentials\*.bin` es la ACL local, la carpeta de datos nunca se copia al NAS (ADR-16, §8.3); se implementa en HU-07, HU-08 y HU-13.
- **Decidido (nota de HT-04)**: el helper de saneado de textos no confiables compartido por servicio y app es `UntrustedText.Describe` de `Backbone.Contracts` (§6.1, §7.5), con el mismo comportamiento que `Untrusted.Describe` de Core. Todo nombre de recurso o de equipo, usuario del NAS o mensaje con valores leídos del NAS que el servicio registre o reenvíe, o que la app muestre, pasa por él.

### HU-05 · Probar la conexión al NAS
Como Luis, quiero comprobar la conexión antes de guardar un trabajo, para saber exactamente qué falla si algo no va.

- **Fase** MVP · **Puntos** 3 · **Depende de** HU-04 · **Agente** backbone-service (backbone-app) · **Requisitos** RF-61
- **Criterios**
  1. Cuando pulso "Probar conexión" con datos correctos, entonces veo "Conexión correcta", el espacio libre y total, y la versión SMB.
  2. Con contraseña errónea veo "El NAS ha rechazado el usuario o la contraseña".
  3. Con servidor apagado veo "No se encuentra el NAS en la red" en menos de 15 s.
  4. Con recurso inexistente o sin permiso de escritura veo un mensaje específico para cada caso.
  5. La prueba no deja archivos en el recurso.
- **Nota de HT-04**: los textos no confiables (mensajes del sistema, nombre del recurso, dialecto SMB) se sanean con `UntrustedText.Describe` de `Backbone.Contracts` antes de registrarlos o mostrarlos (decidido en HU-04, §6.1).

### HU-06 · Inicializar o adoptar un repositorio
Como Luis, quiero que si reinstalo Windows y apunto al mismo destino, Backbone reconozca las copias anteriores, para no perder el historial de versiones.

- **Fase** MVP · **Puntos** 3 · **Depende de** HT-04, HU-04 · **Agente** backbone-service (backbone-core) · **Requisitos** RF-63
- **Criterios**
  1. Dado un destino vacío, cuando guardo el trabajo, entonces se inicializa el repositorio.
  2. Dado un destino con un repositorio v1 válido, entonces se adopta y sus snapshots aparecen en Restaurar.
  3. Dado un destino con archivos que no son un repositorio, entonces la validación falla con un mensaje que lo explica y no se escribe nada.
  4. Dado un repositorio de formato superior, se muestra solo lectura y aviso (RNF-14).

---

## Épica E3 — Trabajos de copia

### HU-07 · Crear un trabajo
Como Ana, quiero elegir las carpetas que se copian y el destino en el NAS, para proteger lo que me importa.

- **Fase** MVP · **Puntos** 5 · **Depende de** HT-03, HU-04 · **Agente** backbone-app (backbone-service) · **Requisitos** RF-01, RF-02, RF-03
- **Criterios**
  1. Cuando creo un trabajo con nombre, una o más carpetas (con `FolderPicker`) y destino UNC, y lo guardo, entonces aparece en Trabajos y en Inicio.
  2. El alias de cada carpeta se propone a partir de su nombre y es editable; dos alias iguales muestran error en línea.
  3. Se rechazan: carpetas inexistentes, no NTFS, unidades de red; destinos no UNC, dentro de un origen o usados por otro trabajo. Cada error se muestra junto al campo.
  4. La validación vive en el servicio (`ValidateJobAsync`); la app no duplica reglas.
  5. `jobs.json` se escribe de forma atómica: matar el servicio a mitad no lo corrompe.
  6. Dado un origen que es `%ProgramData%\Backbone`, una carpeta dentro de ella o un *junction* que apunta a ella o a su interior, cuando valido o guardo el trabajo, entonces `ValidateJobAsync`/`SaveJobAsync` lo rechazan con `SourceNotAllowed` en `Sources[i].Path` (comparando rutas finales, §8.3) y la app muestra el error junto al origen. Dado un origen que la contiene (`C:\`, `C:\ProgramData`), se guarda y la validación devuelve el aviso `SourceContainsDataFolder`. Lo mismo con las carpetas apartadas `%ProgramData%\Backbone.untrusted-<guid>` (ADR-13).
- **Decidido (revisión de seguridad de HU-04, MEDIA-1)**: la carpeta de datos nunca se copia al NAS (ADR-16, §8.3). backbone-service implementa la comprobación en la validación y su repetición al empezar cada ejecución; backbone-app muestra el error y el aviso. Los códigos `SourceNotAllowed` (26) y `SourceContainsDataFolder` (27) ya están en `Backbone.Contracts` (contrato 1.2). Desde HU-13 la regla cubre también las carpetas apartadas (decisión del coordinador, §8.3 "Carpetas protegidas").
- **Decidido (coordinador, HU-07)**:
  - **`jobs.json` rechazado** por las reglas de lectura de la carpeta de datos (propietario no fiable, punto de reanálisis, carpeta o enlace duro): el servicio no arranca y registra el evento 1002, igual que con `settings.json`. Si se rechaza al escribir después del arranque, no se escribe nada y la operación IPC falla (§8.2, ADR-14).
  - **`jobs.json` dañado** (JSON no válido o más de 64 MiB): el servicio arranca sin trabajos, registra el evento **1004** (Advertencia, §8.5) y lo aparta en la siguiente escritura con las mismas reglas que `settings.json` (§8.2 "Copias apartadas `.bad`"). backbone-app no necesita nada especial: mostrará el asistente de primer uso, y el evento deja constancia.
- **Decidido (revisión de seguridad de la parte de servicio de HU-07, coordinador)**:
  - **ALTA-1, resolución controlada de los orígenes** (ADR-20, §8.3). `ILocalFileSystemInspector` deja de abrir siguiendo los puntos de reanálisis. Recorre la ruta componente a componente con `NtCreateFile` relativo al handle del padre y `FILE_OPEN_REPARSE_POINT`. El destino de cada sustituto de nombre se lee con `FSCTL_GET_REPARSE_POINT` y solo se continúa hacia `\??\X:\…` o `\??\Volume{guid}\…`. Cualquier otro destino da `SourceNotLocal` sin abrirlo. Tope de 63 saltos y `GetFinalPathNameByHandle` como defensa en profundidad. La usan la validación, el `JobRunner` al empezar cada ejecución y el cálculo de `%ProgramData%`. Redirection Guard se activa al arrancar como defensa complementaria. Pruebas obligatorias:
    - enlace simbólico de carpeta hacia `\\host\r`, en el último componente y en uno intermedio;
    - *junction* hacia `\RPC Control\x`;
    - enlace relativo que sube por encima de la raíz;
    - ciclo de enlaces;
    - *junction* local legítimo hacia otra carpeta NTFS;
    - en todos los casos de red, comprobar que **no hay ningún intento de conexión** (p. ej. con un doble que registre las aperturas, o con un servidor SMB de prueba en la integración).
  - **Permiso de `ValidateJobAsync`**: pasa a ser de **escritura** (auditada), como `TestConnectionAsync` (§7.3, tabla de permisos).
  - **MEDIA-1**: la validación de orígenes no se hace de forma síncrona en el hilo RPC. Hay como mucho 4 validaciones simultáneas en todo el servicio (el `JobRunner` no cuenta), y cada apertura tiene un plazo de 60 s. La búsqueda de `Backbone.untrusted-*` para al encontrar 101 carpetas reales (§8.3).
  - **BAJA-1, ruta de origen normalizada** (§7.5 "Ruta de origen"): se rechazan con `SourcePathInvalid` (29) los `:` fuera de la posición 1, los segmentos vacíos, `.` y `..`, y los que no cumplen `SnapshotPaths.IsValidSegment` (punto o espacio final…). Lo que se guarda en `jobs.json` y devuelve `SaveJobAsync` es la ruta normalizada. El acceso denegado, el plazo vencido y los componentes que no se atraviesan (solo en la nube, etiqueta no legible) dan `SourceUnreadable` (28) y no `SourceNotFound`. Los dos códigos son del contrato 1.3; backbone-app les da texto en es-ES y en-US.
  - **BAJA-2**: el `JobRunner` comprueba al empezar que hay credencial guardada y, si no, termina `Failed` con `CredentialRequired` sin conectar (§8.4, paso 2).
  - **BAJA-3**: las copias `.bad` no se sobrescriben. Se apartan como `<archivo>.bad-<marca UTC>`, con 5 como máximo por archivo y borrando por handle la más antigua. Vale igual para `settings.json` y `jobs.json` (§8.2).
  - **Destino anidado** (criterio 3): un destino que esté dentro del de otro trabajo o lo contenga (`\\nas\b` frente a `\\nas\b\pc`, en cualquier sentido) cuenta como `DestinationUsedByOtherJob`. Se comparan los destinos normalizados por `UncPaths.TryParse` por segmentos completos, sin distinguir mayúsculas y sin resolver nombres de servidor (RF-03).

### HU-08 · Excluir archivos y carpetas
Como Luis, quiero excluir archivos por patrones, para no copiar temporales ni carpetas enormes que no necesito.

- **Fase** MVP · **Puntos** 3 · **Depende de** HU-13 (y HU-07 para el editor de exclusiones) · **Agente** backbone-core (backbone-app) · **Requisitos** RF-04, RF-05 (F2), RF-06, RF-07 (F2)
- **Criterios**
  1. Las exclusiones por defecto de RF-04 vienen precargadas y se pueden quitar.
  2. Los patrones `*.tmp`, `**/node_modules/**` y `Documentos/Privado/**` excluyen exactamente lo esperado (tabla de casos en pruebas, sin distinguir mayúsculas).
  3. Las *junctions* y los enlaces simbólicos no se siguen y se registran como omitidos.
  4. Los archivos de OneDrive solo en la nube se omiten sin descargarse (R4).
  5. La vista previa muestra cuántos archivos y bytes excluye cada patrón (F2).
  6. Dado un trabajo con un origen que contiene `%ProgramData%\Backbone` (aviso `SourceContainsDataFolder`), cuando abro la sección Exclusiones, entonces veo una nota fija, que no se puede quitar, de que la carpeta de datos de Backbone se excluye siempre; la nota no forma parte de la lista de patrones (RF-04, §8.3).
- **Decidido (revisión de seguridad de HU-04, MEDIA-1)**: la exclusión obligatoria la aplica el motor desde HU-13; esta historia solo añade su indicación en el editor (backbone-app).
- **Nota de HU-13 (revisión de seguridad, MEDIA-2)**: el criterio 3 se cumple con el contrato de `ISourceFileSystem` de §8.2 ("Lectura de los orígenes"): abrir por handle con `FILE_FLAG_OPEN_REPARSE_POINT` (más `FILE_FLAG_BACKUP_SEMANTICS` en carpetas), comprobar sobre el handle que no es un punto de reanálisis y, si lo es, lanzar `SourceAccessException(ReparsePointSkipped)`; enumerar desde ese handle. Hay que probar la sustitución de una carpeta o un archivo por un *junction* entre la enumeración y la apertura.
- **Decidido (revisión de seguridad final de HU-13)**:
  - M-A: `FILE_FLAG_OPEN_REPARSE_POINT` solo protege el último componente. `SourceFileSystem` (backbone-service), tras abrir cualquier elemento y sobre el mismo handle, comprueba con `GetFinalPathNameByHandle` (`FILE_NAME_NORMALIZED | VOLUME_NAME_NT`) que la ruta final es exactamente la ruta final de la raíz del origen más la ruta relativa, y que la ruta relativa no cae en una exclusión obligatoria; si no, `SourceAccessException(ReparsePointSkipped)` (§8.2, ADR-18). Alternativa válida: abrir relativo al handle del padre (`NtCreateFile` con `RootDirectory`, un componente, `OBJ_DONT_REPARSE`). `ISourceFileSystem` no cambia de firma: la implementación se crea por ejecución con los `BackupSource` y deduce la ruta relativa quitando `ReadPath\`; rechaza con `PathInvalid` rutas fuera de los orígenes o con segmentos vacíos, `.` o `..`. Prueba obligatoria: sustituir una carpeta **intermedia** ya enumerada por un *junction* hacia una carpeta con un archivo "secreto" y comprobar que no se lee.
  - M-B: las carpetas apartadas son solo entradas `Backbone.untrusted-*` de la ruta final de `%ProgramData%` que sean **carpetas reales** (abiertas con `FILE_FLAG_OPEN_REPARSE_POINT`; un *junction* o un archivo con ese nombre se ignora), y su ruta es su ubicación, nunca su destino. Como mucho 100; si hay más, los orígenes que contienen `%ProgramData%` fallan cerrados (`SourceNotAllowed` en la validación, `ValidationFailed` en la ejecución) con aviso en el log (§8.3, ADR-16, R11). Lo implementa backbone-service, donde viva el cálculo de las carpetas protegidas (HU-07/HU-13).
  - B-1: la raíz de un origen que no se puede recorrer da el aviso `SourceRootUnreadable` (13, contrato 1.2) en vez del de la causa (§8.4, R11). Lo emite el motor (backbone-core); backbone-app le da un texto propio en es-ES y en-US.
- **Decidido (usuario y coordinador, HU-08; ADR-19)**:
  - **Clasificación por etiqueta** (§8.2 "Clasificación por etiqueta"): solo es enlace (`ReparsePointSkipped`, sin conservar nada) una etiqueta sustituto de nombre (`IsReparseTagNameSurrogate`). Se lee por su contenido lo que no es punto de reanálisis y la lista blanca: `IO_REPARSE_TAG_CLOUD*` totalmente descargado, `IO_REPARSE_TAG_WOF` e `IO_REPARSE_TAG_DEDUP`. Cualquier otra etiqueta es no legible (`ReadError` con la etiqueta en hexadecimal) y conserva la versión del padre (RF-25). backbone-core clasifica en el motor con `SourceEntry.Attributes` y `SourceEntry.ReparseTag` antes de abrir. backbone-service repite la clasificación sobre el handle (`FileAttributeTagInfo`) en `SourceFileSystem`.
  - **`SourceEntry.ReparseTag`** (`uint`, `init`, por defecto 0): cambio de tipo en Core. La firma de `ISourceFileSystem` no cambia. En la enumeración sale de `EaSize` de `FILE_ID_BOTH_DIR_INFO` cuando hay `FILE_ATTRIBUTE_REPARSE_POINT`. Un punto de reanálisis con etiqueta 0 es no legible.
  - **OneDrive solo en la nube** (§8.2 "Archivos y carpetas solo en la nube", R4): el servicio llama a `RtlSetProcessPlaceholderCompatibilityMode(PHCM_EXPOSE_PLACEHOLDERS)` al arrancar. Con `RECALL_ON_OPEN`, `RECALL_ON_DATA_ACCESS` u `OFFLINE` (este, solo con etiqueta de nube o HSM), o con estado `PARTIAL` (`CfGetPlaceholderStateFromAttributeTag`), el elemento se omite sin abrirlo con `CloudOnlySkipped` y conserva la versión del padre, en vivo y en VSS. Antes de leer un marcador de nube se comprueba `FSCTL_QUERY_ALLOCATED_RANGES`.
  - **Carpetas de nube no pobladas** (a raíz de QA): no se recorren (`CloudOnlySkipped`) y conservan del padre lo que tenían debajo. Así no se piden metadatos al proveedor en vivo ni se pierden hijos en VSS.
  - **Apertura para leer el contenido** (§8.2): la primera apertura se hace con `FILE_FLAG_OPEN_REPARSE_POINT` y la comprobación de ruta final (ADR-18). Lo de la lista blanca se lee de un segundo handle sin esa marca (`ReOpenFile`), comprobando que tiene el mismo identificador, la misma clase de etiqueta y la ruta final esperada.
  - **Plazo** de 60 s por apertura, enumeración o lectura de bloque, más 30 s para la cancelación. Al vencer el plazo, `ReadError` y se conserva la versión del padre. Si la cancelación no vuelve, `Failed` con `Internal` (R12).
  - **Vista previa por IPC** (criterio 5, F2, RF-07): se hace con los mismos orígenes validados (§8.3) y las mismas exclusiones obligatorias que una ejecución, leyendo con `SourceFileSystem`.
  - **Patrones de exclusión** (criterio 2): la semántica y la validación están en §4.1 "Patrones de exclusión", incluidas la normalización NFC y el presupuesto de coste de la lista (`ExclusionMatcher.MaxListCost` = 131 584; una lista que lo supera da `ExclusionPatternInvalid` en `Exclusions`, sin índice; el servicio valida con `ExclusionMatcher.ValidateList`).
  - **Prueba manual**: PM-HU08-01 (OneDrive real sin descargar, descargado y fijado, WOF y enlaces, en vivo y desde VSS). Avisos habituales de perfiles de usuario: R13 (pendiente de decidir).
- **Decidido (revisión de seguridad del `SourceFileSystem`, rama `historia/hu-08-servicio`; decisión del coordinador)**. Lo implementa backbone-service:
  - **A-1, apertura relativa obligatoria** (ADR-18, §8.2 "Apertura relativa"): cada elemento se abre con `NtCreateFile` relativo al handle de su carpeta padre (`RootDirectory`, un solo componente, `FILE_OPEN_REPARSE_POINT | FILE_OPEN_FOR_BACKUP_INTENT`), en vivo y en VSS, con SQOS de identificación en `OBJECT_ATTRIBUTES` (nunca `SECURITY_SQOS_PRESENT` en `CreateFile`). Caché de 128 handles de carpeta por ejecución (LRU, raíces fijas aparte, compartición con borrado); si falta el padre, se abre desde el antepasado más cercano componente a componente. La raíz sale de la resolución controlada de ADR-20; en VSS, del dispositivo de la instantánea. La ruta final se mantiene como defensa en profundidad. La reapertura de la lista blanca pasa de `ReOpenFile` a `NtCreateFile` relativo al propio handle.
  - **M-1, oplocks y plazos**: `FILE_COMPLETE_IF_OPLOCKED` en todas las aperturas de lectura de orígenes; presupuesto por origen y ejecución (5 plazos vencidos o 10 min en operaciones de más de 10 s), tras el cual el resto del origen da `ReadError` sin E/S y conserva el padre (sin código de aviso nuevo, el contrato no cambia); con 4 hilos abandonados en todo el servicio (un solo contador para lectura y validación), las ejecuciones y validaciones nuevas fallan con `Internal` y se registra el evento **1005** (§8.5, R12).
  - **`SeBackupPrivilege`** (ADR-21, §8.2 "Token de lectura de los orígenes"): copia del token del proceso con solo `SeBackupPrivilege` activado y `SeRestorePrivilege`, `SeTakeOwnershipPrivilege`, `SeSecurityPrivilege`, `SeDebugPrivilege` y `SeImpersonatePrivilege` quitados, asignada con `SetThreadToken` a hilos dedicados (`UnsafeStart`, trabajo síncrono, `RevertToSelf` al terminar) de lectura y de resolución controlada; aperturas limitadas a `GENERIC_READ | FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES | SYNCHRONIZE` con `FILE_OPEN`, comprobado en código; nada que escriba corre en ellos; si falla, se lee sin el privilegio con aviso.
  - **B-1**: antes de reabrir, se relee `FileAttributeTagInfo` del primer handle. Riesgo residual R15 (§13).
  - **B-2**: raíces `\\?\GLOBALROOT\Device\…` solo con `HarddiskVolumeShadowCopy<n>` (o `HarddiskVolume<n>`), y en VSS solo las instantáneas de esa ejecución.
  - **B-3**: búfer de `min(bloque, tamaño)` o de un *pool* al que solo vuelve si no hubo fallo ni abandono.
  - **I-3**: PM-HU08-01 comprueba que un usuario estándar no puede crear un enlace duro hacia `credentials\*.bin`. **I-4**: una exclusión obligatoria vacía tras dividirla es `ArgumentException`. **I-5**: carpetas protegidas y exclusiones obligatorias se calculan comparando rutas finales `VOLUME_NAME_GUID`, no letras.
  - La señal de abandono es `SourceOperationAbandonedException` (deriva de `SourceReadingFailedException`, no de `IOException`): atraviesa el motor y el `JobRunner` la traduce a `Failed`/`Internal`.
  - **Para backbone-installer (HU-01)**: `SERVICE_CONFIG_REQUIRED_PRIVILEGES_INFO` con la lista de privilegios que dé backbone-service (§10).
- **Decidido (cierre de HU-08 servicio; decisión del coordinador)**, avalado por la revisión de seguridad. Lo implementa backbone-service salvo donde se indica:
  - **`PD` desconocida** (§8.3 "Carpetas protegidas"): si no se obtiene la ruta `VOLUME_NAME_GUID` de `%ProgramData%`, `Classify` falla cerrado con **todos** los orígenes (`ProtectedFoldersUnknown`: `SourceNotAllowed` en la validación, `ValidationFailed` en la ejecución). También falla cerrado un origen cuya ruta GUID viene vacía. El mensaje al usuario tiene que distinguir este caso del de "demasiadas carpetas apartadas". **Decidido por el usuario (R16, §13)**: código de validación propio `ValidationIssueCode.ProtectedFoldersUnavailable` (30, contrato 1.4, §7.5), ya añadido a Backbone.Contracts.
    - **backbone-service**: cuando `Classify` da `ProtectedFoldersUnknown`, emite `ProtectedFoldersUnavailable` (error en `Sources[i].Path`, `Value` nulo) en lugar de `SourceNotAllowed`, en `ValidateJobAsync`, `SaveJobAsync` y en la repetición de la validación del `JobRunner` (la ejecución sigue terminando `Failed` con `ValidationFailed`). Sigue usando `SourceNotAllowed` para el origen dentro de una carpeta protegida y para el exceso de carpetas apartadas. Pruebas de los dos casos.
    - **backbone-app**: traduce `ProtectedFoldersUnavailable` en los dos `.resw` (es-ES y en-US) con los textos de §7.4.
  - **Raíz en vivo** (§8.2 "Apertura relativa", §8.3 punto 6, ADR-18): `SourceFileSystem` no recibe el handle de la resolución controlada. Vuelve a bajar desde el volumen, abierto por `\??\Volume{guid}\` o por `\??\X:\` tras comprobar `GetDriveType`, sin seguir ningún enlace; un enlace aparecido después de la resolución da `SourceRootUnreadable`. **Condición para HU-14**: el `JobRunner` pasa como `ReadPath` la ruta final de la resolución (`FinalPath`, o `GuidPath` si no hay letra), nunca la configurada.
  - **BAJA-5, letra reasignada a una unidad de red** entre la validación y la copia: prueba manual PM-HU08-02 con el NAS de pruebas. Antes de la ejecución da `Failed`/`ValidationFailed` (`SourceNotLocal`) y durante ella `SourceRootUnreadable`, en los dos casos sin abrir nada en la unidad ni autenticarse.
  - **WOF** (backbone-service): en Windows 11 26320 el filtro oculta la etiqueta también a `FileAttributeTagInfo` con `FILE_OPEN_REPARSE_POINT`. Un archivo de `compact` se lee como *normal* y no pasa por la lista blanca (§8.2 "Apertura para leer el contenido"). PM-HU08-01, paso 7, lo comprueba por el SHA-256, también desde VSS.
  - **Lecturas asíncronas abandonadas** (§8.2 "Plazo"): cuentan en el contador de hilos abandonados del servicio hasta que vuelven.
  - **Otras reglas** (§8.2):
    - Un elemento sustituido por un *junction* que hace fallar `FILE_NON_DIRECTORY_FILE` se reabre solo con `FILE_READ_ATTRIBUTES` y da `ReparsePointSkipped`.
    - Dos enumeraciones simultáneas de la misma raíz: la segunda da `ReadError`.
    - Las exclusiones obligatorias se separan por `\` y por `/`.
    - Los nombres bajo la raíz se comparan con `Ordinal` (carpetas que distinguen mayúsculas); la raíz, sin distinguir mayúsculas.
    - `SourceOpen` solo admite como rutas absolutas `\??\X:\`, `\??\Volume{guid}\` y `\Device\HarddiskVolume<n>\` o `\Device\HarddiskVolumeShadowCopy<n>\`, y exige `FILE_OPEN_REPARSE_POINT` salvo en la reapertura con nombre vacío.
  - **Privilegios del servicio** (§10, ADR-21): `SERVICE_CONFIG_REQUIRED_PRIVILEGES_INFO` con `SeBackupPrivilege`, `SeImpersonatePrivilege` y `SeChangeNotifyPrivilege`, ninguno más. Pruebas en HU-01 (backbone-installer) y comprobación de VSS con el token restringido en HU-15.
  - **Informativo, registrador a archivo de `logs\`** (RNF-12, §8.2 "Token de lectura de los orígenes"): escribe desde una cola propia, nunca desde los hilos con el token de lectura (ADR-21).

### HU-09 · Programar un trabajo
Como Ana, quiero que la copia se haga sola a la hora que elija, para no tener que acordarme.

- **Fase** MVP · **Puntos** 5 · **Depende de** HU-07 · **Agente** backbone-service (backbone-core, backbone-app) · **Requisitos** RF-10, RF-11, RF-15
- **Criterios**
  1. Puedo elegir Manual, Diaria (horas + días de la semana) o Intervalo (cada N horas).
  2. El panel muestra la próxima ejecución calculada por el servicio.
  3. Dado un trabajo diario a las 02:00 y el equipo apagado a esa hora, cuando se enciende a las 08:00, entonces la copia empieza a los 5 minutos del arranque del servicio.
  4. Si dos trabajos coinciden, se ejecutan uno detrás de otro en orden de llegada.
  5. Cambiar la programación recalcula la próxima ejecución sin reiniciar el servicio.
  6. Pruebas con `FakeTimeProvider`, incluidos cambios de horario de verano.

### HU-10 · Editar, duplicar y eliminar trabajos
Como Luis, quiero modificar o borrar un trabajo, para adaptar las copias a lo que necesito cada momento.

- **Fase** MVP · **Puntos** 3 · **Depende de** HU-07 · **Agente** backbone-app (backbone-service) · **Requisitos** RF-01
- **Criterios**
  1. Editar un trabajo en ejecución está permitido, pero los cambios se aplican a partir de la siguiente ejecución (aviso visible).
  2. Duplicar crea una copia con nombre "(copia)" y sin destino, que debo rellenar.
  3. Eliminar pide confirmación; el repositorio del NAS no se borra y se indica su ruta.
  4. Desactivar un trabajo detiene sus ejecuciones programadas, pero "Copiar ahora" sigue funcionando.

### HU-11 · Pausar las copias
Como Ana, quiero pausar las copias un rato, para que no consuman red cuando la necesito.

- **Fase** F2 · **Puntos** 2 · **Depende de** HU-09 · **Agente** backbone-service (backbone-app) · **Requisitos** RF-12
- **Criterios**
  1. Desde la bandeja o Ajustes puedo pausar 1 h, 4 h, hasta mañana o indefinidamente.
  2. Durante la pausa el icono lo indica y no se lanzan ejecuciones programadas; las que coincidan se recuperan al terminar la pausa.
  3. "Copiar ahora" funciona durante la pausa.
  4. La pausa sobrevive a un reinicio del servicio.

### HU-12 · Condiciones de ejecución
Como Ana, quiero que no se copie con poca batería ni con el móvil compartiendo datos, para no quedarme sin batería ni sin datos.

- **Fase** F2 · **Puntos** 3 · **Depende de** HU-09 · **Agente** backbone-service · **Requisitos** RF-13
- **Criterios**
  1. Con batería por debajo del umbral y sin cargador, la ejecución queda `Skipped` con motivo "batería baja".
  2. En una conexión de uso medido queda `Skipped` con motivo "conexión de uso medido".
  3. Cuando la condición deja de cumplirse (conecto el cargador, cambio de red), la ejecución pendiente se lanza en menos de 2 minutos.

---

## Épica E4 — Ejecución de copias

### HU-13 · Copia incremental con versiones
Como Ana, quiero que cada copia guarde solo lo que ha cambiado y conserve las versiones anteriores, para recuperar un archivo tal como estaba otro día sin llenar el NAS.

- **Fase** MVP · **Puntos** 8 · **Depende de** HT-04 · **Agente** backbone-core (backbone-qa) · **Requisitos** RF-23, RF-25, §6
- **Criterios**
  1. Primera copia: todos los archivos se suben como objetos y se escribe un snapshot completo.
  2. Segunda copia sin cambios: no se lee ningún archivo de origen (comprobado con un `IFileSystem` instrumentado) y se crea un snapshot nuevo con los mismos hashes.
  3. Dos archivos con el mismo contenido producen un solo objeto.
  4. Un archivo modificado genera un objeto nuevo; el anterior sigue referenciado por el snapshot previo.
  5. Un archivo ilegible se omite, aparece en `warnings` y la ejecución termina `CompletedWithWarnings`; si existía en el snapshot anterior, se conserva su versión anterior.
  6. El hash se calcula mientras se copia; si el contenido cambió, se usa el hash real (§6.4.2).
  7. Rutas de más de 260 caracteres funcionan en origen y destino (RNF-09).
  8. Dado un origen que contiene la carpeta de datos (p. ej. `C:\`), cuando se ejecuta la copia, entonces ningún archivo de `%ProgramData%\Backbone` (en particular `credentials\*.bin`) aparece en el snapshot ni se sube como objeto, también leyendo desde la instantánea VSS, y la exclusión no genera avisos de ejecución. Dado un origen que tras guardarse se ha sustituido por un *junction* hacia la carpeta de datos, la ejecución termina `Failed` con `ValidationFailed` sin leer nada (§8.3).
- **Decidido (revisión de seguridad de HU-04, MEDIA-1)**: exclusión obligatoria de la carpeta de datos (ADR-16, §8.3). El motor (backbone-core) acepta, aparte de los patrones del trabajo, rutas relativas al origen que no recorre nunca; el servicio (backbone-service, en `JobRunner`) calcula esa ruta a partir de las rutas finales y repite la comprobación de `SourceNotAllowed` al empezar cada ejecución.
- **Decidido (nota de HT-04, decisión del usuario)**: una copia con más de 5 000 000 de entradas, o cuyo manifiesto superaría 16 GiB (el tamaño del listado, no de los datos), falla sin publicar nada. Core lanza `RepositoryException` con `RepositoryErrorKind.SnapshotTooLarge` y el servicio lo traduce al código propio `BackboneErrorCode.SnapshotTooLarge` (1017, contrato 1.2) en `RunRecord.ErrorCode`. El mensaje de la app habla de demasiados archivos y carpetas y sugiere dividir el trabajo en varios (§6.3, §7.4).
- **Decidido (decisión del usuario; revisión de seguridad de HU-13, BAJA-4)**: snapshot padre (§6.3 "Snapshot padre", ADR-17). Si el padre del trabajo, o una cabecera de snapshot que no se puede leer (no se puede descartar que sea del trabajo), está dañado, la copia falla con `Corrupt` (`RepositoryCorrupt`) y no publica nada hasta que se verifique o repare (HU-29); no se salta con un aviso. Un snapshot con fecha posterior a `startedUtc` más la tolerancia de reloj (15 min) no se acepta como padre: se ignora y se sigue buscando hacia atrás. Reutilizar los hashes del padre propaga un `h` alterado (R6, riesgo aceptado en v1).
- **Decidido (coordinador)**: la exclusión obligatoria de ADR-16 cubre también las carpetas apartadas `%ProgramData%\Backbone.untrusted-<guid>` (ADR-13), que pueden contener credenciales antiguas. El servicio las incluye en la lista de carpetas protegidas (§8.3) y pasa sus rutas relativas al motor igual que la de la carpeta de datos.
- **Decidido (revisión de seguridad de HU-13, MEDIA-1)**: el motor acota por bytes, no solo por número, la memoria de los avisos (presupuesto de avisos de la cabecera, 16 MiB − 256 KiB en UTF-8) y de los listados de carpetas abiertas (128 MiB; una carpeta que lo supera se trata como ilegible con aviso `ReadError`) (§8.4 "Memoria del motor", RNF-05).
- **Decidido (revisión de seguridad de HU-13, MEDIA-2)**: contrato de `ISourceFileSystem` (§8.2 "Lectura de los orígenes"): las tres operaciones abren por handle sin seguir puntos de reanálisis, lo comprueban sobre el handle y, si lo es, lanzan `SourceAccessException(ReparsePointSkipped)`; la enumeración se hace desde ese handle. backbone-core actualiza la documentación de la interfaz y las pruebas con un simulador que lo ejerza; la implementación real es de backbone-service (HU-08/HU-15).
- **Decidido (revisión de seguridad final de HU-13)**: M-A (ruta final sobre el mismo handle, ADR-18), M-B (carpetas apartadas solo si son carpetas reales, por ubicación, máximo 100) y B-1 (`SourceRootUnreadable`, 13, contrato 1.2, cuando no se puede recorrer la raíz de un origen; la raíz conserva lo del padre también si se ha convertido en punto de reanálisis) (§8.2, §8.3, §8.4, R11). En esta historia: backbone-core emite `SourceRootUnreadable` y actualiza el comentario de `ISourceFileSystem`; lo demás va en HU-08/HU-15 (backbone-service).

### HU-14 · Copiar ahora y cancelar
Como Luis, quiero lanzar una copia en el momento y poder cancelarla, para controlar cuándo se usa la red.

- **Fase** MVP · **Puntos** 3 · **Depende de** HU-13, HT-03 · **Agente** backbone-service (backbone-app) · **Requisitos** RF-20, RF-21
- **Criterios**
  1. "Copiar ahora" en Inicio, en la bandeja ("Copiar todo ahora") y en Trabajos lanza o encola la ejecución.
  2. "Cancelar" detiene la ejecución en menos de 5 s; queda `Cancelled`, sin snapshot nuevo.
  3. Tras cancelar, `check` del repositorio es correcto y la siguiente ejecución termina bien.
- **Requisito previo (revisión final de seguridad de HU-05; decisión del coordinador)**: antes de que las copias usen `ShareConnectionManager`, backbone-service corrige en el gestor (§8.2 "Conexiones al NAS"):
  - **M-1, oráculo de contraseñas**: la reutilización de una conexión se decide por la **identidad** de la credencial guardada (raíz más `UpdatedUtc`), no por su contenido. Se quita la huella HMAC. Una credencial explícita no guardada nunca comparte una conexión ajena. Si ya hay otra conexión con ese servidor que no se puede compartir, la respuesta es `NasBusy` (1018, contrato 1.3), coincida o no el secreto. Además, tras 5 `CredentialsRejected` seguidos de un SID, sus pruebas con credencial explícita dan `NasBusy` durante 5 min. backbone-app traduce `NasBusy` (es-ES y en-US).
  - **B-1**: un `AcquireAsync` de una raíz en estado "cerrando" espera a que termine la desconexión pendiente.
  - **B-2**: en la parada, el gestor queda cerrado (`AcquireAsync` lanza) y las conexiones aún en curso se desconectan a la fuerza cuando terminan. 2250 no se registra como "forzada". El límite de dos pruebas en total cuenta las operaciones abandonadas, y los semáforos por servidor se liberan cuando quedan libres.
  - **B-3**: la ruta final del archivo de prueba se comprueba sobre su handle ya abierto, antes de escribir. Si no se puede obtener, `AccessDenied`.
  - Informativo: la sesión de inicio `0x3e7` es común a todos los servicios LocalSystem (§8.2). Riesgo de *DNS rebinding* y de referencias DFS hacia el propio equipo: R14 (§13).
- **Requisito (revisión final de HU-07, B-3; revisión del `SourceFileSystem` de HU-08, M-1)**: la repetición de la validación en el `JobRunner` no pasa por el límite de 4 validaciones simultáneas, pero sus hilos abandonados cuentan en el **mismo contador** de hilos abandonados que la lectura de orígenes; con 4, la ejecución termina `Failed`/`Internal` sin resolver ni leer nada y se registra el evento 1005 (§8.2 "Plazo de las lecturas de los orígenes").
- **Al empezar cada ejecución** (HU-07, §8.4 paso 2): el `JobRunner` repite la resolución controlada de los orígenes (ADR-20) y la comprobación de la credencial guardada (`CredentialRequired` sin conectar).
- **Requisito (cierre de HU-08 servicio; decisión del coordinador)**: en una copia en vivo, el `JobRunner` pasa como `ReadPath` de cada `BackupSource` la **ruta final** de esa resolución (`FinalPath`, o `GuidPath` si el volumen no tiene letra), **nunca la ruta configurada** en `jobs.json`. `SourceFileSystem` vuelve a bajar hasta ella desde el volumen sin seguir ningún enlace (§8.2 "Apertura relativa", §8.3 punto 6), así que con la ruta configurada un origen con un *junction* legítimo daría `SourceRootUnreadable`. Hace falta una prueba que lo compruebe.

### HU-15 · Copiar archivos abiertos (VSS)
Como Ana, quiero que se copien también los archivos que tengo abiertos, como el correo de Outlook, para que no queden fuera de la copia.

- **Fase** MVP · **Puntos** 8 · **Depende de** HU-13 · **Agente** backbone-service (backbone-qa) · **Requisitos** RF-22, R1
- **Criterios**
  1. Dado un archivo abierto en exclusiva por otro proceso, cuando se ejecuta la copia, entonces se copia desde la instantánea y la ejecución es `Completed`.
  2. La instantánea se elimina al terminar, también si la ejecución falla, se cancela o el servicio se detiene.
  3. Al arrancar, el servicio elimina las instantáneas huérfanas creadas por Backbone (y solo esas).
  4. Si VSS falla, la copia continúa en vivo, termina `CompletedWithWarnings` y se registra el evento 4000.
  5. Orígenes en varios volúmenes: una instantánea por volumen, todas con el mismo instante de inicio.
  6. Prueba de integración real (requiere admin) y prueba unitaria con `IVolumeSnapshotter` simulado.
- **Nota de HU-13 (revisión de seguridad, MEDIA-2)**: la lectura desde la instantánea (y en vivo) la hace `SourceFileSystem` con el contrato de §8.2 ("Lectura de los orígenes"): todo por handle, sin seguir puntos de reanálisis, `SourceAccessException(ReparsePointSkipped)` si el handle abierto es uno, y la enumeración desde ese handle. Si `SourceFileSystem` se implementa antes (HU-08 o HU-14), esta historia solo lo reutiliza con las rutas de la instantánea.
- **Decidido (revisión de seguridad final de HU-13)**:
  - M-A: la comprobación de la ruta final sobre el mismo handle (§8.2, ADR-18) usa `VOLUME_NAME_NT` para que valga dentro de la instantánea, que no tiene letra de unidad; la ruta final de la raíz se obtiene al empezar abriendo `ReadPath` por handle. La copia en vivo (VSS no disponible, criterio 4) es el caso explotable y necesita su prueba: sustituir una carpeta intermedia ya enumerada por un *junction* y comprobar que no se lee su destino.
  - `ReadPath` se calcula a partir de la **ruta final** del origen (la de la comprobación de §8.3), no de la ruta configurada, para que un origen *junction* no se resuelva desde la instantánea hacia el volumen en vivo.
  - **Token restringido (cierre de HU-08 servicio; decisión del coordinador)**: la prueba de integración real (criterio 6) se hace con el servicio instalado con los privilegios de §10 (`SeBackupPrivilege`, `SeImpersonatePrivilege` y `SeChangeNotifyPrivilege`, ninguno más) y comprueba que crear, usar y eliminar la instantánea y limpiar las huérfanas funciona con ese token. Si VSS necesitara otro privilegio, se para y se pregunta: la lista se cambia antes en §10.
  - M-B: las carpetas protegidas se calculan sobre el volumen en vivo por su ubicación (`<final de %ProgramData%>\<nombre>`, solo carpetas reales, como mucho 100; §8.3) y se pasan al motor como rutas relativas, que valen igual en la instantánea.

### HU-16 · Copias sin sesión iniciada y tolerancia a fallos
Como Luis, quiero que las copias funcionen con la sesión cerrada y que soporten cortes de red y reinicios, para confiar en que siempre hay una copia válida.

- **Fase** MVP · **Puntos** 5 · **Depende de** HU-13, HU-04 · **Agente** backbone-service (backbone-qa) · **Requisitos** RF-26, RNF-01, RNF-02
- **Criterios**
  1. Con la sesión cerrada, una ejecución programada termina bien usando las credenciales guardadas.
  2. Un corte de red de menos de 2 minutos se supera con reintentos; uno más largo termina `Failed` con código `NasUnreachable` y evento 3000.
  3. Matar el proceso del servicio durante la copia: el SCM lo reinicia, el repositorio supera `check` y la siguiente ejecución completa el trabajo.
  4. Los bloqueos abandonados se liberan pasado el plazo de §6.4.4.

### HU-17 · No suspender durante la copia y prioridad baja
Como Ana, quiero que el PC no se duerma a mitad de una copia y que no vaya lento mientras trabajo, para que la copia termine sin molestar.

- **Fase** MVP (suspensión) / F2 (prioridad) · **Puntos** 2 · **Depende de** HU-13 · **Agente** backbone-service · **Requisitos** RF-27, RF-29
- **Criterios**
  1. Durante una ejecución, `powercfg /requests` muestra la petición de Backbone; al terminar desaparece.
  2. Si el usuario suspende el equipo manualmente, la ejecución se marca `Failed` (código `Interrupted`) y se recupera al reanudar según RF-11.
  3. (F2) Los hilos de copia usan modo de segundo plano de E/S.

---

## Épica E5 — Supervisión y notificaciones

### HU-18 · Icono en la bandeja
Como Ana, quiero ver de un vistazo en la bandeja si mis copias están bien, para quedarme tranquila sin abrir nada.

- **Fase** MVP · **Puntos** 5 · **Depende de** HT-03 · **Agente** backbone-app · **Requisitos** RF-50, §9.1
- **Criterios**
  1. El icono muestra los cuatro estados de RF-50; el texto emergente resume el estado y la última copia correcta.
  2. Menú contextual: Copiar todo ahora, Pausar (F2), Abrir Backbone, Salir.
  3. Cerrar la ventana la oculta; "Salir" cierra la app sin detener el servicio.
  4. Una segunda ejecución de la app trae al frente la existente.
  5. Si el servicio está detenido, el icono pasa a rojo en menos de 15 s.

### HU-19 · Panel de inicio con progreso en vivo
Como Luis, quiero ver el estado de cada trabajo y el progreso de la copia en curso, para saber qué está pasando.

- **Fase** MVP · **Puntos** 5 · **Depende de** HT-03, HU-13 (el botón Cancelar se conecta al terminar HU-14) · **Agente** backbone-app · **Requisitos** RF-51, §9.2–9.3
- **Criterios**
  1. Cada trabajo muestra estado, última ejecución (con resultado), próxima ejecución y botón Copiar ahora / Cancelar.
  2. Durante una copia veo archivos y bytes procesados, velocidad, archivo actual y tiempo estimado, actualizado al menos una vez por segundo.
  3. Los estados especiales de §9.3 se muestran correctamente (servicio detenido, sin autorización, contrato incompatible, sin trabajos).
  4. Funciona con teclado y Narrador (RNF-10).

### HU-20 · Notificaciones
Como Ana, quiero que Windows me avise si una copia falla o si llevo días sin copia, para poder actuar a tiempo.

- **Fase** MVP (fallida) / F2 (resto) · **Puntos** 3 · **Depende de** HU-18 · **Agente** backbone-app (backbone-service) · **Requisitos** RF-52
- **Criterios**
  1. Una ejecución fallida muestra una notificación con el trabajo, el motivo y el botón "Ver detalles" que abre el historial.
  2. (F2) Se notifican, según preferencias: con avisos, correcta, sin copia en X días, espacio bajo.
  3. Las notificaciones respetan el modo "No molestar" de Windows (las gestiona el sistema).
  4. Si la app no está abierta en ese momento, al abrirse se muestra el aviso pendiente en Inicio.

### HU-21 · Historial de ejecuciones
Como Luis, quiero consultar el historial de copias con sus detalles, para investigar cualquier problema.

- **Fase** MVP · **Puntos** 5 · **Depende de** HU-13 · **Agente** backbone-service (backbone-app) · **Requisitos** RF-53
- **Criterios**
  1. La lista muestra fecha, trabajo, estado, duración, archivos y bytes; filtros por trabajo y estado.
  2. El detalle lista avisos y errores con ruta y mensaje.
  3. Una lista de 10 000 ejecuciones se desplaza con fluidez (virtualización).
  4. Las ejecuciones de más de 1 año se purgan automáticamente.

### HU-22 · Visor de eventos
Como Luis, quiero que Backbone escriba en el Visor de eventos, para supervisarlo con las herramientas de Windows que ya uso.

- **Fase** MVP · **Puntos** 2 · **Depende de** HU-13 (en desarrollo el origen se registra con `scripts/dev-register-eventsource.ps1`; en producción lo hace el instalador, HU-01) · **Agente** backbone-service · **Requisitos** RF-54, §8.5
- **Criterios**
  1. Cada evento de §8.5 se escribe con su ID, nivel y datos.
  2. Los mensajes son legibles sin la app (incluyen trabajo, resultado y motivo).
  3. Desde el detalle del historial, "Abrir en el Visor de eventos" abre `eventvwr.msc` filtrado por el origen Backbone.

### HU-23 · Exportar diagnóstico
Como Luis, quiero exportar un paquete de diagnóstico, para analizar problemas o compartirlo.

- **Fase** F2 · **Puntos** 2 · **Depende de** HU-21 · **Agente** backbone-service (backbone-app, backbone-security-review) · **Requisitos** RF-55, RNF-06
- **Criterios**
  1. El ZIP contiene configuración (sin credenciales), logs de 14 días, últimas 100 ejecuciones y versiones.
  2. Una prueba confirma que ninguna contraseña aparece en el ZIP.

---

## Épica E6 — Restauración

### HU-24 · Ver y explorar snapshots
Como Ana, quiero ver las copias por fecha y navegar por sus carpetas, para encontrar el archivo que necesito.

- **Fase** MVP · **Puntos** 5 · **Depende de** HU-13, HT-03 · **Agente** backbone-app (backbone-core, backbone-service) · **Requisitos** RF-30, RF-31
- **Criterios**
  1. Al elegir un trabajo veo sus snapshots agrupados por día, con hora, número de archivos, tamaño y estado.
  2. Al abrir un snapshot navego por un árbol de carpetas con nombre, tamaño y fecha de cada archivo.
  3. La búsqueda por nombre devuelve resultados en menos de 2 s en un snapshot de 100 000 archivos.
  4. Los manifiestos se cachean en el servicio para no releerlos del NAS en cada navegación.

### HU-25 · Restaurar archivos y carpetas
Como Ana, quiero restaurar los archivos que elija a su sitio o a otra carpeta, para recuperar lo que perdí o estropeé.

- **Fase** MVP · **Puntos** 8 · **Depende de** HU-24 · **Agente** backbone-core (backbone-service, backbone-app, backbone-security-review) · **Requisitos** RF-32, RF-33, RF-34, §8.6
- **Criterios**
  1. Puedo seleccionar varios archivos y carpetas y restaurarlos a la ubicación original o a otra carpeta.
  2. Ante un conflicto se me pregunta (sobrescribir, conservar ambos, omitir) con "aplicar a todos".
  3. Los archivos restaurados tienen la fecha y los atributos guardados y su hash se verifica; un hash erróneo marca el archivo como fallido y no deja un archivo corrupto en destino.
  4. Si no tengo permiso de escritura en el destino, se rechaza con `RestoreTargetNotWritable` antes de copiar nada.
  5. Se rechazan destinos dentro de las carpetas protegidas de §8.6.4.
  6. Puedo cancelar; los archivos ya restaurados se quedan y el resumen indica cuáles.
  7. Al terminar veo el resumen y el botón "Abrir carpeta".
- **Nota de HT-04**: el manifiesto es no confiable (§6.1) y sus rutas son únicas solo con comparación ordinal (§6.3), así que puede traer entradas que en NTFS colisionan: rutas que solo difieren en mayúsculas (`A.txt` y `a.txt`, o carpetas `Fotos/` y `fotos/`) y un archivo y una carpeta con el mismo nombre (`x` como archivo y `x/…` como carpeta, también con distinta capitalización). La restauración debe tratarlas como conflicto (RF-32: preguntar u omitir, nunca sobrescribir en silencio ni mezclar el contenido de un archivo con el de una carpeta) y cubrirlo con pruebas.

### HU-26 · Versiones de un archivo
Como Ana, quiero ver todas las versiones de un archivo concreto, para elegir la que necesito sin revisar snapshot por snapshot.

- **Fase** F2 · **Puntos** 5 · **Depende de** HU-25 · **Agente** backbone-core (backbone-app) · **Requisitos** RF-35
- **Criterios**
  1. Desde el explorador, "Ver versiones" muestra solo los snapshots donde el archivo cambió, con fecha y tamaño.
  2. Puedo restaurar cualquiera de ellas con el flujo de HU-25.

### HU-27 · Integración con el Explorador de Windows
Como Ana, quiero hacer clic derecho en un archivo y ver sus versiones anteriores, para no tener que buscarlo en la app.

- **Fase** F3 · **Puntos** 8 · **Depende de** HU-26 · **Agente** backbone-app (backbone-installer) · **Requisitos** RF-36
- **Criterios**
  1. El menú contextual de Windows 11 (`IExplorerCommand`) muestra "Backbone → Ver versiones anteriores" solo en rutas incluidas en un trabajo.
  2. Al pulsarlo se abre la app en la vista de versiones de ese archivo o carpeta.

---

## Épica E7 — Retención y mantenimiento

### HU-28 · Política de retención
Como Luis, quiero decidir cuántas copias se guardan por día, semana y mes, para equilibrar historial y espacio.

- **Fase** F2 · **Puntos** 5 · **Depende de** HU-13 · **Agente** backbone-core (backbone-app) · **Requisitos** RF-40, RF-44 (F3)
- **Criterios**
  1. Valores por defecto 3/7/4/12/0; editables en el trabajo.
  2. "Simular" muestra qué snapshots se conservarían y cuáles se eliminarían (sin borrar nada).
  3. Tabla de pruebas con fechas fijas que cubre límites de semana (ISO 8601), mes y año, y zona horaria local.
  4. La retención nunca elimina el snapshot más reciente ni (F3) los protegidos.

### HU-29 · Limpieza y verificación
Como Luis, quiero que el repositorio se limpie y se verifique periódicamente, para recuperar espacio y saber que las copias están sanas.

- **Fase** F2 · **Puntos** 8 · **Depende de** HU-28 · **Agente** backbone-core (backbone-service, backbone-qa) · **Requisitos** RF-41, RF-42, §6.4.5
- **Criterios**
  1. La limpieza borra objetos no referenciados con más de 24 h y nunca un objeto referenciado (prueba con repositorios generados aleatoriamente).
  2. Requiere bloqueo exclusivo; si hay una copia en curso, espera o se aplaza.
  3. Verificación rápida y completa según RF-42; un objeto corrupto produce evento 6000, notificación y el detalle de qué snapshots y archivos afecta.
  4. Programación por defecto: limpieza semanal, verificación completa mensual; ambas a petición desde la app.

### HU-30 · Espacio en el NAS
Como Ana, quiero saber cuánto espacio queda en el NAS, para no quedarme sin sitio sin darme cuenta.

- **Fase** F2 · **Puntos** 2 · **Depende de** HU-05 · **Agente** backbone-service (backbone-app) · **Requisitos** RF-43
- **Criterios**
  1. Inicio muestra espacio libre, total y tamaño del repositorio.
  2. Por debajo del 10 % o de 20 GB libres, aviso en Inicio, notificación y evento 3002.

---

## Épica E8 — Seguridad y acceso

### HU-31 · Solo usuarios autorizados gestionan Backbone
Como Luis, quiero que solo los usuarios que yo autorice puedan cambiar o restaurar copias, para que otros usuarios del PC no accedan a mis archivos.

- **Fase** MVP · **Puntos** 3 · **Depende de** HT-03 · **Agente** backbone-service (backbone-app, backbone-security-review) · **Requisitos** §7.3, RNF-08
- **Criterios**
  1. Un usuario estándar no autorizado no puede conectar con el servicio y la app le muestra la pantalla de §9.3.
  2. Desde la app elevada puedo añadir y quitar usuarios autorizados; sin elevar, la opción pide elevación.
  3. Las operaciones de escritura quedan en el log con el usuario (SID) que las hizo, sin argumentos.
  4. Solo se pueden añadir cuentas de usuario cualificadas (`EQUIPO\usuario`, `DOMINIO\usuario` o SID) miembros del grupo local Administradores; grupos, Everyone, SID bien conocidos o cuentas no administradoras se rechazan con un mensaje específico (§7.3).
  5. Si una cuenta autorizada deja de ser administradora o se quita de la lista, pierde el acceso y se cierran sus conexiones.

### HU-32 · Cifrado del repositorio
Como Luis, quiero cifrar el repositorio con una contraseña, para que nadie con acceso al NAS pueda leer mis archivos.

- **Fase** F3 · **Puntos** 13 · **Depende de** HU-34 · **Agente** backbone-core (backbone-security-review) · **Requisitos** RF-70
- **Criterios**
  1. Al crear el repositorio puedo activar el cifrado; aparece un aviso explícito sobre la pérdida de la contraseña.
  2. Objetos y manifiestos cifrados con AES-256-GCM; la clave maestra cifrada con la clave derivada de la contraseña.
  3. Sin la contraseña no se puede leer ningún nombre de archivo ni contenido.
  4. Puedo cambiar la contraseña sin volver a cifrar los datos.

---

## Épica E9 — Rendimiento (F3)

### HU-33 · Detección de cambios con USN Journal
Como Luis, quiero que las copias incrementales sean casi instantáneas en carpetas grandes, para poder programarlas cada hora.

- **Fase** F3 · **Puntos** 8 · **Depende de** HU-13 · **Agente** backbone-service (backbone-core) · **Requisitos** RF-24, RNF-03
- **Criterios**
  1. Con el diario disponible, una ejecución sin cambios sobre 100 000 archivos tarda < 10 s.
  2. Si el diario se reinició, desbordó o cambió su identificador, se hace el recorrido completo y se registra el motivo.
  3. El resultado (snapshot) es idéntico al del recorrido completo (prueba comparativa).

### HU-34 · Formato v2: troceado y compresión
Como Luis, quiero que de un archivo grande modificado solo se suba la parte que cambió y que todo vaya comprimido, para ahorrar tiempo y espacio.

- **Fase** F3 · **Puntos** 13 · **Depende de** HU-29 · **Agente** backbone-core (backbone-architect) · **Requisitos** §6.5
- **Criterios**
  1. Modificar 1 MB de un archivo de 1 GB sube menos de 5 MB.
  2. Compresión zstd configurable; datos ya comprimidos (jpg, zip, mp4) no se recomprimen.
  3. Repositorios v1 siguen siendo legibles; migración explícita v1 → v2 desde la app.
  4. Se actualiza §6 de la especificación con el formato definitivo.

### HU-35 · Límite de ancho de banda
Como Ana, quiero limitar la velocidad de las copias, para que no afecten a las videollamadas.

- **Fase** F3 · **Puntos** 3 · **Depende de** HU-13 · **Agente** backbone-core · **Requisitos** RF-28
- **Criterios**
  1. Con un límite de 10 MB/s, la velocidad media medida durante 60 s no supera 10,5 MB/s.
  2. El límite se puede cambiar durante una ejecución y se aplica en menos de 5 s.

### HU-36 · Despertar el equipo para copiar
Como Ana, quiero que el PC se despierte para la copia nocturna, para que se haga aunque lo deje en suspensión.

- **Fase** F3 · **Puntos** 3 · **Depende de** HU-09 · **Agente** backbone-service · **Requisitos** RF-14
- **Criterios**
  1. Activado en un trabajo, el servicio crea y mantiene una tarea programada con `WakeToRun` 2 minutos antes de la hora.
  2. Desactivarlo o eliminar el trabajo elimina la tarea.
  3. Tras la copia, el equipo vuelve a suspenderse si nadie lo usó (se libera la petición de energía).

---

## Orden de desarrollo recomendado (MVP)

Las historias de una misma fila pueden desarrollarse en paralelo por agentes distintos.

| Iteración | Historias |
|---|---|
| 1 | HT-01 → HT-02 |
| 2 | HT-03 · HT-04 |
| 3 | HU-04 · HU-13 · HU-18 |
| 4 | HU-05 · HU-06 · HU-08 · HU-07 |
| 5 | HU-09 · HU-14 · HU-15 · HU-19 |
| 6 | HU-16 · HU-17 · HU-21 · HU-22 · HU-31 · HU-24 |
| 7 | HU-25 · HU-10 · HU-20 |
| 8 | HU-01 → HU-02 · prueba de aceptación del MVP |

## Seguimiento

| Historia | Fase | Puntos | Agente | Estado |
|---|---|---|---|---|
| HT-01 | MVP | 3 | backbone-architect | Hecha |
| HT-02 | MVP | 3 | backbone-architect | Hecha |
| HT-03 | MVP | 5 | backbone-service | Hecha |
| HT-04 | MVP | 8 | backbone-core | Hecha |
| HU-01 | MVP | 8 | backbone-installer | Pendiente |
| HU-02 | MVP | 3 | backbone-installer | Pendiente |
| HU-03 | F2 | 5 | backbone-app | Pendiente |
| HU-04 | MVP | 5 | backbone-service | Hecha (servicio; criterio 4 pendiente de la app; criterio 5 en PM-HU04-01) |
| HU-05 | MVP | 3 | backbone-service | Hecha (servicio; botón de la app pendiente; PM-HU05-01 pendiente; integración contra el NAS superada el 2026-11-22, salvo el recurso de solo lectura) |
| HU-06 | MVP | 3 | backbone-service | Pendiente |
| HU-07 | MVP | 5 | backbone-app | En curso (servicio hecho; interfaz de la app pendiente; PM-HU07-01 a 05 y pruebas elevadas pendientes) |
| HU-08 | MVP | 3 | backbone-core | Hecha (motor y servicio; traducción en el JobRunner con HU-14; vista previa por IPC (criterio 5, F2) pendiente; PM-HU08-01 y PM-HU08-02 pendientes; pruebas elevadas superadas el 2026-11-22) |
| HU-09 | MVP | 5 | backbone-service | Pendiente |
| HU-10 | MVP | 3 | backbone-app | Pendiente |
| HU-11 | F2 | 2 | backbone-service | Pendiente |
| HU-12 | F2 | 3 | backbone-service | Pendiente |
| HU-13 | MVP | 8 | backbone-core | Hecha (motor; JobRunner, SourceFileSystem y traducción de errores pendientes del servicio; PM-HU13-01) |
| HU-14 | MVP | 3 | backbone-service | Pendiente |
| HU-15 | MVP | 8 | backbone-service | Pendiente |
| HU-16 | MVP | 5 | backbone-service | Pendiente |
| HU-17 | MVP/F2 | 2 | backbone-service | Pendiente |
| HU-18 | MVP | 5 | backbone-app | Hecha |
| HU-19 | MVP | 5 | backbone-app | Pendiente |
| HU-20 | MVP/F2 | 3 | backbone-app | Pendiente |
| HU-21 | MVP | 5 | backbone-service | Pendiente |
| HU-22 | MVP | 2 | backbone-service | Pendiente |
| HU-23 | F2 | 2 | backbone-service | Pendiente |
| HU-24 | MVP | 5 | backbone-app | Pendiente |
| HU-25 | MVP | 8 | backbone-core | Pendiente |
| HU-26 | F2 | 5 | backbone-core | Pendiente |
| HU-27 | F3 | 8 | backbone-app | Pendiente |
| HU-28 | F2 | 5 | backbone-core | Pendiente |
| HU-29 | F2 | 8 | backbone-core | Pendiente |
| HU-30 | F2 | 2 | backbone-service | Pendiente |
| HU-31 | MVP | 3 | backbone-service | Pendiente |
| HU-32 | F3 | 13 | backbone-core | Pendiente |
| HU-33 | F3 | 8 | backbone-service | Pendiente |
| HU-34 | F3 | 13 | backbone-core | Pendiente |
| HU-35 | F3 | 3 | backbone-core | Pendiente |
| HU-36 | F3 | 3 | backbone-service | Pendiente |

**Total MVP**: 119 puntos (HU-17 y HU-20 cuentan en el MVP) · **F2**: 32 · **F3**: 48.

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
  4. Mi usuario figura en `authorizedUsers` y la app, abierta sin elevar, conecta con el servicio.
  5. La app arranca minimizada en la bandeja al iniciar sesión (si dejé marcada la casilla).
  6. Hay MSI para x64 y ARM64.

### HU-02 · Actualizar y desinstalar
Como Luis, quiero actualizar a una versión nueva sin perder la configuración y poder desinstalar limpiamente, para mantener el sistema ordenado.

- **Fase** MVP · **Puntos** 3 · **Depende de** HU-01 · **Agente** backbone-installer
- **Criterios**
  1. Dado Backbone instalado con trabajos, cuando instalo una versión superior, entonces se conservan trabajos, historial y credenciales, y el servicio vuelve a arrancar.
  2. Si hay una copia en curso al actualizar, se cancela de forma controlada (sin corromper el repositorio) antes de detener el servicio.
  3. Al desinstalar con la casilla desmarcada, `%ProgramData%\Backbone` se conserva; marcada, se elimina.
  4. Ninguna desinstalación modifica el repositorio del NAS.

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
  1. Dado un recurso `\\nas\Backups`, cuando guardo usuario y contraseña, entonces se crea un archivo en `credentials\` cifrado con DPAPI `LocalMachine` y entropía propia.
  2. `GetCredentialInfoAsync` devuelve el usuario y la fecha de actualización, nunca la contraseña.
  3. Una búsqueda de la contraseña en `%ProgramData%\Backbone` (logs, config, historial) no encuentra coincidencias.
  4. El campo de contraseña de la app usa `PasswordBox` y se vacía tras guardar.
  5. Con la sesión cerrada, una ejecución programada se conecta con esas credenciales.

### HU-05 · Probar la conexión al NAS
Como Luis, quiero comprobar la conexión antes de guardar un trabajo, para saber exactamente qué falla si algo no va.

- **Fase** MVP · **Puntos** 3 · **Depende de** HU-04 · **Agente** backbone-service (backbone-app) · **Requisitos** RF-61
- **Criterios**
  1. Cuando pulso "Probar conexión" con datos correctos, entonces veo "Conexión correcta", el espacio libre y total, y la versión SMB.
  2. Con contraseña errónea veo "El NAS ha rechazado el usuario o la contraseña".
  3. Con servidor apagado veo "No se encuentra el NAS en la red" en menos de 15 s.
  4. Con recurso inexistente o sin permiso de escritura veo un mensaje específico para cada caso.
  5. La prueba no deja archivos en el recurso.

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

### HU-08 · Excluir archivos y carpetas
Como Luis, quiero excluir archivos por patrones, para no copiar temporales ni carpetas enormes que no necesito.

- **Fase** MVP · **Puntos** 3 · **Depende de** HU-13 (y HU-07 para el editor de exclusiones) · **Agente** backbone-core (backbone-app) · **Requisitos** RF-04, RF-05 (F2), RF-06
- **Criterios**
  1. Las exclusiones por defecto de RF-04 vienen precargadas y se pueden quitar.
  2. Los patrones `*.tmp`, `**/node_modules/**` y `Documentos/Privado/**` excluyen exactamente lo esperado (tabla de casos en pruebas, sin distinguir mayúsculas).
  3. Las *junctions* y los enlaces simbólicos no se siguen y se registran como omitidos.
  4. Los archivos de OneDrive solo en la nube se omiten sin descargarse (R4).
  5. La vista previa muestra cuántos archivos y bytes excluye cada patrón (F2).

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

### HU-14 · Copiar ahora y cancelar
Como Luis, quiero lanzar una copia en el momento y poder cancelarla, para controlar cuándo se usa la red.

- **Fase** MVP · **Puntos** 3 · **Depende de** HU-13, HT-03 · **Agente** backbone-service (backbone-app) · **Requisitos** RF-20, RF-21
- **Criterios**
  1. "Copiar ahora" en Inicio, en la bandeja ("Copiar todo ahora") y en Trabajos lanza o encola la ejecución.
  2. "Cancelar" detiene la ejecución en menos de 5 s; queda `Cancelled`, sin snapshot nuevo.
  3. Tras cancelar, `check` del repositorio es correcto y la siguiente ejecución termina bien.

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
  3. Las operaciones de escritura quedan en el log con el usuario que las hizo.

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
| HT-01 | MVP | 3 | backbone-architect | Pendiente |
| HT-02 | MVP | 3 | backbone-architect | Pendiente |
| HT-03 | MVP | 5 | backbone-service | Pendiente |
| HT-04 | MVP | 8 | backbone-core | Pendiente |
| HU-01 | MVP | 8 | backbone-installer | Pendiente |
| HU-02 | MVP | 3 | backbone-installer | Pendiente |
| HU-03 | F2 | 5 | backbone-app | Pendiente |
| HU-04 | MVP | 5 | backbone-service | Pendiente |
| HU-05 | MVP | 3 | backbone-service | Pendiente |
| HU-06 | MVP | 3 | backbone-service | Pendiente |
| HU-07 | MVP | 5 | backbone-app | Pendiente |
| HU-08 | MVP | 3 | backbone-core | Pendiente |
| HU-09 | MVP | 5 | backbone-service | Pendiente |
| HU-10 | MVP | 3 | backbone-app | Pendiente |
| HU-11 | F2 | 2 | backbone-service | Pendiente |
| HU-12 | F2 | 3 | backbone-service | Pendiente |
| HU-13 | MVP | 8 | backbone-core | Pendiente |
| HU-14 | MVP | 3 | backbone-service | Pendiente |
| HU-15 | MVP | 8 | backbone-service | Pendiente |
| HU-16 | MVP | 5 | backbone-service | Pendiente |
| HU-17 | MVP/F2 | 2 | backbone-service | Pendiente |
| HU-18 | MVP | 5 | backbone-app | Pendiente |
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

## 4. Requisitos funcionales

Prioridad: **MVP** = fase 1, **F2** = fase 2 (uso diario), **F3** = fase 3 (pulido y rendimiento).

### 4.1 Trabajos

| ID | Requisito | Prioridad |
|---|---|---|
| RF-01 | Crear, editar, duplicar y eliminar trabajos. Al eliminar, el repositorio del NAS **no** se borra; se pregunta aparte. | MVP |
| RF-02 | Un trabajo tiene: nombre, uno o más orígenes (ruta + alias), destino UNC, exclusiones, programación, retención, condiciones y activado/desactivado. | MVP |
| RF-03 | Validación: orígenes con ruta local normalizada (§7.5 "Ruta de origen", `SourcePathInvalid`), que existen y son NTFS local, comprobados con la resolución controlada de §8.3, que nunca sigue un enlace que no lleve a un volumen local (ADR-20); alias únicos y válidos como nombre de carpeta; destino es UNC `\\servidor\recurso[\ruta]`; un destino no puede estar dentro de un origen ni ser usado por otro trabajo; **ser usado por otro trabajo** (`DestinationUsedByOtherJob`) incluye que un destino esté **anidado** en el de otro, en cualquier sentido (`\\nas\b` frente a `\\nas\b\pc` o al revés), y no solo que coincida: se comparan los destinos ya normalizados por `UncPaths.TryParse` (§7.5 A-3) por segmentos completos, sin distinguir mayúsculas (ordinal), sin resolver nombres de servidor ni direcciones, porque un repositorio quedaría dentro de otro y el mantenimiento del de fuera (limpieza, verificación) recorrería o tocaría los archivos del de dentro (decisión del coordinador en HU-07); se rechazan como destino el propio equipo o *loopback*, las rutas `\\?\` y `\\.\` y los recursos administrativos (`C$`, `ADMIN$`, `IPC$`) (§7.5). Se rechaza (`SourceNotAllowed`) un origen que sea la carpeta de datos `%ProgramData%\Backbone` o esté dentro de ella, comparando rutas finales tras resolver puntos de reanálisis con la resolución controlada (§8.3, ADR-16, ADR-20). Límites de longitud y número en §7.5. | MVP |
| RF-04 | Exclusiones con patrones *glob* (`*.tmp`, `**/node_modules/**`, `Documentos/Privado/**`); semántica y validación en "Patrones de exclusión" (abajo). Exclusiones por defecto: `desktop.ini`, `thumbs.db`, `~$*`, `*.tmp`, `$RECYCLE.BIN`, `System Volume Information`. Además, cuando un origen contiene la carpeta de datos `%ProgramData%\Backbone` (p. ej. `C:\` o `C:\ProgramData`), esta se excluye **siempre**: no es un patrón de la lista, no se puede quitar y el editor de exclusiones lo indica con una nota fija (§8.3, ADR-16). | MVP |
| RF-05 | Exclusión por tamaño máximo de archivo (opcional). | F2 |
| RF-06 | No se siguen los **enlaces**: puntos de reanálisis cuya etiqueta es sustituto de nombre (*junctions*, enlaces simbólicos, puntos de montaje). Se omiten con `ReparsePointSkipped` y no se conserva nada de ellos. Los demás puntos de reanálisis de la lista blanca se copian **por su contenido**: archivos de OneDrive ya descargados, WOF/CompactOS y Data Deduplication. Los de cualquier otra etiqueta se omiten con aviso como ilegibles y conservan su versión anterior (RF-25). Los marcadores de OneDrive "solo en la nube" (`RECALL_ON_OPEN`, `RECALL_ON_DATA_ACCESS`, u `OFFLINE` con etiqueta de nube o HSM), tanto en vivo como en la instantánea VSS, **no se abren ni se descargan**: se omiten con `CloudOnlySkipped` y aviso, y conservan la versión del snapshot padre si existía. Lo mismo pasa con un archivo que pasa a estar solo en la nube. Las **carpetas** de nube no pobladas del todo se omiten igual, conservando lo que tenían debajo en el padre. Detalle en §8.2 ("Clasificación por etiqueta" y "Archivos y carpetas solo en la nube") y ADR-19. | MVP |
| RF-07 | Vista previa de exclusiones (HU-08, criterio 5): cuántos archivos y bytes excluye cada patrón. Cuando se ofrezca por IPC, el servicio la calcula con los **mismos orígenes validados** (§8.3: ruta final, raíz VSS o en vivo según corresponda) y las **mismas exclusiones obligatorias** (ADR-16) que una ejecución, y lee con el mismo `SourceFileSystem`. Así no lee nada que una copia no leería ni recorre la carpeta de datos. | F2 |

**Patrones de exclusión (RF-04, HU-08).** Semántica que implementa el motor (`ExclusionPattern` y `ExclusionMatcher` en `Backbone.Core/Engine`):

- **Contra qué se compara**: la ruta del snapshot (§6.3, `p`), que empieza por el alias del origen (`Documentos/Facturas/enero.pdf`). Se compara sin distinguir mayúsculas (ordinal). En el patrón, `\` equivale a `/`.
- **Normalización Unicode**: el patrón y cada nombre se normalizan a **NFC** antes de compararlos. Así, `Canción` escrito con `ó` precompuesto (U+00F3) coincide con un nombre que lo tiene descompuesto (`o` + U+0301), y al revés. NTFS no normaliza los nombres, y la misma letra puede llegar de las dos formas según el programa que creó el archivo o el teclado con que se escribió el patrón.
- **Comodines**: `*` es cualquier secuencia de caracteres dentro de un segmento, también vacía, y `?` es exactamente un carácter (unidad UTF-16, tras normalizar). Ninguno de los dos cruza `/`. `**` como segmento completo equivale a cero o más carpetas; dentro de un segmento (`a**b`) equivale a `*`, y dos `**` seguidos equivalen a uno.
- **Sin `/`** (`*.tmp`, `desktop.ini`, `$RECYCLE.BIN`): se compara con el **nombre** de cada elemento a cualquier profundidad (equivale a `**/patrón`) y vale para archivos y carpetas.
- **Con `/`**: va **anclado** a la raíz del snapshot, así que empieza por el alias (`Documentos/Privado/**`) o por `**` (`**/node_modules/**`).
- **`X/**` y `X/`** excluyen solo la **carpeta** `X` con todo su contenido, que no se recorre. No excluyen un archivo que se llame `X`.
- La **raíz de un origen** (el alias) nunca se excluye. Una carpeta excluida se excluye con todo lo que tiene debajo.
- `[` y `]` son **literales**. No hay clases de caracteres, ni negación, ni escape.
- Lo excluido **no se recorre, no se abre, no genera avisos, no cuenta como omitido** y no aparece en el snapshot. Tampoco se conserva del padre lo que excluyen ahora los patrones cuando una carpeta es ilegible (RF-25), para que añadir un patrón tenga efecto en cualquier caso. Las exclusiones obligatorias (ADR-16) son independientes de esta lista y se aplican siempre.
- **Validación** (`ExclusionPattern.Validate`; el servicio la traduce a `ValidationFailed` con `ExclusionPatternInvalid`, §7.5):
  - Como mucho 256 patrones de hasta 512 caracteres (`ContractLimits.MaxExclusions`, `MaxExclusionPatternLength`).
  - Se rechazan los patrones vacíos o de solo espacios y los que tienen espacios al principio o al final.
  - Se rechazan los que empiezan por `/` o `\`.
  - Se rechazan los que contienen `: " < > |`, caracteres de control o suplentes sueltas.
  - Se rechazan los que tienen segmentos vacíos (`a//b`) o segmentos `.` o `..`.
  - Se rechaza la **lista entera** si supera el **presupuesto de coste** (`ExclusionMatcher.MaxListCost`), que acota el coste de evaluarla en el peor caso (ver "Coste"; valores fijados por backbone-core en HU-08):
    - **Presupuesto** por lista: `MaxExclusions × (MaxExclusionPatternLength + 2)` = 256 × 514 = **131 584**.
    - **Coste de un patrón**: la suma del coste de sus segmentos, más 1 por cada `**`, incluido el `**` implícito de un patrón de un solo segmento (`*.tmp` equivale a `**/*.tmp`). El `/**` final (`X/**`) no cuenta.
    - **Coste de un segmento**: 1 + longitud del trozo antes del primer `*` + longitud del trozo tras el último `*` + 255 × ⌈k/64⌉, donde k es la longitud del trozo **más largo entre dos `*`** (0 si no hay ninguno). El último término refleja que un trozo intermedio se busca en un nombre de hasta 255 caracteres.
    - Si la suma de la lista supera 131 584, se rechaza entera con `ExclusionPatternError.TooComplex` (índice de patrón -1). Una lista sin texto entre dos `*` en ningún patrón se admite siempre (con los límites de número y longitud su coste no llega al presupuesto).
    - La comparación se hace tras normalizar patrón y nombre a NFC; un nombre con suplentes sueltas, que no se puede normalizar, se compara tal cual.
  - El servicio valida con `ExclusionMatcher.ValidateList` (la lista entera, no patrón a patrón) y lo traduce a `ValidationFailed` con `ExclusionPatternInvalid` (§7.5): en `Exclusions[i]` si falla el patrón `i`, y en `Exclusions` (sin índice) si la lista es demasiado compleja.
  - Un patrón no válido, o una lista demasiado compleja, hace que la copia se rechace antes de leer nada.
- **Coste**: sin retroceso (§7.5 M-2). No se traduce a expresiones regulares: cada segmento se compara con un algoritmo voraz por trozos entre `*`, como mucho O(n·m) por segmento (n, longitud del nombre; m, la del segmento del patrón), y los `**` se evalúan como un autómata de estados, un segmento cada vez. El presupuesto de coste de la validación acota la suma en el peor caso, para que un trabajo con muchos patrones largos no multiplique el tiempo de recorrido de un origen grande.

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
| RF-25 | Archivos que no se pueden leer (bloqueados sin VSS, sin permiso, ruta inválida) se omiten, se listan en la ejecución y esta termina "con avisos". El snapshot se genera igualmente y conserva la versión anterior de esos archivos si existía. Igual se tratan los archivos solo en la nube (`CloudOnlySkipped`), los puntos de reanálisis de etiqueta fuera de la lista blanca y las lecturas que superan su plazo (RF-06, §8.2). | MVP |
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

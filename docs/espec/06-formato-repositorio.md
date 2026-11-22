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
  - En logs, mensajes de excepción y eventos se recortan a **200 caracteres** (con `…` detrás) y se escriben como `\uXXXX` los caracteres de control (categoría Unicode `Cc`), las suplentes sueltas, U+2028, U+2029 y **toda la categoría Unicode `Cf` (formato)**: controles bidireccionales (U+200E, U+200F, U+061C, U+202A–U+202E, U+2066–U+2069), espacios y uniones de anchura cero (U+200B–U+200D, U+2060), BOM (U+FEFF), guion blando (U+00AD), etiquetas (U+E0000–U+E007F) y el resto. La categoría se decide por punto de código (`Rune.GetUnicodeCategory`), así que un carácter `Cf` fuera del plano básico se escribe como sus dos unidades UTF-16 (`\uXXXX\uXXXX`). Así un repositorio hostil no puede inundar los registros, falsear líneas, ocultar caracteres ni reordenar visualmente el texto. En Core lo hace `Untrusted.Describe`; el servicio y la app usan `UntrustedText.Describe` de `Backbone.Contracts` (§7.5), con el mismo comportamiento, para todo texto leído del repositorio o del NAS (nombres de recurso o de equipo, mensajes del sistema con valores leídos) que registren o muestren.
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
- `parentId` siempre está presente: `null` explícito en el primer snapshot del trabajo. Cómo se elige, en "Snapshot padre" (abajo).
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
- Si la copia llega a más de 5 000 000 de entradas, o el manifiesto superaría 16 GiB, la copia **falla** y no se publica snapshot (el anterior sigue siendo el último restaurable). Los 16 GiB son el tamaño del **manifiesto** (el listado de entradas), no de los datos copiados: en la práctica el límite que se alcanza es el de entradas, y la solución es dividir el trabajo en varios. El escritor (y el motor, al contar entradas) lanza `RepositoryException` con `RepositoryErrorKind.SnapshotTooLarge` y un mensaje técnico que indica el límite superado; el servicio lo traduce a `BackboneErrorCode.SnapshotTooLarge` (contrato 1.2, §7.4) en `RunRecord.ErrorCode` de la ejecución `Failed` (ADR-17).

**Snapshot padre** (ADR-17)

- El padre de una copia es el snapshot **más reciente** (por identificador, en orden ordinal descendente) cuyo `jobId` es el del trabajo. Sus entradas permiten no volver a leer los archivos sin cambios (RF-23) y conservar la versión anterior de los que no se pueden leer (RF-25).
- **Fecha futura**: no se acepta como padre un snapshot cuya fecha (la de su identificador o su `startedUtc`) sea posterior al `startedUtc` de la copia en curso más la tolerancia de reloj (`StaleAfter`, 15 minutos, la misma de §6.4.4). Se ignora (se registra en el log, saneado) y se sigue buscando hacia atrás; si la fecha del identificador ya la supera, se descarta sin leer su cabecera. Así un snapshot plantado con fecha lejana, o de un equipo con el reloj adelantado, no queda como padre de todas las copias siguientes.
- **Padre dañado**: si la cabecera de algún snapshot que haya que leer para elegir el padre no se puede leer o no es válida (no se puede descartar que sea del trabajo), o el manifiesto del padre elegido resulta corrupto al leer sus entradas, la copia **falla** con `RepositoryException` `Corrupt` (`RepositoryCorrupt` en el servicio) y no publica nada. No se salta con un aviso ni se cambia a un padre más antiguo: el repositorio debe verificarse o repararse antes (HU-29). Un error de E/S al leerlo es un error de E/S, no `Corrupt`.
- **Hashes heredados**: un archivo sin cambios (tamaño, fecha y atributos iguales, RF-23) reutiliza el `h` de su entrada en el padre sin volver a leerlo. Por eso un `h` alterado en el padre con un valor bien formado se **propaga** a los snapshots siguientes hasta que el archivo cambie (R6).

**Entradas**

- `p`: ruta relativa con `/` como separador, prefijada por el alias de un origen que figura en `sources`. Un archivo tiene al menos dos segmentos (`alias/nombre`).
  - **Única** dentro del snapshot con comparación **ordinal** (distingue mayúsculas): `A.txt` y `a.txt` (posibles en carpetas con distinción de mayúsculas) son dos entradas. Si al restaurar dos entradas solo difieren en mayúsculas y el destino no las distingue, la segunda se trata como **conflicto** (RF-32).
  - **Reglas de segmento**: cada segmento cumple `SnapshotPaths.IsValidSegment` (§7.5): no vacío; distinto de `.` y `..`; sin `\ / : * ? " < > |` ni caracteres de control; sin punto ni espacio final; sin nombres de dispositivo reservados, tampoco con extensión; como mucho 255 caracteres; **UTF-16 válido** (sin suplentes sueltas, que NTFS admite pero UTF-8 no puede representar; lo comprueba también `IsValidSegment` desde el contrato 1.2).
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

**Integridad**: el manifiesto no lleva suma de comprobación propia. El lector detecta los errores de estructura (JSON, rutas, hashes mal formados, estadísticas), pero no un `h` o un `s` alterados con un valor bien formado: eso solo lo detecta la verificación completa (HU-26), cuando el objeto no existe o no coincide en tamaño, y ni siquiera ella si `h` pasa a apuntar a otro objeto existente del mismo tamaño. Como las copias incrementales heredan el `h` del padre para los archivos sin cambios ("Snapshot padre"), un `h` alterado se propaga a los snapshots siguientes. Ver R6.

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

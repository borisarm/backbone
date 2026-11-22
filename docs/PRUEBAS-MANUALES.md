# Pruebas manuales

Casos que no se pueden automatizar dentro de la batería local. Cada caso indica la historia y el criterio que cubre.

## PM-HT01-01 · Ejecución real de la CI en GitHub Actions

**Cubre:** HT-01, criterio 4 (y, de forma indirecta, el criterio 1 en un runner limpio).

**Por qué es manual:** el flujo `.github/workflows/ci.yml` solo se puede ejecutar en GitHub tras un push; las pruebas locales (`BuildConfigurationTests.CI_workflow_...`) solo comprueban su contenido.

**Pasos**
1. Subir la rama `historia/ht-01` (o lanzar el flujo con *Run workflow*, `workflow_dispatch`).
2. En la pestaña *Actions*, abrir la ejecución "CI" y comprobar que el trabajo "Compilar y probar" corre en `windows-latest`.
3. Comprobar que los pasos Restaurar, Compilar (Release, `-warnaserror`) y Compilar la app para ARM64 terminan en verde.
4. Comprobar que el paso de pruebas ejecuta las pruebas con el filtro `Category!=Integration&Category!=Perf` y que ninguna prueba `Integration` aparece en el `.trx`.
5. Abrir el resumen de la ejecución y comprobar que aparece la tabla de cobertura de `Backbone.Core` (paso "Informe de cobertura").
6. Descargar los artefactos `cobertura-backbone-core`, `instalador-x64` y `resultados-pruebas`; comprobar que el primero contiene `index.html`/`Cobertura.xml`, que el segundo contiene un `.msi` y que el tercero contiene los `.trx`.

**Resultado esperado:** ejecución en verde con los tres artefactos y el resumen de cobertura. Si el informe de cobertura falla por no haber `coverage.cobertura.xml` (aún sin pruebas de Core con instrumentación), anotarlo como defecto para backbone-architect.

**Estado:** pendiente (requiere push).

## PM-HT04-01 · Recurso Samba real (OpenMediaVault): escritura atómica, renombrado sin sobrescribir y nombres

**Cubre:** HT-04, criterios 2 y 4 (§6.1, §6.4.1, §6.4.4); riesgos Q1 y R7.

**Por qué es manual:** las pruebas automáticas usan disco local y memoria; el comportamiento del renombrado, de los nombres y de los relojes sobre SMB/Samba real solo se ve contra un NAS. Requiere un recurso SMB de OpenMediaVault con una carpeta vacía y dos equipos Windows 11 (A y B) con la hora sincronizada.

**Pasos**
1. En el equipo A, desde una consola de pruebas del motor (o la app cuando exista), inicializar un repositorio en `\<nas>\<recurso>\prueba-ht04`.
2. En el NAS, comprobar que existen `repo.json`, `objects`, `snapshots`, `locks`, `tmp` en minúsculas y que `repo.json` está en UTF-8 sin BOM y con sangría.
3. Escribir una copia de 2 GB y, a mitad, desconectar el cable de red del equipo A durante 30 s y reconectarlo. Comprobar que la operación falla o reintenta, que en `objects\` y `snapshots\` no hay archivos incompletos (solo en `tmp\`) y que `tmp\` se limpia en la siguiente limpieza.
4. Desde el equipo B, abrir el mismo repositorio mientras A tiene un bloqueo `shared`: otra copia `shared` debe poder empezar; una limpieza (`exclusive`) debe fallar con "repositorio bloqueado".
5. Matar el proceso de A (Administrador de tareas) con el bloqueo tomado. Comprobar que `locks\<host>-<pid>.lock` queda, que B no puede limpiar durante 15 min y que después sí (y que el bloqueo abandonado desaparece).
6. Crear en el recurso, con otro cliente, un archivo `locks\ñandú 日本.lock` y otro `snapshots\CON.json`; comprobar que Backbone los trata como bloqueo ilegible (exclusivo vigente 15 min por la fecha del archivo) y los ignora al listar snapshots, respectivamente, sin fallar.
7. Comparar la hora de A y B y la del NAS: con un desfase de más de 2 min, anotar si aparecen bloqueos que caducan antes o después de tiempo (riesgo Q1).

**Resultado esperado:** ningún archivo parcial en `objects\` ni `snapshots\`; los bloqueos se comportan como en §6.4.4; los nombres que Backbone no admite se ignoran sin excepción.

**Estado:** pendiente (requiere NAS).

## PM-HT04-02 · Raíz del destino como punto de reanálisis (DFS) y enlaces dentro del repositorio

**Cubre:** HT-04, §6.1 "Puntos de reanálisis" (y la regla de que la raíz no puede serlo); relacionado con R7.

**Por qué es manual:** las pruebas automáticas crean *junctions* y enlaces simbólicos en disco local; una raíz DFS o un recurso con enlaces simbólicos de Samba (`follow symlinks`) solo existe en un entorno de red real.

**Pasos**
1. En un servidor Windows con DFS-N (o en un recurso Samba con `wide links = yes` y un enlace simbólico en la raíz del recurso), crear un destino `\<servidor>\<espacio>\copias` cuya carpeta final sea un vínculo DFS / enlace simbólico a otra carpeta.
2. Intentar crear un trabajo y copiar con ese destino. Anotar el resultado: Backbone debe tratar el repositorio como corrupto (`Corrupt`, "es un punto de reanálisis") y no escribir nada, **o** (si la raíz DFS no se ve como reanálisis desde el cliente SMB) anotar que se acepta y qué hay en el destino final.
3. Con un repositorio válido ya creado, sustituir `objects\ab` en el NAS por un enlace simbólico a otra carpeta del recurso y lanzar una copia: debe fallar con `Corrupt` sin crear archivos en la carpeta enlazada.
4. Repetir con `snapshots\` y `locks\`; después quitar los enlaces y comprobar que el repositorio vuelve a abrirse con normalidad.
5. Anotar si el cliente SMB de Windows expone los enlaces simbólicos de Samba como `ReparsePoint` (si no, la comprobación del motor no los detecta: documentarlo como limitación en la especificación, R7).

**Resultado esperado:** ninguna escritura ni borrado fuera del repositorio; el motivo del fallo es legible y no contiene caracteres de control.

**Estado:** pendiente (requiere DFS/Samba).

## PM-HT04-03 · Rendimiento del manifiesto de 500 000 entradas

**Cubre:** HT-04, criterio 3.

**Por qué es manual:** la prueba `HT04_3_Manifest_with_500000_entries_is_written_and_read_under_400_MB` es `Category=Perf` y no corre en la CI por defecto.

**Pasos**
1. `dotnet test Backbone.slnx --filter "Category=Perf" -c Release`.
2. Anotar duración y memoria máxima que informa la prueba.

**Resultado esperado:** pasa con menos de 400 MB de memoria.

**Estado:** ejecutada por QA en la revisión de HT-04 (ver informe).

## PM-HT03-01 · Usuario real no autorizado ni elevado: acceso denegado por la ACL del pipe

**Cubre:** HT-03, criterio 2 (y el estado "Sin autorización" de §9.1/§9.3 que usa el criterio 5).

**Por qué es manual:** las pruebas automáticas simulan al usuario no autorizado con un token restringido en el mismo proceso; aquí se comprueba con una cuenta de Windows real, otra sesión y el servicio corriendo como LocalSystem, que es lo que aplica la ACL de §7.3 de verdad.

**Preparación**
1. En un equipo de pruebas (o máquina virtual), crear una cuenta local **estándar** (no administradora), p. ej. `prueba-std`, que no figure en `authorizedUsers` de `%ProgramData%\Backbone\config\settings.json`.
2. Instalar el servicio como en PM-HT03-02 (pasos 1 a 3) y comprobar que está en ejecución (`sc.exe query BackboneService` → `RUNNING`).
3. Copiar la app compilada (`src\Backbone.App\bin\...\Backbone.App.exe` y su carpeta) a una ruta que `prueba-std` pueda leer y ejecutar, p. ej. `C:\BackboneApp`.

**Pasos**
1. Iniciar sesión como `prueba-std` (o usar *Ejecutar como otro usuario* sin elevar) y abrir `C:\BackboneApp\Backbone.App.exe`.
2. Esperar a que termine el primer intento de conexión (como mucho unos segundos).
3. Comprobar que la app muestra **de inmediato** la pantalla "Sin autorización" de §9.3, con el comando para añadirse como usuario autorizado, sin pasar antes por "Reconectando" ni "Servicio detenido".
4. Desde una PowerShell **sin elevar** de `prueba-std`, comprobar que el sistema niega la apertura del pipe:
   `[System.IO.Pipes.NamedPipeClientStream]::new('.', 'Backbone.Service.v1', 'InOut').Connect(2000)` → `UnauthorizedAccessException` (acceso denegado).
5. En el log del servicio (`%ProgramData%\Backbone\logs\backbone-YYYYMMDD.log`, leído como administrador) comprobar que no aparece ninguna llamada ni sesión de `prueba-std` (la conexión se rechaza en `CreateFile`, antes de llegar al servidor).
6. Repetir los pasos 1 a 3 con una cuenta **administradora sin elevar** que no esté en `authorizedUsers`: el resultado debe ser el mismo (la ACE de `BUILTIN\Administrators` no se aplica con el grupo en "solo denegar").
7. Opcional: con la app de la cuenta administradora aún abierta, añadirla a `authorizedUsers` (desde una app elevada o editando `settings.json` y reiniciando el servicio) y comprobar que la app pasa sola a conectada en el siguiente reintento (≤ 10 s).

**Resultado esperado:** acceso denegado en el pipe para ambas cuentas; la app muestra "Sin autorización" sin retraso y sigue reintentando; el servicio no registra actividad de esas cuentas.

**Estado:** pendiente.

## PM-HT03-02 · Servicio instalado de verdad: conexión, verificación del servidor y reconexión

**Cubre:** HT-03, criterios 1 y 5; §7.1 A-1 (ADR-12) y la reconexión de §9.1.

**Por qué es manual:** la prueba de integración usa un pipe de nombre aleatorio y un servidor en el proceso de pruebas; la verificación A-1 (PID del SCM, servicio en ejecución, cuenta LocalSystem) y el reinicio por el SCM solo se pueden comprobar con el servicio `BackboneService` registrado y corriendo como LocalSystem.

**Preparación** (hasta que exista el MSI de HU-01; en PowerShell **elevado**)
1. `dotnet publish src\Backbone.Service -c Release -o C:\BackboneService`
2. `sc.exe create BackboneService binPath= "C:\BackboneService\Backbone.Service.exe" start= demand obj= LocalSystem DisplayName= "Backbone — Copias de seguridad"`
3. Asegurarse de que el SID del usuario de pruebas (administrador) está en `authorizedUsers` de `%ProgramData%\Backbone\config\settings.json` y ejecutar `sc.exe start BackboneService`.

**Pasos**
1. Con el usuario de pruebas, abrir la app **sin elevar**. Comprobar que pasa a conectada y que Ajustes → Acerca de muestra las versiones del servicio y del contrato (`GetServiceInfoAsync`).
2. Comprobar el PID: `(Get-CimInstance Win32_Service -Filter "Name='BackboneService'").ProcessId` coincide con el proceso `Backbone.Service.exe` y su cuenta es `LocalSystem` (`StartName`).
3. **Reinicio:** `Restart-Service BackboneService` (elevado). Comprobar que la app muestra "Reconectando" y que, sin tocar nada, vuelve a conectada en pocos segundos; el PID del servicio ha cambiado y la app lo acepta (vuelve a verificar con el nuevo PID).
4. **Parada:** `Stop-Service BackboneService`. Comprobar que la app muestra "Reconectando" y que, unos 8 s después de la pérdida (tres intentos fallidos tras 1 s, 2 s y 5 s), pasa a "Servicio detenido" (§9.3). `Start-Service BackboneService` (o el botón "Iniciar servicio"): la app vuelve a conectar sola en ≤ 10 s, o de inmediato con el botón.
5. **Servidor falso (A-1):** con el servicio detenido, ejecutar como el usuario de pruebas **sin elevar** un proceso que cree un pipe `Backbone.Service.v1` con una ACL que le permita conectar (p. ej. el servicio en modo consola, `dotnet run --project src\Backbone.Service -- --console`, o un `NamedPipeServerStream` en PowerShell). Comprobar que la app **no** pasa a conectada (lo trata como "servicio no disponible") y que el servidor falso no recibe ningún byte: con `NamedPipeServerStream`, `ReadByte()` no devuelve datos antes de que la app cierre la conexión.
6. **Cuenta distinta de LocalSystem (A-1):** `sc.exe config BackboneService obj= "NT AUTHORITY\LocalService"`, arrancar el servicio (puede fallar al usar `%ProgramData%\Backbone`; si arranca) y comprobar que la app lo rechaza como "servicio no disponible". Restaurar con `sc.exe config BackboneService obj= LocalSystem`.

**Limpieza:** `sc.exe stop BackboneService`, `sc.exe delete BackboneService` y borrar `C:\BackboneService`.

**Resultado esperado:** la app conecta sin elevar, verifica el servidor antes de enviar nada, reconecta tras reiniciar el servicio, pasa a "Servicio detenido" tras ~8 s si no vuelve y rechaza servidores que no sean el proceso de `BackboneService` como LocalSystem.

**Estado:** pendiente.

## PM-HT03-03 · Servicio instalado: `settings.json` plantado con propietario usuario o como *junction*

**Cubre:** HT-03 I-1; §8.2 (ADR-14), §8.3 "Contenido" y evento 1002 de §8.5.

**Por qué es manual:** las pruebas automáticas (`TrustedFileTests`, `TrustedFileElevatedTests`, `StartupServiceTests`) comprueban el rechazo con dobles del Visor de eventos y del SCM; aquí se comprueba con el servicio registrado que se escribe el evento real y que el SCM aplica la recuperación.

**Preparación** (PowerShell **elevado**)
1. Instalar el servicio como en PM-HT03-02 (pasos 1 y 2 de la preparación).
2. Si el origen `Backbone` no existe en el Visor de eventos (aún sin MSI): `[System.Diagnostics.EventLog]::CreateEventSource('Backbone', 'Application')`.
3. Configurar la recuperación: `sc.exe failure BackboneService reset= 86400 actions= restart/60000/restart/60000` y `sc.exe failureflag BackboneService 1` (para que también cuente una salida con código distinto de cero).
4. Arrancar una vez (`sc.exe start BackboneService`) para que exista `%ProgramData%\Backbone\config\settings.json`, y detenerlo (`sc.exe stop BackboneService`). Guardar una copia del archivo como administrador.

**Pasos**
1. **Propietario usuario:** borrar `settings.json` y volver a crearlo desde una cuenta estándar o tomando la propiedad para ella, p. ej. (elevado) `icacls "$env:ProgramData\Backbone\config\settings.json" /setowner "<equipo>\prueba-std"`. Comprobar con `Get-Acl` que el propietario es ese usuario.
2. `sc.exe start BackboneService`. Comprobar que el servicio no llega a `RUNNING` y que en el registro Aplicación aparece un evento **1002** de origen `Backbone`: `Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'Backbone'; Id = 1002 } -MaxEvents 1 | Format-List TimeCreated, Message`. El mensaje indica que la carpeta de datos no es segura (entrada con propietario no fiable, §8.3) o que `settings.json` se ha rechazado (`UntrustedOwner`, §8.2), y no contiene el contenido del archivo.
3. Comprobar la recuperación: `sc.exe queryex BackboneService` muestra el servicio detenido y, pasado el minuto configurado, el SCM lo vuelve a intentar (nuevo evento 1002 y, en el registro Sistema, el evento 7031/7034 del SCM con la acción de recuperación).
4. **Junction:** detener el servicio (`sc.exe config BackboneService start= disabled` para cortar la recuperación y después `start= demand`), borrar `settings.json` y crear en su lugar un *junction*: `cmd /c mklink /J "%ProgramData%\Backbone\config\settings.json" C:\Windows\Temp`. Repetir los pasos 2 y 3: evento **1002** (punto de reanálisis) y nada escrito en `C:\Windows\Temp`.
5. Repetir el paso 4 con un enlace duro: `cmd /c mklink /H "%ProgramData%\Backbone\config\settings.json" <archivo de otra carpeta del mismo volumen>`: evento **1002** (`HardLinked`).

**Limpieza:** detener el servicio, quitar el *junction* o el enlace (`rmdir` / `del`), restaurar la copia de `settings.json` como administrador y comprobar que el servicio vuelve a arrancar (evento 1000). Desinstalar como en PM-HT03-02.

**Resultado esperado:** en los tres casos el servicio no arranca, registra 1002 sin leer el archivo plantado, no escribe fuera de la carpeta de datos y el SCM aplica la recuperación.

**Estado:** pendiente.

## PM-HT03-04 · Servicio instalado: raíz `%ProgramData%\Backbone` con propietario usuario

**Cubre:** HT-03 I-1; RNF-07, §8.3 "Raíz de propietario no fiable" (ADR-13) y evento 1003 de §8.5.

**Por qué es manual:** las pruebas de `DataDirectoryGuardTests` usan carpetas temporales; aquí se comprueba sobre la raíz real, con el servicio como LocalSystem y el evento escrito en el Visor de eventos.

**Preparación** (PowerShell **elevado**): servicio instalado y origen del Visor de eventos creado como en PM-HT03-03 (preparación, pasos 1 a 3), con el servicio detenido.

**Pasos**
1. Con el servicio detenido, renombrar la carpeta actual para conservarla: `Rename-Item "$env:ProgramData\Backbone" Backbone.orig`.
2. Crear de nuevo `%ProgramData%\Backbone` con propietario un usuario estándar: crearla como `prueba-std` (sin elevar, `New-Item -ItemType Directory "$env:ProgramData\Backbone"`; `%ProgramData%` permite a Usuarios crear carpetas) o, elevado, `icacls "$env:ProgramData\Backbone" /setowner "<equipo>\prueba-std"`. Opcional: dejar dentro un archivo `config\settings.json` creado por ese usuario.
3. `sc.exe start BackboneService`. Comprobar que el servicio llega a `RUNNING`.
4. Comprobar el evento **1003** (aviso): `Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'Backbone'; Id = 1003 } -MaxEvents 1 | Format-List TimeCreated, Message`. El mensaje cita la ruta apartada `%ProgramData%\Backbone.untrusted-<guid>`.
5. Comprobar que existe `%ProgramData%\Backbone.untrusted-<guid>` con el contenido plantado intacto (nada borrado ni leído) y que la nueva `%ProgramData%\Backbone` tiene propietario Administradores y la DACL protegida de RNF-07 (`Get-Acl "$env:ProgramData\Backbone" | Format-List Owner, AccessToString, AreAccessRulesProtected`).
6. Comprobar que el servicio arranca con la configuración por defecto (sin usuarios autorizados): la app del usuario de pruebas muestra "Sin autorización".

**Limpieza:** detener el servicio, borrar la nueva carpeta y la apartada, `Rename-Item "$env:ProgramData\Backbone.orig" Backbone` y desinstalar como en PM-HT03-02.

**Resultado esperado:** la raíz de propietario no fiable se aparta sin tocarla ni corregirla en el sitio, se crea otra segura, se registra 1003 con la ruta apartada y el servicio arranca fallando cerrado.

**Estado:** pendiente.

## PM-HT03-06 · Servicio instalado: raíz como *junction* y DACL canónica aceptada al reiniciar

**Cubre:** HT-03 I-1; §8.3 (raíz que es punto de reanálisis; archivos con la DACL canónica de `TrustedFile` aceptados sin "corregirlos"), evento 1002 y criterio 4 de la revisión (el servicio no reescribe lo que ya es seguro).

**Por qué es manual:** `DataDirectoryGuardTests` usa carpetas temporales y un Visor de eventos simulado; aquí se comprueba con la raíz real y el servicio como LocalSystem.

**Preparación** (PowerShell **elevado**): servicio instalado y origen del Visor de eventos creado como en PM-HT03-03 (preparación, pasos 1 a 3), con el servicio detenido y una copia de `%ProgramData%\Backbone` guardada.

**Pasos**
1. **DACL canónica:** con el servicio arrancado al menos una vez y una escritura de `settings.json` hecha (p. ej. añadiendo un usuario autorizado desde la app elevada), detenerlo y anotar `Get-Acl "$env:ProgramData\Backbone\config\settings.json" | Format-List Owner, AccessToString, AreAccessRulesProtected`. Arrancar de nuevo (`sc.exe start BackboneService`).
2. Comprobar que llega a `RUNNING` **sin** eventos 1002 ni 1003 nuevos y que la salida de `Get-Acl` es idéntica a la anotada (no se ha "corregido" el archivo) y que `Get-Acl "$env:ProgramData\Backbone"` conserva propietario Administradores y reglas protegidas.
3. **Raíz como *junction*:** detener el servicio, `Rename-Item "$env:ProgramData\Backbone" Backbone.orig`, crear `New-Item -ItemType Directory C:\BackboneJunctionTarget` (con la copia de la carpeta original dentro, también con DACL segura) y `cmd /c mklink /J "%ProgramData%\Backbone" C:\BackboneJunctionTarget`.
4. `sc.exe start BackboneService`. Comprobar que **no** llega a `RUNNING` y que aparece un evento **1002** que cita la carpeta de datos; que el contenido de `C:\BackboneJunctionTarget` no ha cambiado (fechas y DACL) y que no se ha creado ni apartado nada (`Get-ChildItem "$env:ProgramData" -Filter "Backbone.untrusted-*"` vacío).

**Limpieza:** detener el servicio, `rmdir "%ProgramData%\Backbone"` (quita solo el *junction*), borrar `C:\BackboneJunctionTarget`, `Rename-Item "$env:ProgramData\Backbone.orig" Backbone`, y desinstalar como en PM-HT03-02.

**Resultado esperado:** un archivo con la DACL canónica se acepta tal cual; una raíz que es un *junction* nunca se sigue ni se corrige: el servicio no arranca y registra 1002.

**Estado:** pendiente.

## PM-HT03-05 · Pruebas automáticas que requieren elevación

**Cubre:** HT-03, criterios con propietario Administradores (§8.2 y §8.3): `TrustedFileElevatedTests` (`Backbone.IntegrationTests`) y las pruebas elevadas de `DataDirectoryGuardTests` (`Backbone.Service.Tests`).

**Por qué es manual:** la CI y la batería local corren sin elevar; estas pruebas se omiten (`Skip`) si el proceso no es administrador elevado, porque solo así se puede asignar Administradores como propietario.

**Pasos** (PowerShell **elevado**, en la raíz del repositorio)
1. `dotnet test Backbone.slnx --filter "Category=Integration"`. Comprobar que `TrustedFileElevatedTests` se ejecutan (no aparecen como omitidas) y pasan.
2. `DataDirectoryGuardTests` no llevan `Category=Integration` (se omiten solas sin elevar): ejecutarlas también elevado con `dotnet test tests\Backbone.Service.Tests --filter "FullyQualifiedName~DataDirectoryGuardTests"` y comprobar que ninguna queda omitida y que pasan.

**Resultado esperado:** todas pasan, ninguna omitida.

**Estado:** pendiente.

## PM-HU18-01 · Icono de la bandeja: cuatro estados y texto emergente

**Cubre:** HU-18 criterio 1 (RF-50). La lógica de estado, texto y dibujo está probada de forma automática (`Backbone.App.Tests/Tray`); aquí se comprueba lo que solo se ve en pantalla.

**Por qué es manual:** el icono nativo, su animación y el texto emergente los dibuja el Explorador; no se pueden verificar sin pantalla.

**Preparación:** servicio en marcha (`dotnet run --project src/Backbone.Service -- --console`) con un trabajo activado; app con `dotnet run --project src/Backbone.App -p:Platform=x64`. Si el icono no está visible, arrastrarlo desde el desbordamiento de la bandeja.

**Pasos**
1. Servicio y trabajo correctos: el icono es **verde con una marca**; al pasar el ratón el texto dice el estado y "Última copia correcta: …" (hoy HH:mm, o fecha completa si fue otro día).
2. Lanzar una copia (menú "Copiar todo ahora"): el icono pasa a **azul con tres puntos que giran** mientras dura, sin parpadeos ni consumo de CPU apreciable; al terminar vuelve a verde.
3. Provocar un aviso (p. ej. archivo bloqueado en el origen) o un trabajo desactualizado: icono **amarillo con "!"**.
4. Provocar un error (destino inaccesible): icono **rojo con aspa**.
5. Comprobar en tema claro, oscuro, **alto contraste** y escalados 100 %, 150 % y **200 %**: el icono se ve nítido y se distingue cada estado también por la forma (no solo por el color).
6. Con **Narrador** (Win+Ctrl+Intro) mover el foco al icono (Win+B, flechas): lee "Backbone" con el estado y la última copia correcta.
7. Cambiar el idioma de visualización a inglés y reiniciar la app: los textos del tooltip y del menú están en inglés.

**Resultado esperado:** cuatro iconos distinguibles, tooltip correcto en ambos idiomas, Narrador lee el texto emergente.

**Estado:** pendiente.

## PM-HU18-02 · Menú contextual de la bandeja

**Cubre:** HU-18 criterio 2.

**Por qué es manual:** menú nativo del shell y foco; no hay UI automatizable sin pantalla.

**Pasos**
1. Botón derecho en el icono (y también: foco en el icono con Win+B y tecla Menú / Mayús+F10): aparece el menú con **Copiar todo ahora, Pausar (próximamente), Abrir Backbone, Salir**; "Pausar" aparece deshabilitado (F2).
2. Clic fuera o Esc: el menú se cierra y no queda ningún elemento colgado; repetir 5 veces.
3. "Copiar todo ahora" con dos trabajos activados: ambos pasan a ejecutarse/encolarse; con el servicio detenido está deshabilitado.
3b. Con un trabajo ya en ejecución y otro en reposo, "Copiar todo ahora": solo se lanza el que estaba en reposo, sin mensajes de error por el que ya corría. Con el menú abierto mientras el servicio se detiene, elegir "Copiar todo ahora" no produce errores ni cuelgues.
3c. Con el idioma en inglés y en español, pasar el ratón sobre el icono en estado de pausa/servicio detenido: el texto emergente no aparece cortado a mitad de palabra.
4. Teclas de acceso: con el menú abierto, T, P, A, S activan Copiar todo ahora, (Pausar deshabilitado), Abrir, Salir (en inglés: A, P, O, X).
5. Con Narrador, cada elemento se lee con su nombre y el estado "no disponible" cuando está deshabilitado.
6. Comprobar el aspecto del menú en tema oscuro y alto contraste (es un menú nativo: puede seguir el tema del sistema o no; anotar el resultado).

**Resultado esperado:** menú con los cuatro elementos y su accesibilidad correcta.

**Estado:** pendiente.

## PM-HU18-03 · Cerrar oculta, Salir no detiene el servicio, instancia única

**Cubre:** HU-18 criterios 3 y 4 (§9.1).

**Por qué es manual:** ciclo de vida de ventanas y procesos reales, foco y primer plano.

**Pasos** (con el servicio instalado y en marcha, o en modo consola)
1. Abrir la app (clic en el icono o "Abrir Backbone"). Cerrar la ventana con la X o Alt+F4: la ventana desaparece y también su botón de la barra de tareas; el icono de la bandeja sigue y `Get-Process Backbone.App` muestra el proceso.
2. Clic en el icono: la ventana vuelve al frente. Minimizarla y volver a hacer clic: se restaura.
3. Con la app abierta, lanzar de nuevo `Backbone.App.exe` desde el menú Inicio o el Explorador: **no** aparece una segunda ventana; la existente pasa al frente (también si estaba oculta en la bandeja o minimizada), y solo hay un proceso `Backbone.App`.
4. Arrancar con `Backbone.App.exe --minimized` (así lo lanzará la clave Run del instalador): no aparece ventana, solo el icono.
5. "Salir" en el menú: el icono desaparece de la bandeja (sin icono fantasma al pasar el ratón) y el proceso termina; `Get-Service BackboneService` sigue en `Running` y una copia en curso continúa.
6. Reiniciar el Explorador (`Stop-Process explorer` y esperar): el icono reaparece solo.

**Resultado esperado:** cerrar oculta; Salir cierra solo la app; una sola instancia que se trae al frente.

**Estado:** pendiente.

## PM-HU18-04 · Servicio detenido: icono rojo en menos de 15 s

**Cubre:** HU-18 criterio 5 y §9.1 (reconexión). La temporización está probada con tiempo simulado (`TrayServiceStoppedTests`); aquí se mide con el servicio real.

**Pasos** (PowerShell **elevado**, servicio instalado)
1. App abierta y conectada (icono verde).
2. `Stop-Service BackboneService`; cronometrar hasta que el icono sea rojo con aspa. Debe ser menos de 15 s (esperado: unos 8 a 11 s, pasando antes por amarillo "reconectando").
3. El texto emergente dice "servicio detenido".
4. `Start-Service BackboneService`: en menos de 10 s el icono vuelve a verde/azul y el texto se actualiza.
5. Repetir con la app arrancada con el servicio ya detenido: el icono se pone rojo en pocos segundos.

**Resultado esperado:** rojo en menos de 15 s; recuperación automática.

## PM-HU04-01 · Credencial guardada usada por una ejecución programada con la sesión cerrada

**Cubre:** HU-04, criterio 5 (RF-60, ADR-07); y el criterio 3 sobre una instalación real.

**Por qué es manual:** exige el servicio instalado como LocalSystem, un NAS real y cerrar la sesión de Windows. Las pruebas automáticas (`ShareCredentialProviderTests`) solo comprueban que otra instancia del almacén, sin cliente IPC ni perfil de usuario, obtiene la credencial con DPAPI de ámbito equipo y se la pasa a un doble de la conexión. La conexión SMB real es de HU-05 y la ejecución programada de HU-09/HU-13: este caso no se puede ejecutar completo hasta que existan.

**Pasos**
1. Instalar Backbone. Con la app, guardar usuario y contraseña del NAS para `\\<nas>\<recurso>` (usar una contraseña centinela reconocible, p. ej. `CENTINELA-hu04-<fecha>`).
2. En PowerShell elevado, comprobar que existe un único archivo nuevo en `%ProgramData%\Backbone\credentials\` y que `Get-Acl` muestra propietario `BUILTIN\Administradores` y solo SYSTEM y Administradores con control total, sin herencia.
3. Crear un trabajo con destino en ese recurso y programación diaria a dentro de 10 minutos. Reiniciar el equipo y **no iniciar sesión** (o cerrar la sesión de todos los usuarios).
4. Pasada la hora, iniciar sesión y comprobar en el historial y en el Visor de eventos (origen `Backbone`) que la ejecución se conectó y terminó (2000 o 2001), sin 3001 (credenciales rechazadas) ni `CredentialRequired`.
5. Borrar la credencial desde la app y repetir el paso 3: la ejecución debe fallar con `CredentialRequired` sin intentar conectar (en el NAS, ningún intento de inicio de sesión de la cuenta del equipo).
6. Criterio 3: en PowerShell elevado, `Get-ChildItem $env:ProgramData\Backbone -Recurse -File | Select-String -SimpleMatch 'CENTINELA-hu04' -Encoding utf8` y lo mismo con `-Encoding unicode`: ninguna coincidencia (logs, configuración, historial, credenciales).

**Resultado esperado:** la ejecución programada sin sesión usa la credencial guardada; sin credencial no se conecta; la contraseña no aparece en ningún archivo de `%ProgramData%\Backbone`.

**Estado:** pendiente (requiere NAS, HU-05 y la ejecución programada).

## PM-HU04-02 · Prueba automática de credenciales que requiere elevación

**Cubre:** HU-04, criterios 1 y 3 con la política de producción: `CredentialStoreElevatedTests` (`Backbone.IntegrationTests`).

**Por qué es manual:** sin elevar no se puede asignar Administradores como propietario; la prueba se omite (`Skip`).

**Pasos** (PowerShell **elevado**, en la raíz del repositorio)
1. `dotnet test tests\Backbone.IntegrationTests --filter "FullyQualifiedName~CredentialStoreElevatedTests"`.

**Resultado esperado:** pasa y no aparece como omitida.

**Estado:** superada el 2026-11-16 en PowerShell elevado (1 prueba correcta, 0 omitidas).

## PM-HU13-01 · Copia real a un recurso SMB con rutas largas y exclusión de la carpeta de datos

**Cubre:** HU-13, criterios 7 (RNF-09) y 8 (ADR-16, §8.3).

**Por qué es manual:** exige el servicio instalado, un NAS real con SMB y la instantánea VSS de un volumen real. Las pruebas automáticas usan un origen y un repositorio simulados (`BackupEngineLongPathTests`, `BackupEngineSecurityReviewTests`); la lectura real por handle sin seguir puntos de reanálisis es de `SourceFileSystem` (HU-08/HU-15). No se puede ejecutar hasta que existan el servicio y la conexión SMB (HU-05, HU-08, HU-14).

**Pasos**
1. En un disco local crear `C:\PruebaBackbone` con una ruta de más de 260 caracteres (p. ej. 12 carpetas anidadas de 25 caracteres, con acentos y `ñ`) y un archivo de 0 bytes y otro de unos 10 MiB al final.
2. Con la app, crear un trabajo con origen `C:\PruebaBackbone` y destino en un recurso SMB del NAS. Pulsar "Copiar ahora". Comprobar que termina en `Completed` sin avisos y que en el NAS existe `objects\` con los archivos (la ruta larga no hace falta que exista en el NAS: los objetos se guardan por hash).
3. Restaurar el árbol a otra carpeta (cuando exista HU-30) o inspeccionar el snapshot y comprobar que la ruta larga y los nombres Unicode están intactos.
4. Crear un segundo trabajo con origen `C:\` (o un volumen que contenga `%ProgramData%`) con una contraseña centinela guardada para el NAS. Ejecutarlo hasta el final con la instantánea VSS habilitada.
5. En el repositorio, comprobar que ningún snapshot contiene entradas `ProgramData/Backbone/...` ni `ProgramData/Backbone.untrusted-*`, que no hay avisos por ello, y que `Select-String -SimpleMatch` de la contraseña centinela sobre `objects\` no da coincidencias.
6. Crear un *junction* `mklink /J C:\Datos %ProgramData%\Backbone`, editar la configuración para que un trabajo apunte a `C:\Datos` (sustituyendo el origen tras guardarlo) y lanzar la copia: debe terminar `Failed` con `ValidationFailed` sin copiar nada.

**Resultado esperado:** copias correctas con rutas largas; nada de la carpeta de datos en el NAS; el *junction* se rechaza.

**Estado:** pendiente (requiere servicio, NAS y HU-05, HU-08, HU-14).

## PM-HU07-01 · Servicio instalado: matar el servicio a mitad de guardar `jobs.json`

**Cubre:** HU-07, criterio 5 (§8.2, escritura atómica).

**Por qué es manual:** las pruebas automáticas (`JobStoreTests` y `JobStoreFaultTests`) inyectan fallos en cada paso de la escritura (crear el temporal, escribir, validar el destino, renombrar), comprueban que un lector concurrente ve siempre un archivo completo, que los temporales huérfanos se borran al cargar y que un archivo dañado se aparta como `.bad`, pero no matan de verdad el proceso del servicio instalado como SYSTEM.

**Pasos** (PowerShell **elevado**, servicio instalado y en ejecución)
1. Con la app (o un cliente de pruebas), guardar un trabajo y anotar su nombre.
2. Lanzar en bucle guardados del mismo trabajo cambiando el nombre (p. ej. 200 llamadas a `SaveJobAsync` desde un script con el cliente IPC) y, mientras tanto, ejecutar varias veces `Stop-Process -Name Backbone.Service -Force`; el SCM lo reinicia (RNF-02).
3. Tras cada reinicio, comprobar en el log que el servicio ha arrancado ("Trabajos leídos: 1"), que `GetJobsAsync` devuelve el trabajo con uno de los nombres guardados y que `%ProgramData%\Backbone\config\jobs.json` es JSON válido (`Get-Content … | ConvertFrom-Json`).
4. Comprobar que en `config\` no quedan archivos `.jobs.json.*.tmp` después del arranque y que no ha aparecido ningún `jobs.json.bad-*`.

**Resultado esperado:** el servicio arranca siempre con un `jobs.json` completo (el anterior o el nuevo), sin copias `.bad-*` ni temporales.

**Estado:** pendiente (requiere el servicio instalado, HU-01).

## PM-HU07-02 · Pruebas automáticas de `jobs.json` que requieren elevación

**Cubre:** HU-07, criterio 5 con la política de producción: `JobStoreElevatedTests` (`Backbone.IntegrationTests`): propietario Administradores y DACL de RNF-07 en `jobs.json`, y servicio que no arranca (1002) con un `jobs.json` de propietario usuario.

**Por qué es manual:** sin elevar no se puede asignar Administradores como propietario; las pruebas se omiten (`Skip`).

**Pasos** (PowerShell **elevado**, en la raíz del repositorio)
1. `dotnet test tests\Backbone.IntegrationTests --filter "FullyQualifiedName~JobStoreElevatedTests|FullyQualifiedName~TrustedFileElevatedTests"`.

**Resultado esperado:** pasan y no aparecen como omitidas.

**Estado:** superada el 2026-11-21 en PowerShell elevado sobre `main` (`970669c`+), con `dotnet test Backbone.slnx --filter "Category=Integration"`: 16 correctas y 4 omitidas, todas del NAS (`WindowsShareConnectorTests`, sin variables `BACKBONE_IT_SMB_*`); ninguna omitida por falta de elevación.

## PM-HU07-03 · Validación de orígenes con unidades reales: red, extraíble y no NTFS

**Cubre:** HU-07, criterio 3 (RF-03: "no NTFS, unidades de red").

**Por qué es manual:** las pruebas automáticas simulan el tipo de volumen (`JobValidatorTests`) y solo ejercitan con la API real un disco fijo NTFS (`WindowsLocalFileSystemInspectorTests`); hacen falta una memoria USB, una unidad FAT32/exFAT y una unidad de red asignada.

**Pasos** (servicio instalado, app o cliente IPC)
1. Validar un trabajo con un origen en una memoria USB (`E:\Fotos`): error `SourceNotLocal` en `Sources[0].Path`.
2. Validar un origen en una partición o tarjeta FAT32/exFAT de disco fijo: error `SourceNotNtfs`.
3. Asignar una unidad de red `Z:` (`net use Z: \\nas\fotos`) en la sesión del usuario y validar `Z:\`. Las letras asignadas son de cada sesión de inicio: el servicio (SYSTEM, sesión 0) no ve la `Z:` del usuario y responde `SourceNotFound`; con `--console` en la sesión del usuario, `SourceNotLocal`. Anotar el resultado de los dos.
4. Crear (como administrador) un enlace simbólico de carpeta `mklink /D C:\RedFotos \\nas\fotos` y validar `C:\RedFotos` y `C:\RedFotos\2024`: error `SourceNotLocal` al momento. Con la resolución controlada (ADR-20) el destino del enlace no se abre: en el registro del NAS (`/var/log/samba/` o el panel de OMV) **no** debe aparecer ningún intento de acceso de la cuenta del equipo (`PC$`), y en el log del servicio sí "se rechaza un enlace cuyo destino no es un volumen local". Repetirlo con un *junction* creado como usuario estándar hacia `\RPC Control\x` (p. ej. con `CreateMountPoint` de symboliclink-testing-tools): `SourceNotLocal`, sin actividad de red.
5. Validar `C:\ProgramData::$INDEX_ALLOCATION` y `C:\Users\ana\Documentos.` (punto final): `SourcePathInvalid`. Guardar un trabajo con `c:/Users/ana/Documentos/` y comprobar en `jobs.json` y en la respuesta que la ruta es `C:\Users\ana\Documentos`.

**Resultado esperado:** cada caso con su código junto al campo del origen.

**Estado:** pendiente.

## PM-HU07-05 · Redirection Guard con el servicio instalado

**Cubre:** HU-07, revisión de seguridad (§8.3 "Redirection Guard", ADR-20): defensa complementaria activada al arrancar.

**Por qué es manual:** la mitigación se aplica al proceso del servicio y no puede activarse en el proceso de pruebas (dejaría de seguir los *junctions* que crean las propias pruebas). `ProcessMitigationStartupTests` solo comprueba que se llama al arrancar, antes de `StartupService`, y que un fallo es un aviso.

**Pasos** (PowerShell **elevado**, servicio instalado y en ejecución; un usuario estándar `luis` y un administrador `ana`)
1. En el log del servicio tras arrancar: "Redirection Guard activado". `Get-ProcessMitigation -Id (Get-Process Backbone.Service).Id` muestra la política de *redirection trust* aplicada. Si la versión de Windows no la admite, en el log aparece "No se ha podido activar Redirection Guard" y el servicio sigue funcionando.
2. Como `luis` (sin elevar), crear `mklink /J C:\Users\Public\enlace-luis C:\Users\Public\Documents`. Como `ana`, iniciar una restauración (cuando exista HU-21) con destino `C:\Users\Public\enlace-luis\restaurado`: falla con `RestoreTargetNotWritable` y el Visor de eventos registra el intento bloqueado por RedirectionGuard.
3. Validar como origen `C:\Users\Public\enlace-luis`: la resolución controlada lo sigue ella misma (destino local) y es válido; Redirection Guard no interfiere porque ADR-20 abre los enlaces con `FILE_OPEN_REPARSE_POINT`.
4. Validar como origen `C:\Users\ana\Mis documentos` (junction de compatibilidad creado por el sistema): se resuelve hacia `C:\Users\ana\Documents` y no aparece ningún bloqueo de RedirectionGuard en el Visor.
5. Ejecutar una copia (cuando exista HU-14) de un origen que contenga *junctions* del perfil: siguen apareciendo como `ReparsePointSkipped` (RF-06), sin errores nuevos.

**Resultado esperado:** mitigación activa (o aviso si no se admite); los *junctions* creados por usuarios estándar no se siguen en aperturas del sistema y los del perfil no producen errores.

**Estado:** pendiente (requiere el servicio instalado, HU-01; los pasos 2 y 5, HU-21 y HU-14).

## PM-HU07-04 · Editor de trabajos: crear, alias y errores en línea

**Cubre:** HU-07, criterio 1 (el trabajo aparece en Trabajos y en Inicio), criterio 2 (alias propuesto y editable; error en línea por alias repetido sin distinguir mayúsculas), criterio 3 (cada error junto a su campo) y criterio 4 (la app no duplica reglas). *Nota de numeración: los PM-HU07-01, 02, 03 y 05 son de la parte de servicio y están referenciados desde el código; los casos de interfaz son PM-HU07-04, 06, 07 y 08.*

**Por qué es manual:** la lógica del editor está cubierta por `JobEditorTests`, `JobEditorGapTests`, `JobsAndHomeTests` y `ResourceCoverageTests` (`Backbone.App.Tests`) con un servicio simulado; lo que no se automatiza es `FolderPicker` real, la vista WinUI 3 y la unión con el servicio instalado.

**Pasos** (servicio instalado y en marcha, app WinUI 3, NAS con un recurso `\\nas\copias` y una cuenta válida)
1. Trabajos > Nuevo trabajo. Escribir el nombre "Fotos", pulsar "Añadir carpeta", elegir `D:\Fotos` con el selector del sistema (no hay campo de texto para la ruta). Comprobar que el alias propuesto es `Fotos` y que se puede editar.
2. Escribir el destino `\\nas\copias\fotos`, usuario y contraseña del NAS (la contraseña no se muestra) y pulsar Guardar. El editor se cierra, el trabajo aparece en Trabajos y como tarjeta en Inicio; la contraseña no se conserva en ningún campo al reabrir el editor.
3. Alias duplicado: nuevo trabajo con `D:\Fotos` y `E:\fotos`; dejar los alias `Fotos` y `fotos`. Debe salir un error en línea bajo el segundo alias (no un cuadro de diálogo) y no guardarse. Cambiar el alias a `Fotos2` y volver a guardar: se guarda.
4. Errores en línea, cada uno junto a su campo, sin guardar: nombre vacío (junto al nombre); sin carpetas (junto a la lista); origen inexistente (borrar la carpeta tras elegirla); origen en FAT32/exFAT o USB; unidad de red asignada; destino `C:\copias` (no UNC); destino dentro de un origen (`\\nas\copias\fotos` con un origen que sea ese recurso asignado); destino igual al de otro trabajo existente.
5. Corregir cada error y comprobar que su mensaje desaparece al volver a validar. Con el servicio parado, Guardar muestra un error general de servicio no disponible y el editor sigue usable.

**Resultado esperado:** los mensajes salen de los textos `.resw` (es-ES y en-US) para los códigos del servicio; ninguna regla de validación se evalúa en la app (con el servicio parado no hay mensajes de campo).

**Estado:** pendiente.

## PM-HU07-06 · Editor de trabajos: carpeta de datos de Backbone como origen

**Cubre:** HU-07, criterio 6 (§8.3 "Carpetas protegidas", ADR-13, ADR-16) y los códigos 26 a 29 con texto en es-ES y en-US.

**Por qué es manual:** `JobEditorTests` y `JobEditorGapTests` comprueban el reparto de `SourceNotAllowed` (error, bloquea) y `SourceContainsDataFolder` (aviso, guarda) con el servicio simulado; el cálculo real con rutas finales lo cubren las pruebas del servicio. Aquí se ve el conjunto con el servicio instalado.

**Pasos** (servicio instalado, PowerShell elevado para preparar las carpetas)
1. Como origen elegir `C:\ProgramData\Backbone`: error `SourceNotAllowed` junto a ese origen; Guardar no guarda.
2. Elegir `C:\ProgramData\Backbone\config`: el mismo error.
3. Crear un *junction* `mklink /J C:\Datos\enlace C:\ProgramData\Backbone` y otro hacia `C:\ProgramData\Backbone\config`; elegirlos: el mismo error.
4. Elegir `C:\` y luego `C:\ProgramData`: se guarda y el aviso `SourceContainsDataFolder` aparece junto al origen y la nota fija de que la carpeta de datos se excluye siempre.
5. Crear `C:\ProgramData\Backbone.untrusted-<guid>` (una carpeta real, con un guid cualquiera) y elegirla: `SourceNotAllowed`. Elegir `C:\ProgramData`: aviso, no error.
6. Repetir 1 y 4 con el idioma de Windows en inglés (en-US): los textos están en inglés y no quedan claves sin traducir.

**Resultado esperado:** error junto al origen en 1, 2, 3 y 5; aviso (y guardado) en 4; textos correctos en ambos idiomas.

**Estado:** pendiente.

## PM-HU07-07 · Editor de trabajos: validación lenta, progreso y cancelación

**Cubre:** HU-07, la validación puede tardar hasta 60 s: la interfaz no se bloquea, muestra progreso y permite cancelar.

**Por qué es manual:** `Validation_ShowsProgressAndCanBeCancelledWithoutBlockingUi`, `Save_CancelledDuringValidation_*` y `Dispose_CancelsPendingValidation` comprueban `IsBusy`, la cancelación y que no se guarda nada; no se mide la respuesta real de la ventana ni el plazo de 60 s del servicio.

**Pasos** (servicio instalado)
1. Preparar un origen en una unidad que no responda (p. ej. un disco USB desconectado en caliente o una carpeta de OneDrive sin conexión) o un destino UNC cuyo servidor no contesta (IP inexistente de la subred).
2. En el editor elegir ese origen (o escribir ese destino) y pulsar Guardar.
3. Mientras espera: el anillo de progreso está activo, Guardar y Añadir carpeta están deshabilitados, la ventana se puede mover, redimensionar y cambiar de página sin congelarse.
4. Pulsar Cancelar durante la espera: la validación se interrumpe en menos de 2 s, el anillo se para, no aparece ningún error, no se crea ningún trabajo y la contraseña escrita sigue en su campo.
5. Repetir y dejar que termine (hasta 60 s): se muestra el error del servicio junto a su campo y el editor sigue usable.
6. Cancelar con el editor en reposo lo cierra sin guardar.

**Resultado esperado:** interfaz fluida, progreso visible, cancelación inmediata y sin efectos secundarios.

**Estado:** pendiente.

## PM-HU07-08 · Editor de trabajos: accesibilidad, temas e idiomas

**Cubre:** HU-07, interfaz del editor (§9, RNF de accesibilidad y localización).

**Por qué es manual:** no hay automatización de interfaz WinUI 3 en la batería; `ResourceCoverageTests` solo garantiza que cada código tiene texto en los dos idiomas y que las claves coinciden.

**Pasos** (app WinUI 3 con el editor abierto)
1. Narrador: recorrer el editor con Tab; cada control anuncia su nombre (nombre, carpeta con su ruta, alias, destino, usuario, contraseña, Probar conexión, Guardar, Cancelar). Al aparecer un error en línea, Narrador lo lee sin cambiar el foco (regiones *polite*).
2. Teclado: llegar a todos los controles solo con Tab y Mayúsculas+Tab en orden lógico; Intro activa el botón enfocado; Esc o Cancelar cierra el editor en reposo.
3. Alto contraste (Alt izq.+Mayús izq.+Impr Pant), temas Aqua y Anochecer: texto, bordes de error y foco visibles; el error no se indica solo con color.
4. Escala del 200 % (y texto grande del sistema): no se recorta ningún control, el editor se desplaza y los botones siguen accesibles.
5. Tema claro y oscuro: contraste suficiente en errores, avisos y nota informativa.
6. Repetir 1 y 4 con Windows en español (es-ES) y en inglés (en-US): sin cadenas sin traducir ni cortadas.

**Resultado esperado:** todo operable con teclado y Narrador, legible en alto contraste, 200 %, ambos temas y ambos idiomas.

**Estado:** pendiente.

## PM-HU08-01 · OneDrive real, WOF y puntos de reanálisis: en vivo y desde VSS

**Cubre:** HU-08, criterios 3 y 4 (RF-06, R4, ADR-19, ADR-21, §8.2 "Clasificación por etiqueta", "Archivos y carpetas solo en la nube", "Apertura para leer el contenido" y "Token de lectura de los orígenes").

**Por qué es manual:** necesita OneDrive (o otro proveedor de la Cloud Files API) con una cuenta real, la instantánea VSS de un volumen real y el servicio instalado como LocalSystem. Las pruebas automáticas usan simuladores de `ISourceFileSystem` con atributos y etiquetas fijos.

**Preparación**
1. En una máquina con OneDrive iniciado y "Archivos a petición" activado, crear en la carpeta de OneDrive `PruebaBackbone\` con:
   - `nube.bin`: unos 5 MiB de datos aleatorios. Esperar a que se sincronice y elegir "Liberar espacio" (solo en la nube; atributos con `RECALL_ON_DATA_ACCESS`).
   - `descargado.bin`: datos aleatorios, sincronizado y abierto una vez (descargado, sin fijar).
   - `fijado.bin`: datos aleatorios con "Mantener siempre en este dispositivo".
   - Una subcarpeta `sub\` con un archivo descargado.
2. Fuera de OneDrive, en el mismo origen o en otro, crear:
   - `wof.bin` comprimido con `compact /c /exe:xpress4k wof.bin`;
   - un *junction* `mklink /J enlace C:\Windows`;
   - un enlace simbólico a un archivo.
3. Anotar el SHA-256 (`Get-FileHash`) de `descargado.bin`, `fijado.bin` y `wof.bin`, y el de `nube.bin` antes de liberar espacio.

**Pasos**
1. Crear un trabajo con origen `%OneDrive%\PruebaBackbone` y otro con la carpeta del paso 2. Ejecutarlos **con VSS** ("Copiar ahora").
2. Comprobar en el snapshot y en la ejecución:
   - `descargado.bin`, `fijado.bin`, `sub\…` y `wof.bin` están, y su `h` es el SHA-256 anotado;
   - `nube.bin` no está en el primer snapshot y hay un aviso `CloudOnlySkipped`;
   - `enlace` y el enlace simbólico tienen aviso `ReparsePointSkipped` y no están;
   - los atributos de `nube.bin` siguen con `RECALL_ON_DATA_ACCESS` (no se ha descargado) y OneDrive no muestra actividad de descarga.
3. Descargar `nube.bin`, copiar otra vez y comprobar que ahora está con el SHA-256 anotado. Liberar espacio de nuevo y copiar: la entrada se conserva con el mismo `h` (versión del padre) y hay aviso `CloudOnlySkipped`.
4. Modificar `nube.bin` desde otro equipo o desde la web de OneDrive sin descargarlo en esta máquina, y copiar: no debe aparecer ninguna versión nueva. Comprobar sobre `objects\` que ningún objeto del tamaño de `nube.bin` es todo ceros.
5. Repetir los pasos 1 a 4 **sin VSS**, deshabilitando el servicio de instantáneas o con un origen en un volumen sin VSS.
6. Con el servicio en ejecución, comprobar con Process Monitor que el proceso `Backbone.Service` no genera lecturas de datos sobre `nube.bin`.
7. Registrar el resultado de leer los marcadores de nube **desde VSS**, para confirmar §8.2 "Apertura para leer el contenido", punto 3: ¿abre el segundo handle (reapertura relativa al primero, sin `FILE_OPEN_REPARSE_POINT`) o falla con `STATUS_IO_REPARSE_TAG_NOT_HANDLED`/`ERROR_CANT_ACCESS_FILE`?
   - **`wof.bin`**: en Windows 11 26320 el filtro WOF oculta la etiqueta (`FileAttributeTagInfo` da 0 también con `FILE_OPEN_REPARSE_POINT`), así que el servicio lo trata como archivo **normal** y lo lee del primer handle, sin reapertura ni lista blanca (§8.2). No se espera ningún aviso. Lo que se comprueba es que su `h`, en vivo **y desde VSS**, es el SHA-256 anotado del contenido descomprimido. Si desde VSS no coincide (p. ej. ceros), parar y avisar: el filtro no atiende la instantánea y nada lo detecta por la etiqueta. Anotar también la etiqueta que se ve (con `fsutil reparsepoint query wof.bin` y en el log del servicio, si lo registra) por si otra versión de Windows la expone.
8. **Enlaces duros hacia la carpeta de datos** (ADR-21, revisión del `SourceFileSystem` de HU-08, I-3): con el servicio instalado y al menos una credencial guardada, como **usuario estándar** (sin elevar), en una carpeta de su perfil: `mklink /H enlace.bin C:\ProgramData\Backbone\credentials\<hash>.bin`. Tiene que **fallar** (acceso denegado). Repetirlo con `New-Item -ItemType HardLink` en PowerShell. Si alguna de las dos funciona, parar y avisar: con `SeBackupPrivilege` el servicio leería ese enlace y lo subiría al NAS.
9. **`SeBackupPrivilege`** (ADR-21): crear en el origen `privado.bin` y quitarle la herencia dejando solo al usuario con control total (`icacls privado.bin /inheritance:r /grant:r "%USERNAME%:F"`), de modo que SYSTEM no tenga acceso por la DACL. Copiar en vivo y con VSS: `privado.bin` está, con su SHA-256. Cifrar otro archivo con EFS (`cipher /e cifrado.bin`) y copiar: aviso `ReadError` y no está. Con Process Explorer, comprobar que el token del **proceso** `Backbone.Service` tiene `SeBackupPrivilege` desactivado (o ausente) y, si el instalador ya fija los privilegios (`sc qprivs BackboneService`), que no aparecen `SeDebugPrivilege` ni `SeTakeOwnershipPrivilege`.

**Resultado esperado:** los archivos descargados, los fijados y los WOF se copian con su contenido real; un archivo no descargado nunca se descarga ni se guarda como ceros, en vivo ni desde VSS, y conserva su versión anterior; los enlaces no se siguen; un usuario estándar no puede crear un enlace duro hacia `credentials\`; un archivo cuya DACL excluye a SYSTEM se copia y uno cifrado con EFS da `ReadError`.

**Estado:** pendiente (requiere `SourceFileSystem` de HU-08, servicio instalado y VSS de HU-15).

## PM-HU08-02 · Letra de un origen reasignada a una unidad de red entre la validación y la copia (BAJA-5)

**Cubre:** HU-08, cierre de la parte de servicio (BAJA-5), HU-14 (`ReadPath` = ruta final de la resolución); §8.2 "Apertura relativa" (raíz en vivo), §8.3 "Resolución controlada de los orígenes", puntos 1 y 6, y ADR-20.

**Por qué es manual:** hace falta que la **misma letra** pase de un volumen local a una unidad de red **visible para SYSTEM**, es decir, asignada en la sesión de inicio `0x3e7` que comparten los servicios LocalSystem (§8.2 "Sesión de inicio compartida"), y comprobar en el NAS real que no llega ninguna autenticación ni apertura. Las pruebas automáticas simulan `GetDriveType` y `FileFsDeviceInformation`.

**Preparación** (PowerShell elevado; servicio instalado; NAS de pruebas `192.168.1.163`)
1. Crear y montar un VHDX NTFS de 1 GiB con la letra `V:` (`New-VHD`, `Mount-VHD`, `Initialize-Disk`, `New-Partition -DriveLetter V`, `Format-Volume -FileSystem NTFS`). Crear `V:\Datos` con unos archivos.
2. Tener a mano una consola como SYSTEM (`psexec -s -i cmd` de Sysinternals) para asignar la letra en la sesión del servicio.
3. En el NAS, dejar a la vista el registro de Samba (`/var/log/samba/`, con `log level = 3` o más) y `smbstatus`.
4. En el equipo, Process Monitor con un filtro `Process Name is Backbone.Service.exe` y `Path begins with V:` o `Path contains 192.168.1.163`.

**Pasos**
1. Crear y guardar un trabajo con dos orígenes: primero `C:\Users\<usuario>` (o cualquier carpeta grande, para que la copia dure varios minutos) y después `V:\Datos`. Hacer una copia completa: termina bien y `V:\Datos` está en el snapshot.
2. **Reasignación antes de la ejecución**: quitar la letra al VHDX (`Remove-PartitionAccessPath -DriveLetter V -AccessPath V:\`) y, en la consola de SYSTEM, `net use V: \\192.168.1.163\<recurso> /user:<usuario> <contraseña>`. Lanzar "Copiar ahora". Según §8.3, punto 1, la repetición de la resolución en el `JobRunner` da `SourceNotLocal` por `GetDriveType` sin abrir `V:\`, y la ejecución termina `Failed` con `ValidationFailed`. Anotar el resultado.
3. Deshacer: `net use V: /delete` en la consola de SYSTEM y volver a asignar `V:` al VHDX. Comprobar que una copia vuelve a terminar bien.
4. **Reasignación durante la ejecución** (el caso de BAJA-5): lanzar "Copiar ahora" y, **mientras se copia el primer origen** (antes de que empiece `V:\Datos`), hacer la misma reasignación del paso 2. La ejecución termina `CompletedWithWarnings` con el aviso **`SourceRootUnreadable`** para el origen `Datos`, que conserva lo que tenía en el snapshot padre. El primer origen se copia con normalidad. Este paso supone que `SourceFileSystem` abre la raíz de cada origen cuando el motor llega a ella. Si backbone-service la abre al empezar la ejecución, la reasignación tiene que hacerse justo entre la resolución del `JobRunner` y esa apertura (con un punto de interrupción en una compilación de depuración del servicio instalado). Anotar qué variante se ha usado.
5. Durante los pasos 2 y 4, comprobar:
   - en Process Monitor, que `Backbone.Service` **no** hace ninguna operación sobre `V:\…` ni hacia `192.168.1.163` después de la reasignación (sí puede aparecer la consulta del tipo de unidad);
   - en el NAS, que en el registro de Samba no hay ninguna sesión nueva ni apertura de archivo de ese equipo aparte de la del propio `net use`, y en particular ninguna de la cuenta del equipo (`<EQUIPO>$`) ni NTLM anónimo; `smbstatus` no muestra archivos abiertos en el recurso;
   - en el log del servicio, el motivo (`SourceNotLocal` en el paso 2; en el paso 4, la causa dentro del `message` de `SourceRootUnreadable`).
6. Limpiar: `net use V: /delete` como SYSTEM, desmontar y borrar el VHDX.

**Resultado esperado:** con la letra reasignada a una unidad de red, el servicio no abre nada en ella ni intenta autenticarse ante el NAS. Antes de la ejecución el resultado es `Failed`/`ValidationFailed` (`SourceNotLocal`). Durante la ejecución es `SourceRootUnreadable`, conservando el origen del padre.

**Estado:** pendiente (requiere el `JobRunner` de HU-14).

## PM-HU05-01 · Probar la conexión contra el NAS real (OpenMediaVault)

**Cubre:** HU-05, criterios 1 a 5 (RF-61) con `WNetAddConnection2` y Samba reales, y con el servicio instalado como LocalSystem.

**Por qué es manual:** exige un NAS real con un recurso de lectura y escritura, otro de solo lectura y poder apagar el NAS. Las pruebas automáticas usan un doble (`ConnectionTesterTests`, `ConnectionTestIpcTests`). En `Backbone.IntegrationTests`, `WindowsShareConnectorTests` prueba siempre el servidor inexistente y el resto solo si se definen `BACKBONE_IT_SMB_SHARE`, `BACKBONE_IT_SMB_USER`, `BACKBONE_IT_SMB_PASSWORD` y, opcionalmente, `BACKBONE_IT_SMB_READONLY_SHARE`.

**Pasos**
1. En PowerShell (no hace falta elevar), definir esas variables para `\\192.168.1.163\<recurso>` y ejecutar `dotnet test tests\Backbone.IntegrationTests --filter "FullyQualifiedName~WindowsShareConnectorTests"`: 5 correctas y 0 omitidas.
2. Con el servicio instalado y la app (cuando backbone-app implemente el botón), "Probar conexión" con datos correctos: "Conexión correcta", espacio libre y total coherentes con el panel de OMV y la versión SMB (3.1.1 con Samba actual).
3. Con la contraseña errónea: "El NAS ha rechazado el usuario o la contraseña".
4. Con un recurso inexistente y la credencial correcta: el mensaje de recurso inexistente. (Con Samba, si además la contraseña es errónea, gana el rechazo de credenciales: el servidor autentica antes de buscar el recurso.)
5. Con un recurso de solo lectura para ese usuario: el mensaje de "sin permiso de escritura".
6. Apagar el NAS (o desconectar su cable) y probar: "No se encuentra el NAS en la red" en menos de 15 s, cronometrados desde el clic.
7. Tras cada paso, en el NAS: ningún archivo `.backbone-prueba-*.tmp` en el recurso ni en la carpeta del destino (`ls -la` por SSH).
8. Con una conexión del servicio abierta contra el mismo NAS con **otra** cuenta (copia en curso, cuando exista HU-14), probar la conexión: el resultado es `CredentialsRejected` y la copia en curso no se interrumpe. El servicio lo decide sin llamar a Windows (`ShareConnectionManager` compara una huella de la credencial; en el log, "Ya hay una conexión con el servidor … con otra credencial"); Windows daría `ERROR_SESSION_CREDENTIAL_CONFLICT` (1219), porque no admite dos usuarios contra el mismo servidor en la misma sesión de inicio.
9. Con una conexión ya abierta al recurso con la credencial correcta (copia en curso), probar con la contraseña errónea: `CredentialsRejected`, igual que en el paso 8 (revisión de seguridad de HU-05, BAJA-1). Sin el gestor, Windows reutilizaría la sesión SMB existente y daría la prueba por correcta. Con la contraseña correcta: "Conexión correcta" y, al terminar la prueba, la conexión de la copia sigue abierta (`net use` desde una consola de LocalSystem, p. ej. `psexec -s`).
10. Buscar la contraseña centinela en `%ProgramData%\Backbone` como en PM-HU04-01, paso 6: ninguna coincidencia.
11. Resolución al propio equipo (§7.5, ALTA-1): elevado, añadir a `C:\Windows\System32\drivers\etc\hosts` la línea `127.0.0.1 nas-falso` y otra con la IP de la tarjeta de red del equipo (`ipconfig`) para `nas-falso2`. "Probar conexión" con `\\nas-falso\backups` y con `\\nas-falso2\backups`: error de validación (destino no permitido) al momento, sin intentar conectar (en el log, "el servidor resuelve al propio equipo"). Quitar las líneas al terminar.
12. Con un nombre que no existe en la red (`\\no-existe-hu05\backups`): "No se encuentra el NAS en la red" en menos de 15 s (la resolución falla y no se rechaza: decide la conexión).
13. Parada con una prueba colgada: apagar el NAS, lanzar "Probar conexión" y, antes de que responda, `Stop-Service BackboneService`. El servicio se detiene en menos de 20 s y en el log aparece, si la limpieza no ha terminado, "quedan limpiezas de pruebas de conexión pendientes" y "cerrada a la fuerza en la parada del servicio". Tras arrancarlo de nuevo, `net use` (como LocalSystem) no muestra conexiones al NAS.

**Resultado esperado:** cada caso con su código y su mensaje, en menos de 15 s; ningún archivo de prueba en el NAS; la contraseña no aparece en ningún archivo.

**Estado:** pendiente. El 2026-11-18, sin elevar: `A_server_that_does_not_exist_is_NasUnreachable_in_less_than_15_seconds` correcta contra 192.0.2.1 (unos 11 s); con `net use` contra 192.168.1.163, un usuario inexistente y una contraseña errónea, Samba devuelve 86 o 1326 (los dos se traducen a `CredentialsRejected`). El 2026-11-21, paso 1 contra `\192.168.1.163` (sin elevar, ejecutado por el usuario): 4 correctas y 1 omitida (`A_read_only_share_is_AccessDenied_after_reading`, falta `BACKBONE_IT_SMB_READONLY_SHARE`). Pendientes ese caso de solo lectura y los pasos 2 a 13.

## PM-HU06-01 · Reinstalar Windows y apuntar al mismo destino: se adopta el repositorio

**Cubre:** HU-06, criterio 2 (RF-63): un destino con un repositorio v1 válido se adopta y su historial aparece en Restaurar; y, de paso, criterios 1, 3 y 4 con el NAS real.

**Por qué es manual:** exige un equipo (o máquina virtual) recién instalado sin ninguna configuración de Backbone y el NAS real. "Aparecen en Restaurar" depende de `GetSnapshotsAsync` y de la pantalla Restaurar, que aún no existen; las pruebas automáticas (`RepositoryPreparationTests`, y `RepositoryPreparationIntegrationTests` en `Backbone.IntegrationTests`) solo verifican que el repositorio se adopta sin escribir y conserva sus snapshots.

**Pasos**
1. En el equipo original, con un trabajo configurado hacia `\\192.168.1.163\<recurso>\<carpeta>`, hacer al menos dos copias (con algún cambio entre ellas). Anotar los identificadores de los snapshots y la huella de `repo.json` en el NAS (`ls -la snapshots`, `sha256sum repo.json`).
2. Reinstalar Windows 11 (o usar una máquina virtual limpia) e instalar Backbone. No importar ninguna configuración.
3. En la app, guardar la credencial del recurso y crear un trabajo nuevo con el **mismo** destino (otro nombre y otros orígenes si se quiere).
4. Guardar el trabajo: se acepta sin error. En el log del servicio: "Repositorio existente adoptado en ... (creado por Backbone ... en <PC anterior>)".
5. En el NAS, comprobar que no se ha modificado nada: `sha256sum repo.json` igual, ningún archivo con fecha posterior al guardado y ninguna carpeta `tmp` nueva.
6. Abrir Restaurar para ese trabajo (cuando exista `GetSnapshotsAsync` y la pantalla): aparecen los snapshots del paso 1 con sus fechas.
7. Variante, carpeta con archivos ajenos: apuntar un trabajo nuevo a una carpeta del NAS con un `notas.txt` y sin `repo.json`. Guardar: error de validación en el campo Destino que explica que la carpeta tiene archivos y no es un repositorio de Backbone; en el NAS no se ha creado nada.
8. Variante, formato superior: en una carpeta de pruebas, crear un `repo.json` con `"formatVersion": 2`. Guardar el trabajo: se guarda con el aviso de repositorio de solo lectura (formato más reciente) y no se escribe nada en el NAS.
9. Buscar la contraseña en `%ProgramData%\Backbone` como en PM-HU04-01, paso 6: ninguna coincidencia.

**Resultado esperado:** el repositorio existente se adopta sin tocarlo y su historial es visible; las variantes se rechazan o quedan en solo lectura con su mensaje.

**Estado:** pendiente (Restaurar no existe aún).

## HU-06: aviso de destino de solo lectura (app)

- Con un destino creado por una versión más nueva (repo.json con formato superior), guardar el trabajo: la tarjeta de Inicio y la fila de Trabajos muestran un InfoBar de advertencia "Destino de solo lectura" legible en claro, oscuro y alto contraste, y con Narrador.
- Tras reiniciar el servicio no debe aparecer el aviso hasta volver a guardar o ejecutar el trabajo.
- Guardar un trabajo con una carpeta con archivos ajenos como destino: el error aparece bajo el campo Destino con el texto del recurso.
- Escalado al 200 %: el aviso no se recorta.

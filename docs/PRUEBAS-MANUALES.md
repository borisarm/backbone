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

**Estado:** pendiente.

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

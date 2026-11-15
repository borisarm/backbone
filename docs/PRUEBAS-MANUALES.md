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

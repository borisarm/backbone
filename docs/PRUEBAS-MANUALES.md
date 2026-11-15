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

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

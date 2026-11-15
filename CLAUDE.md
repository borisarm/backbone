# Backbone

Servicio de Windows + app WinUI 3 que hace copias de seguridad versionadas y deduplicadas de carpetas de Windows 11 a un NAS (OpenMediaVault) por SMB.

- Especificación: [docs/ESPECIFICACION.md](docs/ESPECIFICACION.md). **Es la fuente de verdad.** Si el código y la especificación discrepan, se para y se pregunta; no se improvisa.
- Historias de usuario y seguimiento: [docs/HISTORIAS.md](docs/HISTORIAS.md).
- Idioma: documentación, mensajes de commit y textos de interfaz en español; identificadores de código en inglés.

## Stack

.NET 10 LTS · WinUI 3 (Windows App SDK, sin empaquetar) · StreamJsonRpc sobre named pipes · Microsoft.Data.Sqlite · xUnit · WiX v5. Detalle y motivos en §3 de la especificación (ADR-01 a ADR-09).

## Estructura

| Ruta | Contenido | Agente propietario |
|---|---|---|
| `Backbone.slnx`, `Directory.*.props`, `.github/` | Solución, versiones de paquetes, CI | backbone-architect |
| `src/Backbone.Contracts/` | Interfaz IPC, DTO, códigos de error | backbone-architect |
| `src/Backbone.Core/` | Motor: repositorio, backup, restore, retención, prune, check | backbone-core |
| `src/Backbone.Service/` | Servicio, scheduler, IPC, integración con el SO, persistencia | backbone-service |
| `src/Backbone.App/` | App WinUI 3 | backbone-app |
| `installer/`, `scripts/` | Instalador WiX y scripts de desarrollo | backbone-installer |
| `tests/` | Pruebas (cada agente escribe las de su componente; backbone-qa las amplía y audita) | — |
| `docs/` | Especificación, historias, pruebas manuales | backbone-architect |

Un agente **solo modifica las rutas de las que es propietario** (y las pruebas de su componente). Si necesita un cambio fuera, lo pide en su informe final.

## Comandos

```powershell
dotnet build Backbone.slnx -c Debug
dotnet test Backbone.slnx --filter "Category!=Integration&Category!=Perf"
dotnet test Backbone.slnx --filter "Category=Integration"     # requiere PowerShell elevado
dotnet run --project src/Backbone.Service -- --console        # servicio como consola, sin instalar
dotnet run --project src/Backbone.App -p:Platform=x64
```

## Flujo de trabajo con subagentes

La sesión principal actúa de **coordinadora**: no implementa, reparte, integra y verifica. Para cada historia:

1. **Elegir** la siguiente historia según "Orden de desarrollo recomendado" en `docs/HISTORIAS.md`, comprobando que sus dependencias están terminadas.
2. **Delegar** en el agente indicado en la historia, pasándole el identificador (p. ej. "Implementa HU-13") y cualquier decisión tomada antes. Si la historia tiene colaboradores, primero el propietario y después cada colaborador con lo que le toca.
3. **Contrato primero**: si una historia requiere cambiar `Backbone.Contracts`, primero lo hace `backbone-architect`, después el resto.
4. **Pruebas**: al terminar, `backbone-qa` revisa que cada criterio de aceptación tiene prueba, añade las que falten y ejecuta toda la batería.
5. **Seguridad**: si la historia toca IPC, credenciales, ACL, restauración, rutas de archivo o el instalador, `backbone-security-review` revisa el diff. Los hallazgos críticos o altos vuelven al agente propietario.
6. **Cerrar**: `dotnet build -warnaserror` y las pruebas pasan; se actualiza la columna Estado en `docs/HISTORIAS.md`; un commit por historia.
7. **Paralelismo**: historias de la misma iteración y de agentes distintos pueden ir en paralelo. Si dos agentes van a tocar la misma solución a la vez, lanzarlos con `isolation: "worktree"` y fusionar en orden.

Ramas y commits: rama `historia/<id>` (p. ej. `historia/hu-13`); mensaje `HU-13: Copia incremental con versiones`. No hacer push ni abrir PR sin pedirlo.

## Convenciones de código

- `Nullable` activado, sin `!` para silenciar advertencias salvo justificación en comentario.
- Async de principio a fin con `CancellationToken` en toda API pública que haga E/S. Nada de `.Result` ni `.Wait()`.
- Reloj solo mediante `TimeProvider`; nada de `DateTime.Now`. Fechas persistidas en UTC.
- Sistema de archivos del repositorio solo mediante `IRepositoryStorage`; integración con el SO solo mediante interfaces en `Backbone.Service` (`IVolumeSnapshotter`, `IShareConnector`, `ICredentialStore`, `IPowerManager`, `INetworkMonitor`, `IEventLogWriter`) para poder simularlas en pruebas.
- P/Invoke con `LibraryImport` (generado en compilación), en una clase `NativeMethods` por componente.
- Logs estructurados (`logger.LogInformation("Copia {JobId} terminada en {Duration}", …)`); **nunca** registrar contraseñas ni contenido de archivos.
- Cadenas visibles al usuario solo en `.resw` (es-ES y en-US).
- Versiones de NuGet solo en `Directory.Packages.props`; versiones estables y publicadas hace al menos dos semanas.

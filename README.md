# Backbone

Copias de seguridad automáticas, versionadas y deduplicadas de carpetas de Windows 11 a un NAS (OpenMediaVault u otro servidor SMB 2.1+).

Backbone consta de un **servicio de Windows**, que hace todo el trabajo aunque no haya ninguna sesión iniciada, y una **aplicación WinUI 3**, para configurar los trabajos, seguir el progreso y restaurar archivos de cualquier fecha.

> **Estado:** en fase de diseño. La [especificación](docs/ESPECIFICACION.md) y las [historias de usuario](docs/HISTORIAS.md) están aprobadas; el código aún no existe. El avance de cada historia se sigue en la tabla de [HISTORIAS.md](docs/HISTORIAS.md#seguimiento).

## Principios

1. **La restauración es el producto.** Una copia que no se puede restaurar fácilmente no vale nada.
2. **Nunca corromper el repositorio.** Un corte de luz o de red, o un cierre forzado a mitad de copia, deja el repositorio válido.
3. **Integrado con Windows.** Se usan los servicios del sistema (SCM, VSS, DPAPI, Visor de eventos, notificaciones, energía y red) en lugar de reinventarlos.
4. **Silencioso cuando todo va bien, insistente cuando algo falla.**

## Qué hace

- **Copias incrementales con versiones:** cada archivo se guarda una sola vez, nombrado por su hash SHA-256. Cada ejecución produce un *snapshot* con el estado de todos los archivos en ese instante.
- **Archivos abiertos:** las copias se hacen desde una instantánea de volumen (VSS).
- **Programación y condiciones:** las copias se programan, y se pueden condicionar a la red y a la energía; se evita que el equipo se suspenda durante una copia.
- **Restauración** de archivos y carpetas de cualquier snapshot, desde la app.
- **Retención, limpieza y verificación** del repositorio.
- **Sin software en el NAS:** el repositorio es una estructura de carpetas sobre SMB normal.
- **Credenciales cifradas** con DPAPI; nunca en texto plano en disco ni en los logs.

### Fuera de alcance (v1)

Imagen completa del sistema, destinos en la nube, FTP o SSH, gestión centralizada de varios PC, orígenes FAT/exFAT o extraíbles, y copia de ACL NTFS y flujos alternativos.

## Requisitos

- Windows 11 22H2 o posterior, x64 o ARM64.
- Un recurso compartido SMB 2.1+ accesible desde el PC.
- Orígenes en volúmenes NTFS locales.

## Arquitectura

```
Backbone.App (WinUI 3, sesión del usuario)
        │  JSON-RPC (StreamJsonRpc) sobre named pipe con ACL
        ▼
Backbone.Service (servicio de Windows, LocalSystem)
        │  SMB
        ▼
\\NAS\Backups\<trabajo>\   repositorio
```

| Proyecto | Contenido |
|---|---|
| `src/Backbone.Contracts` | Interfaz IPC, DTO y códigos de error |
| `src/Backbone.Core` | Motor: repositorio, copia, restauración, retención, limpieza y verificación. No depende de Windows. |
| `src/Backbone.Service` | Host del servicio, scheduler, servidor IPC, integración con el SO y persistencia |
| `src/Backbone.App` | App WinUI 3 sin empaquetar |
| `installer/` | Instalador MSI (WiX v5) |
| `tests/` | Pruebas xUnit unitarias, de arquitectura y de integración |

La app solo depende de `Contracts`: si se cierra, las copias continúan. Las decisiones de arquitectura (ADR-01 a ADR-09) están en el §3 de la [especificación](docs/ESPECIFICACION.md#3-arquitectura).

**Stack:** .NET 10 LTS · WinUI 3 (Windows App SDK) · StreamJsonRpc · Microsoft.Data.Sqlite · xUnit · WiX v5.

## Desarrollo

```powershell
dotnet build Backbone.slnx -c Debug
dotnet test Backbone.slnx --filter "Category!=Integration&Category!=Perf"
dotnet test Backbone.slnx --filter "Category=Integration"     # requiere PowerShell elevado
dotnet run --project src/Backbone.Service -- --console        # servicio como consola, sin instalar
dotnet run --project src/Backbone.App -p:Platform=x64
```

Las convenciones de código, la propiedad de cada carpeta y el flujo de trabajo por historias están en [CLAUDE.md](CLAUDE.md). La especificación es la fuente de verdad: si el código y la especificación discrepan, manda la especificación.

## Hoja de ruta

| Fase | Objetivo |
|---|---|
| **F1 — MVP** | Copias programadas fiables con VSS, restauración básica, app con bandeja y panel, instalador. |
| **F2 — Uso diario** | Retención, limpieza y verificación, condiciones de red y energía, notificaciones completas, versiones de un archivo, asistente de primer uso, diagnóstico. |
| **F3 — Pulido y rendimiento** | USN Journal, formato v2 (troceado, compresión, cifrado), límite de ancho de banda, integración con el Explorador. |

### Estado actual

El MVP está en desarrollo; el detalle por historia está en [docs/HISTORIAS.md](docs/HISTORIAS.md#seguimiento).

- **Hecho**: estructura de la solución y CI (HT-01, HT-02); canal IPC seguro entre app y servicio (HT-03); formato del repositorio endurecido (HT-04); icono de la bandeja con estado y menú (HU-18); credenciales del NAS guardadas con DPAPI de ámbito equipo (HU-04, parte del servicio).
- **En curso**: motor de copia incremental con deduplicación (HU-13).
- **Todavía no**: conexión real al NAS, programación, VSS, restauración e instalador; el servicio aún no hace copias de extremo a extremo.

## Licencia

[MIT](LICENSE)

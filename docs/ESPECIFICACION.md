# Backbone — Especificación funcional y técnica

| | |
|---|---|
| **Producto** | Backbone: copias de seguridad automáticas de Windows 11 a un NAS (OpenMediaVault vía SMB) |
| **Versión del documento** | 1.0 — 2026-11-15 |
| **Estado** | Aprobado para desarrollo (MVP) |
| **Historias de usuario** | [HISTORIAS.md](HISTORIAS.md) |

---

## Índice

La especificación es la **fuente de verdad** y está repartida en un archivo por sección en [`espec/`](espec/). Abre solo el archivo que necesites; en los archivos grandes (§7, §8), localiza la subsección con Grep por su encabezado (p. ej. `^### 8.6`) y lee ese tramo con `offset`/`limit`.

| § | Archivo | Contenido | Subsecciones |
|---|---|---|---|
| 1 | [01-vision.md](espec/01-vision.md) | Qué es Backbone y sus principios. | 1.1 Alcance · 1.2 Fuera de alcance (v1) |
| 2 | [02-glosario.md](espec/02-glosario.md) | Términos: trabajo, origen, destino, repositorio, objeto, snapshot, ejecución, retención, limpieza, verificación, usuario autorizado. | — |
| 3 | [03-arquitectura.md](espec/03-arquitectura.md) | Procesos, proyectos de la solución y decisiones de arquitectura (ADR-01 a ADR-21). | 3.1 Solución · 3.2 Decisiones de arquitectura (ADR) |
| 4 | [04-requisitos-funcionales.md](espec/04-requisitos-funcionales.md) | Requisitos funcionales RF-01 a RF-80. | 4.1 Trabajos · 4.2 Programación y condiciones · 4.3 Ejecución de copias · 4.4 Restauración · 4.5 Retención y mantenimiento · 4.6 Supervisión y notificaciones · 4.7 Conexión al NAS y credenciales · 4.8 Seguridad del repositorio · 4.9 Primer uso |
| 5 | [05-requisitos-no-funcionales.md](espec/05-requisitos-no-funcionales.md) | RNF-01 a RNF-14: fiabilidad (01–02), rendimiento (03–04), recursos (05), seguridad (06–08), compatibilidad (09), accesibilidad (10), localización (11), observabilidad (12), mantenibilidad (13), compatibilidad hacia delante (14). | — |
| 6 | [06-formato-repositorio.md](espec/06-formato-repositorio.md) | Formato del repositorio v1 en el NAS. | 6.1 Estructura · 6.2 `repo.json` · 6.3 Snapshot `snapshots/<id>.json` · 6.4 Reglas de escritura y concurrencia · 6.5 Evolución (v2, F3) |
| 7 | [07-contrato-ipc.md](espec/07-contrato-ipc.md) | Contrato IPC entre app y servicio (`Backbone.Contracts`). | 7.1 Transporte · 7.2 Interfaz (`IBackboneService`) · 7.3 Seguridad del canal · 7.4 Errores · 7.5 Convenciones del contrato |
| 8 | [08-servicio.md](espec/08-servicio.md) | Servicio de Windows: registro, componentes, rutas, ciclo de ejecución, eventos. Es el archivo más grande (~90 KB); §8.2 tiene párrafos en negrita por tema (carpeta de datos, conexiones al NAS, lectura de orígenes, puntos de reanálisis, solo en la nube…). | 8.1 Registro · 8.2 Componentes internos · 8.3 Rutas · 8.4 Ciclo de una ejecución · 8.5 Identificadores del Visor de eventos · 8.6 Restauración segura · 8.7 Exportación segura del diagnóstico (F2) |
| 9 | [09-app.md](espec/09-app.md) | Aplicación WinUI 3. | 9.1 Generalidades · 9.2 Navegación (`NavigationView`) · 9.3 Estados especiales |
| 10 | [10-instalador.md](espec/10-instalador.md) | Instalador WiX v5. | — |
| 11 | [11-pruebas.md](espec/11-pruebas.md) | Estrategia de pruebas: niveles (unitarias, contrato, integración, fallos, manual) y proyecto de cada uno. | — |
| 12 | [12-fases.md](espec/12-fases.md) | Fases F1 (MVP), F2 y F3. | — |
| 13 | [13-riesgos.md](espec/13-riesgos.md) | Riesgos y cuestiones abiertas R1 a R16, con su mitigación. | — |

Los números de sección (§x.y) y los encabezados son los mismos que en el documento único anterior; las referencias "§8.6" siguen valiendo.

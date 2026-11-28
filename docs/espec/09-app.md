## 9. Aplicación WinUI 3

### 9.1 Generalidades

- Sin empaquetar, una sola instancia (`AppInstance.FindOrRegisterForKey`); una segunda ejecución trae al frente la existente.
- Se inicia con la sesión (clave `HKCU\…\Run` creada por el instalador, desactivable en Ajustes) minimizada en la bandeja.
- Cerrar la ventana la oculta en la bandeja; "Salir" en el menú de la bandeja cierra la app (no el servicio).
- Fondo Mica, tema claro/oscuro del sistema, iconos de Segoe Fluent Icons.
- Patrón MVVM con `CommunityToolkit.Mvvm`; inyección de dependencias con `Microsoft.Extensions.DependencyInjection`.
- Icono de bandeja con `H.NotifyIcon.WinUI`; notificaciones con `Microsoft.Windows.AppNotifications`.
- Cliente IPC con una única conexión compartida y reconexión automática (espera 1 s, 2 s, 5 s, 10 s, luego cada 10 s) e indicador "reconectando":
  - Tras **perder una conexión que funcionaba**, el estado es "Reconectando" mientras fallan los primeros intentos (un reinicio del servicio dura unos segundos). La pérdida cuenta como primer fallo; los intentos tras 1 s y 2 s siguen en "Reconectando" y, si falla también el tercero (tras 5 s, unos 8 s desde la pérdida), pasa a "Servicio detenido" (§9.3) y sigue reintentando cada 10 s. Parámetro: `BackboneClientOptions.ReconnectingAttempts = 4` fallos seguidos contando la pérdida.
  - Si nunca hubo conexión (arranque de la app), un fallo de disponibilidad se muestra directamente como "Servicio detenido".
  - "Sin autorización" (ACL del pipe o `NotAuthorized`) y "Versión de contrato incompatible" se muestran **de inmediato**, sin pasar por "Reconectando", y el cliente sigue reintentando con las mismas esperas (p. ej. se recupera solo cuando un administrador lo añade a `authorizedUsers`).
  - Un fallo de la verificación del servidor (§7.1 A-1) cuenta como "servicio no disponible".
  - "Iniciar servicio" interrumpe la espera y reintenta en el acto.

### 9.2 Navegación (`NavigationView`)

| Página | Contenido |
|---|---|
| **Inicio** | Tarjeta de estado general; una tarjeta por trabajo (estado, última y próxima ejecución, botón Copiar ahora / Cancelar, progreso en vivo); espacio del NAS (F2); avisos activos. |
| **Trabajos** | Lista de trabajos; editor con secciones: General (nombre, activado), Orígenes (añadir con `FolderPicker`, alias), Destino (UNC, credenciales, Probar conexión), Exclusiones (lista editable con vista previa de cuántos archivos excluye; si `ValidateJobAsync` devuelve `SourceContainsDataFolder`, nota fija no eliminable de que la carpeta de datos de Backbone se excluye siempre, §8.3), Programación, Retención (F2), Condiciones (F2). Validación en línea mediante `ValidateJobAsync`. |
| **Restaurar** | Selector de trabajo → lista de snapshots (línea de tiempo agrupada por día) → explorador del snapshot (`TreeView` + lista con nombre, tamaño, fecha) con búsqueda → selección múltiple → diálogo de restauración (original / otra carpeta, política de conflictos) → progreso y resumen. |
| **Historial** | Lista virtualizada de ejecuciones con filtros; panel de detalle con avisos y errores; botón "Abrir en el Visor de eventos". |
| **Ajustes** | Pausa, notificaciones, inicio con Windows, periodo de gracia, usuarios autorizados (requiere elevar), exportar diagnóstico (F2), acerca de (versiones de app, servicio y contrato). |

### 9.3 Estados especiales

- **Servicio no instalado o detenido**: banner rojo persistente con "Iniciar servicio" (lanza `sc start` elevado mediante `runas`).
- **Sin autorización**: pantalla explicativa con la cuenta del usuario actual y cómo añadirla: un administrador abre Backbone con "Ejecutar como administrador" y la añade en Ajustes → Usuarios autorizados. No hay comando aparte (decisión del usuario en HU-19: en un equipo doméstico suele haber un solo usuario, que es quien instala).
- **Versión de contrato incompatible**: aviso con la versión de cada parte.
- **Sin trabajos**: estado vacío con botón al asistente de primer uso.

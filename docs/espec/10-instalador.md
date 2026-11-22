## 10. Instalador (WiX v5)

- MSI por arquitectura (x64, ARM64), per-machine, requiere elevación.
- Instala servicio y app en `%ProgramFiles%\Backbone\`.
- `ServiceInstall` + `ServiceConfig` (inicio retrasado, recuperación), `ServiceControl` para arrancar al instalar y detener al desinstalar o actualizar.
- **Privilegios requeridos** (endurecimiento, ADR-21): el servicio se configura con `SERVICE_CONFIG_REQUIRED_PRIVILEGES_INFO` (tabla `MsiServiceConfig`, `ConfigType` 6, que WiX genera desde `ServiceConfig`/`RequiredPrivilege` o equivalente; lo confirma backbone-installer) con la lista de privilegios que usa el servicio, para que el SCM quite los demás del token del proceso al arrancar. **Lista fijada** (cierre de HU-08 servicio, decisión del coordinador avalada por la revisión de seguridad): `SeBackupPrivilege` (lectura de orígenes, ADR-21), `SeImpersonatePrivilege` (suplantación del cliente en la restauración, §8.6) y `SeChangeNotifyPrivilege` (se pide de forma explícita aunque el SCM no lo quite nunca), **ninguno más**. En particular no se piden `SeRestorePrivilege`, `SeTakeOwnershipPrivilege`, `SeSecurityPrivilege` ni `SeDebugPrivilege`. Si VSS (HU-15) u otro componente necesitara otro, se cambia esta lista en la especificación antes de cambiar el instalador. La prueba de aceptación de HU-01 comprueba:
  - con `sc qprivs BackboneService`, que la lista es exactamente esa;
  - que el servicio **arranca** con el token ya limitado;
  - que, tras borrar `%ProgramData%\Backbone` con el servicio parado, al arrancar la **vuelve a crear** con Administradores como propietario y la DACL de RNF-07, y que **corrige** una raíz de propietario fiable con otra DACL (§8.3). Así se comprueba que asignar Administradores como propietario no necesita `SeRestorePrivilege`.
- Crea el origen del Visor de eventos `Backbone`.
- Crea `%ProgramData%\Backbone` con las ACL de RNF-07, con la misma regla que el servicio (§8.3, ADR-13): si ya existe y es un punto de reanálisis, se rechaza; si su propietario no es SYSTEM ni Administradores, se aparta a `Backbone.untrusted-<guid>` y se crea otra con el descriptor en la creación; **nunca** se toma la propiedad ni se cambia la DACL en el sitio de una carpeta de propietario no fiable (HU-01 I-1).
- Añade el SID del usuario que instala a `authorizedUsers` solo si es miembro de Administradores (§7.3).
- Acceso directo en el menú Inicio; entrada `HKCU\…\Run` opcional (casilla, activada por defecto).
- Actualizaciones mayores (`MajorUpgrade`) que conservan la configuración, el historial y las credenciales.
- Desinstalación: casilla "Eliminar también configuración e historial" (desactivada por defecto). **Nunca** toca repositorios del NAS.
- Firma de código (Authenticode) del MSI y los ejecutables en CI cuando haya certificado disponible.

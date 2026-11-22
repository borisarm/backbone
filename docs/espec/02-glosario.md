## 2. Glosario

| Término | Definición |
|---|---|
| **Trabajo** (*job*) | Configuración de una copia: orígenes, destino, exclusiones, programación y retención. |
| **Origen** | Carpeta local incluida en un trabajo. Tiene un **alias** único dentro del trabajo (p. ej. `Documentos`). |
| **Destino** | Ruta UNC de la carpeta del repositorio en el NAS, p. ej. `\\192.168.1.50\Backups\PC-Salon`. |
| **Repositorio** | Estructura de carpetas en el destino con objetos y snapshots (§6). Un repositorio por trabajo. |
| **Objeto** | Contenido de un archivo, guardado una sola vez y nombrado por su hash SHA-256. |
| **Snapshot** | Manifiesto que describe el estado de todos los archivos del trabajo en un instante. |
| **Ejecución** (*run*) | Cada vez que se lanza un trabajo. Una ejecución correcta produce un snapshot. |
| **Retención** | Reglas que deciden qué snapshots se conservan. |
| **Limpieza** (*prune*) | Borrado de objetos que ningún snapshot referencia. |
| **Verificación** (*check*) | Comprobación de que todos los objetos referenciados existen y son íntegros. |
| **Usuario autorizado** | Cuenta de usuario de Windows, miembro del grupo local Administradores, que puede gestionar Backbone desde la app sin elevar (§7.3). |

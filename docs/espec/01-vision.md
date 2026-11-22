## 1. Visión

Backbone es un servicio de Windows que copia automáticamente carpetas del PC a una carpeta compartida de un NAS, guardando **versiones** de los archivos sin duplicar espacio, y una aplicación WinUI 3 para gestionarlo: configurar trabajos, ver el estado y **restaurar** archivos de cualquier fecha.

Principios:

1. **La restauración es el producto.** Una copia que no se puede restaurar fácilmente no vale nada.
2. **Nunca corromper el repositorio.** Un corte de luz, de red o un cierre forzado a mitad de copia debe dejar el repositorio válido.
3. **Integrado con Windows.** Usa los servicios del sistema (SCM, VSS, DPAPI, Visor de eventos, notificaciones, gestión de energía y de red) en lugar de reinventarlos.
4. **Silencioso cuando todo va bien, insistente cuando algo falla.**

### 1.1 Alcance

- Un PC con Windows 11 (22H2 o posterior, x64 y ARM64) y uno o varios usuarios locales.
- Destino: carpeta compartida SMB (OpenMediaVault u otro NAS/servidor SMB 2.1+).
- Origen: carpetas en volúmenes NTFS locales.

### 1.2 Fuera de alcance (v1)

- Copia de la imagen completa del sistema o *bare-metal restore*.
- Destinos en la nube (S3, OneDrive…), FTP o SSH.
- Gestión centralizada de varios PC.
- Volúmenes FAT/exFAT, unidades extraíbles como origen.
- Copia de permisos NTFS (ACL) y flujos alternativos (ADS). Se guardan contenido, fecha de modificación y atributos básicos.

## 5. Requisitos no funcionales

| ID | Categoría | Requisito |
|---|---|---|
| RNF-01 | Fiabilidad | Matar el proceso del servicio o cortar la red en cualquier momento de una ejecución **nunca** deja un snapshot que referencie objetos inexistentes ni objetos con contenido que no coincide con su nombre. Comprobado con pruebas de inyección de fallos. |
| RNF-02 | Fiabilidad | El servicio se recupera solo: acciones de recuperación del SCM "reiniciar" a los 1, 1 y 5 minutos; contador a cero tras 1 día. |
| RNF-03 | Rendimiento | Ejecución incremental sin cambios sobre 100 000 archivos: < 60 s sin USN (F1), < 10 s con USN (F3). |
| RNF-04 | Rendimiento | Primera copia por Ethernet de 1 Gb: ≥ 80 % del rendimiento de `robocopy` sobre el mismo conjunto. |
| RNF-05 | Recursos | Servicio en reposo: CPU ≈ 0 %, memoria < 60 MB. En ejecución con 500 000 archivos: < 400 MB. |
| RNF-06 | Seguridad | Ninguna contraseña en texto plano en disco, logs, IPC de salida ni volcados de diagnóstico. |
| RNF-07 | Seguridad | `%ProgramData%\Backbone` con propietario Administradores (o SYSTEM) y DACL protegida (sin herencia): SYSTEM y Administradores control total heredable; usuarios sin acceso. El servicio lo comprueba al arrancar, antes de leer ninguna configuración, y nunca corrige en el sitio una carpeta de propietario no fiable: la aparta y crea otra (§8.3, ADR-13). |
| RNF-08 | Seguridad | El canal IPC solo admite a SYSTEM, Administradores y usuarios autorizados, que son siempre cuentas administradoras (§7.3); el cliente verifica que el servidor es el servicio (§7.1). Toda entrada recibida por IPC se valida en el servicio con los límites de §7.5. |
| RNF-09 | Compatibilidad | Windows 11 22H2+ x64 y ARM64. SMB 2.1+. Rutas largas (> 260 caracteres) admitidas en origen y destino. |
| RNF-10 | Accesibilidad | Navegable entera con teclado; nombres accesibles para Narrador en todos los controles; compatible con temas de alto contraste; escalado hasta 200 %. |
| RNF-11 | Localización | Interfaz en español (es-ES) y en inglés (en-US) mediante archivos `.resw`; ninguna cadena visible escrita en el código. Fechas y tamaños con la cultura del usuario. |
| RNF-12 | Observabilidad | Logs estructurados con `Microsoft.Extensions.Logging` en `%ProgramData%\Backbone\logs`, rotación diaria, 14 días, máx. 10 MB por archivo. El registrador a archivo escribe desde una cola propia, nunca desde el hilo que registra (los hilos con el token de lectura, ADR-21, §8.2). |
| RNF-13 | Mantenibilidad | Análisis de código .NET a nivel `latest-recommended`, `Nullable` activado, advertencias como errores en CI. Cobertura de Backbone.Core ≥ 85 %. |
| RNF-14 | Compatibilidad hacia delante | El repositorio lleva versión de formato; una versión de Backbone nunca escribe en un repositorio de formato superior al que entiende (solo lectura y aviso). |

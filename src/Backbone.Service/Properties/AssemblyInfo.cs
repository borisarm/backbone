using System.Runtime.InteropServices;

// Todas las DLL nativas de NativeMethods son del sistema: se cargan solo desde System32, nunca desde la carpeta de la
// aplicación ni del directorio actual (evita la carga de una DLL plantada).
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

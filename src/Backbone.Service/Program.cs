using Backbone.Service;
using Backbone.Service.Hosting;
using Microsoft.Extensions.Hosting;

// Servicio de Windows "BackboneService" (§8.1). Con --console se ejecuta como aplicación de consola, sin instalar
// (requiere una consola elevada: la carpeta de datos solo es accesible para SYSTEM y Administradores, RNF-07).
// Los argumentos no se usan como fuente de configuración (HT-03 I-5): solo se reconoce --console.
var options = new BackboneHostOptions
{
    ConsoleMode = args.Contains("--console", StringComparer.OrdinalIgnoreCase),
};

// Sin elevar no se puede crear la carpeta de datos: se avisa antes de construir el host (en modo servicio no cambia nada).
if (!ConsoleElevationCheck.CanStart(options.ConsoleMode, ConsoleElevationCheck.IsProcessElevated, Console.Error))
{
    return ConsoleElevationCheck.ExitCode;
}

// Sin "using": RunAsync libera el host al terminar (también si el arranque falla).
var host = BackboneHost.CreateBuilder(options).Build();
try
{
    await host.RunAsync().ConfigureAwait(false);
    return 0;
}
catch (ServiceStartupException)
{
    // Ya registrado en el Visor de eventos y en el log. Un código distinto de cero hace que el SCM aplique la recuperación.
    return 1;
}

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

using var host = BackboneHost.CreateBuilder(options).Build();
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

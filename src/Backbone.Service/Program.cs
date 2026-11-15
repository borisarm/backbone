using Microsoft.Extensions.Hosting;

// Esqueleto mínimo del host (HT-01). La configuración del servicio, el IPC y el scheduler
// llegan en sus historias (HT-03 y siguientes).
var builder = Host.CreateApplicationBuilder(args);
using var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);

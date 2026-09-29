using Microsoft.Extensions.Hosting.WindowsServices;
using Pos.Terminal.Agent;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // Como servicio de Windows el directorio de trabajo es System32: el contenido se busca junto al ejecutable.
    ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,
});
builder.AddTerminalAgent();

var app = builder.Build();
app.UseTerminalAgent();
await app.RunAsync();

/// <summary>Punto de entrada; parcial y público para las pruebas (WebApplicationFactory).</summary>
public partial class Program;

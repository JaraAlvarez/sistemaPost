using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor;
using MudBlazor.Services;
using Pos.Client;
using Pos.Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// La API es el mismo servidor que sirve la aplicación (D15-02).
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<SessionState>();
builder.Services.AddScoped<ApiClient>();
builder.Services.AddScoped<AgentPrinter>();
builder.Services.AddMudServices(options =>
{
    options.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomCenter;
    options.SnackbarConfiguration.VisibleStateDuration = 6000;
});

await builder.Build().RunAsync();

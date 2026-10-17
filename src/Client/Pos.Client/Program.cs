using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor;
using MudBlazor.Services;
using Pos.Client;
using Pos.Client.Services;

// Formato de Colombia en toda la aplicación (D17C-04), sin importar el idioma del navegador: coma decimal y punto de miles en los campos
// numéricos ("41.300,02"); con un navegador en inglés la coma se tomaba como separador de miles (41300,02 → 4.130.002).
var colombia = System.Globalization.CultureInfo.GetCultureInfo("es-CO");
System.Globalization.CultureInfo.DefaultThreadCurrentCulture = colombia;
System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = colombia;
System.Globalization.CultureInfo.CurrentCulture = colombia;
System.Globalization.CultureInfo.CurrentUICulture = colombia;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// La API es el mismo servidor que sirve la aplicación (D15-02).
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<SessionState>();
builder.Services.AddScoped<ApiClient>();
builder.Services.AddScoped<AgentPrinter>();
builder.Services.AddScoped<Tema>();
builder.Services.AddMudServices(options =>
{
    options.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomCenter;
    options.SnackbarConfiguration.VisibleStateDuration = 6000;
    options.SnackbarConfiguration.ShowCloseIcon = true;
    options.SnackbarConfiguration.PreventDuplicates = true;
    options.SnackbarConfiguration.SnackbarVariant = Variant.Filled;
    options.SnackbarConfiguration.MaxDisplayedSnackbars = 3;
});

await builder.Build().RunAsync();

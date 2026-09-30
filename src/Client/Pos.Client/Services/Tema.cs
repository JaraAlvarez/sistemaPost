using Microsoft.JSInterop;
using MudBlazor;

namespace Pos.Client.Services;

/// <summary>
/// Preferencias de apariencia del equipo (docs/guia-diseno.md): modo claro, oscuro o el del sistema, y el menú lateral compacto. Se
/// recuerdan en el almacenamiento del navegador de ESE equipo (no son datos del negocio).
/// </summary>
public sealed class Tema(IJSRuntime js)
{
    public const string Claro = "claro";
    public const string Oscuro = "oscuro";
    public const string Sistema = "sistema";

    private const string ClaveTema = "pos.tema";
    private const string ClaveMenu = "pos.menu";
    private bool _cargado;

    public string Preferencia { get; private set; } = Sistema;

    public bool SistemaOscuro { get; private set; }

    public bool EsOscuro => Preferencia == Oscuro || (Preferencia == Sistema && SistemaOscuro);

    /// <summary>Menú lateral de la administración reducido a iconos.</summary>
    public bool MenuCompacto { get; private set; }

    public event Action? Changed;

    public async Task CargarAsync()
    {
        if (_cargado)
        {
            return;
        }

        _cargado = true;
        try
        {
            var tema = await js.InvokeAsync<string?>("pos.pref.get", ClaveTema);
            Preferencia = tema is Claro or Oscuro ? tema : Sistema;
            MenuCompacto = await js.InvokeAsync<string?>("pos.pref.get", ClaveMenu) == "compacto";
        }
        catch (JSException)
        {
            // Sin almacenamiento del navegador: valores por defecto.
        }

        Changed?.Invoke();
    }

    public void SistemaCambio(bool oscuro)
    {
        if (SistemaOscuro != oscuro)
        {
            SistemaOscuro = oscuro;
            Changed?.Invoke();
        }
    }

    public async Task ElegirAsync(string preferencia)
    {
        Preferencia = preferencia is Claro or Oscuro ? preferencia : Sistema;
        await GuardarAsync(ClaveTema, Preferencia);
        Changed?.Invoke();
    }

    /// <summary>Alterna entre claro y oscuro (el botón del encabezado).</summary>
    public Task AlternarAsync() => ElegirAsync(EsOscuro ? Claro : Oscuro);

    public async Task CompactarMenuAsync(bool compacto)
    {
        MenuCompacto = compacto;
        await GuardarAsync(ClaveMenu, compacto ? "compacto" : "completo");
        Changed?.Invoke();
    }

    private async Task GuardarAsync(string clave, string valor)
    {
        try
        {
            await js.InvokeVoidAsync("pos.pref.set", clave, valor);
            await js.InvokeVoidAsync("pos.pref.aplicarTema", EsOscuro);
        }
        catch (JSException)
        {
            // Sin almacenamiento: la preferencia dura hasta cerrar la pestaña.
        }
    }
}

/// <summary>
/// Tema de MudBlazor de BusinessPost. Los valores están documentados en docs/guia-diseno.md; cada par texto/fondo cumple el contraste AA
/// (4,5:1) o más.
/// </summary>
public static class TemaBusinessPost
{
    private static readonly string[] Fuente = ["Inter", "Segoe UI", "system-ui", "-apple-system", "Roboto", "Helvetica Neue", "Arial", "sans-serif"];

    public static readonly MudTheme Theme = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#3341B0",
            PrimaryDarken = "#27328A",
            PrimaryLighten = "#5A67D8",
            PrimaryContrastText = "#FFFFFF",
            Secondary = "#0F766E",
            SecondaryContrastText = "#FFFFFF",
            Tertiary = "#6D28D9",
            Info = "#0369A1",
            Success = "#15803D",
            Warning = "#B45309",
            Error = "#B91C1C",
            Dark = "#1E293B",
            TextPrimary = "#111827",
            TextSecondary = "#4B5563",
            TextDisabled = "#9CA3AF",
            ActionDefault = "#4B5563",
            Background = "#F4F6FA",
            BackgroundGray = "#EEF1F6",
            Surface = "#FFFFFF",
            AppbarBackground = "#FFFFFF",
            AppbarText = "#111827",
            DrawerBackground = "#111A3A",
            DrawerText = "#CBD5E1",
            DrawerIcon = "#94A3B8",
            LinesDefault = "#E2E8F0",
            LinesInputs = "#94A3B8",
            TableLines = "#E5E9F0",
            TableStriped = "#F8FAFC",
            TableHover = "#EEF2FF",
            Divider = "#E2E8F0",
            Skeleton = "#E2E8F0",
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#8B9CFF",
            PrimaryDarken = "#6F82F5",
            PrimaryLighten = "#AEBBFF",
            PrimaryContrastText = "#0B1033",
            Secondary = "#2DD4BF",
            SecondaryContrastText = "#062B28",
            Tertiary = "#C4B5FD",
            Info = "#38BDF8",
            InfoContrastText = "#082F49",
            Success = "#4ADE80",
            SuccessContrastText = "#052E16",
            Warning = "#FBBF24",
            WarningContrastText = "#422006",
            Error = "#F87171",
            ErrorContrastText = "#450A0A",
            Dark = "#0B1120",
            TextPrimary = "#E5E7EB",
            TextSecondary = "#9CA3AF",
            TextDisabled = "#6B7280",
            ActionDefault = "#CBD5E1",
            Background = "#0B1120",
            BackgroundGray = "#131C2E",
            Surface = "#111827",
            AppbarBackground = "#111827",
            AppbarText = "#E5E7EB",
            DrawerBackground = "#0A1024",
            DrawerText = "#CBD5E1",
            DrawerIcon = "#94A3B8",
            LinesDefault = "#1F2A3D",
            LinesInputs = "#4B5563",
            TableLines = "#1F2A3D",
            TableStriped = "#0F1624",
            TableHover = "#1A2340",
            Divider = "#1F2A3D",
            Skeleton = "#1F2A3D",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = Fuente, FontSize = ".9375rem", LineHeight = "1.5", LetterSpacing = "normal" },
            H4 = new H4Typography { FontFamily = Fuente, FontSize = "1.75rem", FontWeight = "700", LineHeight = "1.25", LetterSpacing = "-.015em" },
            H5 = new H5Typography { FontFamily = Fuente, FontSize = "1.375rem", FontWeight = "700", LineHeight = "1.3", LetterSpacing = "-.01em" },
            H6 = new H6Typography { FontFamily = Fuente, FontSize = "1.0625rem", FontWeight = "650", LineHeight = "1.4", LetterSpacing = "normal" },
            Subtitle1 = new Subtitle1Typography { FontFamily = Fuente, FontSize = "1rem", FontWeight = "600", LineHeight = "1.5" },
            Subtitle2 = new Subtitle2Typography { FontFamily = Fuente, FontSize = ".875rem", FontWeight = "600", LineHeight = "1.5" },
            Body1 = new Body1Typography { FontFamily = Fuente, FontSize = ".9375rem", LineHeight = "1.55" },
            Body2 = new Body2Typography { FontFamily = Fuente, FontSize = ".875rem", LineHeight = "1.5" },
            Button = new ButtonTypography { FontFamily = Fuente, FontSize = ".9rem", FontWeight = "600", TextTransform = "none", LetterSpacing = ".005em" },
            Caption = new CaptionTypography { FontFamily = Fuente, FontSize = ".8rem", LineHeight = "1.45" },
            Overline = new OverlineTypography { FontFamily = Fuente, FontSize = ".72rem", FontWeight = "700", LetterSpacing = ".08em" },
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "8px",
            AppbarHeight = "60px",
            DrawerWidthLeft = "264px",
            DrawerMiniWidthLeft = "68px",
        },
    };
}

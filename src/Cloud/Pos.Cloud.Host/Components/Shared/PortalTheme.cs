using MudBlazor;

namespace Pos.Cloud.Host.Components.Shared;

/// <summary>
/// Tema de MudBlazor del portal (docs/guia-diseno-portal.md): la misma marca que el programa de caja (índigo sobrio y verde
/// azulado, fuente Inter local). Cada par texto/fondo cumple el contraste AA (4,5:1) o más, en claro y en oscuro.
/// </summary>
public static class PortalTheme
{
    private static readonly string[] Font = ["Inter", "Segoe UI", "system-ui", "-apple-system", "Roboto", "Helvetica Neue", "Arial", "sans-serif"];

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
            TextDisabled = "#6B7280",
            ActionDefault = "#4B5563",
            Background = "#F4F6FA",
            BackgroundGray = "#EEF1F6",
            Surface = "#FFFFFF",
            AppbarBackground = "#FFFFFF",
            AppbarText = "#111827",
            DrawerBackground = "#111A3A",
            DrawerText = "#D5DCE8",
            DrawerIcon = "#A5B1C6",
            LinesDefault = "#E2E8F0",
            LinesInputs = "#8391A7",
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
            TextSecondary = "#A3ACBA",
            TextDisabled = "#6B7280",
            ActionDefault = "#CBD5E1",
            Background = "#0B1120",
            BackgroundGray = "#131C2E",
            Surface = "#111827",
            AppbarBackground = "#111827",
            AppbarText = "#E5E7EB",
            DrawerBackground = "#0A1024",
            DrawerText = "#D5DCE8",
            DrawerIcon = "#A5B1C6",
            LinesDefault = "#243047",
            LinesInputs = "#5B6679",
            TableLines = "#1F2A3D",
            TableStriped = "#0F1624",
            TableHover = "#1A2340",
            Divider = "#243047",
            Skeleton = "#1F2A3D",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = Font, FontSize = ".9375rem", LineHeight = "1.5", LetterSpacing = "normal" },
            H4 = new H4Typography { FontFamily = Font, FontSize = "1.625rem", FontWeight = "700", LineHeight = "1.25", LetterSpacing = "-.015em" },
            H5 = new H5Typography { FontFamily = Font, FontSize = "1.375rem", FontWeight = "700", LineHeight = "1.3", LetterSpacing = "-.01em" },
            H6 = new H6Typography { FontFamily = Font, FontSize = "1.0625rem", FontWeight = "650", LineHeight = "1.4", LetterSpacing = "normal" },
            Subtitle1 = new Subtitle1Typography { FontFamily = Font, FontSize = "1rem", FontWeight = "600", LineHeight = "1.5" },
            Subtitle2 = new Subtitle2Typography { FontFamily = Font, FontSize = ".875rem", FontWeight = "600", LineHeight = "1.5" },
            Body1 = new Body1Typography { FontFamily = Font, FontSize = ".9375rem", LineHeight = "1.55" },
            Body2 = new Body2Typography { FontFamily = Font, FontSize = ".875rem", LineHeight = "1.5" },
            Button = new ButtonTypography { FontFamily = Font, FontSize = ".875rem", FontWeight = "600", TextTransform = "none", LetterSpacing = ".005em" },
            Caption = new CaptionTypography { FontFamily = Font, FontSize = ".8rem", LineHeight = "1.45" },
            Overline = new OverlineTypography { FontFamily = Font, FontSize = ".7rem", FontWeight = "700", LetterSpacing = ".08em" },
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "8px",
            AppbarHeight = "60px",
            DrawerWidthLeft = "264px",
        },
    };
}

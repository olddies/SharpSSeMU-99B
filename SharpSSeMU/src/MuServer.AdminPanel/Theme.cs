using MudBlazor;

namespace MuServer.AdminPanel;

/// <summary>Paleta oscura del panel -- lo que reemplaza al Bootstrap por defecto de OpenMU.</summary>
public static class AdminTheme
{
    public static readonly MudTheme Theme = new()
    {
        PaletteDark = new PaletteDark
        {
            Primary = "#8b5cf6",
            Secondary = "#22d3ee",
            Tertiary = "#f59e0b",
            Background = "#0b0e14",
            Surface = "#12161f",
            AppbarBackground = "#12161f",
            DrawerBackground = "#0b0e14",
            DrawerText = "#c7cad1",
            TextPrimary = "#e8eaed",
            TextSecondary = "#9aa0ac",
            ActionDefault = "#9aa0ac",
            LinesDefault = "#22262f",
            TableLines = "#22262f",
            Divider = "#22262f",
            Success = "#34d399",
            Error = "#f87171",
            Warning = "#fbbf24",
            Info = "#60a5fa",
        },
        PaletteLight = new PaletteLight
        {
            Primary = "#7c3aed",
            Secondary = "#0891b2",
            Tertiary = "#d97706",
            Background = "#f5f6f8",
            Surface = "#ffffff",
            AppbarBackground = "#ffffff",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = ["Inter", "-apple-system", "sans-serif"] },
            H1 = new H1Typography { FontFamily = ["Inter", "sans-serif"], FontWeight = "700" },
            H2 = new H2Typography { FontFamily = ["Inter", "sans-serif"], FontWeight = "700" },
            H3 = new H3Typography { FontFamily = ["Inter", "sans-serif"], FontWeight = "600" },
            H6 = new H6Typography { FontFamily = ["Inter", "sans-serif"], FontWeight = "600" },
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "10px",
        },
    };
}

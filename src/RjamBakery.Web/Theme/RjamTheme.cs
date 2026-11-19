using MudBlazor;

namespace RjamBakery.Web.Theme;

/// <summary>
/// Warm artisanal bakery palette inspired by the reference UI while keeping minimalist operational clarity.
/// </summary>
public static class RjamTheme
{
    public static readonly MudTheme BakeryTheme = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#C88A55",
            PrimaryContrastText = "#FFFFFF",
            PrimaryDarken = "#B37440",
            PrimaryLighten = "#DFAA7D",

            Secondary = "#2E1C16",
            SecondaryContrastText = "#FFFFFF",

            Tertiary = "#6F5B53",
            TertiaryContrastText = "#FFFFFF",

            Background = "#F7F1EB",
            BackgroundGray = "#EFE5DC",
            Surface = "#FFFFFF",
            DrawerBackground = "#2E1C16",
            DrawerText = "#F7F1EB",
            DrawerIcon = "#DFAA7D",

            AppbarBackground = "#FFFFFF",
            AppbarText = "#2E1C16",

            TextPrimary = "#2E1C16",
            TextSecondary = "#6F5B53",
            TextDisabled = "#A6968E",

            LinesDefault = "#E6DBD1",
            LinesInputs = "#D8C9BC",
            TableLines = "#EAE0D7",
            Divider = "#E6DBD1",

            Success = "#3E7B5A",
            SuccessContrastText = "#FFFFFF",
            Warning = "#D99B26",
            WarningContrastText = "#2E1C16",
            Error = "#B84A39",
            ErrorContrastText = "#FFFFFF",
            Info = "#5D6D7E",
            InfoContrastText = "#FFFFFF"
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "10px",
            DrawerWidthLeft = "256px",
            AppbarHeight = "60px"
        },
        Typography = new Typography
        {
            Default = new DefaultTypography
            {
                FontFamily = ["Segoe UI", "system-ui", "-apple-system", "Roboto", "sans-serif"],
                FontSize = "0.875rem"
            },
            H4 = new H4Typography { FontWeight = "700", LetterSpacing = "-0.01em" },
            H5 = new H5Typography { FontWeight = "700", LetterSpacing = "-0.01em" },
            H6 = new H6Typography { FontWeight = "600" }
        }
    };
}

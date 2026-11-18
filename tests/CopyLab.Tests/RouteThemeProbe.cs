using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace FolderTwin;

internal static class RouteThemeProbe
{
    internal static async Task RunAsync()
    {
        var window = new Window { Width = 600, Height = 500, ShowInTaskbar = false };
        window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("ProductRoute.xaml", UriKind.Relative) });
        var button = new Button { Content = "Prueba de contraste", Style = (Style)window.FindResource("RoutePrimary") };
        window.Content = button;
        RouteTheme.SetUsePalette(window, true);
        try
        {
            window.Show();
#pragma warning disable WPF0001
            foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                window.ThemeMode = theme;
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                var background = (SolidColorBrush)window.Resources["RouteCanvas"];
                bool dark = theme == ThemeMode.Dark;
                if (dark != (background.Color.R < 100))
                    throw new InvalidDataException("Ruta did not follow the selected window theme.");
                foreach (var pair in new[] { ("RouteInk", "RoutePaper"), ("RouteMuted", "RoutePaper"),
                    ("RouteOnAccent", "RouteAccent"), ("RouteWarning", "RouteWarningFill") })
                    if (Contrast(Brush(pair.Item1).Color, Brush(pair.Item2).Color) < 4.5)
                        throw new InvalidDataException("A Ruta text pair failed its contrast requirement.");
                if (!ReferenceEquals(button.Background, window.Resources[PrimaryBackground()]) ||
                    !ReferenceEquals(button.Foreground, window.Resources["RouteOnAccent"]))
                    throw new InvalidDataException("Ruta controls kept a stale theme brush.");
                var dialog = new ExistingBackupWindow(() => throw new IOException("Theme fixture cannot open configuration.")) { Owner = window };
                try
                {
                    dialog.Show();
                    await dialog.Dispatcher.InvokeAsync(dialog.UpdateLayout, DispatcherPriority.ApplicationIdle);
                    if (dialog.ThemeMode != theme ||
                        ((SolidColorBrush)dialog.Background).Color != background.Color)
                        throw new InvalidDataException("The recovery dialog differs from its owner's theme.");
                }
                finally { dialog.Close(); }
            }
#pragma warning restore WPF0001
            RouteTheme.Apply(window, dark: false, highContrast: true);
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
            if (Brush("RouteInk").Color != SystemColors.WindowTextColor ||
                Brush("RoutePaper").Color != SystemColors.WindowColor ||
                Brush("RouteSelectedFill").Color != SystemColors.HighlightColor ||
                Brush("RouteSelectedInk").Color != SystemColors.HighlightTextColor ||
                Brush("RouteDisabledInk").Color != SystemColors.GrayTextColor ||
                (double)window.Resources["RouteDisabledOpacity"] != 1.0 ||
                !ReferenceEquals(button.Background, window.Resources[PrimaryBackground()]))
                throw new InvalidDataException("The contrast palette did not use the Windows system colors.");
            Console.WriteLine("PASS Ruta light/dark text contrast, dynamic brush changes, shared recovery dialog theme and synthetic high-contrast palette.");
        }
        finally { window.Close(); }

        SolidColorBrush Brush(string key) => (SolidColorBrush)window.Resources[key];
        string PrimaryBackground() => button.IsMouseOver || button.IsKeyboardFocused ? "RouteAccentHover" : "RouteAccent";
    }

    private static double Contrast(Color first, Color second)
    {
        double a = Luminance(first), b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
    private static double Luminance(Color color) =>
        .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
    private static double Linear(byte channel)
    {
        double value = channel / 255.0;
        return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
    }
}

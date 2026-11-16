using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace CopyLab;

internal static class RouteTheme
{
    internal static readonly DependencyProperty UsePaletteProperty = DependencyProperty.RegisterAttached(
        "UsePalette", typeof(bool), typeof(RouteTheme), new PropertyMetadata(false, Attach));
    public static void SetUsePalette(DependencyObject owner, bool value) => owner.SetValue(UsePaletteProperty, value);
    public static bool GetUsePalette(DependencyObject owner) => (bool)owner.GetValue(UsePaletteProperty);

    private static void Attach(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        if (owner is Window window && args.NewValue is true) _ = new WindowPalette(window);
    }

    private sealed class WindowPalette
    {
        private readonly Window window;
        private string mode = "";
        private bool systemDark;
        private bool closed;

        internal WindowPalette(Window window)
        {
            this.window = window;
            systemDark = ReadSystemDark();
            Refresh();
            window.SetResourceReference(Window.BackgroundProperty, "RouteCanvas");
            window.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "RouteInk");
            window.LayoutUpdated += LayoutUpdated;
            window.Loaded += Loaded;
            window.Closed += Closed;
            SystemEvents.UserPreferenceChanged += PreferencesChanged;
            SystemParameters.StaticPropertyChanged += ParametersChanged;
        }

        private static bool ReadSystemDark() =>
            Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1) is int value && value == 0;

        private void PreferencesChanged(object sender, UserPreferenceChangedEventArgs args) => ScheduleRefresh();
        private void ParametersChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(SystemParameters.HighContrast)) ScheduleRefresh();
        }
        private void ScheduleRefresh()
        {
            if (closed || window.Dispatcher.HasShutdownStarted) return;
            window.Dispatcher.BeginInvoke(() =>
            {
                if (closed) return;
                systemDark = ReadSystemDark();
                Refresh();
            });
        }
        private void LayoutUpdated(object? sender, EventArgs args)
        {
            if (mode != EffectiveMode()) Refresh();
        }
        private void Loaded(object sender, RoutedEventArgs args)
        {
#pragma warning disable WPF0001
            if (window.Owner is { } owner) window.ThemeMode = owner.ThemeMode;
#pragma warning restore WPF0001
            Refresh();
        }
        private string EffectiveMode()
        {
#pragma warning disable WPF0001
            var selected = window.ThemeMode == ThemeMode.None ? Application.Current.ThemeMode : window.ThemeMode;
#pragma warning restore WPF0001
            return selected.ToString();
        }
        private void Refresh()
        {
            mode = EffectiveMode();
            Apply(window, mode == "Dark" || mode != "Light" && systemDark, SystemParameters.HighContrast);
        }
        private void Closed(object? sender, EventArgs args)
        {
            closed = true;
            window.LayoutUpdated -= LayoutUpdated;
            window.Loaded -= Loaded;
            window.Closed -= Closed;
            SystemEvents.UserPreferenceChanged -= PreferencesChanged;
            SystemParameters.StaticPropertyChanged -= ParametersChanged;
        }
    }

    internal static void Apply(Window window, bool dark, bool highContrast)
    {
        var colors = dark ? new[]
        {
            "#14231F", "#1C3029", "#EDF6F1", "#B8CCC1", "#486358", "#8DDDC3",
            "#B3EFDA", "#263F34", "#192C25", "#FFDA9B", "#49351A", "#063A2D"
        } : new[]
        {
            "#EAF1EE", "#F9FCF9", "#142B27", "#48615A", "#C5D6CE", "#12634F",
            "#0B513F", "#DCECE4", "#E3ECE6", "#774715", "#FAEBCE", "#FFFFFF"
        };
        string[] keys =
        [
            "RouteCanvas", "RoutePaper", "RouteInk", "RouteMuted", "RouteRule", "RouteAccent",
            "RouteAccentHover", "RouteSoft", "RouteRail", "RouteWarning", "RouteWarningFill", "RouteOnAccent"
        ];
        for (int index = 0; index < keys.Length; index++)
        {
            Color color = highContrast ? index is 2 or 3 or 4 or 5 or 6 or 9
                ? SystemColors.WindowTextColor : SystemColors.WindowColor :
                (Color)ColorConverter.ConvertFromString(colors[index]);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            window.Resources[keys[index]] = brush;
        }
        var map = new DrawingBrush(new GeometryDrawing((Brush)window.Resources["RouteSoft"], null, new RectangleGeometry(new Rect(0, 0, 15, 15))));
        if (!highContrast)
        {
            var drawing = new DrawingGroup();
            drawing.Children.Add(new GeometryDrawing((Brush)window.Resources["RouteSoft"], null, new RectangleGeometry(new Rect(0, 0, 15, 15))));
            drawing.Children.Add(new GeometryDrawing((Brush)window.Resources["RouteRule"], null, new EllipseGeometry(new Point(7.5, 7.5), 1, 1)));
            map.Drawing = drawing;
            map.TileMode = TileMode.Tile;
            map.Viewport = map.Viewbox = new Rect(0, 0, 15, 15);
            map.ViewportUnits = map.ViewboxUnits = BrushMappingMode.Absolute;
            map.Stretch = Stretch.None;
        }
        map.Freeze();
        window.Resources["RouteMap"] = map;
    }
}

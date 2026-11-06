using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;

namespace CopyLab;

internal static class ProductIdentity
{
    internal static string Read()
    {
        string file = Path.Combine(AppContext.BaseDirectory, "COMPILACION.json");
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 4096) throw new InvalidDataException("Identity exceeds its limit.");
            using var json = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 4 });
            string? revision = json.RootElement.GetProperty("sourceRevision").GetString();
            string? runtime = json.RootElement.GetProperty("runtimeVersion").GetString();
            if (revision is null || !Regex.IsMatch(revision, "^[0-9a-f]{40}$") ||
                runtime is null || !Regex.IsMatch(runtime, @"^\d{1,3}\.\d{1,3}\.\d{1,3}$"))
                throw new InvalidDataException("Invalid package identity.");
            return "FolderTwin · candidata de software\nCompilación · " + revision + "\nRuntime incluido · " + runtime;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return "FolderTwin · compilación de desarrollo sin identidad de paquete verificada";
        }
    }
}

internal sealed class ProductHelpWindow : Window
{
    internal TabControl Pages { get; } = new();
    internal Button CloseButton { get; } = new() { Content = "_Cerrar ayuda" };
    internal Button PrepareButton { get; } = new() { Content = "_Preparar resumen", Margin = new(0, 0, 8, 0) };
    internal Button CopyButton { get; } = new() { Content = "_Copiar resumen revisado", IsEnabled = false };
    internal TextBox Diagnostic { get; } = new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new(12)
    };

    internal ProductHelpWindow((string Label, string Text)[] entries, Func<string> diagnostic)
    {
        Title = "FolderTwin · Ayuda y versión";
        Width = 700; Height = 620; MinWidth = 500; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var grid = new Grid { Margin = new(16) };
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = ProductIdentity.Read(), TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12) });
        Grid.SetRow(Pages, 1); grid.Children.Add(Pages);
        var context = new StackPanel { Margin = new(12) };
        foreach (var entry in entries)
        {
            var section = new Expander { Header = entry.Label, Margin = new(0, 0, 0, 8), IsExpanded = entries.Length <= 3 };
            section.Content = new TextBlock { Text = entry.Text, TextWrapping = TextWrapping.Wrap, Margin = new(12, 4, 4, 8) };
            AutomationProperties.SetName(section, entry.Label);
            context.Children.Add(section);
        }
        Pages.Items.Add(new TabItem { Header = "Esta _pantalla", Content = new ScrollViewer { Content = context, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        Pages.Items.Add(new TabItem { Header = "_Guía de uso", Content = Guide("GUIA.md") });
        Pages.Items.Add(new TabItem { Header = "_Soporte", Content = Guide("SUPPORT.md") });
        var diagnostics = new DockPanel();
        var buttons = new WrapPanel { Margin = new(12) };
        PrepareButton.Click += (_, _) => { Diagnostic.Text = diagnostic(); CopyButton.IsEnabled = true; Diagnostic.Focus(); };
        CopyButton.Click += (_, _) =>
        {
            try { Clipboard.SetText(Diagnostic.Text); }
            catch (System.Runtime.InteropServices.COMException) { Diagnostic.Text += "\nNo se pudo copiar. Selecciona el texto y usa Ctrl+C."; }
        };
        buttons.Children.Add(PrepareButton); buttons.Children.Add(CopyButton);
        DockPanel.SetDock(buttons, Dock.Top); diagnostics.Children.Add(buttons);
        Diagnostic.Text = "Prepara un resumen local de versión, entorno y recuentos de estado. Revísalo antes de copiar. Luego abre el formulario de soporte y describe los pasos con datos sintéticos.";
        AutomationProperties.SetName(Diagnostic, "Diagnóstico local para revisar antes de compartir");
        diagnostics.Children.Add(Diagnostic);
        Pages.Items.Add(new TabItem { Header = "_Diagnóstico", Content = diagnostics });
        Grid.SetRow(CloseButton, 2);
        CloseButton.HorizontalAlignment = HorizontalAlignment.Right;
        CloseButton.Margin = new(0, 12, 0, 0);
        CloseButton.Click += (_, _) => Close();
        grid.Children.Add(CloseButton);
        Content = grid;
        Loaded += (_, _) => Pages.Focus();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
    }

    private static FlowDocumentScrollViewer Guide(string name)
    {
        var document = new FlowDocument { PagePadding = new(16), FontSize = 14 };
        string file = Path.Combine(AppContext.BaseDirectory, name);
        string text;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 256 * 1024) throw new InvalidDataException("Guide too large.");
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { text = "La guía no está disponible en esta compilación. Extrae el paquete completo y vuelve a abrir Ayuda."; }
        foreach (string line in text.Split('\n'))
        {
            string plain = line.Trim();
            if (plain.Length == 0 || Regex.IsMatch(plain, @"^\|?[-:| ]+\|?$")) continue;
            bool heading = plain.StartsWith('#');
            plain = plain.TrimStart('#').Trim().Replace("**", "").Replace(((char)96).ToString(), "");
            var paragraph = new Paragraph { Margin = new(0, 0, 0, 10), FontSize = heading ? 19 : 14,
                FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal };
            int position = 0;
            foreach (Match match in Regex.Matches(plain, @"\[([^\]]+)\]\(([^)]+)\)"))
            {
                paragraph.Inlines.Add(plain[position..match.Index]);
                string target = match.Groups[2].Value;
                if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
                    uri.Host is "github.com" or "learn.microsoft.com" or "sqlite.org" or "www.sqlite.org")
                {
                    var link = new Hyperlink(new Run(match.Groups[1].Value)) { NavigateUri = uri };
                    link.RequestNavigate += (_, e) =>
                    {
                        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
                        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
                        e.Handled = true;
                    };
                    paragraph.Inlines.Add(link);
                }
                else paragraph.Inlines.Add(match.Groups[1].Value);
                position = match.Index + match.Length;
            }
            paragraph.Inlines.Add(plain[position..]);
            document.Blocks.Add(paragraph);
        }
        return new() { Document = document, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, IsToolBarVisible = false };
    }
}

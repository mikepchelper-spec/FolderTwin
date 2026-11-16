using System.IO;
using System.Security.Cryptography;
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
    internal sealed record Package(string Revision, string Runtime, string ManifestHash);

    internal static string Read()
    {
        var package = ReadPackage(Environment.ProcessPath);
        return package is null ? "FolderTwin · compilación de desarrollo sin identidad de paquete verificada" :
            "FolderTwin · candidata de software\nCompilación · " + package.Revision + "\nRuntime incluido · " + package.Runtime;
    }

    internal static Package? ReadPackage(string? executable)
    {
        if (executable is null || Path.GetDirectoryName(executable) is not { } root) return null;
        try
        {
            using var fence = new DirectoryFence(root);
            using var manifest = WindowsProbeNative.OpenPlainFile(fence.Root, "SHA256SUMS.txt", FileAccess.Read, FileShare.Read);
            byte[] bytes = LabInstallation.ReadManifestBytes(manifest);
            var files = LabInstallation.ParseManifest(bytes);
            foreach (string name in new[] { Path.GetFileName(executable), "CopyLab.dll", "CopyProbe.dll" })
            {
                using var file = WindowsProbeNative.OpenPlainFile(fence.Root, name, FileAccess.Read, FileShare.Read);
                if (!files.TryGetValue(name, out var digest) || !SHA256.HashData(file).AsSpan().SequenceEqual(digest)) return null;
            }
            using var stream = WindowsProbeNative.OpenPlainFile(fence.Root, "COMPILACION.json", FileAccess.Read, FileShare.Read);
            if (stream.Length is < 1 or > 4096 || !files.TryGetValue("COMPILACION.json", out var identityHash) ||
                !SHA256.HashData(stream).AsSpan().SequenceEqual(identityHash)) return null;
            stream.Position = 0;
            using var json = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 4 });
            string? revision = json.RootElement.GetProperty("sourceRevision").GetString();
            string? runtime = json.RootElement.GetProperty("runtimeVersion").GetString();
            if (revision is null || !Regex.IsMatch(revision, "^[0-9a-f]{40}$") ||
                runtime is null || !Regex.IsMatch(runtime, @"^\d{1,3}\.\d{1,3}\.\d{1,3}$"))
                throw new InvalidDataException("Invalid package identity.");
            return new(revision, runtime, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        catch (Exception error) when (VersionJob.Expected(error) || error is InvalidOperationException or KeyNotFoundException)
        {
            return null;
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

    internal ProductHelpWindow((string Label, string Text)[] entries, Func<string> diagnostic, Func<string>? executors = null)
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
        var version = new TextBlock { Text = "Ejecutable abierto\n" + Environment.ProcessPath, TextWrapping = TextWrapping.Wrap, Margin = new(12) };
        if (executors is not null)
            Loaded += async (_, _) =>
            {
                string summary = await Task.Run(executors);
                if (IsVisible) version.Text += "\n\n" + summary;
            };
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
        Pages.Items.Add(new TabItem { Header = "_Ejecutores", Content = new ScrollViewer { Content = version, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
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

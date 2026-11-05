using System.IO;
using System.Windows;

namespace CopyLab;

public partial class ProductWindow
{
    // Development-only desktop fixture. Never opens the user's job registry.
    internal static async Task OpenSyntheticPreviewAsync(string? variant = null)
    {
        var fixture = LabWorkspace.Create();
        string settings = Path.Combine(fixture.Root, "settings");
        Directory.CreateDirectory(settings);
        ProductJobRegistry Open() => new(settings);
        await Task.Run(() =>
        {
            using var registry = Open();
            foreach (var mode in Enum.GetValues<ProductBackupMode>())
            {
                string source = Directory.CreateDirectory(Path.Combine(fixture.Root, "source-" + mode)).FullName;
                string parent = Directory.CreateDirectory(Path.Combine(fixture.Root, "target-" + mode)).FullName;
                string documents = Directory.CreateDirectory(Path.Combine(source, "Documentos")).FullName;
                File.WriteAllText(Path.Combine(documents, "Propuesta.txt"), "Contenido sintético de la propuesta.");
                File.WriteAllText(Path.Combine(source, "Notas.txt"), "Contenido sintético de notas.");
                var job = registry.Create(new(mode switch
                {
                    ProductBackupMode.Accumulative => "Documentos · copia acumulativa",
                    ProductBackupMode.Versions => "Proyectos · versiones",
                    _ => "Archivo · espejo protegido"
                }, mode, null), LocalJobRoots.ReviewDedicatedDestination(source, parent));
                var result = ProductRunner.Run(registry, job.Id);
                if (result.State != ProductRunState.Confirmed) throw new IOException(result.Detail ?? "No se pudo crear el respaldo sintético.");
            }
        });
        string installations = Directory.CreateDirectory(Path.Combine(fixture.Root, "installations")).FullName;
        var window = new ProductWindow(Open, testSchedule: (_, _, _) => { },
            testPackageInstall: () => LabInstallation.EnsureAt(AppContext.BaseDirectory, installations), testInstallations: installations)
        { Title = "FolderTwin · Prueba desechable" };
        if (variant == "compact") { window.Width = 600; window.Height = 500; }
        // The pinned WPF SDK still labels the theme API experimental; this override is confined to the visual fixture.
#pragma warning disable WPF0001
        if (variant == "light") window.ThemeMode = ThemeMode.Light;
#pragma warning restore WPF0001
        Application.Current.MainWindow = window;
        Application.Current.ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Closed += (_, _) =>
        {
            try
            {
                using (fixture.OpenRegisteredRoot()) { }
                using (var registry = Open())
                    if (registry.List().Jobs.Any(job => registry.RunHistory(job.Id).Runs.Any(run => run.Running))) return;
                Directory.Delete(fixture.Root, recursive: true);
            }
            catch (Exception error) when (VersionJob.Expected(error))
            { Console.Error.WriteLine("Se conserva la sesión desechable para revisión: " + error.Message); }
        };
        window.Show();
        await window.InitializeAsync();
    }
}

using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace CopyLab;

public partial class ProductWindow
{
    internal static async Task RunUsabilitySmokeAsync()
    {
        var fixture = LabWorkspace.Create();
        ProductWindow? window = null;
        try
        {
            using (var root = fixture.OpenRegisteredRoot())
                if (LabWorkspace.AvailableSpace(root.Root) < 256UL * 1024 * 1024) throw new IOException("Usability fixture requires 256 MiB reserve.");
            string source = Folder("source"), parent = Folder("backups"), settings = Folder("private-settings");
            string secret = "PRIVATE-NAME-must-not-be-in-diagnostic";
            File.WriteAllText(Path.Combine(source, "sample.txt"), "Independent known synthetic content.");
            Directory.CreateDirectory(Path.Combine(source, "Empty"));
            var picks = new Queue<string>();
            ProductJobRegistry Open() => new(settings);
            window = new ProductWindow(Open, _ => picks.Dequeue(), testSchedule: (_, _, _) => { });
            window.Show();
            await window.InitializeAsync();
            Check(window.FirstBackupButton.IsVisible && !window.BackupJobButton.IsEnabled, "Empty-state onboarding is unavailable.");
            var controls = Controls(window).Where(control => control is ButtonBase or TextBox or ComboBox or ListBox or Expander).ToArray();
            Check(controls.All(control => window.helpEntries.ContainsKey(control.Name) && control.ToolTip is ToolTip &&
                ToolTipService.GetShowOnDisabled(control) && AutomationProperties.GetHelpText(control).Length > 10),
                "A product control has no contextual help or disabled-control help.");
            foreach (var mode in Enum.GetValues<ProductBackupMode>())
            {
                await Click(window, mode == ProductBackupMode.Accumulative ? window.FirstBackupButton : window.NewJobButton);
                Check(window.HelpFor(window.NextStepButton).Contains("Para habilitar", StringComparison.Ordinal), "A disabled next step has no prerequisite.");
                var tooltip = (ToolTip)window.NextStepButton.ToolTip;
                tooltip.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.ToolTip.OpenedEvent));
                Check(((TextBlock)tooltip.Content).Text.Contains("Elige la carpeta", StringComparison.Ordinal), "Hover help did not expose the missing selection.");
                window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.F1)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Check(window.helpWindow is { IsVisible: true }, "F1 did not open help.");
                await ClickHelpCloseAsync();
                picks.Enqueue(source); picks.Enqueue(parent);
                await Click(window, window.ChooseOriginButton); await Click(window, window.NextStepButton);
                await Click(window, window.ChooseParentButton); await Click(window, window.NextStepButton);
                window.JobName.Text = secret + mode;
                window.BackupMode.SelectedIndex = (int)mode;
                Check(window.ModeDetail.Text.Contains("Por ejemplo", StringComparison.Ordinal), "A mode has no plain-language example.");
                await Click(window, window.NextStepButton); await Click(window, window.SaveJobButton);
                var job = ((JobRow)window.JobList.SelectedItem).Job!;
                Check(window.NextProtectionStep.Text.Contains("primera copia", StringComparison.Ordinal), "Saving skipped first-copy guidance.");
                await Click(window, window.BackupJobButton);
                if (mode == ProductBackupMode.ProtectedMirror) await Click(window, window.ApplyMirrorButton);
                Check(window.NextProtectionStep.Text.Contains("verifica", StringComparison.Ordinal), "Completed backup did not guide verification.");
                await Click(window, window.RecoverJobButton);
                await Click(window, window.VerifyPointButton);
                string outputParent = Folder("restore-" + mode);
                picks.Enqueue(outputParent);
                await Click(window, window.ReviewRestoreButton);
                Check(window.RestoreReviewText.Text.Contains("carpeta nueva", StringComparison.Ordinal), "Restore review omitted the new output.");
                await Click(window, window.RestorePointButton);
                string output = Directory.EnumerateDirectories(outputParent).Single();
                Check(File.ReadAllText(Path.Combine(output, "sample.txt")) == "Independent known synthetic content." &&
                    Directory.Exists(Path.Combine(output, "Empty")), "Restored bytes or empty directory differ.");
                string? opened = null;
                window.TestOpenRestoredFolder = path => opened = path;
                await Click(window, window.OpenRecoveryOutputButton);
                Check(opened is not null, "Open restored folder did not use the completed request. " + window.StatusText.Text);
                using (var expectedOutput = new DirectoryFence(output))
                using (var openedOutput = new DirectoryFence(opened!))
                    Check(WindowsProbeNative.DirectoryIdentity(expectedOutput.Root).AsSpan().SequenceEqual(
                        WindowsProbeNative.DirectoryIdentity(openedOutput.Root)), "Opened another recovery folder.");
                await Click(window, window.ReturnToJobsButton);
                await Click(window, window.OpenRestoredFolderButton);
                string diagnostic = window.BuildDiagnostic();
                Check(!diagnostic.Contains(secret, StringComparison.Ordinal) && !diagnostic.Contains(fixture.Root, StringComparison.OrdinalIgnoreCase) &&
                    !diagnostic.Contains(job.Id.ToString("N"), StringComparison.OrdinalIgnoreCase) &&
                    !diagnostic.Contains("sample.txt", StringComparison.Ordinal), "Diagnostic included private job or file information.");
                await Click(window, window.HelpButton);
                Check(window.helpWindow!.Pages.Items.Count == 4, "Guide, support or diagnostic is missing.");
                var help = window.helpWindow;
                help.Pages.SelectedIndex = 3;
                Check(!help.CopyButton.IsEnabled, "The diagnostic was ready to copy before review.");
                help.PrepareButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(help.Diagnostic.Text == diagnostic && help.CopyButton.IsEnabled, "Preparing the local diagnostic changed its safe fields.");
                foreach (int page in new[] { 1, 2 })
                {
                    string file = Path.Combine(AppContext.BaseDirectory, page == 1 ? "GUIA.md" : "SUPPORT.md");
                    if (!File.Exists(file)) continue; // Development output has no bundled guides; package acceptance requires them.
                    var viewer = (FlowDocumentScrollViewer)((TabItem)help.Pages.Items[page]).Content;
                    string document = new System.Windows.Documents.TextRange(viewer.Document.ContentStart, viewer.Document.ContentEnd).Text;
                    Check(document.Contains(page == 1 ? "Crear un trabajo" : "Evidencia mínima", StringComparison.Ordinal),
                        "The packaged guide or support page did not render.");
                }
                await ClickHelpCloseAsync();
            }
            ProductJob versioned;
            using (var registry = Open()) versioned = registry.List().Jobs.Single(job => job.Options.Mode == ProductBackupMode.Versions);
            window.SelectJob(versioned.Id);
            string completeDate = window.RuntimeStatusText.Text.Split('\n').Single(line => line.StartsWith("Último respaldo completo", StringComparison.Ordinal));
            using (var occupied = new FileStream(Path.Combine(source, "sample.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                await Click(window, window.BackupJobButton);
                Check(window.StatusText.Text.Contains("en uso", StringComparison.Ordinal), "Occupied file had no actionable explanation.");
            }
            string detached = Path.Combine(fixture.Root, "source-absent");
            Directory.Move(source, detached);
            try
            {
                await Click(window, window.BackupJobButton);
                Check(window.StatusText.Text.Contains("Reconecta", StringComparison.Ordinal), "Missing source had no reconnection action.");
                await Click(window, window.RecoverJobButton);
                await Click(window, window.VerifyPointButton);
                await Click(window, window.ReturnToJobsButton);
                Check(window.RuntimeStatusText.Text.Contains("respaldo requiere atención", StringComparison.Ordinal) &&
                    window.RuntimeStatusText.Text.Contains(completeDate, StringComparison.Ordinal),
                    "Verifying an old point hid an unresolved backup or changed its date.");
            }
            finally { Directory.Move(detached, source); }
            string target = versioned.Destination.LastVolumePath;
            string detachedTarget = target + "-synthetic-absent";
            Directory.Move(target, detachedTarget);
            try
            {
                await Click(window, window.BackupJobButton);
                Check(window.StatusText.Text.Contains("destino", StringComparison.Ordinal) &&
                    window.StatusText.Text.Contains("Reconecta", StringComparison.Ordinal), "Missing disk had no reconnection action.");
                Directory.CreateDirectory(target);
                File.Copy(Path.Combine(detachedTarget, DestinationBinding.MarkerName), Path.Combine(target, DestinationBinding.MarkerName));
                await Click(window, window.BackupJobButton);
                Check(!window.StatusText.Text.StartsWith("Respaldo completo", StringComparison.Ordinal) &&
                    window.StatusText.Text.Contains("Comprueba las carpetas", StringComparison.Ordinal), "A substituted destination had no review action.");
                Check(Directory.GetFileSystemEntries(target).Length == 1, "The substituted synthetic target was modified.");
                File.Delete(Path.Combine(target, DestinationBinding.MarkerName));
                Directory.Delete(target);
            }
            finally
            {
                if (Directory.Exists(target)) throw new IOException("Preserve substituted synthetic target for investigation.");
                Directory.Move(detachedTarget, target);
            }
            using (var registry = Open())
            using (var attempt = registry.StartRun(versioned.Id))
                _ = attempt.Finish(ProductRunState.Review, errorCode: unchecked((int)0x80070070), detail: "Synthetic capacity error.");
            await window.RefreshRuntimeAsync();
            Check(window.StatusText.Text.Contains("Espacio insuficiente", StringComparison.Ordinal) &&
                window.StatusText.Text.Contains("diario", StringComparison.Ordinal), "Disk-full presentation did not explain preserved partial state.");
            await Click(window, window.BackupJobButton);
            Check(window.StatusText.Text.StartsWith("Respaldo completo", StringComparison.Ordinal), "Retry failed after resolving synthetic incidents.");
            await window.CheckDesktopLayoutAsync();
#pragma warning disable WPF0001
            foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                window.ThemeMode = theme;
#pragma warning restore WPF0001
                window.Width = 600; window.Height = 500;
                window.EditJobName.Text = new string('W', 100);
                window.JobSettingsPanel.IsExpanded = true;
                window.JobDetailsScroll.ScrollToBottom();
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                var button = window.BackupJobButton;
                var bounds = button.TranslatePoint(new Point(), window);
                Check(button.IsVisible && button.ActualHeight > 0 && bounds.Y >= 0 &&
                    bounds.Y + button.ActualHeight <= window.ActualHeight && !button.IsDescendantOf(window.JobDetailsScroll),
                    "Primary actions scrolled away at minimum size.");
                await Click(window, window.HelpButton);
                await ClickHelpCloseAsync();
            }
            Console.Error.WriteLine($"PASS usability: {controls.Length} controls with contextual/disabled help, F1 and click help, safe diagnostic, first-copy guidance, three modes via wizard, verify/restore/open output, sticky primary actions and keyboard focus in both themes. Only synthetic data; no native tasks.");
            async Task ClickHelpCloseAsync()
            {
                var help = window.helpWindow!;
                help.CloseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Check(window.helpWindow is null && FocusManager.GetFocusedElement(window) == window.HelpButton,
                    "Closing help did not restore keyboard focus.");
            }
        }
        finally
        {
            window?.Close();
            string root = Path.GetFullPath(fixture.Root);
            if (Path.GetDirectoryName(root) != Path.TrimEndingDirectorySeparator(Path.GetTempPath()) ||
                !Path.GetFileName(root).StartsWith(LabWorkspace.Prefix, StringComparison.Ordinal)) throw new IOException("Wrong cleanup scope.");
            using (fixture.OpenRegisteredRoot()) { }
            Directory.Delete(root, recursive: true);
        }
        string Folder(string name) => Directory.CreateDirectory(Path.Combine(fixture.Root, name)).FullName;
    }

    private static IEnumerable<Control> Controls(DependencyObject parent)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is Control control && control.Name.Length > 0) yield return control;
            if (child is DependencyObject node)
                foreach (var descendant in Controls(node)) yield return descendant;
        }
    }
}

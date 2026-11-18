using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;

namespace FolderTwin;

public partial class MainWindow
{
    internal async Task RunSmokeAsync()
    {
        Click(SourceButton); // Cancelled selection.
        Check(!sourceSelected && !CopyButton.IsEnabled, "Cancellation changed selection.");
        Click(SourceButton); // Generated folder, but the wrong role.
        Check(!sourceSelected && !CopyButton.IsEnabled, "Wrong source enabled copying.");
        Click(SourceButton);
        Check(sourceSelected && !CopyButton.IsEnabled, "Copy requires a destination.");
        Click(DestinationButton);
        Check(AnalyzeButton.IsEnabled && !CopyButton.IsEnabled && !RestoreButton.IsEnabled,
            "Copy became available before analysis.");
        File.WriteAllText(session.CopyFile, "synthetic conflict");
        File.SetCreationTimeUtc(session.CopyFile, new DateTime(1995, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        try
        {
            Click(AnalyzeButton);
            await activeTask;
            Check(!analyzed && !CopyButton.IsEnabled && File.ReadAllText(session.CopyFile) == "synthetic conflict" &&
                StatusText.Text.StartsWith("El análisis no se completó.", StringComparison.Ordinal), "Analysis accepted or changed a conflicting destination.");
        }
        finally
        {
            File.Delete(session.CopyFile);
        }
        Click(AnalyzeButton);
        await activeTask;
        Check(analyzed && CopyButton.IsEnabled && PreviewText.Text.Contains("3 archivos", StringComparison.Ordinal) &&
            PreviewText.Text.Contains("bytes de contenido", StringComparison.Ordinal),
            "The valid preview did not enable the copy.");
        string firstFile = session.Plan!.Files[0].RelativePath;
        bool pauseRequested = false;
        copyCheckpoint = (_, step, _) =>
        {
            if (step == CopyStep.Writing && !pauseRequested)
                Dispatcher.Invoke(() =>
                {
                    Check(OperationProgress.Visibility == Visibility.Visible && FileProgress.Maximum == 3 && FileProgress.Value == 0 &&
                        ProgressText.Text.Contains(firstFile, StringComparison.Ordinal),
                        "The active copy did not show its first file and total.");
                    pauseRequested = true;
                    Click(PauseCopyButton);
                }, System.Windows.Threading.DispatcherPriority.Background);
        };
        Click(CopyButton);
        await WaitForPauseAsync();
        try
        {
            Check(FileProgress.Value == 1 && File.Exists(Path.Combine(session.Destination, firstFile)) &&
                !File.Exists(session.CopyFile) && !activeTask.IsCompleted,
                "Pausing did not finish the current file before stopping.");
        }
        finally { Click(CancelCopyButton); }
        await activeTask;
        copyCheckpoint = null;
        Check(StatusText.Text.StartsWith("Copia cancelada.", StringComparison.Ordinal) && CopyButton.IsEnabled &&
            !RestoreButton.IsEnabled && CancelCopyButton.Visibility == Visibility.Collapsed &&
            OperationProgress.Visibility == Visibility.Collapsed &&
            File.Exists(Path.Combine(session.Destination, firstFile)) && !File.Exists(session.CopyFile) &&
            !File.Exists(Path.Combine(session.Destination, BatchCatalog.CommitName)) &&
            !Directory.EnumerateFiles(session.Destination, "*.tmp", SearchOption.AllDirectories).Any(),
            "Cancelling during a file did not leave the completed file and a retryable batch.");
        using (var writer = new FileStream(session.SourceFile, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            Click(CopyButton);
            await activeTask;
            Check(!copied && !File.Exists(session.CopyFile) && !RestoreButton.IsEnabled &&
                StatusText.Text.StartsWith("Resultado parcial: 2 de 3", StringComparison.Ordinal) &&
                ResultsList.Items.Cast<string>().Any(row => row.Contains("está en uso", StringComparison.Ordinal)) &&
                !AnalyzeButton.IsEnabled && CopyButton.IsEnabled, "A partial batch or its retry was hidden.");
        }
        session.ForgetPlan();
        bool sawRetryProgress = false;
        copyCheckpoint = (path, step, _) =>
        {
            if (step == CopyStep.Writing)
                Dispatcher.Invoke(() =>
                {
                    sawRetryProgress = FileProgress.Value == 1 && ProgressText.Text.Contains("1 verificados", StringComparison.Ordinal) &&
                        ProgressText.Text.Contains(path, StringComparison.Ordinal);
                    Click(PauseCopyButton);
                }, System.Windows.Threading.DispatcherPriority.Background);
        };
        Click(CopyButton);
        await WaitForPauseAsync();
        try
        {
            Check(FileProgress.Value == 2 && !File.Exists(Path.Combine(session.Destination, BatchCatalog.CommitName)),
                "The paused retry did not stop before confirming the batch.");
        }
        finally { Click(PauseCopyButton); }
        await activeTask;
        copyCheckpoint = null;
        Check(sawRetryProgress && copied && File.Exists(session.CopyFile) && RestoreButton.IsEnabled, StatusText.Text);
        session.ForgetPlan();
        string primaryCatalog = Path.Combine(session.Destination, BatchCatalog.PrimaryName);
        string backupCatalog = Path.Combine(session.Destination, BatchCatalog.BackupName);
        byte[] intactCatalog = File.ReadAllBytes(backupCatalog);
        File.WriteAllText(primaryCatalog, "truncated\n");
        File.WriteAllText(backupCatalog, "truncated\n");
        Click(RestoreButton);
        await activeTask;
        Check(!restored && !File.Exists(session.RestoredFile) && RestoreButton.IsEnabled &&
            StatusText.Text.StartsWith("La prueba no se completó.", StringComparison.Ordinal),
            "Invalid catalogs enabled restoration or hid the error.");
        File.WriteAllBytes(backupCatalog, intactCatalog);
        Click(RestoreButton);
        await activeTask;
        Check(restored && !RestoreButton.IsEnabled && StatusText.Text.Contains("réplica íntegra", StringComparison.Ordinal), StatusText.Text);
        session.CheckRestored();
        string[] ownHistory = HistoryList.Items.Cast<string>().ToArray();
        Check(ownHistory.Length == 5 && ownHistory[0].Contains("Se usó una sola réplica", StringComparison.Ordinal) &&
            ownHistory[1].Contains("Sin completar", StringComparison.Ordinal) &&
            ownHistory[3].Contains("muestra.txt · Archivo en uso", StringComparison.Ordinal) &&
            ownHistory[4].Contains("Copia · Cancelada · 1 archivo verificado", StringComparison.Ordinal),
            "Copy or restore attempt history was lost.");

        using var interrupted = new BatchFixture();
        interrupted.Analyze();
        using (var writer = new FileStream(interrupted.SourceFile, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            BatchResult partial = interrupted.Copy();
            Check(partial.VerifiedFiles == 2, "The previous session was not left partial.");
            AttemptHistory.Append(interrupted.Root, interrupted.DestinationBinding, AttemptOperation.Copy, partial);
        }
        string interruptedLog = Path.Combine(interrupted.Root, AttemptHistory.Name);
        string legacyRecord = File.ReadAllText(interruptedLog)
            .Replace(",\"Verify\":false", "", StringComparison.Ordinal)
            .Replace(",\"Cancelled\":false", "", StringComparison.Ordinal);
        File.WriteAllText(interruptedLog, legacyRecord + "{\"FinishedAt\":");
        interrupted.ForgetPlan();

        var reopened = new MainWindow(smoke: true);
        try
        {
            reopened.testSelections!.Clear();
            reopened.testSelections.Enqueue(Path.GetTempPath());
            reopened.testSelections.Enqueue(interrupted.Root);
            Click(reopened.OpenPreviousButton);
            await reopened.activeTask;
            Check(reopened.previous is null && reopened.OpenPreviousButton.IsEnabled, "An unrelated folder was accepted.");
            Click(reopened.OpenPreviousButton);
            await reopened.activeTask;
            Check(reopened.CopyButton.IsEnabled && !reopened.RestoreButton.IsEnabled && !File.Exists(interrupted.CopyFile),
                "Opening a pending session wrote files or showed the wrong actions.");
            Check(reopened.HistoryList.Items.Count == 1 &&
                reopened.HistoryStatus.Text.Contains("ilegibles: 1", StringComparison.Ordinal),
                "A torn history record hid a previous valid attempt.");
            Click(reopened.CopyButton);
            await reopened.activeTask;
            Check(reopened.copied && reopened.RestoreButton.IsEnabled, reopened.StatusText.Text);
            Check(reopened.HistoryList.Items.Count == 2 &&
                ((string)reopened.HistoryList.Items[1]).Contains("Archivo en uso", StringComparison.Ordinal),
                "A successful retry replaced an older incident or failed to append after a torn record.");
        }
        finally { reopened.Close(); }
        Check(File.Exists(interrupted.CopyFile), "Closing a reopened session removed its data.");

        var confirmedWindow = new MainWindow(smoke: true);
        try
        {
            confirmedWindow.testSelections!.Clear();
            confirmedWindow.testSelections.Enqueue(interrupted.Root);
            Click(confirmedWindow.OpenPreviousButton);
            await confirmedWindow.activeTask;
            Check(confirmedWindow.RestoreButton.IsEnabled && confirmedWindow.VerifyButton.IsEnabled && !confirmedWindow.CopyButton.IsEnabled,
                "A confirmed session did not offer restoration.");
            byte[] original = File.ReadAllBytes(interrupted.CopyFile);
            byte[] altered = (byte[])original.Clone();
            altered[0] ^= 1;
            string empty = Path.Combine(interrupted.Destination, BatchFixture.SampleNames[2]);
            DateTime originalDate = File.GetLastWriteTimeUtc(interrupted.CopyFile);
            DateTime emptyDate = File.GetLastWriteTimeUtc(empty);
            DateTime emptyCreation = File.GetCreationTimeUtc(empty);
            File.WriteAllBytes(interrupted.CopyFile, altered);
            File.Delete(empty);
            try
            {
                Click(confirmedWindow.VerifyButton);
                await confirmedWindow.activeTask;
                string[] findings = confirmedWindow.ResultsList.Items.Cast<string>().ToArray();
                Check(confirmedWindow.StatusText.Text.StartsWith("Resultado parcial: 1 de 3", StringComparison.Ordinal) &&
                    findings.Any(row => row.StartsWith("muestra.txt", StringComparison.Ordinal) && row.Contains("no coincide", StringComparison.Ordinal)) &&
                    findings.Any(row => row.StartsWith("vacío.bin", StringComparison.Ordinal) && row.Contains("Falta", StringComparison.Ordinal)),
                    "Verification failed to identify both changed and missing files.");
                Check(File.ReadAllBytes(interrupted.CopyFile).SequenceEqual(altered) && !File.Exists(empty) &&
                    !File.Exists(confirmedWindow.session.RestoredFile), "Verification changed or restored data.");
            }
            finally
            {
                File.WriteAllBytes(interrupted.CopyFile, original);
                File.WriteAllBytes(empty, []);
                File.SetLastWriteTimeUtc(interrupted.CopyFile, originalDate);
                File.SetCreationTimeUtc(empty, emptyCreation);
                File.SetLastWriteTimeUtc(empty, emptyDate);
            }
            Click(confirmedWindow.VerifyButton);
            await confirmedWindow.activeTask;
            Check(confirmedWindow.StatusText.Text.StartsWith("Respaldo verificado", StringComparison.Ordinal) &&
                ((string)confirmedWindow.HistoryList.Items[0]).Contains("Verificación · Completa", StringComparison.Ordinal) &&
                ((string)confirmedWindow.HistoryList.Items[1]).Contains("Verificación · Parcial", StringComparison.Ordinal),
                "Verification results were not kept in history.");
            using (var logLock = new FileStream(interruptedLog, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Click(confirmedWindow.RestoreButton);
                await confirmedWindow.activeTask;
                Check(confirmedWindow.restored && !File.Exists(interrupted.RestoredFile), confirmedWindow.StatusText.Text);
                Check(confirmedWindow.HistoryStatus.Text.Contains("No se pudo guardar este intento", StringComparison.Ordinal),
                    "A history write failure was hidden or prevented restoration.");
            }
            confirmedWindow.session.CheckRestored();
        }
        finally { confirmedWindow.Close(); }

        var alternateWindow = new MainWindow(smoke: true);
        try
        {
            alternateWindow.testSelections!.Clear();
            alternateWindow.testSelections.Enqueue(alternateWindow.session.AlternateSource);
            alternateWindow.testSelections.Enqueue(alternateWindow.session.Destination);
            Click(alternateWindow.SourceButton);
            Click(alternateWindow.DestinationButton);
            Click(alternateWindow.AnalyzeButton);
            await alternateWindow.activeTask;
            Check(alternateWindow.CopyButton.IsEnabled &&
                alternateWindow.PreviewText.Text.Contains("4 archivos", StringComparison.Ordinal),
                "The alternate source was not reflected in the preview.");
            string extra = Path.Combine(alternateWindow.session.AlternateSource, "resumen ñ.txt");
            using (var writer = new FileStream(extra, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
                Click(alternateWindow.CopyButton);
                await alternateWindow.activeTask;
                Check(alternateWindow.StatusText.Text.StartsWith("Resultado parcial: 3 de 4", StringComparison.Ordinal),
                    "The alternate session did not report the actual partial file count.");
            }

            var retryWindow = new MainWindow(smoke: true);
            try
            {
                retryWindow.testSelections!.Clear();
                retryWindow.testSelections.Enqueue(alternateWindow.session.Root);
                Click(retryWindow.OpenPreviousButton);
                await retryWindow.activeTask;
                Check(retryWindow.previous?.Source == alternateWindow.session.AlternateSource && retryWindow.CopyButton.IsEnabled,
                    "The alternate pending session reopened with the wrong source.");
                Click(retryWindow.CopyButton);
                await retryWindow.activeTask;
                Check(retryWindow.copied && retryWindow.VerifyButton.IsEnabled &&
                    retryWindow.HistoryList.Items.Cast<string>().Any(row => row.Contains("resumen ñ.txt · Archivo en uso", StringComparison.Ordinal)),
                    "Retry or the alternate history failed.");
                Click(retryWindow.VerifyButton);
                await retryWindow.activeTask;
                Check(retryWindow.StatusText.Text.Contains("Coinciden los 4 archivos", StringComparison.Ordinal),
                    "The alternate confirmed catalog failed verification.");
                Click(retryWindow.RestoreButton);
                await retryWindow.activeTask;
                Check(retryWindow.restored && File.ReadAllBytes(extra).AsSpan().SequenceEqual(
                    File.ReadAllBytes(Path.Combine(retryWindow.session.Restored, Path.GetFileName(extra)))),
                    "The alternate file did not restore.");
            }
            finally { retryWindow.Close(); }
        }
        finally { alternateWindow.Close(); }

        await RunWorkspaceSmokeAsync();
        await VersionWindow.RunSmokeAsync();
        await RunCompatibleDemonstrationSmokeAsync();
        await TrayProbe.RunAsync();

        async Task WaitForPauseAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                while (!StatusText.Text.StartsWith("Copia en pausa.", StringComparison.Ordinal) && !activeTask.IsCompleted)
                    await Task.Delay(15, deadline.Token);
                Check(!activeTask.IsCompleted, "The operation completed without pausing.");
            }
            catch
            {
                Click(CancelCopyButton);
                await activeTask;
                throw;
            }
        }

        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        static void Check(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
    }

    private static async Task RunWorkspaceSmokeAsync()
    {
        var initial = new MainWindow(smoke: true);
        LabWorkspace? current = null;
        try
        {
            try
            {
                Click(initial.CreateWorkspaceButton);
                await initial.activeTask;
                current = initial.workspace ?? throw new InvalidOperationException(initial.StatusText.Text);
                string source = Directory.CreateDirectory(Path.Combine(current.Root, "Entrada café")).FullName;
                string target = Directory.CreateDirectory(Path.Combine(current.Root, "Respaldo 漢字")).FullName;
                string file = Path.Combine(source, "informe ñ.txt");
                File.WriteAllText(file, "contenido de la prueba de interfaz");
                File.WriteAllBytes(Path.Combine(source, "segunda prueba.bin"), [12, 34]);
                initial.testSelections!.Clear();
                initial.testSelections.Enqueue(Path.GetTempPath());
                initial.testSelections.Enqueue(source);
                initial.testSelections.Enqueue(target);
                Click(initial.SourceButton);
                Check(!initial.sourceSelected, "A path outside the workspace was selected.");
                Click(initial.SourceButton);
                Click(initial.DestinationButton);
                Click(initial.AnalyzeButton);
                await initial.activeTask;
                Check(initial.CopyButton.IsEnabled && initial.PreviewText.Text.Contains("2 archivos", StringComparison.Ordinal) &&
                    initial.PreviewText.Text.Contains("Espacio disponible al analizar", StringComparison.Ordinal) &&
                    !Directory.EnumerateFileSystemEntries(target).Any(), "The configurable preview failed or modified the destination.");
                using (var writer = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                {
                    Click(initial.CopyButton);
                    await initial.activeTask;
                    Check(initial.StatusText.Text.StartsWith("Resultado parcial: 1 de 2", StringComparison.Ordinal) &&
                        initial.HistoryList.Items.Cast<string>().Any(row => row.Contains("informe ñ.txt · Archivo en uso", StringComparison.Ordinal)),
                        "A partial configurable job did not report its actual progress and history.");
                }
            }
            finally { initial.Close(); }

            var reopened = new MainWindow(smoke: true);
            try
            {
                string source = Path.Combine(current.Root, "Entrada café");
                string target = Path.Combine(current.Root, "Respaldo 漢字");
                string second = Directory.CreateDirectory(Path.Combine(current.Root, "Otro destino")).FullName;
                string output = Directory.CreateDirectory(Path.Combine(current.Root, "Restauración elegida")).FullName;
                byte[] hash = SHA256.HashData(File.ReadAllBytes(Path.Combine(source, "informe ñ.txt")));
                reopened.testSelections!.Clear();
                reopened.testSelections.Enqueue(current.Root);
                reopened.testSelections.Enqueue(target);
                reopened.testSelections.Enqueue(output);
                reopened.testSelections.Enqueue(second);
                Click(reopened.OpenWorkspaceButton);
                await reopened.activeTask;
                Click(reopened.OpenWorkspaceJobButton);
                await reopened.activeTask;
                Check(reopened.workspaceJob is { Confirmed: false } && reopened.CopyButton.IsEnabled &&
                    reopened.HistoryList.Items.Count == 1, "The configurable job did not reopen with its history.");
                Click(reopened.CopyButton);
                await reopened.activeTask;
                Check(reopened.workspaceJob is { Confirmed: true } && reopened.VerifyButton.IsEnabled, reopened.StatusText.Text);
                Click(reopened.VerifyButton);
                await reopened.activeTask;
                Check(reopened.StatusText.Text.Contains("Coinciden los 2 archivos", StringComparison.Ordinal), reopened.StatusText.Text);
                Check(current.Start(current.Analyze(source, second)).Complete, "Creating the independent history fixture failed.");
                Directory.Move(source, Path.Combine(current.Root, "Origen apartado"));
                Click(reopened.RestoreButton);
                await reopened.activeTask;
                Check(reopened.restored && File.ReadAllBytes(Path.Combine(output, "segunda prueba.bin")).SequenceEqual(new byte[] { 12, 34 }) &&
                    SHA256.HashData(File.ReadAllBytes(Path.Combine(output, "informe ñ.txt"))).AsSpan().SequenceEqual(hash) &&
                    reopened.HistoryList.Items.Count == 4, "Restoration without the source failed or was not logged.");
                Click(reopened.OpenWorkspaceJobButton);
                await reopened.activeTask;
                Check(reopened.workspaceJob?.Destination == second && reopened.HistoryList.Items.Count == 0,
                    "The second destination inherited history from another job.");
            }
            finally { reopened.Close(); }
        }
        finally
        {
            if (current is not null)
                Directory.Delete(current.Root, recursive: true); // The test created this workspace and every entry in it.
        }

        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        static void Check(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
    }
}

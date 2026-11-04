using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using Microsoft.Win32;

namespace CopyLab;

public partial class MainWindow : Window
{
    private readonly BatchFixture session;
    private readonly Queue<string?>? testSelections;
    private PreviousSession? previous;
    private bool sourceSelected;
    private bool destinationSelected;
    private bool analyzed;
    private bool copyStarted;
    private bool copied;
    private bool restored;
    private bool busy;
    private CancellationTokenSource? copyCancellation;
    private ManualResetEventSlim? copyPause;
    private Action<string, CopyStep, string>? copyCheckpoint;
    private Task activeTask = Task.CompletedTask;

    internal MainWindow(bool smoke)
    {
        InitializeComponent();
        session = new BatchFixture();
        SessionText.Text = $"Sesión nueva · {Path.GetFileName(session.Root)}\nSe conserva en la carpeta temporal de Windows cuando inicias una copia.";
        if (smoke)
            testSelections = new Queue<string?>([null, session.Destination, session.Source, session.Destination]);
    }

    private void ChooseSource(object sender, RoutedEventArgs e) =>
        SelectFolder("Elegir origen", session.Source, SourceText, ref sourceSelected);

    private void ChooseDestination(object sender, RoutedEventArgs e) =>
        SelectFolder("Elegir destino", session.Destination, DestinationText, ref destinationSelected);

    private void SelectFolder(string title, string expected, TextBlock label, ref bool selected)
    {
        string? candidate = PickFolder(title);
        if (candidate is null)
            return;
        selected = string.Equals(Path.TrimEndingDirectorySeparator(candidate), expected, StringComparison.OrdinalIgnoreCase);
        analyzed = false;
        PreviewText.Text = "Analiza las carpetas para revisar qué se copiará.";
        string folder = Path.GetFileName(expected);
        label.Text = selected ? $"{folder} · carpeta de prueba" : $"Elige la carpeta {folder} del laboratorio.";
        SetStatus(selected
            ? sourceSelected && destinationSelected ? "Todo listo para analizar las carpetas de prueba." : "Selecciona la otra carpeta."
            : $"Para esta prueba, elige la carpeta {folder}.");
        RefreshActions();
    }

    private string? PickFolder(string title, string? root = null)
    {
        if (testSelections is not null)
            return testSelections.Dequeue();
        var dialog = new OpenFolderDialog
        {
            Title = title,
            RootDirectory = root ?? session.Root,
            InitialDirectory = root ?? session.Root,
            AddToRecent = false,
            DereferenceLinks = false
        };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private void OpenPreviousClick(object sender, RoutedEventArgs e)
    {
        if (!OpenPreviousButton.IsEnabled)
            return;
        string? root = PickFolder("Elige la carpeta FolderTwin-Batch de la sesión", Path.GetTempPath());
        if (root is not null)
            activeTask = OpenPreviousAsync(Path.TrimEndingDirectorySeparator(root));
    }

    private async Task OpenPreviousAsync(string root)
    {
        busy = true;
        RefreshActions();
        SetStatus("Comprobando la sesión anterior…");
        try
        {
            var candidate = await Task.Run(() => new PreviousSession(root));
            bool confirmed = candidate.Confirmed;
            previous = candidate;
            copied = confirmed;
            analyzed = true;
            SourceText.Text = "Origen de la sesión anterior";
            DestinationText.Text = "Destino de la sesión anterior";
            SessionText.Text = $"Sesión abierta · {Path.GetFileName(candidate.Root)}";
            PreviewText.Text = confirmed
                ? "Catálogo confirmado. La restauración verifica los archivos y escribe en una carpeta nueva de este laboratorio."
                : "Lote pendiente. Al reintentar se comprobará lo guardado y se mostrarán las incidencias por archivo.";
            ResultsList.ItemsSource = null;
            SetStatus(confirmed ? "Sesión anterior abierta. Puedes restaurar el lote." : "Sesión anterior abierta. Puedes reintentar la copia.");
            await RefreshHistoryAsync();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or Win32Exception or ArgumentException or JsonException)
        {
            SetStatus("No se pudo abrir la sesión. " + error.Message);
        }
        finally
        {
            busy = false;
            RefreshActions();
        }
    }

    private void AnalyzeClick(object sender, RoutedEventArgs e)
    {
        if (AnalyzeButton.IsEnabled)
            activeTask = AnalyzeAsync();
    }

    private async Task AnalyzeAsync()
    {
        busy = true;
        analyzed = false;
        PreviewText.Text = "Analizando las carpetas…";
        SetStatus("Analizando sin modificar los archivos…");
        RefreshActions();
        try
        {
            BatchPlan plan = await Task.Run(session.Analyze);
            analyzed = true;
            PreviewText.Text = $"{plan.Files.Length:N0} archivos · {plan.Bytes:N0} bytes de contenido · {plan.Directories.Length:N0} carpetas\n" +
                "Se conservan las fechas de creación y modificación y el atributo Oculto de los archivos. Los permisos se heredan de la carpeta de destino.\n" +
                "La copia puede ocupar más espacio en disco que un original comprimido o disperso.\n" +
                string.Join("\n", plan.Files.Select(file => file.RelativePath));
            ResultsList.ItemsSource = null;
            SetStatus("Revisión completada. Puedes copiar el lote de prueba.");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or Win32Exception or ArgumentException)
        {
            PreviewText.Text = "No hay una vista previa válida para copiar.";
            SetStatus("El análisis no se completó. " + error.Message);
        }
        finally
        {
            busy = false;
            RefreshActions();
        }
    }

    private void CopyClick(object sender, RoutedEventArgs e)
    {
        if (CopyButton.IsEnabled)
            activeTask = RunAsync(AttemptOperation.Copy);
    }

    private void RestoreClick(object sender, RoutedEventArgs e)
    {
        if (RestoreButton.IsEnabled)
            activeTask = RunAsync(AttemptOperation.Restore);
    }

    private void VerifyClick(object sender, RoutedEventArgs e)
    {
        if (VerifyButton.IsEnabled)
            activeTask = RunAsync(AttemptOperation.Verify);
    }

    private void CancelCopyClick(object sender, RoutedEventArgs e)
    {
        if (copyCancellation is not { IsCancellationRequested: false } cancellation)
            return;
        cancellation.Cancel();
        RefreshActions();
        SetStatus("Cancelación solicitada. Espera a que termine el paso actual.");
    }

    private void PauseCopyClick(object sender, RoutedEventArgs e)
    {
        if (!PauseCopyButton.IsEnabled || copyPause is not { } pause)
            return;
        if (pause.IsSet)
        {
            pause.Reset();
            SetStatus("Pausa solicitada. Espera a que termine el paso actual.");
        }
        else
        {
            pause.Set();
            SetStatus("Continuando la copia…");
        }
        RefreshActions();
    }

    private async Task RunAsync(AttemptOperation operation)
    {
        bool restore = operation == AttemptOperation.Restore;
        using var cancellation = operation == AttemptOperation.Copy ? new CancellationTokenSource() : null;
        using var pause = cancellation is null ? null : new ManualResetEventSlim(initialState: true);
        copyCancellation = cancellation;
        copyPause = pause;
        CancellationToken token = cancellation?.Token ?? CancellationToken.None;
        busy = true;
        RefreshActions();
        ResultsList.ItemsSource = null;
        SetStatus(operation switch
        {
            AttemptOperation.Verify => "Verificando el respaldo…",
            AttemptOperation.Restore => "Restaurando y verificando…",
            _ => "Copiando y verificando…"
        });
        if (operation == AttemptOperation.Copy && previous is null)
            copyStarted = true;
        bool showProgress = true;
        OperationProgress.Visibility = Visibility.Visible;
        FileProgress.IsIndeterminate = true;
        ProgressText.Text = "Preparando la operación…";
        var progress = new Progress<BatchProgress>(update =>
        {
            if (!showProgress || token.IsCancellationRequested)
                return;
            ShowProgress(update, update.CurrentPath is { } path ? $"En curso · {path}" : "Preparando el resultado final…");
        });
        BatchResult? outcome = null;
        try
        {
            BatchResult result = await Task.Run(() => operation switch
            {
                AttemptOperation.Verify => BatchCatalog.Verify(previous?.Destination ?? session.Destination,
                    previous?.Binding ?? session.DestinationBinding, progress),
                AttemptOperation.Restore => previous?.Restore(session, progress) ?? session.Restore(progress),
                _ => previous?.Resume(token, progress, WaitWhilePaused) ?? session.Copy(token, copyCheckpoint, progress, WaitWhilePaused)
            });
            HideProgress();
            copyCancellation = null;
            copyPause = null;
            RefreshActions();
            outcome = result;
            if (restore)
                restored = result.Complete;
            else if (operation == AttemptOperation.Copy)
                copied = result.Complete;
            ResultsList.ItemsSource = result.Entries.Select(entry => $"{entry.RelativePath} · {Describe(entry, operation)}").ToArray();
            string[] inUse = result.Entries.Where(entry => entry.InUse).Take(16)
                .Select(entry => Path.Combine(operation != AttemptOperation.Copy ? previous?.Destination ?? session.Destination : previous?.Source ?? session.Source,
                    entry.RelativePath)).ToArray();
            var users = await Task.Run(() => FileUseDiagnostics.TryGetUsers(inUse));
            string[] applications = users.Select(user => user.Application.Trim())
                .Where(name => name.Length != 0).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
            string detail = applications.Length == 0 ? "" : " Aplicaciones detectadas por Windows: " + string.Join(", ", applications) + ".";
            string status = result.Cancelled
                ? $"Copia cancelada. Se conserva lo ya verificado ({result.VerifiedFiles:N0} de {BatchFixture.SampleNames.Length:N0}). Puedes reintentar el lote."
                : result.Complete
                ? operation switch
                {
                    AttemptOperation.Verify => $"Respaldo verificado. Coinciden los {result.VerifiedFiles:N0} archivos y sus carpetas.",
                    AttemptOperation.Restore => $"Restauración verificada: {result.VerifiedFiles:N0} archivos y sus carpetas.",
                    _ => $"Lote copiado y verificado: {result.VerifiedFiles:N0} archivos. Ya puedes restaurarlo."
                }
                : $"Resultado parcial: {result.VerifiedFiles:N0} de {BatchFixture.SampleNames.Length:N0} archivos verificados. Revisa las incidencias y reintenta.";
            SetStatus(status + (result.Problem is null ? "" : " " + result.Problem) +
                (result.Note is null ? "" : " " + result.Note) + detail);
            if (restore && result.Complete)
                PreviewText.Text = $"Archivos restaurados en {session.Restored}" +
                    (previous is null ? "" : "\nEsta carpeta de prueba se retira al cerrar la ventana.");
        }
        catch (Exception error) when (IsLabError(error))
        {
            outcome ??= new([], "La operación necesita revisión.");
            SetStatus($"La prueba no se completó. {error.Message}");
        }
        finally
        {
            HideProgress();
            copyCancellation = null;
            copyPause = null;
            RefreshActions();
            if (outcome is not null)
                await RefreshHistoryAsync(outcome, operation);
            busy = false;
            RefreshActions();
        }

        void HideProgress()
        {
            showProgress = false;
            OperationProgress.Visibility = Visibility.Collapsed;
        }

        void WaitWhilePaused(BatchProgress update)
        {
            if (pause is null || pause.IsSet)
                return;
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (copyPause == pause && !pause.IsSet && !token.IsCancellationRequested)
                {
                    ShowProgress(update, "En pausa entre archivos");
                    SetStatus("Copia en pausa. Puedes continuarla o cancelarla.");
                }
            });
            pause.Wait(token);
        }
    }

    private void ShowProgress(BatchProgress update, string stage)
    {
        FileProgress.IsIndeterminate = false;
        FileProgress.Maximum = Math.Max(1, update.TotalFiles);
        FileProgress.Value = update.ProcessedFiles;
        ProgressText.Text = $"{update.ProcessedFiles:N0} de {update.TotalFiles:N0} procesados · {update.VerifiedFiles:N0} verificados\n{stage}";
    }

    private async Task RefreshHistoryAsync(BatchResult? outcome = null, AttemptOperation operation = AttemptOperation.Copy)
    {
        string root = previous?.Root ?? session.Root;
        var binding = previous?.Binding ?? session.DestinationBinding;
        bool appendFailed = false;
        if (outcome is not null)
        {
            try { await Task.Run(() => AttemptHistory.Append(root, binding, operation, outcome)); }
            catch (Exception error) when (IsLabError(error)) { appendFailed = true; }
        }

        try
        {
            AttemptLog log = await Task.Run(() => AttemptHistory.Read(root, binding));
            HistoryList.ItemsSource = log.Records.Take(20).Select(DescribeAttempt).ToArray();
            HistoryStatus.Text = log.Records.Length == 0 ? "Aún no hay intentos registrados."
                : $"Se muestran {Math.Min(20, log.Records.Length):N0} de {log.Records.Length:N0} intentos, del más reciente al más antiguo.";
            if (log.Unreadable != 0)
                HistoryStatus.Text += $" Registros incompletos o ilegibles: {log.Unreadable:N0}.";
        }
        catch (Exception error) when (IsLabError(error))
        {
            HistoryList.ItemsSource = null;
            HistoryStatus.Text = "El historial no está disponible.";
        }
        if (appendFailed)
            HistoryStatus.Text += " No se pudo guardar este intento.";
    }

    private static string DescribeAttempt(AttemptRecord record)
    {
        string action = record.Verify ? "Verificación" : record.Restore ? "Restauración" : "Copia";
        string state = record.Cancelled ? "Cancelada" : record.Complete ? "Completa" :
            record.VerifiedFiles == 0 ? "Sin completar" : "Parcial";
        var lines = new List<string>
        {
            $"{record.FinishedAt.ToLocalTime():g} · {action} · {state} · " +
                (record.VerifiedFiles == 1 ? "1 archivo verificado" : $"{record.VerifiedFiles:N0} archivos verificados")
        };
        if (record.HasProblem)
            lines.Add("La operación requiere revisión.");
        if (record.Degraded)
            lines.Add("Se usó una sola réplica del catálogo.");
        foreach (var issue in record.Issues)
        {
            string reason = issue.Kind switch
            {
                AttemptIssueKind.InUse => "Archivo en uso",
                AttemptIssueKind.Cancelled => "Cancelado",
                _ => "Requiere revisión"
            };
            lines.Add($"{issue.Path} · {reason}");
        }
        return string.Join("\n", lines);
    }

    private static bool IsLabError(Exception error) => error is IOException or InvalidDataException or
        UnauthorizedAccessException or Win32Exception or ArgumentException or JsonException;

    private void RefreshActions()
    {
        OpenPreviousButton.IsEnabled = !busy && !copyStarted && previous is null;
        SourceButton.IsEnabled = DestinationButton.IsEnabled = OpenPreviousButton.IsEnabled;
        AnalyzeButton.IsEnabled = OpenPreviousButton.IsEnabled && sourceSelected && destinationSelected;
        CopyButton.IsEnabled = !busy && !copied && analyzed;
        CopyButton.Content = (copyStarted || previous is not null) && !copied ? "_Reintentar copia" : "_Copiar y verificar";
        RestoreButton.IsEnabled = !busy && copied && !restored;
        VerifyButton.IsEnabled = !busy && copied;
        CancelCopyButton.Visibility = copyCancellation is null ? Visibility.Collapsed : Visibility.Visible;
        CancelCopyButton.IsEnabled = copyCancellation is { IsCancellationRequested: false };
        PauseCopyButton.Visibility = CancelCopyButton.Visibility;
        PauseCopyButton.IsEnabled = CancelCopyButton.IsEnabled;
        PauseCopyButton.Content = copyPause is { IsSet: false } ? "C_ontinuar copia" : "_Pausar copia";
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
        UIElementAutomationPeer.FromElement(StatusText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private static string Describe(BatchEntryResult entry, AttemptOperation operation) => entry.Status switch
    {
        BatchStatus.Copied => "Copia verificada",
        BatchStatus.Recovered => "Temporal recuperado y verificado",
        BatchStatus.AlreadyVerified => "Copia existente verificada",
        BatchStatus.DirectoryReady => operation == AttemptOperation.Verify ? "Carpeta presente" : "Carpeta preparada",
        _ => entry.Detail ?? "Pendiente de revisión"
    };

    protected override void OnClosing(CancelEventArgs e)
    {
        if (busy)
        {
            e.Cancel = true;
            SetStatus("Espera a que termine la operación para cerrar la ventana.");
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        try
        {
            if (!copyStarted || testSelections is not null)
                session.Dispose();
        }
        catch (Exception error) when (testSelections is null &&
            error is IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception)
        {
            MessageBox.Show("La carpeta temporal necesita revisión. " + error.Message,
                "FolderTwin · Laboratorio", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        base.OnClosed(e);
    }

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
}

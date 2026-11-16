using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace CopyLab;

public partial class ProductCloudWindow : Window
{
    private sealed record DateChoice(CloudRecoveryPoint Recovery)
    {
        public string Label => $"{VersionStore.GenerationTime(Recovery.Generation).ToLocalTime():g} · {Recovery.Point.UserFiles} archivos · {Recovery.Job.ToString("N")[..8]}";
    }

    private sealed record TransferChoice(CloudTransfer Transfer)
    {
        public string Label => $"{VersionStore.GenerationTime(Transfer.Ticket.Generation).ToLocalTime():G} · {TransferState(Transfer)} · {Transfer.Ticket.Operation.ToString("N")[..8]}";
    }

    private readonly Func<ProductJobRegistry> openRegistry;
    private readonly ProductJob? job;
    private readonly Func<string> kopia;
    private readonly Action<ProductJobRegistry> startMonitor;
    private readonly Func<CloudDestination, CloudCredentials, CloudRepositorySession>? testSession;
    private readonly Func<ProductJobRegistry, CancellationToken, Task>? testDispatch;
    private readonly Func<string>? testOutput;
    private readonly CancellationTokenSource closed = new();
    private CloudConnection? readConnection;
    private const int TransferPageSize = 100;
    private CloudTransfer[] transfers = [];
    private int transferOffset;

    internal ProductCloudWindow(Func<ProductJobRegistry> openRegistry, ProductJob? job, Func<string> kopia,
        Action<ProductJobRegistry> startMonitor, Func<CloudDestination, CloudCredentials, CloudRepositorySession>? testSession = null,
        Func<ProductJobRegistry, CancellationToken, Task>? testDispatch = null, Func<string>? testOutput = null)
    {
        this.openRegistry = openRegistry;
        this.job = job;
        this.kopia = kopia;
        this.startMonitor = startMonitor;
        this.testSession = testSession;
        this.testDispatch = testDispatch;
        this.testOutput = testOutput;
        InitializeComponent();
        CloudHeading.Text = job is null ? "Recuperar una copia en nube" : $"Copia en nube · {job.Options.Name}";
        CreateCloud.IsEnabled = ConnectCloud.IsEnabled = job is { Enabled: true, Retired: false };
        NewRepositoryReview.Visibility = job is null ? Visibility.Collapsed : Visibility.Visible;
        RefreshCloud.IsEnabled = job is not null;
        Loaded += async (_, _) => await RunAsync(_ =>
        {
            if (job is not null)
            {
                using var registry = openRegistry();
                if (registry.ReadCloudConnection(job.Id) is { } saved)
                {
                    Provider.SelectedIndex = (int)saved.Destination.Provider;
                    Endpoint.Text = saved.Destination.Endpoint;
                    Region.Text = saved.Destination.Region;
                    Bucket.Text = saved.Destination.Bucket;
                    Prefix.Text = saved.Destination.Prefix;
                    NewRepositoryReview.IsExpanded = false;
                }
                ReadStatus(registry);
            }
            return Task.CompletedTask;
        });
        Closed += (_, _) => closed.Cancel();
    }

    private void ProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SessionToken is null) return;
        var provider = (CloudProvider)Provider.SelectedIndex;
        bool remote = provider is CloudProvider.Filen or CloudProvider.Mega;
        Region.Visibility = RegionLabel.Visibility = Endpoint.Visibility = EndpointLabel.Visibility =
            Bucket.Visibility = BucketLabel.Visibility = remote ? Visibility.Collapsed : Visibility.Visible;
        Endpoint.IsEnabled = provider == CloudProvider.CompatibleS3;
        PrefixLabel.Content = remote ? "Carpeta dedicada del repositorio" : "Carpeta del repositorio (opcional)";
        AccessKeyLabel.Content = remote ? "_Correo de la cuenta" : "_Identificador de acceso";
        SecretKeyLabel.Content = remote ? "_Contraseña de la cuenta" : "_Clave de acceso";
        SessionToken.Visibility = SessionTokenLabel.Visibility = provider == CloudProvider.Mega ? Visibility.Collapsed : Visibility.Visible;
        SessionTokenLabel.Content = provider == CloudProvider.Filen ? "Clave API de Filen" : "Token de sesión (opcional)";
        ProviderHelp.Text = remote
            ? "Proveedor experimental. La conexión con una cuenta real y la recuperación desde otro equipo siguen pendientes. Usa una carpeta dedicada y guarda la contraseña del repositorio fuera de este equipo."
            : "Vista previa para carpetas sintéticas. Usa un bucket privado y guarda la contraseña del repositorio fuera de este equipo.";
        AccessKey.Clear();
        SecretKey.Clear();
        SessionToken.Clear();
    }

    private CloudDestination Destination()
    {
        var provider = (CloudProvider)Provider.SelectedIndex;
        string prefix = Prefix.Text.Trim().Trim('/');
        if (provider is CloudProvider.Filen or CloudProvider.Mega)
            return new(provider, "", "", "", prefix.Length == 0 ? "" : prefix + "/");
        string region = Region.Text.Trim();
        string endpoint = provider switch
        {
            CloudProvider.AwsS3 => $"s3.{region}.amazonaws.com",
            CloudProvider.BackblazeB2 => $"s3.{region}.backblazeb2.com",
            _ => Endpoint.Text.Trim()
        };
        return new(provider, endpoint, region, Bucket.Text.Trim(), prefix.Length == 0 ? "" : prefix + "/");
    }

    private CloudCredentials Credentials() =>
        new(RepositoryPassword.Password, AccessKey.Text.Trim(), SecretKey.Password,
            (CloudProvider)Provider.SelectedIndex == CloudProvider.Mega || SessionToken.Password.Length == 0 ? null : SessionToken.Password);

    private async void CreateClick(object sender, RoutedEventArgs e) => await ConfigureAsync(create: true);
    private async void ConnectClick(object sender, RoutedEventArgs e) => await ConfigureAsync(create: false);

    private void RecoveryPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (RecoveryInformationSaved is not null) RecoveryInformationSaved.IsChecked = false;
    }

    private async Task ConfigureAsync(bool create) => await RunAsync(async token =>
    {
        if (job is not { Enabled: true, Retired: false }) return;
        var destination = Destination();
        var credentials = Credentials();
        string confirmation = ConfirmRepositoryPassword.Password;
        if (create && (confirmation.Length == 0 || confirmation != credentials.Password))
            throw new InvalidDataException("Vuelve a escribir la misma contraseña de recuperación en la confirmación.");
        if (create && RecoveryInformationSaved.IsChecked != true)
            throw new InvalidDataException("Guarda la ubicación y la contraseña del repositorio fuera de este equipo y marca la confirmación.");
        using var registry = openRegistry();
        var current = registry.Get(job.Id);
        if (!current.Enabled || current.Retired)
            throw new InvalidDataException("Habilita el trabajo antes de activar la copia en nube.");
        CloudConnection connection = await Task.Run(async () =>
        {
            CloudConnection created;
            using (var session = Session(destination, credentials))
                created = await session.OpenAsync(create, readOnly: false, token);
            if (create)
            {
                var recoveryCredentials = new CloudCredentials(confirmation, credentials.AccessKey, credentials.SecretKey, credentials.SessionToken);
                using var recovery = Session(destination, recoveryCredentials);
                if (await recovery.OpenAsync(create: false, readOnly: true, token) != created)
                    throw new InvalidDataException("La conexión de recuperación abrió otro repositorio. Revisa su ubicación antes de activar la copia.");
            }
            return created;
        }, token);
        registry.SaveCloudConnection(job.Id, connection, credentials, lab: testSession is not null);
        if (testDispatch is null) { using var worker = ProductWorker.StartCloud(registry.Location); }
        else await testDispatch(registry, token);
        try { startMonitor(registry); }
        catch (Exception error) when (ProductAutomation.Expected(error))
        {
            CloudStatus.Text = "Conexión guardada. Abre Aplicación y vuelve a iniciar el monitor para los próximos envíos.";
            return;
        }
        ReadStatus(registry);
    });

    private async void ListClick(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        CloudDates.ItemsSource = null;
        readConnection = null;
        var destination = Destination();
        var credentials = Credentials();
        var (connection, points) = await Task.Run(async () =>
        {
            using var session = Session(destination, credentials);
            var connection = await session.OpenAsync(create: false, readOnly: true, token);
            return (connection, await session.Repository.ListAsync(token));
        }, token);
        readConnection = connection;
        CloudDates.ItemsSource = points.Select(point => new DateChoice(point)).ToArray();
        CloudDates.SelectedIndex = points.Length == 0 ? -1 : 0;
        CloudStatus.Text = points.Length == 0 ? "Este repositorio aún no contiene fechas de FolderTwin." : $"{points.Length} fechas disponibles.";
    });

    private async void RestoreClick(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        if (readConnection is not { } reviewed || CloudDates.SelectedItem is not DateChoice selected || Destination() != reviewed.Destination)
            throw new InvalidDataException("Consulta las fechas del repositorio y elige la que quieres recuperar.");
        string? selectedParent = testOutput?.Invoke();
        if (selectedParent is null)
        {
            var dialog = new OpenFolderDialog { Title = "Elige dónde crear la carpeta recuperada", AddToRecent = false, DereferenceLinks = false };
            if (dialog.ShowDialog(this) != true) return;
            selectedParent = dialog.FolderName;
        }
        string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selectedParent));
        var credentials = Credentials();
        string output = await Task.Run(async () =>
        {
            using var session = Session(reviewed.Destination, credentials);
            if (await session.OpenAsync(create: false, readOnly: true, token) != reviewed)
                throw new InvalidDataException("El repositorio cambió. Vuelve a consultar sus fechas.");
            var observed = (await session.Repository.ListAsync(token)).SingleOrDefault(point => point == selected.Recovery) ??
                throw new InvalidDataException("La fecha cambió o ya no está disponible.");
            using var root = new DirectoryFence(parent);
            string name = $"FolderTwin-Nube-{Guid.NewGuid():N}";
            string target = Path.Combine(WindowsProbeNative.VolumePath(root.Root), name);
            using var registry = openRegistry();
            var local = registry.List();
            if (local.Unreadable != 0 || local.Jobs.Any(item => LocalJobRoots.Overlap(item.Source.VolumePath, target) ||
                LocalJobRoots.Overlap(item.Destination.LastVolumePath, target)))
                throw new InvalidDataException("Elige una carpeta separada de los trabajos locales.");
            using var folder = WindowsProbeNative.CreatePlainDirectory(root.Root, name);
            await session.Repository.RestoreAsync(observed.Point, target, token);
            registry.RecordCloudRestore(reviewed.Repository, observed.Point);
            return Path.Combine(parent, name);
        }, token);
        CloudStatus.Text = $"Recuperación completada y autenticada por Kopia. {output}";
    }, TimeSpan.FromHours(2));

    private CloudRepositorySession Session(CloudDestination destination, CloudCredentials credentials) =>
        testSession?.Invoke(destination, credentials) ?? new(kopia(), destination, credentials, rclone: ProductCloudWorker.ReviewedRclone);

    private async void RefreshClick(object sender, RoutedEventArgs e) => await RunAsync(_ =>
    {
        using var registry = openRegistry();
        ReadStatus(registry);
        return Task.CompletedTask;
    });

    private async void PauseClick(object sender, RoutedEventArgs e) => await ControlAsync(CloudTransferAction.Pause);
    private async void CancelClick(object sender, RoutedEventArgs e) => await ControlAsync(CloudTransferAction.Cancel);
    private async void RetryClick(object sender, RoutedEventArgs e) => await ControlAsync(CloudTransferAction.Retry);
    private async void PauseCloudJobClick(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        using var registry = openRegistry();
        if (job is null || registry.ReadCloudConnection(job.Id) is null) return;
        bool pause = !registry.IsCloudPaused(job.Id);
        registry.SetCloudPaused(job.Id, pause);
        if (!pause)
        {
            if (testDispatch is null) { using var worker = ProductWorker.StartCloud(registry.Location); }
            else await testDispatch(registry, token);
            startMonitor(registry);
        }
        ReadStatus(registry);
    });

    private async Task ControlAsync(CloudTransferAction action) => await RunAsync(async token =>
    {
        using var registry = openRegistry();
        if (job is null || CloudTransfers.SelectedItem is not TransferChoice selected)
            throw new InvalidDataException("Selecciona la fecha del envío que quieres controlar.");
        var connection = registry.ReadCloudConnection(job.Id);
        if (connection?.Repository != selected.Transfer.Ticket.Repository || selected.Transfer.Ticket.Job != job.Id)
            throw new InvalidDataException("El destino cambió. Actualiza los envíos y revisa la fecha elegida.");
        var current = registry.ReadCloudTransfer(selected.Transfer.Ticket);
        if (!CanControl(current.State, action))
        {
            ReadStatus(registry);
            throw new InvalidDataException("El estado del envío cambió. Revisa la fecha y elige una acción disponible.");
        }
        registry.ControlCloudTransfer(current.Ticket, action);
        if (action == CloudTransferAction.Retry)
        {
            if (testDispatch is null) { using var process = ProductWorker.StartCloud(registry.Location); }
            else await testDispatch(registry, token);
        }
        ReadStatus(registry);
    });

    private static bool CanControl(CloudTransferState state, CloudTransferAction action) => action switch
    {
        CloudTransferAction.Pause => state is CloudTransferState.Pending or CloudTransferState.Retry,
        CloudTransferAction.Cancel => state is CloudTransferState.Pending or CloudTransferState.Retry or CloudTransferState.Paused or CloudTransferState.Review,
        CloudTransferAction.Retry => state is CloudTransferState.Paused or CloudTransferState.Cancelled or CloudTransferState.Review or CloudTransferState.Retry,
        _ => false
    };

    private void TransferSelected(object sender, SelectionChangedEventArgs e) => RefreshTransferActions();

    private void RefreshTransferActions()
    {
        if (PauseCloud is null) return;
        var selected = CloudTransfers.SelectedItem as TransferChoice;
        PauseCloud.IsEnabled = selected is not null && CanControl(selected.Transfer.State, CloudTransferAction.Pause);
        CancelCloud.IsEnabled = selected is not null && CanControl(selected.Transfer.State, CloudTransferAction.Cancel);
        RetryCloud.IsEnabled = selected is not null && CanControl(selected.Transfer.State, CloudTransferAction.Retry);
    }

    private void PreviousTransfersClick(object sender, RoutedEventArgs e)
    {
        transferOffset = Math.Max(0, transferOffset - TransferPageSize);
        ShowTransferPage();
    }

    private void NextTransfersClick(object sender, RoutedEventArgs e)
    {
        if (transferOffset + TransferPageSize < transfers.Length) transferOffset += TransferPageSize;
        ShowTransferPage();
    }

    private void ShowTransferPage(CloudTicket? selected = null)
    {
        var page = transfers.Skip(transferOffset).Take(TransferPageSize).Select(transfer => new TransferChoice(transfer)).ToArray();
        CloudTransfers.ItemsSource = page;
        CloudTransfers.SelectedItem = page.FirstOrDefault(item => item.Transfer.Ticket == selected) ?? page.FirstOrDefault();
        PreviousTransfers.IsEnabled = transferOffset > 0;
        NextTransfers.IsEnabled = transferOffset + page.Length < transfers.Length;
        TransferPage.Text = page.Length == 0 ? "Sin envíos" : $"{transferOffset + 1:N0}–{transferOffset + page.Length:N0} de {transfers.Length:N0}";
        RefreshTransferActions();
    }

    private static string TransferState(CloudTransfer transfer) => transfer.State switch
    {
        CloudTransferState.Pending => "Pendiente o en curso",
        CloudTransferState.Paused => "Pausado",
        CloudTransferState.Cancelled => "Cancelado",
        CloudTransferState.Retry => $"Reintento {transfer.RetryUtc?.ToLocalTime():g}",
        CloudTransferState.Review => "Requiere revisión",
        CloudTransferState.Available => "Confirmado",
        _ => "Estado desconocido"
    };

    private void ReadStatus(ProductJobRegistry registry)
    {
        var connection = job is null ? null : registry.ReadCloudConnection(job.Id);
        bool paused = connection is not null && registry.IsCloudPaused(job!.Id);
        PauseCloudJob.IsEnabled = connection is not null;
        PauseCloudJob.Content = paused ? "Reanudar copia de este trabajo" : "Pausar copia de este trabajo";
        var selected = (CloudTransfers.SelectedItem as TransferChoice)?.Transfer.Ticket;
        transfers = connection is null ? [] : registry.CloudTransfers(job!.Id, connection.Repository);
        int selectedIndex = Array.FindIndex(transfers, item => item.Ticket == selected);
        if (selectedIndex < 0) selectedIndex = Array.FindIndex(transfers, item => item.State is CloudTransferState.Pending or CloudTransferState.Retry);
        if (selectedIndex < 0) selectedIndex = Math.Clamp(transferOffset, 0, Math.Max(0, transfers.Length - 1));
        transferOffset = selectedIndex / TransferPageSize * TransferPageSize;
        ShowTransferPage(transfers.ElementAtOrDefault(selectedIndex)?.Ticket);
        var last = transfers.LastOrDefault(item => item.State == CloudTransferState.Available)?.Point;
        var next = transfers.FirstOrDefault(item => item.State is CloudTransferState.Pending or CloudTransferState.Retry) ??
            transfers.FirstOrDefault(item => item.State is CloudTransferState.Review or CloudTransferState.Paused);
        if (connection is null) { CloudStatus.Text = "Este trabajo todavía no tiene destino en nube."; return; }
        string state = next?.State switch
        {
            CloudTransferState.Pending => "Envío pendiente o en curso.",
            CloudTransferState.Paused => "Envío pausado.",
            CloudTransferState.Cancelled => "Envío cancelado.",
            CloudTransferState.Retry => $"Próximo intento {next.RetryUtc?.ToLocalTime():g}.",
            CloudTransferState.Review => "El envío requiere revisión.",
            _ => "Sin envíos en la cola."
        };
        string issue = next?.Issue switch
        {
            CloudTransferIssue.LocalPoint => " Comprueba que el disco local del respaldo esté conectado y el punto siga disponible.",
            CloudTransferIssue.Credentials => " Vuelve a conectar con credenciales válidas.",
            CloudTransferIssue.Space => " Libera espacio para preparar el punto.",
            CloudTransferIssue.Quota => " El proveedor informó que alcanzó su cuota de almacenamiento.",
            CloudTransferIssue.DestinationMissing => " Reconecta el disco del respaldo local para preparar el envío.",
            CloudTransferIssue.Repository => " Revisa el destino, la cuota y la conexión.",
            _ => ""
        };
        string restored = registry.LastCloudRestore(connection.Repository)?.ToLocalTime().ToString("g") ?? "Todavía ninguna";
        string pauseText = paused ? "Copia en nube pausada para este trabajo. " : "";
        CloudStatus.Text = $"Último envío confirmado en nube · {last?.Finished.ToLocalTime().ToString("g") ?? "Todavía ninguno"}. " +
            $"Última recuperación verificada · {restored}. {transfers.Count(item => item.State != CloudTransferState.Available)} sin confirmar. {pauseText}{state}{issue}";
    }

    private async Task RunAsync(Func<CancellationToken, Task> action, TimeSpan? limit = null)
    {
        if (!Actions.IsEnabled) return;
        Actions.IsEnabled = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(closed.Token);
        deadline.CancelAfter(limit ?? TimeSpan.FromSeconds(60));
        try { await action(deadline.Token); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        { if (!closed.IsCancellationRequested) CloudStatus.Text = "La operación excedió el tiempo de espera. Revisa la conexión."; }
        catch (Exception error) when (ProductAutomation.Expected(error))
        { CloudStatus.Text = error.Message; }
        finally { Actions.IsEnabled = true; }
    }
}

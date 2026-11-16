using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;

namespace CopyLab;

public partial class ProductCloudWindow
{
    internal static async Task RunSmokeAsync()
    {
        var fixture = LabWorkspace.Create();
        bool passed = false;
        ProductCloudWindow? window = null;
        var destination = new CloudDestination(CloudProvider.Filen, "", "", "", "synthetic/");
        var account = new CloudCredentials(Guid.NewGuid().ToString("N"), "synthetic@example.invalid",
            Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        try
        {
            string kopia = ProductCloudWorker.ReviewedKopia(), rclone = ProductCloudWorker.ReviewedRclone();
            string source = Folder("source"), target = Folder("backup"), registryPath = Folder("registry");
            string remote = Folder("remote"), clean = Folder("empty-profile"), output = Folder("recovered");
            ProductJobRegistry Open() => new(registryPath);
            ProductJobRegistry OpenClean() => new(clean);
            int sessions = 0;
            CloudRepositorySession Session(CloudDestination address, CloudCredentials credentials)
            {
                sessions++;
                return new(kopia, address, credentials, lab: true, () => rclone, remote);
            }
            string input = Path.Combine(source, "archivo.txt");
            File.WriteAllText(input, "Primera fecha sintética de Ruta");
            byte[] first = SHA256.HashData(File.ReadAllBytes(input));
            ProductJob job;
            using (var registry = Open())
            {
                job = registry.Create(new("Prueba de Ruta", ProductBackupMode.Versions, null),
                    LocalJobRoots.ReviewDedicatedDestination(source, target));
                Check(ProductRunner.Run(registry, job.Id).State == ProductRunState.Confirmed, "Local backup did not complete.");
            }
            window = new ProductCloudWindow(Open, job, () => kopia, _ => { }, Session,
                (registry, token) => ProductCloudDispatch.RunPassAsync(registry, kopia, token, lab: true, () => rclone, remote));
            window.Show();
            await CheckHelpAsync(window);
            Fill(window, CloudProvider.Mega);
            Check(window.SessionToken.Visibility == Visibility.Collapsed, "MEGA exposed the Filen-only field.");
            Fill(window, CloudProvider.Filen);
            Check(window.Region.Visibility == Visibility.Collapsed && window.SessionToken.IsVisible, "Filen exposed S3 fields or hid its key.");
            window.ConfirmRepositoryPassword.Password = "mismatched-password";
            window.RecoveryInformationSaved.IsChecked = true;
            await ClickAsync(window, window.CreateCloud);
            Check(sessions == 0 && window.CloudStatus.Text.Contains("misma contraseña", StringComparison.Ordinal),
                "Mismatched recovery passwords reached the provider.");
            window.ConfirmRepositoryPassword.Password = account.Password;
            await ClickAsync(window, window.CreateCloud);
            Check(sessions == 0 && window.CloudStatus.Text.Contains("fuera de este equipo", StringComparison.Ordinal),
                "Creation proceeded without the recovery-information acknowledgment.");
            window.RecoveryInformationSaved.IsChecked = true;
            await ClickAsync(window, window.CreateCloud);
            Check(sessions == 2, "New repository creation did not reopen an independent recovery session.");
            string repository;
            await ClickAsync(window, window.PauseCloudJob);
            File.WriteAllText(input, "Segunda fecha sintética de Ruta");
            byte[] second = SHA256.HashData(File.ReadAllBytes(input));
            using (var registry = Open())
            {
                repository = registry.ReadCloudConnection(job.Id)?.Repository ?? throw new IOException(window.CloudStatus.Text);
                Check(registry.CloudTransfers(job.Id, repository).Single().State == CloudTransferState.Available,
                    "Ruta did not report the first cloud point.");
                Check(ProductRunner.Run(registry, job.Id).State == ProductRunState.Confirmed, "Second local backup did not complete.");
                await ProductCloudDispatch.RunPassAsync(registry, kopia, CancellationToken.None, lab: true, () => rclone, remote);
                Check(registry.IsCloudPaused(job.Id) && registry.CloudTransfers(job.Id, repository).Length == 1,
                    "The cloud pause did not persist or blocked the local backup.");
            }
            await ClickAsync(window, window.PauseCloudJob);
            await ClickAsync(window, window.RefreshCloud);
            Check(window.CloudStatus.Text.Contains("0 sin confirmar", StringComparison.Ordinal), "Ruta did not show the current cloud state.");
            window.Close();
            Directory.Move(source, Path.Combine(fixture.Root, "source-aside"));
            Directory.Move(target, Path.Combine(fixture.Root, "backup-aside"));
            window = new ProductCloudWindow(OpenClean, null, () => kopia, _ => { }, Session, testOutput: () => output);
            window.Show();
            Check(!window.ConnectCloud.IsEnabled && !window.CreateCloud.IsEnabled, "A clean recovery profile could activate a job.");
            Fill(window, CloudProvider.Filen);
            await ClickAsync(window, window.ListCloud);
            Check(window.CloudDates.Items.Count == 2, window.CloudStatus.Text);
            for (int i = 0; i < 2; i++)
            {
                window.CloudDates.SelectedIndex = i;
                var before = Directory.GetDirectories(output);
                await ClickAsync(window, window.RestoreCloud);
                string? created = Directory.GetDirectories(output).Except(before, StringComparer.Ordinal).SingleOrDefault();
                Check(window.CloudStatus.Text.Contains("Recuperación completada", StringComparison.Ordinal) && created is not null,
                    window.CloudStatus.Text);
                Check(SHA256.HashData(File.ReadAllBytes(Path.Combine(created!, "archivo.txt"))).AsSpan()
                    .SequenceEqual(i == 0 ? second : first), "Ruta restored the wrong bytes.");
            }
            using (var registry = OpenClean())
                Check(registry.List().Jobs.Length == 0 && registry.LastCloudRestore(repository) is not null,
                    "A clean recovery profile lost its verification record or created a job.");
            await CheckSelectedTransfersAsync();
            passed = true;
            Console.WriteLine("PASS packaged Ruta confirms the recovery password, reopens the new repository, controls the selected cloud ticket and recovers two dates from an empty profile.");
        }
        finally
        {
            window?.Close();
            if (passed)
            {
                using (fixture.OpenRegisteredRoot()) { }
                Directory.Delete(fixture.Root, recursive: true);
            }
        }

        string Folder(string name) => Directory.CreateDirectory(Path.Combine(fixture.Root, name)).FullName;
        void Fill(ProductCloudWindow view, CloudProvider provider)
        {
            view.Provider.SelectedIndex = (int)provider;
            view.Prefix.Text = destination.Prefix;
            view.AccessKey.Text = account.AccessKey;
            view.SecretKey.Password = account.SecretKey;
            view.SessionToken.Password = account.SessionToken!;
            view.RepositoryPassword.Password = account.Password;
        }

        async Task CheckSelectedTransfersAsync()
        {
            string settings = Folder("control-profile"), source = Folder("control-source"), backup = Folder("control-backup");
            ProductJobRegistry Open() => new(settings);
            using var registry = Open();
            var job = registry.Create(new("Control de fechas", ProductBackupMode.Versions, null),
                LocalJobRoots.ReviewDedicatedDestination(source, backup));
            string repository = new('c', 64);
            registry.SaveCloudConnection(job.Id, new(repository, destination), account, lab: true);
            File.WriteAllText(Path.Combine(source, "dato.txt"), "Primera fecha");
            var first = ProductRunner.Run(registry, job.Id);
            File.WriteAllText(Path.Combine(source, "dato.txt"), "Segunda fecha");
            var second = ProductRunner.Run(registry, job.Id);
            Check(first.State == ProductRunState.Confirmed && second.State == ProductRunState.Confirmed,
                "Could not prepare two local cloud-control points.");
            var cancelled = registry.EnqueueCloud(job.Id, repository, first.Generation!);
            var pending = registry.EnqueueCloud(job.Id, repository, second.Generation!);
            registry.ControlCloudTransfer(cancelled, CloudTransferAction.Cancel);
            var controls = new ProductCloudWindow(Open, job, () => "unused", _ => { }, testDispatch: (_, _) => Task.CompletedTask);
            try
            {
                controls.Show();
                await ClickAsync(controls, controls.RefreshCloud);
                Check(controls.CloudTransfers.SelectedItem is TransferChoice { Transfer.Ticket: var selected } && selected == pending,
                    "A cancelled earlier ticket hid the pending ticket.");
                await ClickAsync(controls, controls.PauseCloud);
                Check(registry.ReadCloudTransfer(pending).State == CloudTransferState.Paused &&
                    registry.ReadCloudTransfer(cancelled).State == CloudTransferState.Cancelled, "Pause affected the wrong cloud ticket.");
                await ClickAsync(controls, controls.CancelCloud);
                Check(registry.ReadCloudTransfer(pending).State == CloudTransferState.Cancelled && !controls.PauseCloud.IsEnabled &&
                    !controls.CancelCloud.IsEnabled && controls.RetryCloud.IsEnabled, "Cancelled selection exposes invalid actions.");
                await ClickAsync(controls, controls.RetryCloud);
                Check(registry.ReadCloudTransfer(pending).State == CloudTransferState.Pending &&
                    registry.ReadCloudTransfer(cancelled).State == CloudTransferState.Cancelled, "Retry revived another cloud ticket.");
                controls.CloudTransfers.SelectedIndex = 0;
                await ClickAsync(controls, controls.RefreshCloud);
                Check(controls.CloudTransfers.SelectedItem is TransferChoice { Transfer.Ticket: var preserved } && preserved == cancelled,
                    "Refresh changed an explicit history selection.");
                await ClickAsync(controls, controls.RetryCloud);
                Check(registry.ReadCloudTransfer(cancelled).State == CloudTransferState.Pending, "Explicit retry of a cancelled ticket failed.");
            }
            finally { controls.Close(); }
        }
    }

    private static async Task CheckHelpAsync(ProductCloudWindow window)
    {
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        foreach (var name in window.cloudHelp.Keys)
        {
            var control = window.FindName(name) as Control ?? throw new InvalidOperationException("Missing named cloud help control: " + name);
            Check(control.ToolTip is ToolTip && ToolTipService.GetShowOnDisabled(control) &&
                System.Windows.Automation.AutomationProperties.GetHelpText(control).Length > 10, "Cloud help did not reach " + name);
        }
        window.Bucket.BringIntoView();
        window.Bucket.Focus();
        window.CloudHelpClick(window, new RoutedEventArgs());
        Check(window.cloudHelpWindow is { IsVisible: true }, "Cloud help did not open.");
        window.cloudHelpWindow!.Close();
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Check(System.Windows.Input.FocusManager.GetFocusedElement(window) == window.Bucket, "Closing cloud help lost the prior keyboard focus.");
    }

    private static async Task ClickAsync(ProductCloudWindow window, Button button)
    {
        Check(button.IsEnabled, "The expected cloud control is disabled.");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var clock = Stopwatch.StartNew();
        while (!window.Actions.IsEnabled && clock.Elapsed < TimeSpan.FromSeconds(55)) await Task.Delay(30);
        Check(window.Actions.IsEnabled, "Cloud UI operation timed out.");
    }

    private static void Check(bool condition, string detail)
    {
        if (!condition) throw new InvalidDataException(detail);
    }
}

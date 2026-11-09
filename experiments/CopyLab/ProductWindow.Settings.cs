using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace CopyLab;

public partial class ProductWindow
{
    private void ShowJobSettings()
    {
        if (JobSettingsPanel is null) return;
        var job = (JobList.SelectedItem as JobRow)?.Job;
        JobSettingsPanel.IsEnabled = job is not null;
        EditJobName.Text = job?.Options.Name ?? "";
        EditDailyTime.Text = job?.Options.DailyAt?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "";
        EditEnabled.IsChecked = job?.Enabled == true;
        EditRetired.IsChecked = job?.Retired == true;
        EditWatch.IsChecked = job?.WatchMode is ProductWatchMode.Watching or ProductWatchMode.Paused;
        EditAllowMirrorRetire.Visibility = job?.Options.Mode == ProductBackupMode.ProtectedMirror ? Visibility.Visible : Visibility.Collapsed;
        EditAllowMirrorRetire.IsChecked = job?.AutomaticMirrorPolicy == AutomaticMirrorRules.Version;
        ToggleJobWatchButton.Content = job?.WatchMode == ProductWatchMode.Paused ? "Reanudar vigilancia" : "Pausar vigilancia";
        ToggleJobWatchButton.IsEnabled = job?.WatchMode is ProductWatchMode.Watching or ProductWatchMode.Paused;
        RepairWatchButton.IsEnabled = job?.WatchMode == ProductWatchMode.Watching;
        SetRouteEndpoint(RouteSource, RouteSourceVolume, job?.Source.VolumePath, "Carpeta de origen");
        SetRouteEndpoint(RouteDestination, RouteDestinationVolume, job?.Destination.LastVolumePath, "Volumen local");
        scheduleObserved = 0;
        ScheduleStatusText.Text = "";
        WatchStatusText.Text = "";
        SpaceStatusText.Text = "";
        ResetProductRetention();
    }

    private void ConfigureSchedule(string location, Guid id, bool remove = false)
    {
        if (testSchedule is not null) testSchedule(location, id, remove);
        else if (remove) ProductAutomation.Remove(location, id);
        else ProductAutomation.Apply(location, id);
    }

    private async Task ApplyScheduleAsync(string location, Guid id)
    {
        try
        {
            await Task.Run(() => ConfigureSchedule(location, id));
            SetStatus("Configuración guardada y horario comprobado. Se ejecuta con tu sesión iniciada; los horarios perdidos se intentan al estar disponible.");
        }
        catch (Exception error) when (ProductAutomation.Expected(error))
        {
            SetStatus("Configuración guardada; el horario requiere atención. Usa Reparar horario. " + error.Message);
        }
        scheduleObserved = 0;
        await RefreshScheduleAsync();
    }

    private void SaveSettingsClick(object sender, RoutedEventArgs e)
    {
        if (busy || (JobList.SelectedItem as JobRow)?.Job is not { } job || IsJobRunning(job.Id)) return;
        string name = EditJobName.Text.Trim(), time = EditDailyTime.Text.Trim();
        bool retired = EditRetired.IsChecked == true, enabled = EditEnabled.IsChecked == true && !retired;
        var watch = enabled && EditWatch.IsChecked == true
            ? job.WatchMode == ProductWatchMode.Paused ? ProductWatchMode.Paused : ProductWatchMode.Watching
            : ProductWatchMode.Off;
        bool allowRetire = watch != ProductWatchMode.Off && job.Options.Mode == ProductBackupMode.ProtectedMirror &&
            EditAllowMirrorRetire.IsChecked == true;
        Start(async () =>
        {
            TimeOnly? hour = null;
            if (time.Length != 0)
            {
                if (!TimeOnly.TryParseExact(time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                    throw new InvalidDataException("Introduce una hora en formato HH:mm, por ejemplo 20:30.");
                hour = parsed;
            }
            new ProductJobOptions(name, job.Options.Mode, hour).Validate();
            string location = await Task.Run(() =>
            {
                using var registry = openRegistry();
                // Retire the owned native task before recording a disabled revision.
                if (!enabled || retired || hour is null) ConfigureSchedule(registry.Location, job.Id, remove: true);
                registry.UpdateSettings(job.Id, name, hour, enabled, retired, watch,
                    allowRetire == (job.AutomaticMirrorPolicy != 0) ? null : allowRetire);
                return registry.Location;
            });
            await LoadListAsync();
            SelectJob(job.Id);
            ResetProductActions();
            if (enabled && hour is not null) await ApplyScheduleAsync(location, job.Id);
            if (watch == ProductWatchMode.Watching) await StartWatchAsync();
            else if (watch == ProductWatchMode.Paused) SetStatus("Cambios guardados. La vigilancia permanece pausada.");
            else if (hour is null || !enabled) SetStatus(retired ? "Trabajo retirado. Sus respaldos se conservan para recuperar." :
                enabled ? "Cambios guardados. El trabajo se ejecuta a demanda." : "Trabajo deshabilitado. Sus respaldos se conservan.");
        });
    }

    private async Task StartWatchAsync(bool? atLogon = null)
    {
        try
        {
            await Task.Run(() =>
            {
                using var registry = openRegistry();
                if (testMonitor is not null) testMonitor(registry, atLogon);
                else if (testFolderWorkspace is not null || testPicker is not null)
                    throw new InvalidDataException("Este ensayo no instala tareas de Windows.");
                else ProductAutomation.StartMonitor(registry, atLogon);
            });
            if (testMonitor is null && testPicker is null && testFolderWorkspace is null)
                explainWatchOnClose = true;
            SetStatus("Inicio del monitor solicitado. La vigilancia continúa al cerrar la ventana con tu sesión iniciada. Consulta su estado en Ruta.");
        }
        catch (Exception error) when (ProductAutomation.Expected(error))
        {
            SetStatus("Configuración guardada. El monitor requiere atención. Usa Iniciar monitor. " + error.Message);
        }
        scheduleObserved = 0;
        await RefreshScheduleAsync();
    }

    private void RepairWatchClick(object sender, RoutedEventArgs e)
    {
        if (!busy && (JobList.SelectedItem as JobRow)?.Job is { WatchMode: ProductWatchMode.Watching })
            Start(() => StartWatchAsync());
    }

    private void ToggleJobWatchClick(object sender, RoutedEventArgs e)
    {
        if (busy || (JobList.SelectedItem as JobRow)?.Job is not { Enabled: true, Retired: false } job ||
            job.WatchMode == ProductWatchMode.Off || IsJobRunning(job.Id)) return;
        Start(async () =>
        {
            var next = job.WatchMode == ProductWatchMode.Watching ? ProductWatchMode.Paused : ProductWatchMode.Watching;
            await Task.Run(() =>
            {
                using var registry = openRegistry();
                var current = registry.Get(job.Id);
                if (current.WatchMode != job.WatchMode) throw new InvalidDataException("La vigilancia cambió. Actualiza los trabajos antes de continuar.");
                registry.UpdateSettings(current.Id, current.Options.Name, current.Options.DailyAt, current.Enabled, current.Retired, next);
            });
            await LoadListAsync();
            SelectJob(job.Id);
            if (next == ProductWatchMode.Watching) await StartWatchAsync();
            else SetStatus("Vigilancia pausada. Puedes continuar desde los ajustes del trabajo.");
        });
    }

    private void ApplyScheduleClick(object sender, RoutedEventArgs e)
    {
        if (busy || (JobList.SelectedItem as JobRow)?.Job is not { Enabled: true, Retired: false } job) return;
        Start(async () =>
        {
            string location;
            using (var registry = openRegistry()) location = registry.Location;
            await ApplyScheduleAsync(location, job.Id);
        });
    }

    private long scheduleObserved;
    private async Task RefreshScheduleAsync()
    {
        if (closed || scheduleObserved != 0 && Environment.TickCount64 - scheduleObserved < 5000) return;
        var job = (JobList.SelectedItem as JobRow)?.Job;
        if (job is null) return;
        scheduleObserved = Environment.TickCount64;
        var observed = await Task.Run(() =>
        {
            string space;
            try
            {
                using var target = new VersionScope(job).OpenRegisteredRoot();
                space = $"Espacio disponible en el destino comprobado · {LabWorkspace.AvailableSpace(target.Root) / (1024d * 1024 * 1024):N1} GiB";
            }
            catch (Exception error) when (VersionJob.Expected(error)) { space = "Destino no consultable · " + error.Message; }
            return (Space: space, Schedule: ReadSchedule(), Watch: ReadWatch(),
                Source: DisplayRoutePath(job.Source.VolumePath), Destination: DisplayRoutePath(job.Destination.LastVolumePath));
        });
        if (!closed && (JobList.SelectedItem as JobRow)?.Id == job.Id)
        {
            ScheduleStatusText.Text = observed.Schedule;
            WatchStatusText.Text = observed.Watch;
            SpaceStatusText.Text = observed.Space;
            SetRouteEndpoint(RouteSource, RouteSourceVolume, observed.Source, "Carpeta de origen");
            SetRouteEndpoint(RouteDestination, RouteDestinationVolume, observed.Destination, "Volumen local");
            RefreshRouteNextAttempt();
        }

        string ReadWatch()
        {
            if (job.WatchMode == ProductWatchMode.Off) return "";
            if (job.WatchMode == ProductWatchMode.Paused) return "Vigilancia pausada";
            try
            {
                using var registry = openRegistry();
                if (registry.ReadMonitorProcess(job.Id, out var activity) is null)
                    return "Monitor detenido o iniciándose · Usa Iniciar monitor si el estado persiste";
                return activity switch
                {
                    ProductWatchActivity.Running => "Monitor activo · Respaldando o comparando",
                    ProductWatchActivity.Pending => "Monitor activo · Cambios o comparación pendientes",
                    ProductWatchActivity.SourceUnavailable => "Monitor activo · Origen pendiente de comprobar",
                    ProductWatchActivity.Watching => "Vigilando cambios con la sesión iniciada",
                    _ => "Monitor activo · Preparando vigilancia de este trabajo"
                };
            }
            catch (Exception error) when (ProductAutomation.Expected(error)) { return "Monitor requiere atención · " + error.Message; }
        }

        string ReadSchedule()
        {
            if (!job.Enabled || job.Retired) return "Horario desactivado · Respaldos conservados";
            if (job.Options.DailyAt is null) return job.WatchMode == ProductWatchMode.Off ? "Ejecución a demanda" : "";
            if (testSchedule is not null) return "Horario simulado por el arnés; sin tarea en este equipo";
            try
            {
                using var registry = openRegistry();
                using var scheduler = new WindowsSchedule();
                return scheduler.ReadProduct(registry, job.Id) is { Enabled: true, MeetsPolicy: true } schedule ?
                    $"Próximo horario · {schedule.NextRun:g} · Requiere sesión iniciada" :
                    "Horario pendiente o modificado · Usa Reparar horario";
            }
            catch (Exception error) when (ProductAutomation.Expected(error)) { return "Revisa el horario · " + error.Message; }
        }
    }

    private static void SetRouteEndpoint(System.Windows.Controls.TextBlock address,
        System.Windows.Controls.TextBlock volume, string? path, string label)
    {
        address.ToolTip = path;
        System.Windows.Automation.AutomationProperties.SetName(address, path ?? "");
        bool drive = path is { Length: >= 3 } && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\';
        volume.Text = drive ? $"{path![..2]} · {label}" : label;
        address.Text = drive && path!.Length > 40 ? $@"{path[..2]}\…\{Path.GetFileName(path)}" : path ?? "";
    }

    private static string DisplayRoutePath(string path)
    {
        // A drive letter is for display only. Keep the registered volume path for all operations.
        string? volume = Path.GetPathRoot(path);
        if (volume is null || !volume.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase)) return path;
        if (!volume.EndsWith('\\')) return path;
        var names = new char[512];
        if (!GetVolumePathNamesForVolumeName(volume, names, (uint)names.Length, out _)) return path;
        foreach (string mount in new string(names).Split('\0', StringSplitOptions.RemoveEmptyEntries))
            if (mount.Length == 3 && char.IsAsciiLetter(mount[0]) && mount[1] == ':' && mount[2] == '\\')
                return mount + path[volume.Length..];
        return path;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetVolumePathNamesForVolumeNameW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNamesForVolumeName(string volume, [Out] char[] paths, uint capacity, out uint required);
}

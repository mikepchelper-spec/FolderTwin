using System.Globalization;
using System.IO;
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
        scheduleObserved = 0;
        ScheduleStatusText.Text = "";
        SpaceStatusText.Text = "";
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
                registry.UpdateSettings(job.Id, name, hour, enabled, retired);
                return registry.Location;
            });
            await LoadListAsync();
            SelectJob(job.Id);
            ResetProductActions();
            if (enabled && hour is not null) await ApplyScheduleAsync(location, job.Id);
            else SetStatus(retired ? "Trabajo retirado. Sus respaldos se conservan para recuperar." :
                enabled ? "Cambios guardados. El trabajo se ejecuta a demanda." : "Trabajo deshabilitado. Sus respaldos se conservan.");
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
            return (Space: space, Schedule: ReadSchedule());
        });
        if (!closed && (JobList.SelectedItem as JobRow)?.Id == job.Id)
        {
            ScheduleStatusText.Text = observed.Schedule;
            SpaceStatusText.Text = observed.Space;
        }

        string ReadSchedule()
        {
            if (!job.Enabled || job.Retired) return "Horario desactivado · Respaldos conservados";
            if (job.Options.DailyAt is null) return "Ejecución a demanda";
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
}

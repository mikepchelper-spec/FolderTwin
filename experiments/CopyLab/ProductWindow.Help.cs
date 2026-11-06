using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace CopyLab;

public partial class ProductWindow
{
    private readonly Dictionary<string, (string Text, string Requires)> helpEntries = new()
    {
        ["HelpButton"] = ("Abre la ayuda de los controles, la guía, soporte y la identidad de esta compilación. F1 también abre esta ventana.", ""),
        ["ReturnToJobsButton"] = ("Regresa a la lista y conserva el trabajo seleccionado.", "Espera a que termine la comprobación de esta pantalla."),
        ["FirstBackupButton"] = ("Empieza el asistente de cuatro pasos. Elige origen, destino, modo y revisa antes de guardar.", ""),
        ["NewJobButton"] = ("Crea otra configuración con su propia carpeta de respaldo.", ""),
        ["RefreshJobsButton"] = ("Vuelve a leer los trabajos guardados y los registros pendientes.", ""),
        ["ImportConfigurationButton"] = ("Abre una configuración exportada y comprueba sus carpetas antes de guardarla deshabilitada.", ""),
        ["ProductTrayButton"] = ("Oculta la ventana y muestra un icono junto al reloj para consultar el estado y abrir la aplicación.", ""),
        ["LegacyRecoveryButton"] = ("Abre las herramientas para respaldos anteriores de alfa. Usa Nuevo trabajo para una copia nueva.", ""),
        ["ManageApplicationButton"] = ("Revisa el ejecutor, sus horarios y las versiones instaladas. Entrar aquí no modifica los trabajos.", ""),
        ["ResumeRegistrationButton"] = ("Completa el registro original de un trabajo interrumpido, conservando su revisión.", "Selecciona un registro pendiente en la lista."),
        ["JobList"] = ("Selecciona un trabajo para ver sus carpetas, resultados y acciones. Su nombre y estado se conservan al volver.", ""),
        ["BackupJobButton"] = ("Crea una copia nueva o reintenta un plan compatible. En espejo prepara primero una revisión de cambios.", "Selecciona un trabajo habilitado y activo. Espera a que termine su operación actual."),
        ["RecoverJobButton"] = ("Abre los puntos disponibles para verificar, buscar y recuperar archivos. También sirve con el origen ausente.", "Selecciona un trabajo y espera a que termine su operación actual. Debe existir un punto consultable."),
        ["ExportConfigurationButton"] = ("Guarda opciones, rutas e identidades para importar el trabajo. Revisa las rutas antes de compartir el archivo.", "Selecciona un trabajo y espera a que termine su operación actual."),
        ["AcceptEmptySource"] = ("Confirma que has comprobado personalmente el origen vacío que muestra esta revisión del espejo.", "Revisa el inventario y la identidad del origen."),
        ["AcceptMassChanges"] = ("Confirma que has revisado las altas, cambios y retiradas de gran alcance.", "Revisa el resumen del espejo."),
        ["ApplyMirrorButton"] = ("Aplica solo los cambios revisados y conserva el contenido anterior en un punto recuperable.", "Completa el análisis del espejo, resuelve los conflictos y acepta las revisiones adicionales indicadas."),
        ["JobFoldersPanel"] = ("Muestra las carpetas registradas de origen y destino para comprobar dónde lee y escribe el trabajo.", ""),
        ["RunHistoryPanel"] = ("Despliega resultados e incidencias por intento. Un fallo anterior permanece documentado después del reintento.", ""),
        ["RunHistoryList"] = ("Consulta la operación, fecha, resultado y ruta de cada intento reciente. Usa flechas para recorrerlos.", ""),
        ["PendingVersionPanel"] = ("Revisa un plan incompleto que ya no coincide con el origen antes de permitir un análisis nuevo.", ""),
        ["ReviewPendingButton"] = ("Inspecciona el plan pendiente y sus datos conservados. Esta revisión no crea una copia nueva.", "Selecciona un trabajo de versionado con un plan pendiente y espera al ejecutor."),
        ["AcceptPendingPlan"] = ("Acepta conservar el plan incompleto y permitir uno nuevo. Los parciales seguirán ocupando espacio.", "Lee la revisión del plan pendiente."),
        ["ApplyPendingButton"] = ("Conserva el plan anterior mediante su diario y permite un respaldo nuevo. El plan conservado no cuenta como copia completa.", "Revisa el plan y marca su aceptación antes de continuar."),
        ["JobSettingsPanel"] = ("Edita nombre y horario, habilita o retira el trabajo. Para cambiar carpetas o modo crea otro trabajo.", "Selecciona un trabajo."),
        ["EditJobName"] = ("Escribe un nombre reconocible, por ejemplo Documentos de C a D. Usa hasta 100 caracteres.", ""),
        ["EditDailyTime"] = ("Hora local diaria HH:mm, por ejemplo 20:30. Vacío significa a demanda. El horario requiere tu sesión iniciada.", ""),
        ["EditEnabled"] = ("Permite respaldos nuevos y horarios. Deshabilitar conserva puntos e historial para recuperar.", ""),
        ["EditRetired"] = ("Desactiva nuevos respaldos y su horario. Conserva configuración, parciales y puntos recuperables.", ""),
        ["ReviewImportedSourceButton"] = ("Comprueba la identidad del origen importado antes de permitir futuras copias.", "Selecciona un trabajo importado deshabilitado y espera al ejecutor."),
        ["SaveSettingsButton"] = ("Guarda los cambios de nombre, hora, habilitación y retirada. Comprueba el estado observado del horario.", "Selecciona un trabajo y espera a que termine su operación actual."),
        ["ApplyScheduleButton"] = ("Repara el horario propio con la hora guardada e instala un ejecutor comprobado si hace falta.", "Guarda una hora diaria válida y habilita el trabajo. Espera al ejecutor."),
        ["ProductRetentionPanel"] = ("Revisa qué fechas conservar. La limpieza es manual y tiene revisión, cuarentena, deshacer y purga.", "Selecciona un trabajo y espera a que termine su operación actual."),
        ["ProductRetentionDays"] = ("Cantidad de días recientes con puntos diarios que conservar. La selección usa calendario UTC.", ""),
        ["ProductRetentionWeeks"] = ("Cantidad de semanas con puntos semanales que conservar, además de los diarios.", ""),
        ["ProductRetentionMonths"] = ("Cantidad de meses con puntos mensuales que conservar, además de días y semanas.", ""),
        ["ProductRetentionGrace"] = ("Días de espera desde que termina la cuarentena hasta permitir purgar. La espera no borra automáticamente.", ""),
        ["PreviewProductRetentionButton"] = ("Calcula fechas y objetos afectados. Revisa esta lista antes de aceptar movimientos.", "Selecciona un trabajo inactivo y una política válida. Los catálogos y registros deben estar íntegros."),
        ["AcceptProductRetention"] = ("Confirma las fechas que muestra la revisión actual antes de enviarlas a cuarentena.", "Pulsa Revisar retención y lee su resultado."),
        ["ApplyProductRetentionButton"] = ("Expira las fechas revisadas y mueve objetos exclusivos a cuarentena. Puedes deshacer y todavía ocupa espacio.", "Revisa una política con fechas u objetos que retirar y marca la aceptación. Si cambia el respaldo, revisa de nuevo."),
        ["ProductRetentionChoice"] = ("Elige una cuarentena para consultar su estado, reanudarla, deshacerla o purgarla.", "Debe existir una operación de cuarentena consultable."),
        ["ResumeProductRetentionButton"] = ("Continúa los movimientos pendientes de la cuarentena seleccionada usando su diario.", "Selecciona una cuarentena con movimientos pendientes y espera al ejecutor."),
        ["UndoProductRetentionButton"] = ("Devuelve los objetos de la cuarentena y vuelve a retener las fechas. También reanuda un deshacer incompleto.", "Selecciona una cuarentena reversible y espera al ejecutor."),
        ["AcceptProductPurge"] = ("Acepta el borrado irreversible de objetos exclusivos de la cuarentena seleccionada.", "Comprueba la cuarentena y la gracia antes de aceptar."),
        ["PurgeProductRetentionButton"] = ("Borra los objetos de la cuarentena tras comprobar gracia, referencias y puntos retenidos. Este borrado es irreversible.", "Selecciona una cuarentena terminada o purga pendiente, acepta el borrado y espera la gracia. Catálogos y registros deben estar íntegros."),
        ["AcceptApplicationUpdate"] = ("Acepta actualizar el ejecutor y los horarios propios de los trabajos revisados.", "Lee los trabajos afectados y espera a que terminen operaciones y reintentos."),
        ["InstallApplicationButton"] = ("Instala una copia comprobada del paquete y dirige los horarios habilitados a ese ejecutor.", "Revisa los trabajos y acepta la actualización. Espera a que terminen operaciones y reintentos."),
        ["AcceptRemoveSchedules"] = ("Acepta deshabilitar todos los trabajos revisados y retirar sus horarios propios. Los respaldos permanecen.", "Lee la revisión de trabajos."),
        ["RemoveApplicationSchedulesButton"] = ("Retira horarios propios y deshabilita los trabajos revisados. Conserva datos, configuración e historial.", "Revisa los trabajos y marca la aceptación de retirar sus horarios."),
        ["ReviewInstalledVersionsButton"] = ("Consulta las versiones instaladas y las retiradas interrumpidas que puedes revisar.", ""),
        ["InstalledVersionChoice"] = ("Selecciona una versión concreta y comprueba su ubicación antes de retirarla.", "Pulsa Revisar versiones instaladas."),
        ["AcceptInstalledVersionRemoval"] = ("Acepta eliminar únicamente los archivos de la versión seleccionada. Los respaldos y la configuración permanecen.", "Lee la ubicación y la versión elegidas."),
        ["RemoveInstalledVersionButton"] = ("Retira la versión revisada con un diario reanudable y comprobación de archivos y referencias.", "Deshabilita los trabajos y retira sus horarios. Selecciona otra versión distinta de la abierta y acepta su retirada."),
        ["ChooseImportDestinationButton"] = ("Elige la carpeta dedicada del respaldo existente, con sus marcadores y catálogos.", ""),
        ["ChooseImportSourceButton"] = ("Elige el origen original para revisar su identidad y preparar copias futuras.", ""),
        ["ClearImportSourceButton"] = ("Importa para recuperar sin disponer del origen. El trabajo permanece deshabilitado.", ""),
        ["ReviewImportButton"] = ("Contrasta las identidades del respaldo, configuración y origen elegido. No adopta otro disco por tener el mismo nombre.", "Elige el respaldo existente. Si vas a copiar, elige también el origen."),
        ["AcceptImport"] = ("Confirma el modo y las carpetas comprobadas que aparecen en el resumen.", "Comprueba identidades y lee la revisión."),
        ["SaveImportButton"] = ("Registra el trabajo importado deshabilitado. Después podrás recuperar o revisar el origen antes de habilitar copias.", "Comprueba identidades y marca la aceptación de la revisión."),
        ["RecoveryDates"] = ("Selecciona el punto por fecha. Las fechas expiradas o dañadas indican su motivo.", "Debe existir un punto consultable y el trabajo debe estar inactivo."),
        ["SearchRecoveryButton"] = ("Busca un nombre o parte de ruta en todas las carpetas del punto elegido.", "Selecciona una fecha legible y espera a que termine la consulta."),
        ["RecoverySearch"] = ("Escribe un nombre o parte de ruta. Enter busca en el punto completo. Vacío muestra la carpeta actual.", ""),
        ["RecoveryOnlyChanges"] = ("Muestra altas, cambios y eliminaciones respecto al punto anterior disponible. La comparación identifica ambos puntos.", "Selecciona un punto con un anterior consultable."),
        ["RecoveryParentButton"] = ("Sube un nivel dentro del respaldo.", "Ya estás en la raíz del respaldo."),
        ["OpenRecoveryFolderButton"] = ("Entra en la carpeta seleccionada dentro del respaldo.", "Selecciona una carpeta de los resultados."),
        ["RecoveryFiles"] = ("Selecciona carpetas para navegar o archivos para recuperar. Un eliminado procede del punto anterior que conserva su contenido.", "Selecciona un punto legible."),
        ["PreviousRecoveryPageButton"] = ("Muestra la página anterior de resultados.", "Estás en la primera página."),
        ["NextRecoveryPageButton"] = ("Muestra más resultados del mismo punto o búsqueda.", "No quedan más resultados."),
        ["VerifyPointButton"] = ("Vuelve a leer el contenido del punto y comprueba sus hashes. No cambia la fecha del último respaldo completo.", "Selecciona una fecha no expirada, con catálogo legible, y espera al ejecutor."),
        ["ReviewRestoreButton"] = ("Elige dónde crear una carpeta nueva para recuperar todo el punto y revisa el resumen.", "Selecciona un punto recuperable y espera al ejecutor."),
        ["ReviewFileRestoreButton"] = ("Elige dónde recuperar el archivo seleccionado. Un eliminado se toma del punto anterior disponible.", "Selecciona un archivo y un punto recuperable. Para un eliminado debe existir el punto anterior."),
        ["RestorePointButton"] = ("Recupera los archivos revisados en la carpeta nueva indicada, conservando el origen y el respaldo.", "Elige el alcance y la carpeta de salida antes de confirmar."),
        ["OpenRecoveryOutputButton"] = ("Abre en el Explorador la última carpeta recuperada con resultado confirmado para este trabajo.", "Completa una recuperación de este trabajo."),
        ["OpenRestoredFolderButton"] = ("Abre en el Explorador la última carpeta recuperada con resultado confirmado para este trabajo.", "Completa una recuperación de este trabajo."),
        ["BackStepButton"] = ("Vuelve al paso anterior conservando lo elegido. Puedes corregirlo antes de guardar.", "Estás en el primer paso del asistente."),
        ["NextStepButton"] = ("Avanza y comprueba lo elegido. En el último paso revisas todas las opciones antes de guardar.", "Elige la carpeta que solicita este paso."),
        ["SaveJobButton"] = ("Crea la carpeta dedicada y guarda la configuración. Después pulsa Respaldar ahora para hacer la primera copia.", "Completa los pasos y revisa origen, destino, modo y horario."),
        ["CancelWizardButton"] = ("Sale del asistente y descarta la selección sin crear el trabajo.", ""),
        ["ChooseOriginButton"] = ("Selecciona la carpeta cuyos archivos quieres copiar. Se leerá su contenido.", ""),
        ["ChooseParentButton"] = ("Selecciona dónde crear la subcarpeta dedicada de este trabajo. Para proteger frente al fallo de un disco, usa otro disco físico.", ""),
        ["JobName"] = ("Nombre para reconocer el trabajo. Por ejemplo, Documentos de C a D.", ""),
        ["BackupMode"] = ("Acumulativo conserva archivos retirados del origen; Versionado permite volver por fecha; Espejo refleja el origen y conserva lo retirado.", ""),
        ["DailyTime"] = ("Hora local diaria HH:mm. Deja vacío para ejecutar cuando tú quieras. Un horario necesita tu sesión iniciada.", ""),
        ["PauseRunButton"] = ("Solicita una pausa o continúa el intento pausado. El ejecutor atiende la orden en un límite recuperable.", "Debe haber una operación activa para este trabajo."),
        ["CancelRunButton"] = ("Detiene el intento en un límite recuperable y conserva los parciales. Podrás revisar o reintentar.", "Debe haber una operación activa para este trabajo.")
    };

    private void InitializeProductHelp()
    {
        foreach (var (name, entry) in helpEntries)
        {
            if (FindName(name) is not Control control)
                throw new InvalidOperationException("Missing help control " + name);
            AutomationProperties.SetHelpText(control, entry.Text);
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 380 };
            var tip = new ToolTip { Content = text };
            tip.Opened += (_, _) => text.Text = HelpFor(control);
            control.ToolTip = tip;
            ToolTipService.SetShowOnDisabled(control, true);
            ToolTipService.SetShowDuration(control, 30_000);
        }
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F1) { e.Handled = true; HelpClick(this, new RoutedEventArgs()); }
        };
    }

    private string HelpFor(Control control)
    {
        var entry = helpEntries[control.Name];
        string reason = control.IsEnabled ? "" : busy ? "Espera a que termine la comprobación actual." : entry.Requires;
        if (reason.Length == 0 && !control.IsEnabled)
            reason = "Selecciona un trabajo y espera a que termine su operación actual.";
        return entry.Text + (reason.Length == 0 ? "" : "\n\nPara habilitar esta acción\n" + reason);
    }

    private ProductHelpWindow? helpWindow;
    private void HelpClick(object sender, RoutedEventArgs e)
    {
        if (helpWindow is not null) { helpWindow.Activate(); return; }
        var context = helpEntries.Keys.Select(name => (Control)FindName(name))
            .Where(control => control.IsVisible && control != HelpButton)
            .Select(control => (Label: HelpLabel(control), Text: HelpFor(control))).ToArray();
#pragma warning disable WPF0001
        helpWindow = new ProductHelpWindow(context, BuildDiagnostic) { Owner = this, ThemeMode = ThemeMode };
#pragma warning restore WPF0001
        helpWindow.Closed += (_, _) => { helpWindow = null; RestoreKeyboardFocus(HelpButton); };
        helpWindow.Show();
    }

    private static string HelpLabel(Control control) =>
        (control is HeaderedContentControl header ? header.Header?.ToString() :
            control is ContentControl content ? content.Content?.ToString() : null)?.Replace("_", "") ??
        AutomationProperties.GetName(control);

    private string BuildDiagnostic()
    {
        // Deliberate allowlist. Never include job names, IDs, paths, detail, hashes of user files or exception text.
        var rows = JobList.Items.OfType<JobRow>().ToArray();
        return ProductIdentity.Read() + "\nWindows · " + Environment.OSVersion.Version +
            "\nArquitectura · " + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture +
            "\nRuntime · " + Environment.Version + "\nTrabajos consultables · " + rows.Count(row => row.Job is not null) +
            "\nRegistros pendientes · " + rows.Count(row => row.Pending) + "\n" +
            string.Join("\n", Enum.GetValues<ProductBackupMode>().Select(mode =>
                mode + " · " + rows.Count(row => row.Job?.Options.Mode == mode))) + "\n" +
            string.Join("\n", Enum.GetValues<ProductRunState>().Select(state =>
                state + " · " + runSnapshots.Values.Count(history => history.Runs.FirstOrDefault()?.Result?.State == state))) +
            "\nRevisa este resumen antes de copiarlo. Describe los pasos del fallo con datos inventados.";
    }
}

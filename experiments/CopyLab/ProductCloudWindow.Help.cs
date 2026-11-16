using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace CopyLab;

public partial class ProductCloudWindow
{
    private readonly Dictionary<string, (string Label, string Help)> cloudHelp = new()
    {
        ["Provider"] = ("Proveedor", "Elige dónde guardar la tercera copia. Las conexiones con cuentas reales siguen pendientes de validación."),
        ["Region"] = ("Región", "Copia la región del bucket que muestra tu proveedor. Ejemplos de formato: us-east-1 en AWS o us-west-004 en B2."),
        ["Endpoint"] = ("Servidor S3", "Para un servicio compatible, escribe el nombre de su servidor, sin https:// ni rutas. Usa el endpoint indicado por el proveedor."),
        ["Bucket"] = ("Bucket", "Es el contenedor privado que has creado en el servicio S3. Copia su nombre exacto."),
        ["Prefix"] = ("Carpeta del repositorio", "Una carpeta dedicada identifica este respaldo dentro del destino. Filen y MEGA la requieren. Usa letras, números, guiones o guiones bajos y barras para subcarpetas."),
        ["AccessKey"] = ("Identificador o correo", "En S3, introduce el identificador de la clave de acceso. En Filen o MEGA, el correo de la cuenta de prueba."),
        ["SecretKey"] = ("Clave del proveedor", "En S3, introduce el secreto de acceso. En Filen o MEGA, la contraseña de la cuenta. FolderTwin protege esta credencial en tu perfil de Windows."),
        ["SessionToken"] = ("Token o clave API", "AWS puede requerir un token para credenciales temporales. Filen requiere su clave API. MEGA usa los campos de correo y contraseña."),
        ["RepositoryPassword"] = ("Contraseña del repositorio", "Al crear, elige la contraseña de cifrado y guárdala fuera del equipo. Al conectar o recuperar, escribe la contraseña con la que se creó este repositorio."),
        ["ConfirmRepositoryPassword"] = ("Confirmación de recuperación", "Repite exactamente la contraseña elegida para crear un repositorio. FolderTwin comprobará una conexión independiente de lectura antes de activar el envío."),
        ["RecoveryInformationSaved"] = ("Datos para recuperar", "Marca esta opción después de guardar la ubicación y la contraseña fuera de este equipo. Se exige al crear un repositorio nuevo."),
        ["CreateCloud"] = ("Crear y activar copia", "Crea un repositorio nuevo, comprueba la contraseña de recuperación y activa los envíos del trabajo seleccionado."),
        ["ConnectCloud"] = ("Conectar y activar copia", "Abre un repositorio existente con su contraseña y activa los envíos del trabajo seleccionado."),
        ["ListCloud"] = ("Consultar fechas", "Abre el repositorio en lectura y muestra sus fechas. También funciona con un perfil de FolderTwin vacío."),
        ["CloudTransfers"] = ("Envío seleccionado", "Elige una fecha y comprueba su estado. Pausar, Cancelar y Reintentar se aplican a ese envío concreto."),
        ["PauseCloudJob"] = ("Pausa del trabajo", "Pausa o reanuda todos los envíos de nube de este trabajo. La copia local mantiene su configuración."),
        ["PreviousTransfers"] = ("Envíos anteriores", "Muestra la página anterior de hasta cien envíos. Conserva la selección cuando permanece en la página."),
        ["NextTransfers"] = ("Envíos siguientes", "Muestra la página siguiente de hasta cien envíos de este repositorio."),
        ["RefreshCloud"] = ("Actualizar envíos", "Vuelve a consultar el diario de envíos de este trabajo y sus incidencias."),
        ["PauseCloud"] = ("Pausar envío", "Detiene el envío seleccionado. Usa Reintentar para volver a continuarlo. La copia local continúa."),
        ["CancelCloud"] = ("Cancelar envío", "Cancela el intento seleccionado y conserva su registro. Si el proveedor ya confirmó la fecha, la fecha remota puede seguir disponible."),
        ["RetryCloud"] = ("Reintentar envío", "Vuelve a intentar el envío seleccionado después de resolver su incidencia. Comprueba primero la conexión y el espacio disponible."),
        ["NewRepositoryReview"] = ("Revisión al crear", "Expande la confirmación de contraseña y la revisión de los datos necesarios para recuperar sin este equipo."),
        ["CloudHelpButton"] = ("Ayuda de nube", "Abre las explicaciones de los controles visibles. También puedes pulsar F1. Al cerrar la ayuda, vuelve el foco al control anterior."),
        ["CloseCloudButton"] = ("Cerrar nube", "Cierra esta ventana. Los trabajos de copia local mantienen su configuración. Una recuperación iniciada en esta ventana conserva su salida parcial si se interrumpe."),
        ["CloudDates"] = ("Fecha para recuperar", "Elige uno de los puntos consultados en el repositorio remoto."),
        ["RestoreCloud"] = ("Recuperar", "Recupera la fecha elegida en una carpeta nueva. Consulta las fechas con las credenciales y la contraseña del repositorio antes de continuar.")
    };
    private ProductHelpWindow? cloudHelpWindow;

    private void InitializeCloudHelp(object sender, RoutedEventArgs e)
    {
        foreach (var (name, entry) in cloudHelp)
        {
            if (FindName(name) is not Control control) continue;
            AutomationProperties.SetHelpText(control, entry.Help);
            control.ToolTip = new ToolTip
            {
                Content = new TextBlock { Text = entry.Help, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 }
            };
            ToolTipService.SetShowOnDisabled(control, true);
            ToolTipService.SetShowDuration(control, 30_000);
        }
        Provider.SelectionChanged += (_, _) => RefreshProviderHelp();
        RefreshProviderHelp();
        Closed += (_, _) => cloudHelpWindow?.Close();
        _ = Dispatcher.InvokeAsync(() => { Provider.Focus(); }, DispatcherPriority.Input);
    }

    private void RefreshProviderHelp() => ProviderAccessHelp.Text = (CloudProvider)Provider.SelectedIndex switch
    {
        CloudProvider.Filen => "Filen requiere correo, contraseña y clave API. Usa una carpeta dedicada. La autenticación real y las opciones de seguridad de la cuenta siguen pendientes de prueba.",
        CloudProvider.Mega => "MEGA usa correo y contraseña. La autenticación real, el doble factor y las cuotas siguen pendientes de prueba.",
        _ => "Usa una clave de prueba con acceso al bucket privado. Para una sesión temporal de AWS, incluye también su token."
    };

    private void CloudHelpKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F1) return;
        e.Handled = true;
        CloudHelpClick(this, new RoutedEventArgs());
    }

    private void CloudHelpClick(object sender, RoutedEventArgs e)
    {
        if (cloudHelpWindow is not null) { cloudHelpWindow.Activate(); return; }
        var focus = Keyboard.FocusedElement as Control;
        var entries = cloudHelp.Where(entry => FindName(entry.Key) is Control { IsVisible: true })
            .Select(entry => (entry.Value.Label, entry.Value.Help)).ToArray();
        cloudHelpWindow = new ProductHelpWindow(entries, ProductIdentity.Read) { Owner = this };
        cloudHelpWindow.Closed += (_, _) =>
        {
            cloudHelpWindow = null;
            if (IsVisible && focus is { IsVisible: true, IsEnabled: true }) focus.Focus();
        };
        cloudHelpWindow.Show();
    }
}

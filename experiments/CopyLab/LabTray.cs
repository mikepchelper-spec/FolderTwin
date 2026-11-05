using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace CopyLab;

internal sealed class LabTray : IDisposable
{
    private readonly Func<TraySnapshot> read;
    private readonly Action<Guid> start;
    private readonly Forms.NotifyIcon icon;
    private readonly Forms.ContextMenuStrip menu = new();
    private readonly Forms.ToolStripItem menuRun;
    private readonly Window flyout;
    private readonly TextBlock status = new() { Text = "Consulta el estado de los trabajos.", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock age = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 16) };
    private readonly Button run = new() { Content = "Respaldar ahora", IsEnabled = false, Margin = new(0, 0, 8, 0) };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMinutes(5) };
    private readonly HashSet<string> notified = new(StringComparer.Ordinal);
    private readonly bool notifications;
    private TraySnapshot? snapshot;
    private bool refreshing;
    private bool disposed;
    internal string StatusText => status.Text;
    internal string AgeText => age.Text;
    internal bool CanRun => run.IsEnabled;
    internal int NotificationCount { get; private set; }

    internal LabTray(LabWorkspace workspace, Action openMain, Action exit, bool notifications = true)
        : this(() => TraySnapshot.Read(workspace), id => LabWorker.Start(workspace, id), openMain, exit, notifications,
            "FolderTwin · espacio de pruebas") { }

    internal LabTray(Func<ProductJobRegistry> openRegistry, Action openMain, Action exit, bool notifications = true)
        : this(() => { using var registry = openRegistry(); return TraySnapshot.ReadProduct(registry); },
            id =>
            {
                using var registry = openRegistry();
                var job = registry.Get(id);
                if (!job.Enabled || job.Retired || registry.RunHistory(id).Runs.Any(run => run.Running)) return;
                using var worker = ProductWorker.Start(registry.Location, id);
            }, openMain, exit, notifications, "FolderTwin") { }

    private LabTray(Func<TraySnapshot> read, Action<Guid> start, Action openMain, Action exit, bool notifications, string iconText)
    {
        this.read = read;
        this.start = start;
        this.notifications = notifications;
        var panel = new StackPanel { Margin = new(20) };
        panel.Children.Add(new TextBlock { Text = "FolderTwin", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 12) });
        panel.Children.Add(status);
        panel.Children.Add(age);
        var buttons = new WrapPanel();
        var open = new Button { Content = "Abrir aplicación" };
        open.Click += (_, _) => { flyout!.Hide(); openMain(); };
        run.Click += (_, _) => RunNow();
        buttons.Children.Add(run);
        buttons.Children.Add(open);
        panel.Children.Add(buttons);
        flyout = new Window
        {
            Title = "FolderTwin", Width = 370, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStyle = WindowStyle.ToolWindow,
            ShowInTaskbar = false, Topmost = true, Content = panel
        };
        flyout.Deactivated += (_, _) => flyout.Hide();
        flyout.Closing += (_, e) => { if (!disposed) { e.Cancel = true; flyout.Hide(); } };
        flyout.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { flyout.Hide(); e.Handled = true; } };
        menu.Items.Add("Abrir FolderTwin", null, (_, _) => openMain());
        menuRun = menu.Items.Add("Respaldar ahora", null, (_, _) => RunNow());
        menuRun.Enabled = false;
        menu.Items.Add("Salir", null, (_, _) => exit());
        icon = new Forms.NotifyIcon
        {
            Text = iconText, Icon = System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu, Visible = true
        };
        icon.MouseClick += async (_, e) => { if (e.Button == Forms.MouseButtons.Left) await ShowAsync(); };
        icon.BalloonTipClicked += (_, _) => openMain();
        timer.Tick += async (_, _) => await RefreshAsync();
        timer.Start();
    }

    internal async Task ShowAsync()
    {
        if (disposed)
            return;
        flyout.Left = Math.Max(SystemParameters.WorkArea.Left, SystemParameters.WorkArea.Right - flyout.Width - 12);
        flyout.Top = Math.Max(SystemParameters.WorkArea.Top, SystemParameters.WorkArea.Bottom - 270);
        flyout.Show();
        flyout.Activate();
        await RefreshAsync();
    }

    internal async Task RefreshAsync()
    {
        if (disposed || refreshing)
            return;
        refreshing = true;
        try
        {
            TraySnapshot current;
            try { current = await Task.Run(read); }
            catch (Exception error) when (VersionJob.Expected(error))
            {
                current = new("La configuración requiere revisión.", "No se pudo consultar el último punto.", [], ["workspace"]);
            }
            if (disposed)
                return;
            snapshot = current;
            status.Text = current.Status;
            age.Text = current.Age;
            run.IsEnabled = menuRun.Enabled = current.Jobs.Length != 0;
            notified.IntersectWith(current.Attention);
            bool changed = false;
            foreach (string key in current.Attention)
                if (notified.Add(key))
                    changed = true;
            if (changed)
            {
                NotificationCount++;
                if (notifications)
                {
                    icon.BalloonTipTitle = "FolderTwin requiere atención";
                    icon.BalloonTipText = current.Status + " Abre la aplicación para revisar los trabajos.";
                    icon.BalloonTipIcon = Forms.ToolTipIcon.Warning;
                    icon.ShowBalloonTip(10_000);
                }
            }
        }
        finally { refreshing = false; }
    }

    internal void RunNow()
    {
        if (disposed || snapshot is null)
            return;
        try
        {
            foreach (Guid id in snapshot.Jobs)
                start(id);
            status.Text = "Se solicitó la copia de los trabajos guardados. Consulta su resultado al terminar.";
        }
        catch (Exception error) when (VersionJob.Expected(error))
        {
            status.Text = "No se pudo iniciar un trabajo. " + error.Message;
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        timer.Stop();
        icon.Visible = false;
        icon.Dispose();
        menu.Dispose();
        flyout.Close();
    }
}

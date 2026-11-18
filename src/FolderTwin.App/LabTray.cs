using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace FolderTwin;

internal sealed class LabTray : IDisposable
{
    private readonly Func<TraySnapshot> read;
    private readonly Action<Guid> start;
    private readonly Forms.NotifyIcon icon;
    private readonly System.Drawing.Icon trayIcon;
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

    internal LabTray(Func<ProductJobRegistry> openRegistry, Action openMain, Action exit, bool notifications = true,
        Action<Guid>? openIssue = null, ProductJobRegistry.DisplayRunHistory? displayHistory = null)
        : this(() => { using var registry = openRegistry(); return TraySnapshot.ReadProduct(registry, displayHistory); },
            id =>
            {
                using var registry = openRegistry();
                var job = registry.Get(id);
                if (!job.Enabled || job.Retired || registry.RunHistory(id).Runs.Any(run => run.Running)) return;
                using var worker = ProductWorker.Start(registry.Location, id);
            }, openMain, exit, notifications, "FolderTwin", openIssue) { }

    private LabTray(Func<TraySnapshot> read, Action<Guid> start, Action openMain, Action exit, bool notifications, string iconText,
        Action<Guid>? openIssue = null)
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
        open.Click += (_, _) => { flyout!.Hide(); OpenAttention(); };
        run.Click += async (_, _) => await RunNowAsync();
        buttons.Children.Add(run);
        buttons.Children.Add(open);
        panel.Children.Add(buttons);
        flyout = new Window
        {
            Title = "FolderTwin", Width = 370, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStyle = WindowStyle.ToolWindow,
            ShowInTaskbar = false, Topmost = true, Content = panel,
            Language = XmlLanguage.GetLanguage("es-CO"),
            Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/FolderTwin.ico", UriKind.Absolute))
        };
        flyout.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("ProductRoute.xaml", UriKind.Relative) });
        RouteTheme.SetUsePalette(flyout, true);
        flyout.SizeChanged += (_, _) => PositionFlyout();
        flyout.Deactivated += (_, _) => flyout.Hide();
        flyout.Closing += (_, e) => { if (!disposed) { e.Cancel = true; flyout.Hide(); } };
        flyout.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { flyout.Hide(); e.Handled = true; } };
        menu.Items.Add("Abrir FolderTwin", null, (_, _) => openMain());
        menuRun = menu.Items.Add("Respaldar ahora", null, async (_, _) => await RunNowAsync());
        menuRun.Enabled = false;
        menu.Items.Add("Salir", null, (_, _) => exit());
        using (var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/FolderTwin.ico")).Stream)
        using (var original = new System.Drawing.Icon(resource, Forms.SystemInformation.SmallIconSize))
            trayIcon = (System.Drawing.Icon)original.Clone();
        icon = new Forms.NotifyIcon
        {
            Text = iconText, Icon = trayIcon,
            ContextMenuStrip = menu, Visible = true
        };
        icon.MouseClick += async (_, e) => { if (e.Button == Forms.MouseButtons.Left) await ShowAsync(); };
        icon.BalloonTipClicked += (_, _) => OpenAttention();
        timer.Tick += async (_, _) => await RefreshAsync();
        timer.Start();

        void OpenAttention()
        {
            if (openIssue is not null && snapshot is { } current)
            {
                foreach (string key in current.Attention)
                {
                    if (key.Length <= 32 || key[32] != '-' || !Guid.TryParseExact(key.AsSpan(0, 32), "N", out Guid id)) continue;
                    openIssue(id);
                    return;
                }
            }
            openMain();
        }
    }

    private void PositionFlyout()
    {
        var workArea = SystemParameters.WorkArea;
        double width = flyout.ActualWidth > 0 ? flyout.ActualWidth : flyout.Width;
        double height = flyout.ActualHeight > 0 ? flyout.ActualHeight : 220;
        double rightEdge = workArea.Right - width - 12;
        double bottomEdge = workArea.Bottom - height - 12;
        // Anchor near the taskbar corner (handles top/left/right/bottom taskbars cleanly).
        bool taskbarAtTop = workArea.Top > SystemParameters.VirtualScreenTop + 8;
        bool taskbarAtLeft = workArea.Left > SystemParameters.VirtualScreenLeft + 8;
        flyout.Left = Math.Clamp(taskbarAtLeft ? workArea.Left + 12 : rightEdge, workArea.Left + 4, Math.Max(workArea.Left + 4, workArea.Right - width - 4));
        flyout.Top = Math.Clamp(taskbarAtTop ? workArea.Top + 12 : bottomEdge, workArea.Top + 4, Math.Max(workArea.Top + 4, workArea.Bottom - height - 4));
    }

    internal async Task ShowAsync()
    {
        if (disposed)
            return;
        PositionFlyout();
        flyout.Show();
        flyout.UpdateLayout();
        PositionFlyout();
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

    internal void RunNow() => _ = RunNowAsync();

    internal async Task RunNowAsync()
    {
        if (disposed || snapshot is null)
            return;
        Guid[] targets = snapshot.Jobs;
        if (targets.Length == 0) return;
        run.IsEnabled = menuRun.Enabled = false;
        try
        {
            await Task.Run(() =>
            {
                foreach (Guid id in targets)
                    start(id);
            });
            if (!disposed)
                status.Text = "Se solicitó la copia de los trabajos guardados. Consulta su resultado al terminar.";
        }
        catch (Exception error) when (VersionJob.Expected(error))
        {
            if (!disposed)
                status.Text = "No se pudo iniciar un trabajo. " + error.Message;
        }
        finally
        {
            if (!disposed)
                run.IsEnabled = menuRun.Enabled = snapshot?.Jobs.Length > 0;
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
        trayIcon.Dispose();
        menu.Dispose();
        flyout.Close();
    }
}

using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace CopyLab;

internal sealed record ScheduleInfo(bool Enabled, DateTime NextRun, DateTime LastRun, uint LastResult,
    string Executable, bool MeetsPolicy, bool Running);

internal sealed class WindowsSchedule : IDisposable
{
    private static readonly XNamespace Schema = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private sealed record Owner(string Name, string Description, string Arguments, bool Tray, bool Product,
        string? LegacyArguments = null, TimeOnly? DailyAt = null);
    private readonly dynamic service;
    private readonly dynamic folder;
    private readonly string user;

    internal WindowsSchedule()
    {
        user = WindowsIdentity.GetCurrent().User?.Value ?? throw new IOException("No se pudo identificar al usuario.");
        Type type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new IOException("El Programador de tareas no está disponible.");
        service = Activator.CreateInstance(type) ?? throw new IOException("No se pudo conectar con el Programador de tareas.");
        try { service.Connect(); folder = service.GetFolder("\\"); }
        catch { Marshal.FinalReleaseComObject(service); throw; }
    }

    internal ScheduleInfo? Read(LabWorkspace workspace, Guid id) => ReadTask(LabOwner(workspace, id));
    internal ScheduleInfo? ReadTray(LabWorkspace workspace) => ReadTask(LabOwner(workspace, ScopeId(workspace), tray: true));
    internal ScheduleInfo? ReadProduct(ProductJobRegistry registry, Guid id) => ReadTask(ProductOwner(registry, id));
    internal ScheduleInfo? ReadProductReviewed(ProductJobRegistry registry, ProductJob job) => ReadTask(ProductOwner(registry, job));

    private ScheduleInfo? ReadTask(Owner owner)
    {
        dynamic? task = Find(owner.Name);
        if (task is null) return null;
        try
        {
            string executable = VerifyOwnership((string)task.Xml, owner);
            return new((bool)task.Enabled, (DateTime)task.NextRunTime, (DateTime)task.LastRunTime,
                unchecked((uint)(int)task.LastTaskResult), executable, PolicyProblem((string)task.Xml, owner, ReadRunLevel(task)) is null, (int)task.State == 4);
        }
        finally { Marshal.FinalReleaseComObject(task); }
    }

    internal void Register(LabWorkspace workspace, Guid id, string executable, DateTime firstRun, bool daily = true, bool tray = false) =>
        Register(LabOwner(workspace, id, tray), executable, firstRun, daily);

    internal void RegisterProduct(ProductJobRegistry registry, Guid id, string executable, DateTime firstRun, bool daily = true)
    {
        var job = registry.Get(id);
        RegisterProductReviewed(registry, job, executable, firstRun, daily);
    }

    internal void RegisterProductReviewed(ProductJobRegistry registry, ProductJob job, string executable, DateTime firstRun, bool daily = true)
    {
        if (!job.Enabled || job.Retired || job.Options.DailyAt is null)
            throw new InvalidDataException("Habilita el trabajo y elige una hora antes de programarlo.");
        Register(ProductOwner(registry, job), executable, firstRun, daily);
    }

    private void Register(Owner owner, string executable, DateTime firstRun, bool daily)
    {
        if (!LabInstallation.IsInstalledWorker(executable, owner.Product))
            throw new InvalidDataException("El horario requiere un ejecutor instalado y comprobado en su ubicación estable.");
        bool exists = ReadTask(owner) is not null;
        XElement trigger = owner.Tray ? Element("LogonTrigger", Element("Enabled", true), Element("UserId", user), Element("Delay", "PT30S")) :
            Element(daily ? "CalendarTrigger" : "TimeTrigger",
                Element("StartBoundary", firstRun.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)), Element("Enabled", true),
                daily ? Element("ScheduleByDay", Element("DaysInterval", 1)) : null);
        var document = new XDocument(new XElement(Schema + "Task", new XAttribute("version", "1.4"),
            Element("RegistrationInfo", Element("Description", owner.Description)), Element("Triggers", trigger),
            Element("Principals", new XElement(Schema + "Principal", new XAttribute("id", "Author"),
                Element("UserId", user), Element("LogonType", "InteractiveToken"), Element("RunLevel", "LeastPrivilege"))),
            Element("Settings", Element("MultipleInstancesPolicy", "IgnoreNew"), Element("DisallowStartIfOnBatteries", false),
                Element("StopIfGoingOnBatteries", false), Element("AllowHardTerminate", false), Element("StartWhenAvailable", !owner.Tray),
                Element("AllowStartOnDemand", true), Element("Enabled", false), Element("ExecutionTimeLimit", "PT0S")),
            new XElement(Schema + "Actions", new XAttribute("Context", "Author"), Element("Exec", Element("Command", executable),
                Element("Arguments", owner.Arguments), Element("WorkingDirectory", Path.GetDirectoryName(executable))))));
        dynamic registered = folder.RegisterTask(owner.Name, document.ToString(), exists ? 6 : 2, user, null, 3, null);
        try
        {
            _ = VerifyOwnership((string)registered.Xml, owner);
            if (PolicyProblem((string)registered.Xml, owner, ReadRunLevel(registered)) is { } problem)
                throw new IOException("La tarea registrada requiere revisión de la opción " + problem + ".");
            registered.Enabled = true;
            if (!(bool)registered.Enabled) throw new IOException("El horario no pudo habilitarse.");
        }
        finally { Marshal.FinalReleaseComObject(registered); }
    }

    internal void Remove(LabWorkspace workspace, Guid id) => Remove(LabOwner(workspace, id));
    internal void RemoveProduct(ProductJobRegistry registry, Guid id) => Remove(ProductOwner(registry, id));
    internal void RemoveProductReviewed(ProductJobRegistry registry, ProductJob job) => Remove(ProductOwner(registry, job));
    private void Remove(Owner owner)
    {
        if (ReadTask(owner) is not null) folder.DeleteTask(owner.Name, 0);
    }

    internal void RunNow(LabWorkspace workspace, Guid id) => RunNow(LabOwner(workspace, id));
    internal void RunProductNow(ProductJobRegistry registry, Guid id) => RunNow(ProductOwner(registry, id));
    private void RunNow(Owner owner)
    {
        dynamic task = Find(owner.Name) ?? throw new InvalidDataException("Este trabajo no tiene horario.");
        try
        {
            _ = VerifyOwnership((string)task.Xml, owner);
            if (!(bool)task.Enabled || PolicyProblem((string)task.Xml, owner, ReadRunLevel(task)) is not null)
                throw new InvalidDataException("El horario necesita revisión antes de ejecutarse.");
            try
            {
                dynamic running = task.Run(null);
                if (running is not null) Marshal.FinalReleaseComObject(running);
            }
            catch (COMException error) when (error.HResult == unchecked((int)0x8004131F)) { /* IgnoreNew: already running. */ }
        }
        finally { Marshal.FinalReleaseComObject(task); }
    }

    internal void SetTrayStartup(LabWorkspace workspace, string? executable, bool enabled)
    {
        var owner = LabOwner(workspace, ScopeId(workspace), tray: true);
        if (enabled) Register(owner, executable ?? throw new ArgumentException("Falta el ejecutor."), DateTime.Now, daily: false);
        else Remove(owner);
    }

    private dynamic? Find(string name)
    {
        try { return folder.GetTask(name); }
        catch (Exception error) when (error is FileNotFoundException || error is COMException { HResult: unchecked((int)0x80070002) }) { return null; }
    }

    internal void RequireNoVersionReferences(params string[] versionPaths)
    {
        var paths = versionPaths.Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        int folders = 0, tasks = 0;
        Visit(folder, 0);
        void Visit(dynamic current, int depth)
        {
            if (++folders > 4096 || depth > 64) throw new InvalidDataException("La consulta de horarios superó su límite. No se retira la versión.");
            dynamic collection = current.GetTasks(1); // Include hidden tasks; observe only.
            try
            {
                int count = (int)collection.Count;
                for (int index = 1; index <= count; index++)
                {
                    if (++tasks > 20_000) throw new InvalidDataException("Hay demasiadas tareas para comprobar la retirada.");
                    dynamic task = collection[index];
                    try
                    {
                        var xml = XDocument.Parse((string)task.Xml);
                        foreach (var command in xml.Descendants(Schema + "Actions").Elements(Schema + "Exec").Elements(Schema + "Command"))
                        {
                            string candidate = Environment.ExpandEnvironmentVariables(command.Value.Trim().Trim('"'));
                            if (!Path.IsPathFullyQualified(candidate)) continue;
                            string? parent = Path.GetDirectoryName(candidate);
                            if (parent is not null && paths.Contains(Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent))))
                                throw new InvalidDataException("Una tarea aún usa esta versión. Retira su horario o cambia su ejecutor antes de borrar la aplicación.");
                        }
                    }
                    finally { Marshal.FinalReleaseComObject(task); }
                }
            }
            finally { Marshal.FinalReleaseComObject(collection); }
            dynamic children = current.GetFolders(0);
            try
            {
                int count = (int)children.Count;
                for (int index = 1; index <= count; index++)
                {
                    dynamic child = children[index];
                    try { Visit(child, depth + 1); }
                    finally { Marshal.FinalReleaseComObject(child); }
                }
            }
            finally { Marshal.FinalReleaseComObject(children); }
        }
    }

    private string VerifyOwnership(string xml, Owner owner)
    {
        var root = XDocument.Parse(xml).Root ?? throw new InvalidDataException("La tarea no tiene definición.");
        var principals = root.Element(Schema + "Principals")?.Elements(Schema + "Principal").ToArray() ?? [];
        var principal = principals.Length == 1 ? principals[0] : null;
        var actions = root.Element(Schema + "Actions")?.Elements().ToArray() ?? [];
        string? executable = actions.Length == 1 ? (string?)actions[0].Element(Schema + "Command") : null;
        string? arguments = actions.Length == 1 ? (string?)actions[0].Element(Schema + "Arguments") : null;
        if ((string?)root.Element(Schema + "RegistrationInfo")?.Element(Schema + "Description") != owner.Description ||
            NormalizeSid((string?)principal?.Element(Schema + "UserId")) != user ||
            (string?)principal?.Element(Schema + "LogonType") != "InteractiveToken" || actions.Length != 1 ||
            actions[0].Name != Schema + "Exec" ||
            arguments != owner.Arguments && (owner.LegacyArguments is null || arguments != owner.LegacyArguments) ||
            executable is null || !LabInstallation.IsInstalledWorker(executable, owner.Product))
            throw new InvalidDataException("La tarea existente no coincide con el trabajo. Se conservó para revisión.");
        return executable;
    }

    private static int ReadRunLevel(dynamic task)
    {
        dynamic definition = task.Definition;
        try
        {
            dynamic principal = definition.Principal;
            try { return (int)principal.RunLevel; }
            finally { Marshal.FinalReleaseComObject(principal); }
        }
        finally { Marshal.FinalReleaseComObject(definition); }
    }

    private string? PolicyProblem(string xml, Owner owner, int runLevel)
    {
        var root = XDocument.Parse(xml).Root;
        var settings = root?.Element(Schema + "Settings");
        var triggers = root?.Element(Schema + "Triggers")?.Elements().ToArray() ?? [];
        if (triggers.Length != 1) return "Triggers";
        // Trigger.Enabled and AllowStartOnDemand default to true in the SDK schema.
        if ((string?)triggers[0].Element(Schema + "Enabled") is not (null or "true")) return "Trigger.Enabled";
        if (owner.Tray && (triggers[0].Name != Schema + "LogonTrigger" || NormalizeSid((string?)triggers[0].Element(Schema + "UserId")) != user))
            return "LogonTrigger.UserId";
        if (!owner.Tray && triggers[0].Name != Schema + "TimeTrigger" && triggers[0].Name != Schema + "CalendarTrigger") return "TimeTrigger/CalendarTrigger";
        if (triggers[0].Name == Schema + "CalendarTrigger" && owner.Product &&
            (!DateTime.TryParse((string?)triggers[0].Element(Schema + "StartBoundary"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ||
                owner.DailyAt != TimeOnly.FromDateTime(start) || (string?)triggers[0].Element(Schema + "ScheduleByDay")?.Element(Schema + "DaysInterval") != "1"))
            return "CalendarTrigger.StartBoundary";
        if ((string?)settings?.Element(Schema + "MultipleInstancesPolicy") != "IgnoreNew") return "MultipleInstancesPolicy";
        // Battery restrictions default to true; only an explicit false preserves our uninterrupted policy.
        if ((string?)settings?.Element(Schema + "DisallowStartIfOnBatteries") != "false") return "DisallowStartIfOnBatteries";
        if ((string?)settings?.Element(Schema + "StopIfGoingOnBatteries") != "false") return "StopIfGoingOnBatteries";
        if ((string?)settings?.Element(Schema + "AllowStartOnDemand") is not (null or "true")) return "AllowStartOnDemand";
        // Both constraints default to false; the local backup must not wait for idle or a network.
        if ((string?)settings?.Element(Schema + "RunOnlyIfIdle") is not (null or "false")) return "RunOnlyIfIdle";
        if ((string?)settings?.Element(Schema + "RunOnlyIfNetworkAvailable") is not (null or "false")) return "RunOnlyIfNetworkAvailable";
        if (!owner.Tray && (string?)settings?.Element(Schema + "StartWhenAvailable") != "true") return "StartWhenAvailable";
        if ((string?)settings?.Element(Schema + "ExecutionTimeLimit") != "PT0S") return "ExecutionTimeLimit";
        if (!owner.Tray && settings?.Element(Schema + "RestartOnFailure") is not null) return "RestartOnFailure";
        if (!owner.Tray && (string?)root?.Element(Schema + "Actions")?.Element(Schema + "Exec")?.Element(Schema + "Arguments") != owner.Arguments)
            return "Actions.Exec.Arguments";
        // Windows can omit the default from exported XML. Verify the effective COM value.
        if (runLevel != 0)
            return "RunLevel";
        if ((string?)settings?.Element(Schema + "AllowHardTerminate") != "false") return "AllowHardTerminate";
        return null;
    }

    private static string? NormalizeSid(string? account)
    {
        if (account is null) return null;
        try
        {
            return account.StartsWith("S-1-", StringComparison.Ordinal) ? new SecurityIdentifier(account).Value :
                ((SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier))).Value;
        }
        catch (IdentityNotMappedException error) { throw new InvalidDataException("No se pudo comprobar la identidad de la tarea.", error); }
    }

    private static Owner ProductOwner(ProductJobRegistry registry, Guid id)
        => ProductOwner(registry, registry.Get(id));

    private static Owner ProductOwner(ProductJobRegistry registry, ProductJob job) =>
        new($"FolderTwin-Job-{job.Id:N}", $"FolderTwin trabajo v1 {job.Id:N} {registry.RegistrationStamp(job.Id)}",
            $"--run-scheduled-product-job \"{registry.Location}\" {job.Id:N}", false, true, DailyAt: job.Options.DailyAt);

    private static Owner LabOwner(LabWorkspace workspace, Guid id, bool tray = false)
    {
        Guid scope = ScopeId(workspace);
        if (tray ? id != scope : !VersionJob.List(workspace).Jobs.Any(job => job.Id == id))
            throw new InvalidDataException("El trabajo no está registrado en este espacio.");
        return new(tray ? $"FolderTwin-Lab-Tray-{id:N}" : $"FolderTwin-Lab-{id:N}",
            tray ? $"FolderTwin bandeja v1 {id:N}" : $"FolderTwin laboratorio v1 {id:N}",
            tray ? $"--tray-workspace \"{workspace.Root}\"" : $"--run-scheduled-version-job \"{workspace.Root}\" {id:N}",
            tray, false, tray ? null : $"--run-version-job \"{workspace.Root}\" {id:N}");
    }

    private static Guid ScopeId(LabWorkspace workspace)
    {
        using var scope = workspace.OpenRegisteredRoot();
        if (Path.GetDirectoryName(workspace.Root) != LabWorkspace.PersistentParent)
            throw new InvalidDataException("Crea un espacio de pruebas persistente para programar sus trabajos.");
        return new Guid(SHA256.HashData(WindowsProbeNative.DirectoryIdentity(scope.Root)).AsSpan(0, 16));
    }

    private static XElement Element(string name, params object?[] content) => new(Schema + name, content);
    public void Dispose() { Marshal.FinalReleaseComObject(folder); Marshal.FinalReleaseComObject(service); }
}

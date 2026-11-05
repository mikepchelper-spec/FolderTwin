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
    private readonly dynamic service;
    private readonly dynamic folder;
    private readonly string user;

    internal WindowsSchedule()
    {
        user = WindowsIdentity.GetCurrent().User?.Value ?? throw new IOException("No se pudo identificar al usuario.");
        Type type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new IOException("El Programador de tareas no está disponible.");
        service = Activator.CreateInstance(type) ?? throw new IOException("No se pudo conectar con el Programador de tareas.");
        try
        {
            service.Connect();
            folder = service.GetFolder("\\");
        }
        catch { Marshal.FinalReleaseComObject(service); throw; }
    }

    internal ScheduleInfo? Read(LabWorkspace workspace, Guid id)
    {
        ValidateJob(workspace, id);
        return ReadTask(workspace, id, tray: false);
    }

    internal ScheduleInfo? ReadTray(LabWorkspace workspace) => ReadTask(workspace, ScopeId(workspace), tray: true);

    private ScheduleInfo? ReadTask(LabWorkspace workspace, Guid id, bool tray)
    {
        dynamic? task = Find(id, tray);
        if (task is null)
            return null;
        try
        {
            string executable = VerifyOwnership((string)task.Xml, workspace, id, tray);
            return new((bool)task.Enabled, (DateTime)task.NextRunTime, (DateTime)task.LastRunTime,
                unchecked((uint)(int)task.LastTaskResult), executable, MeetsPolicy((string)task.Xml, tray), (int)task.State == 4);
        }
        finally { Marshal.FinalReleaseComObject(task); }
    }

    internal void Register(LabWorkspace workspace, Guid id, string executable, DateTime firstRun, bool daily = true, bool tray = false)
    {
        if (tray)
        {
            if (id != ScopeId(workspace))
                throw new InvalidDataException("La bandeja no corresponde a este espacio.");
        }
        else
            ValidateJob(workspace, id);
        if (!LabInstallation.IsInstalledWorker(executable))
            throw new InvalidDataException("El horario requiere un ejecutor instalado en la ubicación estable del laboratorio.");
        bool exists = ReadTask(workspace, id, tray) is not null;
        XElement trigger = tray ? Element("LogonTrigger", Element("Enabled", true), Element("UserId", user), Element("Delay", "PT30S")) :
            Element(daily ? "CalendarTrigger" : "TimeTrigger",
            Element("StartBoundary", firstRun.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)),
            Element("Enabled", true),
            daily ? Element("ScheduleByDay", Element("DaysInterval", 1)) : null);
        var document = new XDocument(new XElement(Schema + "Task", new XAttribute("version", "1.4"),
            Element("RegistrationInfo", Element("Description", Description(id, tray))),
            Element("Triggers", trigger),
            Element("Principals", new XElement(Schema + "Principal", new XAttribute("id", "Author"),
                Element("UserId", user), Element("LogonType", "InteractiveToken"), Element("RunLevel", "LeastPrivilege"))),
            Element("Settings",
                Element("MultipleInstancesPolicy", "IgnoreNew"), Element("DisallowStartIfOnBatteries", false),
                Element("StopIfGoingOnBatteries", false), Element("AllowHardTerminate", false),
                Element("StartWhenAvailable", !tray), Element("AllowStartOnDemand", true),
                Element("Enabled", false), Element("ExecutionTimeLimit", "PT0S")),
            new XElement(Schema + "Actions", new XAttribute("Context", "Author"),
                Element("Exec", Element("Command", executable), Element("Arguments", Arguments(workspace, id, tray)),
                    Element("WorkingDirectory", Path.GetDirectoryName(executable))))));
        dynamic registered = folder.RegisterTask(Name(id, tray), document.ToString(), exists ? 6 : 2, user, null, 3, null);
        try
        {
            _ = VerifyOwnership((string)registered.Xml, workspace, id, tray);
            if (PolicyProblem((string)registered.Xml, tray) is { } problem)
                throw new IOException("La tarea registrada requiere revisión de la opción " + problem + ".");
            registered.Enabled = true;
            if (!(bool)registered.Enabled)
                throw new IOException("El horario no pudo habilitarse.");
        }
        finally { Marshal.FinalReleaseComObject(registered); }
    }

    internal void Remove(LabWorkspace workspace, Guid id)
    {
        if (Read(workspace, id) is not null)
            folder.DeleteTask(Name(id), 0);
    }

    internal void RunNow(LabWorkspace workspace, Guid id)
    {
        ValidateJob(workspace, id);
        dynamic task = Find(id) ?? throw new InvalidDataException("Este trabajo no tiene horario.");
        try
        {
            string xml = task.Xml;
            _ = VerifyOwnership(xml, workspace, id);
            if (!(bool)task.Enabled || !MeetsPolicy(xml, tray: false))
                throw new InvalidDataException("El horario necesita revisión antes de ejecutarse.");
            try
            {
                dynamic running = task.Run(null);
                if (running is not null)
                    Marshal.FinalReleaseComObject(running);
            }
            catch (COMException error) when (error.HResult == unchecked((int)0x8004131F))
            {
                // IgnoreNew may report SCHED_E_ALREADY_RUNNING.
            }
        }
        finally { Marshal.FinalReleaseComObject(task); }
    }

    internal void SetTrayStartup(LabWorkspace workspace, string? executable, bool enabled)
    {
        Guid id = ScopeId(workspace);
        if (enabled)
            Register(workspace, id, executable ?? throw new ArgumentException("Falta el ejecutor."), DateTime.Now, tray: true);
        else if (ReadTray(workspace) is not null)
            folder.DeleteTask(Name(id, tray: true), 0);
    }

    private dynamic? Find(Guid id, bool tray = false)
    {
        try { return folder.GetTask(Name(id, tray)); }
        catch (Exception error) when (error is FileNotFoundException ||
            error is COMException { HResult: unchecked((int)0x80070002) }) { return null; }
    }

    private string VerifyOwnership(string xml, LabWorkspace workspace, Guid id, bool tray = false)
    {
        var document = XDocument.Parse(xml);
        var root = document.Root ?? throw new InvalidDataException("La tarea no tiene definición.");
        var principals = root.Element(Schema + "Principals")?.Elements(Schema + "Principal").ToArray() ?? [];
        var principal = principals.Length == 1 ? principals[0] : null;
        var actions = root.Element(Schema + "Actions")?.Elements().ToArray() ?? [];
        string? account = (string?)principal?.Element(Schema + "UserId");
        string? executable = actions.Length == 1 ? (string?)actions[0].Element(Schema + "Command") : null;
        string? arguments = actions.Length == 1 ? (string?)actions[0].Element(Schema + "Arguments") : null;
        bool recognized = arguments == Arguments(workspace, id, tray) ||
            !tray && arguments == $"--run-version-job \"{workspace.Root}\" {id:N}";
        string? sid = NormalizeSid(account);
        if ((string?)root.Element(Schema + "RegistrationInfo")?.Element(Schema + "Description") != Description(id, tray) ||
            sid != user || (string?)principal?.Element(Schema + "LogonType") != "InteractiveToken" ||
            actions.Length != 1 || actions[0].Name != Schema + "Exec" ||
            !recognized ||
            executable is null || !LabInstallation.IsInstalledWorker(executable))
            throw new InvalidDataException("La tarea existente no coincide con el trabajo. Se conservó para revisión.");
        return executable;
    }

    private bool MeetsPolicy(string xml, bool tray)
        => PolicyProblem(xml, tray) is null;

    private string? PolicyProblem(string xml, bool tray)
    {
        var root = XDocument.Parse(xml).Root;
        var settings = root?.Element(Schema + "Settings");
        var triggers = root?.Element(Schema + "Triggers")?.Elements().ToArray() ?? [];
        if (triggers.Length != 1)
            return "Triggers";
        if (tray && (triggers[0].Name != Schema + "LogonTrigger" ||
            NormalizeSid((string?)triggers[0].Element(Schema + "UserId")) != user))
            return "LogonTrigger.UserId";
        if (!tray && triggers[0].Name != Schema + "TimeTrigger" && triggers[0].Name != Schema + "CalendarTrigger")
            return "TimeTrigger/CalendarTrigger";
        if ((string?)settings?.Element(Schema + "MultipleInstancesPolicy") != "IgnoreNew")
            return "MultipleInstancesPolicy";
        if (!tray && (string?)settings?.Element(Schema + "StartWhenAvailable") != "true")
            return "StartWhenAvailable";
        if ((string?)settings?.Element(Schema + "ExecutionTimeLimit") != "PT0S")
            return "ExecutionTimeLimit";
        if (!tray && settings?.Element(Schema + "RestartOnFailure") is not null)
            return "RestartOnFailure";
        string? arguments = (string?)root?.Element(Schema + "Actions")?.Element(Schema + "Exec")?.Element(Schema + "Arguments");
        if (!tray && arguments?.StartsWith("--run-scheduled-version-job ", StringComparison.Ordinal) != true)
            return "Actions.Exec.Arguments";
        return null;
    }

    private static string? NormalizeSid(string? account)
    {
        if (account is null)
            return null;
        try
        {
            return account.StartsWith("S-1-", StringComparison.Ordinal) ? new SecurityIdentifier(account).Value :
                ((SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier))).Value;
        }
        catch (IdentityNotMappedException error)
        {
            throw new InvalidDataException("No se pudo comprobar la identidad de la tarea.", error);
        }
    }

    private static void ValidateJob(LabWorkspace workspace, Guid id)
    {
        _ = ScopeId(workspace);
        if (!VersionJob.List(workspace).Jobs.Any(job => job.Id == id))
            throw new InvalidDataException("El trabajo no está registrado en este espacio.");
    }

    private static Guid ScopeId(LabWorkspace workspace)
    {
        using var scope = workspace.OpenRegisteredRoot();
        if (Path.GetDirectoryName(workspace.Root) != LabWorkspace.PersistentParent)
            throw new InvalidDataException("Crea un espacio de pruebas persistente para programar sus trabajos.");
        return new Guid(SHA256.HashData(WindowsProbeNative.DirectoryIdentity(scope.Root)).AsSpan(0, 16));
    }

    private static XElement Element(string name, params object?[] content) => new(Schema + name, content);
    private static string Name(Guid id, bool tray = false) => tray ? $"FolderTwin-Lab-Tray-{id:N}" : $"FolderTwin-Lab-{id:N}";
    private static string Description(Guid id, bool tray = false) => tray ? $"FolderTwin bandeja v1 {id:N}" : $"FolderTwin laboratorio v1 {id:N}";
    private static string Arguments(LabWorkspace workspace, Guid id, bool tray = false) => tray ?
        $"--tray-workspace \"{workspace.Root}\"" : $"--run-scheduled-version-job \"{workspace.Root}\" {id:N}";

    public void Dispose()
    {
        Marshal.FinalReleaseComObject(folder);
        Marshal.FinalReleaseComObject(service);
    }
}

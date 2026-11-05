using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;

namespace CopyLab;

internal sealed record ScheduleInfo(bool Enabled, DateTime NextRun, DateTime LastRun, uint LastResult,
    string Executable, bool MeetsPolicy);

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
        dynamic? task = Find(id);
        if (task is null)
            return null;
        try
        {
            string executable = VerifyOwnership((string)task.Xml, workspace, id);
            return new((bool)task.Enabled, (DateTime)task.NextRunTime, (DateTime)task.LastRunTime,
                unchecked((uint)(int)task.LastTaskResult), executable, MeetsPolicy((string)task.Xml));
        }
        finally { Marshal.FinalReleaseComObject(task); }
    }

    internal void Register(LabWorkspace workspace, Guid id, string executable, DateTime firstRun, bool daily = true)
    {
        ValidateJob(workspace, id);
        if (!LabInstallation.IsInstalledWorker(executable))
            throw new InvalidDataException("El horario requiere un ejecutor instalado en la ubicación estable del laboratorio.");
        bool exists = Read(workspace, id) is not null;
        XElement trigger = Element(daily ? "CalendarTrigger" : "TimeTrigger",
            Element("StartBoundary", firstRun.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)),
            Element("Enabled", true),
            daily ? Element("ScheduleByDay", Element("DaysInterval", 1)) : null);
        var document = new XDocument(new XElement(Schema + "Task", new XAttribute("version", "1.4"),
            Element("RegistrationInfo", Element("Description", Description(id))),
            Element("Triggers", trigger),
            Element("Principals", new XElement(Schema + "Principal", new XAttribute("id", "Author"),
                Element("UserId", user), Element("LogonType", "InteractiveToken"), Element("RunLevel", "LeastPrivilege"))),
            Element("Settings",
                Element("MultipleInstancesPolicy", "IgnoreNew"), Element("DisallowStartIfOnBatteries", false),
                Element("StopIfGoingOnBatteries", false), Element("AllowHardTerminate", false),
                Element("StartWhenAvailable", true), Element("AllowStartOnDemand", true),
                Element("Enabled", false), Element("ExecutionTimeLimit", "PT0S"),
                Element("RestartOnFailure", Element("Interval", "PT5M"), Element("Count", 3))),
            new XElement(Schema + "Actions", new XAttribute("Context", "Author"),
                Element("Exec", Element("Command", executable), Element("Arguments", Arguments(workspace, id)),
                    Element("WorkingDirectory", Path.GetDirectoryName(executable))))));
        dynamic registered = folder.RegisterTask(Name(id), document.ToString(), exists ? 6 : 2, user, null, 3, null);
        try
        {
            _ = VerifyOwnership((string)registered.Xml, workspace, id);
            if (!MeetsPolicy((string)registered.Xml))
                throw new IOException("El horario registrado no conserva las opciones de ejecución previstas.");
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

    private dynamic? Find(Guid id)
    {
        try { return folder.GetTask(Name(id)); }
        catch (COMException error) when (error.HResult == unchecked((int)0x80070002)) { return null; }
    }

    private string VerifyOwnership(string xml, LabWorkspace workspace, Guid id)
    {
        var document = XDocument.Parse(xml);
        var root = document.Root ?? throw new InvalidDataException("La tarea no tiene definición.");
        var principals = root.Element(Schema + "Principals")?.Elements(Schema + "Principal").ToArray() ?? [];
        var principal = principals.Length == 1 ? principals[0] : null;
        var actions = root.Element(Schema + "Actions")?.Elements().ToArray() ?? [];
        string? account = (string?)principal?.Element(Schema + "UserId");
        string? executable = actions.Length == 1 ? (string?)actions[0].Element(Schema + "Command") : null;
        string? sid = account is null ? null : account.StartsWith("S-1-", StringComparison.Ordinal) ? account :
            ((SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier))).Value;
        if ((string?)root.Element(Schema + "RegistrationInfo")?.Element(Schema + "Description") != Description(id) ||
            sid != user || (string?)principal?.Element(Schema + "LogonType") != "InteractiveToken" ||
            actions.Length != 1 || actions[0].Name != Schema + "Exec" ||
            (string?)actions[0].Element(Schema + "Arguments") != Arguments(workspace, id) ||
            executable is null || !LabInstallation.IsInstalledWorker(executable))
            throw new InvalidDataException("La tarea existente no coincide con el trabajo. Se conservó para revisión.");
        return executable;
    }

    private static bool MeetsPolicy(string xml)
    {
        var root = XDocument.Parse(xml).Root;
        var settings = root?.Element(Schema + "Settings");
        return (string?)settings?.Element(Schema + "MultipleInstancesPolicy") == "IgnoreNew" &&
            (string?)settings?.Element(Schema + "StartWhenAvailable") == "true" &&
            (string?)settings?.Element(Schema + "ExecutionTimeLimit") == "PT0S" &&
            (string?)settings?.Element(Schema + "RestartOnFailure")?.Element(Schema + "Count") == "3";
    }

    private static void ValidateJob(LabWorkspace workspace, Guid id)
    {
        using var scope = workspace.OpenRegisteredRoot();
        if (Path.GetDirectoryName(workspace.Root) != LabWorkspace.PersistentParent)
            throw new InvalidDataException("Crea un espacio de pruebas persistente para programar sus trabajos.");
        if (!VersionJob.List(workspace).Jobs.Any(job => job.Id == id))
            throw new InvalidDataException("El trabajo no está registrado en este espacio.");
    }

    private static XElement Element(string name, params object?[] content) => new(Schema + name, content);
    private static string Name(Guid id) => $"FolderTwin-Lab-{id:N}";
    private static string Description(Guid id) => $"FolderTwin laboratorio v1 {id:N}";
    private static string Arguments(LabWorkspace workspace, Guid id) => $"--run-version-job \"{workspace.Root}\" {id:N}";

    public void Dispose()
    {
        Marshal.FinalReleaseComObject(folder);
        Marshal.FinalReleaseComObject(service);
    }
}

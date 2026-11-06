using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace CopyLab;

// XML-only regression: never constructs the COM scheduler, registers tasks or reads user tasks.
internal static class SchedulePolicyAuditProbe
{
    internal static void Run()
    {
        const string arguments = "--run-scheduled-product-job synthetic-registry synthetic-job";
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var baseline = new XDocument(new XElement(ns + "Task",
            new XElement(ns + "Triggers", new XElement(ns + "CalendarTrigger",
                Element("Enabled", "true"), Element("StartBoundary", "2026-01-01T20:30:00"),
                new XElement(ns + "ScheduleByDay", Element("DaysInterval", "1")))),
            new XElement(ns + "Settings", Element("MultipleInstancesPolicy", "IgnoreNew"),
                Element("DisallowStartIfOnBatteries", "false"), Element("StopIfGoingOnBatteries", "false"),
                Element("AllowHardTerminate", "false"), Element("StartWhenAvailable", "true"),
                Element("AllowStartOnDemand", "true"), Element("ExecutionTimeLimit", "PT0S")),
            new XElement(ns + "Actions", new XElement(ns + "Exec", Element("Arguments", arguments)))));
        XElement Element(string name, string value) => new(ns + name, value);

        var schedulerType = typeof(WindowsSchedule);
        var ownerType = schedulerType.GetNestedType("Owner", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Scheduler owner contract unavailable.");
        var owner = Activator.CreateInstance(ownerType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, ["synthetic-task", "synthetic-owner", arguments, false, true, null, (TimeOnly?)new TimeOnly(20, 30)], null)
            ?? throw new InvalidOperationException("Scheduler owner could not be created.");
        var policy = schedulerType.GetMethod("PolicyProblem", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Scheduler policy contract unavailable.");
        var scheduler = RuntimeHelpers.GetUninitializedObject(schedulerType);
        string? Problem(XDocument xml) => (string?)policy.Invoke(scheduler, [xml.ToString(), owner, 0]);
        if (Problem(baseline) is { } baselineProblem)
            throw new InvalidOperationException("Synthetic baseline is not accepted: " + baselineProblem);

        var omissions = new List<string>();
        foreach (var (name, mutate) in new (string, Action<XDocument>)[]
        {
            ("disabled trigger", xml => xml.Descendants(ns + "CalendarTrigger").Single().Element(ns + "Enabled")!.Value = "false"),
            ("battery start disabled", xml => xml.Descendants(ns + "DisallowStartIfOnBatteries").Single().Value = "true"),
            ("battery transition stops worker", xml => xml.Descendants(ns + "StopIfGoingOnBatteries").Single().Value = "true"),
            ("implicit battery start restriction", xml => xml.Descendants(ns + "DisallowStartIfOnBatteries").Single().Remove()),
            ("manual native execution disabled", xml => xml.Descendants(ns + "AllowStartOnDemand").Single().Value = "false"),
            ("requires idle", xml => xml.Root!.Element(ns + "Settings")!.Add(Element("RunOnlyIfIdle", "true"))),
            ("requires network", xml => xml.Root!.Element(ns + "Settings")!.Add(Element("RunOnlyIfNetworkAvailable", "true")))
        })
        {
            var changed = new XDocument(baseline);
            mutate(changed);
            if (Problem(changed) is null) omissions.Add(name);
        }
        if (omissions.Count != 0)
            throw new InvalidOperationException("Scheduler marked changed policy as valid: " + string.Join("; ", omissions));

        // Trigger.Enabled defaults to true; an omitted default must not falsely reject a native definition.
        var defaultTrigger = new XDocument(baseline);
        defaultTrigger.Descendants(ns + "CalendarTrigger").Single().Element(ns + "Enabled")!.Remove();
        if (Problem(defaultTrigger) is not null)
            throw new InvalidOperationException("An omitted enabled trigger default was rejected.");
        Console.Error.WriteLine("PASS XML-only schedule audit rejects seven disabled-trigger, battery, demand, idle and network policy mutations; omitted safe defaults remain valid. No COM tasks were created or queried.");
    }
}

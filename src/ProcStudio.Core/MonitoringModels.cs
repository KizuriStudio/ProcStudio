using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ProcStudio.Core;

public sealed record ProcessSnapshot(
    int ProcessId,
    int? ParentProcessId,
    string Name,
    string ExecutablePath,
    string? CommandLine,
    string? UserName,
    string? Company,
    string? Description,
    bool IsElevated,
    bool IsSigned,
    DateTimeOffset StartTime,
    double CpuPercent,
    double PrivateMemoryMb,
    double WorkingSetMb,
    double CommitMb,
    double GpuPercent,
    double GpuDedicatedMemoryMb,
    double GpuSharedMemoryMb,
    int HandleCount,
    IReadOnlyList<ThreadSnapshot> Threads,
    IReadOnlyList<ModuleSnapshot> Modules,
    IReadOnlyList<NetworkConnectionSnapshot> Connections,
    IReadOnlyList<MemoryRegionSnapshot> MemoryRegions,
    IReadOnlyList<ProcessEvent> Events);

public sealed record ThreadSnapshot(
    int ThreadId,
    ThreadStateKind State,
    WaitReasonKind WaitReason,
    double CpuPercent,
    TimeSpan TotalProcessorTime,
    int BasePriority,
    int CurrentPriority,
    DateTimeOffset? StartTime);

public sealed record ModuleSnapshot(
    string Name,
    string FilePath,
    string? Company,
    string? Product,
    string? Version,
    bool IsSigned,
    string? SignatureSubject);

public sealed record NetworkConnectionSnapshot(
    string Protocol,
    string LocalAddress,
    int LocalPort,
    string RemoteAddress,
    int RemotePort,
    string State,
    string? ApproximateLocation);

public sealed record MemoryRegionSnapshot(
    ulong BaseAddress,
    ulong Size,
    string State,
    string Protection,
    string Type,
    bool IsPrivate,
    bool IsImage,
    bool IsMapped);

public sealed record ProcessEvent(
    DateTimeOffset Timestamp,
    ProcessEventKind Kind,
    string Message);

public enum ProcessEventKind
{
    Started,
    Terminated,
    CpuSpike,
    MemorySpike,
    NetworkBurst,
    ModuleLoaded,
    SignatureMismatch
}

public enum ThreadStateKind
{
    Unknown,
    Initialized,
    Ready,
    Running,
    Standby,
    Terminated,
    Wait,
    Transition,
    DeferredReady,
    GateWait,
    WaitingForProcessInSwap
}

public enum WaitReasonKind
{
    Unknown,
    Executive,
    FreePage,
    PageIn,
    SystemAllocation,
    ExecutionDelay,
    Suspended,
    UserRequest,
    EventPairHigh,
    EventPairLow,
    LpcReceive,
    LpcReply,
    VirtualMemory,
    PageOut,
    Unknown13
}

public sealed record ProcessSnapshotEnvelope(
    DateTimeOffset Timestamp,
    IReadOnlyList<ProcessSnapshot> Processes,
    IReadOnlyList<ProcessTreeNode> Tree,
    IReadOnlyList<AlertNotification> Alerts);

public sealed record ProcessTreeNode(
    int ProcessId,
    string Name,
    IReadOnlyList<ProcessTreeNode> Children);

public sealed record ProcessSample(
    DateTimeOffset Timestamp,
    double CpuPercent,
    double WorkingSetMb,
    double PrivateMemoryMb,
    double GpuPercent,
    int HandleCount);

public sealed record AlertRule(
    string Name,
    double CpuThresholdPercent,
    double MemoryThresholdMb,
    double GpuThresholdPercent,
    int HandleThreshold,
    TimeSpan SustainedFor,
    bool Enabled = true)
{
    public static AlertRule AggressiveDefault => new(
        "Agressivo",
        CpuThresholdPercent: 70,
        MemoryThresholdMb: 1024,
        GpuThresholdPercent: 50,
        HandleThreshold: 5000,
        SustainedFor: TimeSpan.FromSeconds(10));
}

public sealed record AlertNotification(
    DateTimeOffset Timestamp,
    int ProcessId,
    string ProcessName,
    string RuleName,
    string Reason);

public sealed record MonitoringProfile(
    string Name,
    string? NameContains,
    int? ProcessId,
    string? Company,
    bool? SignedOnly,
    string? UserName,
    double? MinimumCpuPercent,
    double? MinimumMemoryMb)
{
    public bool Matches(ProcessSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(NameContains) &&
            !snapshot.Name.Contains(NameContains, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (ProcessId is not null && snapshot.ProcessId != ProcessId.Value)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(Company) &&
            !string.Equals(snapshot.Company, Company, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (SignedOnly is true && !snapshot.IsSigned)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(UserName) &&
            !string.Equals(snapshot.UserName, UserName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (MinimumCpuPercent is not null && snapshot.CpuPercent < MinimumCpuPercent.Value)
        {
            return false;
        }

        return MinimumMemoryMb is null || snapshot.WorkingSetMb >= MinimumMemoryMb.Value;
    }
}

public interface ISystemInspector
{
    Task<ProcessSnapshotEnvelope> CaptureAsync(MonitoringProfile? profile, CancellationToken cancellationToken = default);
}

public interface IReportExporter
{
    string ToJson(ProcessSnapshotEnvelope envelope);
    string ToCsv(ProcessSnapshotEnvelope envelope);
    string ToHtml(ProcessSnapshotEnvelope envelope);
}

public sealed class ProcessTimelineStore
{
    private readonly int _maxSamplesPerProcess;
    private readonly Dictionary<int, Queue<ProcessSample>> _samples = new();

    public ProcessTimelineStore(int maxSamplesPerProcess = 300)
    {
        _maxSamplesPerProcess = Math.Max(30, maxSamplesPerProcess);
    }

    public void Append(ProcessSnapshot snapshot, DateTimeOffset timestamp)
    {
        if (!_samples.TryGetValue(snapshot.ProcessId, out var queue))
        {
            queue = new Queue<ProcessSample>();
            _samples[snapshot.ProcessId] = queue;
        }

        queue.Enqueue(new ProcessSample(
            timestamp,
            snapshot.CpuPercent,
            snapshot.WorkingSetMb,
            snapshot.PrivateMemoryMb,
            snapshot.GpuPercent,
            snapshot.HandleCount));

        while (queue.Count > _maxSamplesPerProcess)
        {
            queue.Dequeue();
        }
    }

    public IReadOnlyList<ProcessSample> GetSamples(int processId)
        => _samples.TryGetValue(processId, out var queue)
            ? queue.ToArray()
            : Array.Empty<ProcessSample>();
}

public sealed class AlertEngine
{
    private readonly AlertRule _rule;
    private readonly Dictionary<int, Queue<ProcessSample>> _windows = new();

    public AlertEngine(AlertRule? rule = null)
    {
        _rule = rule ?? AlertRule.AggressiveDefault;
    }

    public IReadOnlyList<AlertNotification> Evaluate(IEnumerable<ProcessSnapshot> processes, DateTimeOffset timestamp)
    {
        if (!_rule.Enabled)
        {
            return Array.Empty<AlertNotification>();
        }

        var alerts = new List<AlertNotification>();

        foreach (var process in processes)
        {
            if (!_windows.TryGetValue(process.ProcessId, out var queue))
            {
                queue = new Queue<ProcessSample>();
                _windows[process.ProcessId] = queue;
            }

            queue.Enqueue(new ProcessSample(
                timestamp,
                process.CpuPercent,
                process.WorkingSetMb,
                process.PrivateMemoryMb,
                process.GpuPercent,
                process.HandleCount));

            while (queue.Count > 0 && timestamp - queue.Peek().Timestamp > _rule.SustainedFor)
            {
                queue.Dequeue();
            }

            if (queue.Count == 0)
            {
                continue;
            }

            var samples = queue.ToArray();
            var avgCpu = samples.Average(x => x.CpuPercent);
            var avgMemory = samples.Average(x => x.WorkingSetMb);
            var avgGpu = samples.Average(x => x.GpuPercent);
            var avgHandles = samples.Average(x => x.HandleCount);

            var reasons = new List<string>();
            if (avgCpu >= _rule.CpuThresholdPercent) reasons.Add($"CPU {avgCpu:F1}%");
            if (avgMemory >= _rule.MemoryThresholdMb) reasons.Add($"RAM {avgMemory:F0} MB");
            if (avgGpu >= _rule.GpuThresholdPercent) reasons.Add($"GPU {avgGpu:F1}%");
            if (avgHandles >= _rule.HandleThreshold) reasons.Add($"Handles {avgHandles:F0}");

            if (reasons.Count > 0)
            {
                alerts.Add(new AlertNotification(
                    timestamp,
                    process.ProcessId,
                    process.Name,
                    _rule.Name,
                    string.Join(", ", reasons)));
            }
        }

        return alerts;
    }
}

public sealed class ReportExporter : IReportExporter
{
    public string ToJson(ProcessSnapshotEnvelope envelope)
        => JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true });

    public string ToCsv(ProcessSnapshotEnvelope envelope)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Timestamp,PID,PPID,Name,CPUPercent,WorkingSetMb,PrivateMemoryMb,GpuPercent,Handles,Company,Signed,User");

        foreach (var process in envelope.Processes)
        {
            sb.AppendJoin(',',
                Csv(envelope.Timestamp.ToString("O", CultureInfo.InvariantCulture)),
                process.ProcessId.ToString(CultureInfo.InvariantCulture),
                process.ParentProcessId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                Csv(process.Name),
                process.CpuPercent.ToString("F2", CultureInfo.InvariantCulture),
                process.WorkingSetMb.ToString("F2", CultureInfo.InvariantCulture),
                process.PrivateMemoryMb.ToString("F2", CultureInfo.InvariantCulture),
                process.GpuPercent.ToString("F2", CultureInfo.InvariantCulture),
                process.HandleCount.ToString(CultureInfo.InvariantCulture),
                Csv(process.Company ?? string.Empty),
                process.IsSigned.ToString(),
                Csv(process.UserName ?? string.Empty));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public string ToHtml(ProcessSnapshotEnvelope envelope)
    {
        var rows = string.Join(Environment.NewLine, envelope.Processes.Select(process =>
            $"<tr><td>{process.ProcessId}</td><td>{Escape(process.Name)}</td><td>{process.CpuPercent:F1}%</td><td>{process.WorkingSetMb:F0} MB</td><td>{process.GpuPercent:F1}%</td><td>{process.HandleCount}</td><td>{Escape(process.Company ?? "-")}</td></tr>"));

        return $$"""
                 <!DOCTYPE html>
                 <html lang="pt-BR">
                 <head>
                   <meta charset="utf-8" />
                   <title>ProcStudio Report</title>
                   <style>
                     body { font-family: Segoe UI, sans-serif; background: #0b1220; color: #e5e7eb; padding: 24px; }
                     h1 { margin-top: 0; }
                     table { width: 100%; border-collapse: collapse; background: #111827; }
                     th, td { border: 1px solid #1f2937; padding: 10px; text-align: left; }
                     th { background: #1e293b; }
                     .meta { margin-bottom: 16px; color: #93c5fd; }
                   </style>
                 </head>
                 <body>
                   <h1>ProcStudio</h1>
                   <div class="meta">Snapshot em {{envelope.Timestamp:O}} | Processos: {{envelope.Processes.Count}} | Alertas: {{envelope.Alerts.Count}}</div>
                   <table>
                     <thead>
                       <tr><th>PID</th><th>Nome</th><th>CPU</th><th>RAM</th><th>GPU</th><th>Handles</th><th>Empresa</th></tr>
                     </thead>
                     <tbody>
                       {{rows}}
                     </tbody>
                   </table>
                 </body>
                 </html>
                 """;
    }

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private static string Escape(string value)
        => value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);
}

public static class ProcessTreeBuilder
{
    public static IReadOnlyList<ProcessTreeNode> Build(IEnumerable<ProcessSnapshot> processes)
    {
        var list = processes.ToList();
        var knownIds = list.Select(x => x.ProcessId).ToHashSet();
        var byParent = list
            .GroupBy(x => x.ParentProcessId)
            .ToDictionary(
                x => x.Key.GetValueOrDefault(-1),
                x => x.ToList());

        List<ProcessTreeNode> BuildNodes(int? parentId)
            => byParent.TryGetValue(parentId.GetValueOrDefault(-1), out var children)
                ? children
                    .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(x => new ProcessTreeNode(x.ProcessId, x.Name, BuildNodes(x.ProcessId)))
                    .ToList()
                : new List<ProcessTreeNode>();

        var roots = list
            .Where(x => x.ParentProcessId is null || !knownIds.Contains(x.ParentProcessId.Value))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => new ProcessTreeNode(x.ProcessId, x.Name, BuildNodes(x.ProcessId)))
            .ToList();

        return roots;
    }
}

public sealed class ObservableProcessTreeNode
{
    public ObservableProcessTreeNode(ProcessTreeNode node)
    {
        ProcessId = node.ProcessId;
        Name = node.Name;
        Children = new ObservableCollection<ObservableProcessTreeNode>(node.Children.Select(x => new ObservableProcessTreeNode(x)));
    }

    public int ProcessId { get; }
    public string Name { get; }
    public ObservableCollection<ObservableProcessTreeNode> Children { get; }
}

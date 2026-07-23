using ProcStudio.Core;
using Xunit;

namespace ProcStudio.Tests;

public sealed class MonitoringTests
{
    [Fact]
    public void AlertEngine_Gera_Alerta_Quando_Limite_E_Excedido()
    {
        var engine = new AlertEngine(new AlertRule(
            "Teste",
            CpuThresholdPercent: 50,
            MemoryThresholdMb: 512,
            GpuThresholdPercent: 25,
            HandleThreshold: 1000,
            SustainedFor: TimeSpan.FromSeconds(5)));

        var timestamp = DateTimeOffset.UtcNow;
        var process = CreateProcess(cpu: 80, ramMb: 1024, gpu: 40, handles: 2000);

        engine.Evaluate([process], timestamp.AddSeconds(-4));
        var alerts = engine.Evaluate([process], timestamp);

        Assert.Single(alerts);
        Assert.Contains("CPU", alerts[0].Reason);
        Assert.Contains("RAM", alerts[0].Reason);
    }

    [Fact]
    public void ReportExporter_Gera_Json_Csv_E_Html()
    {
        var exporter = new ReportExporter();
        var envelope = new ProcessSnapshotEnvelope(
            DateTimeOffset.UtcNow,
            [CreateProcess(cpu: 12.5, ramMb: 256, gpu: 5, handles: 120)],
            [],
            []);

        var json = exporter.ToJson(envelope);
        var csv = exporter.ToCsv(envelope);
        var html = exporter.ToHtml(envelope);

        Assert.Contains("\"Processes\"", json);
        Assert.Contains("PID,PPID,Name", csv);
        Assert.Contains("<html", html, StringComparison.OrdinalIgnoreCase);
    }

    private static ProcessSnapshot CreateProcess(double cpu, double ramMb, double gpu, int handles)
        => new(
            ProcessId: 1234,
            ParentProcessId: 10,
            Name: "demo.exe",
            ExecutablePath: @"C:\demo.exe",
            CommandLine: "demo.exe --run",
            UserName: @"CONTOSO\dev",
            Company: "Contoso",
            Description: "Demo",
            IsElevated: false,
            IsSigned: true,
            StartTime: DateTimeOffset.UtcNow.AddMinutes(-5),
            CpuPercent: cpu,
            PrivateMemoryMb: ramMb - 32,
            WorkingSetMb: ramMb,
            CommitMb: ramMb + 20,
            GpuPercent: gpu,
            GpuDedicatedMemoryMb: 64,
            GpuSharedMemoryMb: 32,
            HandleCount: handles,
            Threads:
            [
                new ThreadSnapshot(1, ThreadStateKind.Running, WaitReasonKind.Unknown, cpu, TimeSpan.FromSeconds(20), 8, 10, DateTimeOffset.UtcNow.AddMinutes(-5))
            ],
            Modules:
            [
                new ModuleSnapshot("demo.dll", @"C:\demo.dll", "Contoso", "Demo", "1.0.0", true, "CN=Contoso")
            ],
            Connections:
            [
                new NetworkConnectionSnapshot("TCP", "127.0.0.1", 5000, "8.8.8.8", 443, "Established", "IP publico")
            ],
            MemoryRegions:
            [
                new MemoryRegionSnapshot(0x1000, 4096, "Commit", "ReadWrite", "Private", true, false, false)
            ],
            Events:
            [
                new ProcessEvent(DateTimeOffset.UtcNow, ProcessEventKind.CpuSpike, "CPU alta")
            ]);
}

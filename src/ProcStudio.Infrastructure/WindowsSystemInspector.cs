using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using ProcStudio.Core;

namespace ProcStudio.Infrastructure;

public sealed class WindowsSystemInspector : ISystemInspector
{
    private readonly ConcurrentDictionary<int, TimeSample> _processTimes = new();
    private readonly ConcurrentDictionary<long, TimeSample> _threadTimes = new();
    private readonly AlertEngine _alertEngine = new();
    private readonly GpuMetricsProvider _gpuMetricsProvider = new();

    public Task<ProcessSnapshotEnvelope> CaptureAsync(MonitoringProfile? profile, CancellationToken cancellationToken = default)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var processMetadata = ProcessMetadataProvider.ReadAll();
        var connections = NetworkTableReader.ReadAll();
        var gpuMetrics = _gpuMetricsProvider.Read();
        var snapshots = new List<ProcessSnapshot>();

        foreach (var process in Process.GetProcesses().OrderBy(x => x.ProcessName, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var snapshot = BuildProcessSnapshot(process, timestamp, processMetadata, connections, gpuMetrics);
                if (profile is null || profile.Matches(snapshot))
                {
                    snapshots.Add(snapshot);
                }
            }
            catch
            {
                // Ignore protected or terminating processes to keep the capture loop stable.
            }
            finally
            {
                process.Dispose();
            }
        }

        var alerts = _alertEngine.Evaluate(snapshots, timestamp);
        var tree = ProcessTreeBuilder.Build(snapshots);
        return Task.FromResult(new ProcessSnapshotEnvelope(timestamp, snapshots, tree, alerts));
    }

    private ProcessSnapshot BuildProcessSnapshot(
        Process process,
        DateTimeOffset timestamp,
        IReadOnlyDictionary<int, ProcessMetadata> metadataLookup,
        IReadOnlyDictionary<int, IReadOnlyList<NetworkConnectionSnapshot>> connectionLookup,
        IReadOnlyDictionary<int, GpuProcessMetrics> gpuLookup)
    {
        metadataLookup.TryGetValue(process.Id, out var metadata);
        var path = TryGetExecutablePath(process, metadata);
        var versionInfo = TryGetVersionInfo(path);
        var user = TryGetUser(process);
        var isElevated = TryGetIsElevated(process);
        var startTime = TryGet(() => new DateTimeOffset(process.StartTime), DateTimeOffset.MinValue);
        var threads = ReadThreads(process, timestamp);
        var modules = ReadModules(process);
        var memoryRegions = MemoryMapReader.Read(process);
        var gpu = gpuLookup.TryGetValue(process.Id, out var metrics) ? metrics : GpuProcessMetrics.Empty;
        var cpuPercent = ComputeCpuPercent(process.Id, process.TotalProcessorTime, timestamp);
        var connectionList = connectionLookup.TryGetValue(process.Id, out var processConnections)
            ? processConnections
            : Array.Empty<NetworkConnectionSnapshot>();

        return new ProcessSnapshot(
            ProcessId: process.Id,
            ParentProcessId: metadata?.ParentProcessId,
            Name: process.ProcessName,
            ExecutablePath: path,
            CommandLine: metadata?.CommandLine,
            UserName: user,
            Company: versionInfo?.CompanyName,
            Description: versionInfo?.FileDescription,
            IsElevated: isElevated,
            IsSigned: SignatureReader.IsSigned(path),
            StartTime: startTime,
            CpuPercent: cpuPercent,
            PrivateMemoryMb: process.PrivateMemorySize64 / 1024d / 1024d,
            WorkingSetMb: process.WorkingSet64 / 1024d / 1024d,
            CommitMb: process.PagedMemorySize64 / 1024d / 1024d,
            GpuPercent: gpu.UtilizationPercent,
            GpuDedicatedMemoryMb: gpu.DedicatedMemoryMb,
            GpuSharedMemoryMb: gpu.SharedMemoryMb,
            HandleCount: TryGet(() => process.HandleCount, 0),
            Threads: threads,
            Modules: modules,
            Connections: connectionList,
            MemoryRegions: memoryRegions,
            Events: BuildEvents(process.ProcessName, cpuPercent, process.WorkingSet64 / 1024d / 1024d, connectionList.Count));
    }

    private static string TryGetExecutablePath(Process process, ProcessMetadata? metadata)
    {
        if (!string.IsNullOrWhiteSpace(metadata?.ExecutablePath))
        {
            return metadata.ExecutablePath!;
        }

        return TryGet(() => process.MainModule?.FileName, string.Empty) ?? string.Empty;
    }

    private static FileVersionInfo? TryGetVersionInfo(string path)
        => !string.IsNullOrWhiteSpace(path) && File.Exists(path)
            ? TryGet(() => FileVersionInfo.GetVersionInfo(path), null as FileVersionInfo)
            : null;

    private double ComputeCpuPercent(int processId, TimeSpan totalProcessorTime, DateTimeOffset now)
    {
        var current = new TimeSample(now, totalProcessorTime);
        if (_processTimes.TryGetValue(processId, out var previous))
        {
            var elapsedMs = (current.Timestamp - previous.Timestamp).TotalMilliseconds;
            if (elapsedMs > 0)
            {
                var cpuMs = (current.ProcessorTime - previous.ProcessorTime).TotalMilliseconds;
                var percent = cpuMs / (elapsedMs * Environment.ProcessorCount) * 100d;
                _processTimes[processId] = current;
                return Math.Max(0d, percent);
            }
        }

        _processTimes[processId] = current;
        return 0d;
    }

    private IReadOnlyList<ThreadSnapshot> ReadThreads(Process process, DateTimeOffset now)
    {
        var result = new List<ThreadSnapshot>();
        foreach (ProcessThread thread in process.Threads)
        {
            try
            {
                var key = ((long)process.Id << 32) | (uint)thread.Id;
                var totalTime = thread.TotalProcessorTime;
                var current = new TimeSample(now, totalTime);
                double cpuPercent = 0;

                if (_threadTimes.TryGetValue(key, out var previous))
                {
                    var elapsedMs = (current.Timestamp - previous.Timestamp).TotalMilliseconds;
                    if (elapsedMs > 0)
                    {
                        var cpuMs = (current.ProcessorTime - previous.ProcessorTime).TotalMilliseconds;
                        cpuPercent = Math.Max(0d, cpuMs / elapsedMs * 100d);
                    }
                }

                _threadTimes[key] = current;

                result.Add(new ThreadSnapshot(
                    thread.Id,
                    Map(thread.ThreadState),
                    Map(thread.ThreadState == System.Diagnostics.ThreadState.Wait ? thread.WaitReason : null),
                    cpuPercent,
                    totalTime,
                    thread.BasePriority,
                    thread.CurrentPriority,
                    TryGet(() => new DateTimeOffset(thread.StartTime), null as DateTimeOffset?)));
            }
            catch
            {
                // Ignore inaccessible thread details.
            }
        }

        return result.OrderByDescending(x => x.CpuPercent).ToList();
    }

    private static IReadOnlyList<ModuleSnapshot> ReadModules(Process process)
    {
        var result = new List<ModuleSnapshot>();
        try
        {
            foreach (ProcessModule module in process.Modules)
            {
                var filePath = module.FileName;
                var version = TryGet(() => FileVersionInfo.GetVersionInfo(filePath), null as FileVersionInfo);
                var certificate = SignatureReader.TryReadSubject(filePath);

                result.Add(new ModuleSnapshot(
                    module.ModuleName,
                    filePath,
                    version?.CompanyName,
                    version?.ProductName,
                    version?.FileVersion,
                    certificate is not null,
                    certificate));
            }
        }
        catch
        {
            // Some processes deny module enumeration.
        }

        return result.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IReadOnlyList<ProcessEvent> BuildEvents(string processName, double cpuPercent, double memoryMb, int connectionCount)
    {
        var now = DateTimeOffset.UtcNow;
        var events = new List<ProcessEvent>();
        if (cpuPercent >= 75)
        {
            events.Add(new ProcessEvent(now, ProcessEventKind.CpuSpike, $"{processName} atingiu {cpuPercent:F1}% de CPU."));
        }

        if (memoryMb >= 1024)
        {
            events.Add(new ProcessEvent(now, ProcessEventKind.MemorySpike, $"{processName} excedeu 1 GB de RAM."));
        }

        if (connectionCount >= 20)
        {
            events.Add(new ProcessEvent(now, ProcessEventKind.NetworkBurst, $"{processName} abriu {connectionCount} conexoes."));
        }

        return events;
    }

    private static string? TryGetUser(Process process)
    {
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!NativeMethods.OpenProcessToken(process.Handle, NativeMethods.TOKEN_QUERY, out token))
            {
                return null;
            }

            using var identity = new WindowsIdentity(token);
            return identity.Name;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (token != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(token);
            }
        }
    }

    private static bool TryGetIsElevated(Process process)
    {
        IntPtr token = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!NativeMethods.OpenProcessToken(process.Handle, NativeMethods.TOKEN_QUERY, out token))
            {
                return false;
            }

            var size = Marshal.SizeOf<int>();
            buffer = Marshal.AllocHGlobal(size);
            if (!NativeMethods.GetTokenInformation(token, NativeMethods.TOKEN_INFORMATION_CLASS.TokenElevation, buffer, size, out _))
            {
                return false;
            }

            return Marshal.ReadInt32(buffer) != 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }

            if (token != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(token);
            }
        }
    }

    private static T? TryGet<T>(Func<T> action, T? fallback)
    {
        try
        {
            return action();
        }
        catch
        {
            return fallback;
        }
    }

    private readonly record struct TimeSample(DateTimeOffset Timestamp, TimeSpan ProcessorTime);

    private sealed record ProcessMetadata(int ProcessId, int? ParentProcessId, string? CommandLine, string? ExecutablePath);

    private static class ProcessMetadataProvider
    {
        public static IReadOnlyDictionary<int, ProcessMetadata> ReadAll()
        {
            var result = new Dictionary<int, ProcessMetadata>();
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId, CommandLine, ExecutablePath FROM Win32_Process");
                foreach (ManagementObject process in searcher.Get())
                {
                    var pid = Convert.ToInt32(process["ProcessId"], CultureInfo.InvariantCulture);
                    var parent = process["ParentProcessId"] is null
                        ? null
                        : Convert.ToInt32(process["ParentProcessId"], CultureInfo.InvariantCulture);
                    result[pid] = new ProcessMetadata(
                        pid,
                        parent,
                        process["CommandLine"]?.ToString(),
                        process["ExecutablePath"]?.ToString());
                }
            }
            catch
            {
                // WMI can be unavailable in restricted environments.
            }

            return result;
        }
    }

    private static class SignatureReader
    {
        public static bool IsSigned(string path) => TryReadSubject(path) is not null;

        public static string? TryReadSubject(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            try
            {
                var raw = X509Certificate.CreateFromSignedFile(path);
                var cert = new X509Certificate2(raw);
                return cert.Subject;
            }
            catch
            {
                return null;
            }
        }
    }

    private static class MemoryMapReader
    {
        private const int MaxRegions = 2048;

        public static IReadOnlyList<MemoryRegionSnapshot> Read(Process process)
        {
            var handle = IntPtr.Zero;
            var regions = new List<MemoryRegionSnapshot>();

            try
            {
                handle = NativeMethods.OpenProcess(
                    NativeMethods.ProcessAccessFlags.QueryInformation | NativeMethods.ProcessAccessFlags.VirtualMemoryRead,
                    false,
                    process.Id);

                if (handle == IntPtr.Zero)
                {
                    return Array.Empty<MemoryRegionSnapshot>();
                }

                long address = 0;
                var infoSize = Marshal.SizeOf<NativeMethods.MEMORY_BASIC_INFORMATION>();
                var count = 0;

                while (count < MaxRegions &&
                       NativeMethods.VirtualQueryEx(handle, new IntPtr(address), out var info, (IntPtr)infoSize) != IntPtr.Zero)
                {
                    var regionSize = (ulong)Math.Max(info.RegionSize.ToInt64(), 0);
                    regions.Add(new MemoryRegionSnapshot(
                        BaseAddress: unchecked((ulong)info.BaseAddress.ToInt64()),
                        Size: regionSize,
                        State: NativeMethods.FormatState(info.State),
                        Protection: NativeMethods.FormatProtection(info.Protect),
                        Type: NativeMethods.FormatType(info.Type),
                        IsPrivate: info.Type == NativeMethods.MEM_PRIVATE,
                        IsImage: info.Type == NativeMethods.MEM_IMAGE,
                        IsMapped: info.Type == NativeMethods.MEM_MAPPED));

                    if (regionSize == 0)
                    {
                        break;
                    }

                    var nextAddress = info.BaseAddress.ToInt64() + info.RegionSize.ToInt64();
                    if (nextAddress <= address)
                    {
                        break;
                    }

                    address = nextAddress;
                    count++;
                }
            }
            catch
            {
                return Array.Empty<MemoryRegionSnapshot>();
            }
            finally
            {
                if (handle != IntPtr.Zero)
                {
                    NativeMethods.CloseHandle(handle);
                }
            }

            return regions;
        }
    }

    private sealed class GpuMetricsProvider
    {
        public IReadOnlyDictionary<int, GpuProcessMetrics> Read()
        {
            try
            {
                var counters = new Dictionary<int, GpuProcessMetrics>();
                ReadUtilization(counters);
                ReadMemory("GPU Process Memory", "Dedicated Usage", (current, value) => current with { DedicatedMemoryMb = value / 1024d / 1024d }, counters);
                ReadMemory("GPU Process Memory", "Shared Usage", (current, value) => current with { SharedMemoryMb = value / 1024d / 1024d }, counters);
                return counters;
            }
            catch
            {
                return new Dictionary<int, GpuProcessMetrics>();
            }
        }

        private static void ReadUtilization(IDictionary<int, GpuProcessMetrics> counters)
        {
            var category = new PerformanceCounterCategory("GPU Engine");
            foreach (var instance in category.GetInstanceNames())
            {
                var pid = ParsePid(instance);
                if (pid is null)
                {
                    continue;
                }

                using var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", instance, true);
                var current = counters.TryGetValue(pid.Value, out var existing) ? existing : GpuProcessMetrics.Empty;
                counters[pid.Value] = current with { UtilizationPercent = current.UtilizationPercent + counter.NextValue() };
            }
        }

        private static void ReadMemory(
            string categoryName,
            string counterName,
            Func<GpuProcessMetrics, float, GpuProcessMetrics> assign,
            IDictionary<int, GpuProcessMetrics> counters)
        {
            var category = new PerformanceCounterCategory(categoryName);
            foreach (var instance in category.GetInstanceNames())
            {
                var pid = ParsePid(instance);
                if (pid is null)
                {
                    continue;
                }

                using var counter = new PerformanceCounter(categoryName, counterName, instance, true);
                var current = counters.TryGetValue(pid.Value, out var existing) ? existing : GpuProcessMetrics.Empty;
                counters[pid.Value] = assign(current, counter.NextValue());
            }
        }

        private static int? ParsePid(string instanceName)
        {
            var parts = instanceName.Split('_', StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index < parts.Length - 1; index++)
            {
                if (parts[index].Equals("pid", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(parts[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
                {
                    return pid;
                }
            }

            return null;
        }
    }

    private static class NetworkTableReader
    {
        public static IReadOnlyDictionary<int, IReadOnlyList<NetworkConnectionSnapshot>> ReadAll()
        {
            var result = new Dictionary<int, List<NetworkConnectionSnapshot>>();
            ReadTcp(result);
            ReadUdp(result);
            return result.ToDictionary(x => x.Key, x => (IReadOnlyList<NetworkConnectionSnapshot>)x.Value);
        }

        private static void ReadTcp(IDictionary<int, List<NetworkConnectionSnapshot>> result)
        {
            var size = 0;
            NativeMethods.GetExtendedTcpTable(IntPtr.Zero, ref size, true, NativeMethods.AF_INET, NativeMethods.TCP_TABLE_CLASS.TcpTableOwnerPidAll, 0);
            if (size <= 0)
            {
                return;
            }

            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (NativeMethods.GetExtendedTcpTable(buffer, ref size, true, NativeMethods.AF_INET, NativeMethods.TCP_TABLE_CLASS.TcpTableOwnerPidAll, 0) != 0)
                {
                    return;
                }

                var count = Marshal.ReadInt32(buffer);
                var rowPtr = IntPtr.Add(buffer, sizeof(int));
                var rowSize = Marshal.SizeOf<NativeMethods.MIB_TCPROW_OWNER_PID>();
                for (var i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<NativeMethods.MIB_TCPROW_OWNER_PID>(rowPtr);
                    Add(result, row.owningPid, new NetworkConnectionSnapshot(
                        "TCP",
                        new IPAddress(row.localAddr).ToString(),
                        NativeMethods.ConvertPort(row.localPort),
                        new IPAddress(row.remoteAddr).ToString(),
                        NativeMethods.ConvertPort(row.remotePort),
                        row.state.ToString(),
                        ApproximateLocation(new IPAddress(row.remoteAddr))));
                    rowPtr = IntPtr.Add(rowPtr, rowSize);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static void ReadUdp(IDictionary<int, List<NetworkConnectionSnapshot>> result)
        {
            var size = 0;
            NativeMethods.GetExtendedUdpTable(IntPtr.Zero, ref size, true, NativeMethods.AF_INET, NativeMethods.UDP_TABLE_CLASS.UdpTableOwnerPid, 0);
            if (size <= 0)
            {
                return;
            }

            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (NativeMethods.GetExtendedUdpTable(buffer, ref size, true, NativeMethods.AF_INET, NativeMethods.UDP_TABLE_CLASS.UdpTableOwnerPid, 0) != 0)
                {
                    return;
                }

                var count = Marshal.ReadInt32(buffer);
                var rowPtr = IntPtr.Add(buffer, sizeof(int));
                var rowSize = Marshal.SizeOf<NativeMethods.MIB_UDPROW_OWNER_PID>();
                for (var i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<NativeMethods.MIB_UDPROW_OWNER_PID>(rowPtr);
                    Add(result, row.owningPid, new NetworkConnectionSnapshot(
                        "UDP",
                        new IPAddress(row.localAddr).ToString(),
                        NativeMethods.ConvertPort(row.localPort),
                        string.Empty,
                        0,
                        "Listening",
                        ApproximateLocation(new IPAddress(row.localAddr))));
                    rowPtr = IntPtr.Add(rowPtr, rowSize);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static void Add(IDictionary<int, List<NetworkConnectionSnapshot>> result, int pid, NetworkConnectionSnapshot snapshot)
        {
            if (!result.TryGetValue(pid, out var list))
            {
                list = new List<NetworkConnectionSnapshot>();
                result[pid] = list;
            }

            list.Add(snapshot);
        }

        private static string ApproximateLocation(IPAddress address)
        {
            if (IPAddress.IsLoopback(address))
            {
                return "Loopback";
            }

            var bytes = address.GetAddressBytes();
            if (bytes.Length == 4 &&
                (bytes[0] == 10 ||
                 (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                 (bytes[0] == 192 && bytes[1] == 168)))
            {
                return "Rede privada";
            }

            return address.Equals(IPAddress.Any) ? "Local" : "IP publico";
        }
    }

    private sealed record GpuProcessMetrics(double UtilizationPercent, double DedicatedMemoryMb, double SharedMemoryMb)
    {
        public static GpuProcessMetrics Empty => new(0, 0, 0);
    }

    private static ThreadStateKind Map(System.Diagnostics.ThreadState threadState)
        => threadState switch
        {
            System.Diagnostics.ThreadState.Initialized => ThreadStateKind.Initialized,
            System.Diagnostics.ThreadState.Ready => ThreadStateKind.Ready,
            System.Diagnostics.ThreadState.Running => ThreadStateKind.Running,
            System.Diagnostics.ThreadState.Standby => ThreadStateKind.Standby,
            System.Diagnostics.ThreadState.Terminated => ThreadStateKind.Terminated,
            System.Diagnostics.ThreadState.Wait => ThreadStateKind.Wait,
            System.Diagnostics.ThreadState.Transition => ThreadStateKind.Transition,
            System.Diagnostics.ThreadState.Unknown => ThreadStateKind.Unknown,
            _ => ThreadStateKind.Unknown
        };

    private static WaitReasonKind Map(System.Diagnostics.ThreadWaitReason? waitReason)
        => waitReason switch
        {
            System.Diagnostics.ThreadWaitReason.Executive => WaitReasonKind.Executive,
            System.Diagnostics.ThreadWaitReason.FreePage => WaitReasonKind.FreePage,
            System.Diagnostics.ThreadWaitReason.PageIn => WaitReasonKind.PageIn,
            System.Diagnostics.ThreadWaitReason.SystemAllocation => WaitReasonKind.SystemAllocation,
            System.Diagnostics.ThreadWaitReason.ExecutionDelay => WaitReasonKind.ExecutionDelay,
            System.Diagnostics.ThreadWaitReason.Suspended => WaitReasonKind.Suspended,
            System.Diagnostics.ThreadWaitReason.UserRequest => WaitReasonKind.UserRequest,
            System.Diagnostics.ThreadWaitReason.EventPairHigh => WaitReasonKind.EventPairHigh,
            System.Diagnostics.ThreadWaitReason.EventPairLow => WaitReasonKind.EventPairLow,
            System.Diagnostics.ThreadWaitReason.LpcReceive => WaitReasonKind.LpcReceive,
            System.Diagnostics.ThreadWaitReason.LpcReply => WaitReasonKind.LpcReply,
            System.Diagnostics.ThreadWaitReason.VirtualMemory => WaitReasonKind.VirtualMemory,
            System.Diagnostics.ThreadWaitReason.PageOut => WaitReasonKind.PageOut,
            _ => WaitReasonKind.Unknown
        };
}

internal static class NativeMethods
{
    public const int AF_INET = 2;
    public const uint MEM_PRIVATE = 0x00020000;
    public const uint MEM_MAPPED = 0x00040000;
    public const uint MEM_IMAGE = 0x01000000;
    public const uint TOKEN_QUERY = 0x0008;

    [Flags]
    public enum ProcessAccessFlags : uint
    {
        QueryInformation = 0x0400,
        VirtualMemoryRead = 0x0010
    }

    public enum TCP_TABLE_CLASS
    {
        TcpTableBasicListener,
        TcpTableBasicConnections,
        TcpTableBasicAll,
        TcpTableOwnerPidListener,
        TcpTableOwnerPidConnections,
        TcpTableOwnerPidAll,
        TcpTableOwnerModuleListener,
        TcpTableOwnerModuleConnections,
        TcpTableOwnerModuleAll
    }

    public enum UDP_TABLE_CLASS
    {
        UdpTableBasic,
        UdpTableOwnerPid,
        UdpTableOwnerModule
    }

    public enum TOKEN_INFORMATION_CLASS
    {
        TokenUser = 1,
        TokenGroups,
        TokenPrivileges,
        TokenOwner,
        TokenPrimaryGroup,
        TokenDefaultDacl,
        TokenSource,
        TokenType,
        TokenImpersonationLevel,
        TokenStatistics,
        TokenRestrictedSids,
        TokenSessionId,
        TokenGroupsAndPrivileges,
        TokenSessionReference,
        TokenSandBoxInert,
        TokenAuditPolicy,
        TokenOrigin,
        TokenElevationType,
        TokenLinkedToken,
        TokenElevation
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MIB_TCPROW_OWNER_PID
    {
        public uint state;
        public uint localAddr;
        public uint localPort;
        public uint remoteAddr;
        public uint remotePort;
        public int owningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MIB_UDPROW_OWNER_PID
    {
        public uint localAddr;
        public uint localPort;
        public int owningPid;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(ProcessAccessFlags processAccess, bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        TOKEN_INFORMATION_CLASS tokenInformationClass,
        IntPtr tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr VirtualQueryEx(
        IntPtr hProcess,
        IntPtr lpAddress,
        out MEMORY_BASIC_INFORMATION lpBuffer,
        IntPtr dwLength);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int pdwSize,
        [MarshalAs(UnmanagedType.Bool)] bool sort,
        int ipVersion,
        TCP_TABLE_CLASS tableClass,
        uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern uint GetExtendedUdpTable(
        IntPtr pUdpTable,
        ref int pdwSize,
        [MarshalAs(UnmanagedType.Bool)] bool sort,
        int ipVersion,
        UDP_TABLE_CLASS tableClass,
        uint reserved);

    public static int ConvertPort(uint port)
    {
        var bytes = BitConverter.GetBytes(port);
        return (bytes[0] << 8) + bytes[1];
    }

    public static string FormatState(uint state)
        => state switch
        {
            0x1000 => "Commit",
            0x2000 => "Reserve",
            0x10000 => "Free",
            _ => $"0x{state:X}"
        };

    public static string FormatProtection(uint protection)
        => protection switch
        {
            0x01 => "NoAccess",
            0x02 => "ReadOnly",
            0x04 => "ReadWrite",
            0x08 => "WriteCopy",
            0x10 => "Execute",
            0x20 => "ExecuteRead",
            0x40 => "ExecuteReadWrite",
            0x80 => "ExecuteWriteCopy",
            _ => $"0x{protection:X}"
        };

    public static string FormatType(uint type)
        => type switch
        {
            MEM_PRIVATE => "Private",
            MEM_MAPPED => "Mapped",
            MEM_IMAGE => "Image",
            _ => $"0x{type:X}"
        };
}

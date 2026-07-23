using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using ProcStudio.Core;
using ProcStudio.Infrastructure;

namespace ProcStudio.App;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly ISystemInspector _inspector = new WindowsSystemInspector();
    private readonly ProcessTimelineStore _timelineStore = new();
    private readonly IReportExporter _exporter = new ReportExporter();
    private ProcessSnapshotEnvelope? _lastEnvelope;
    private ProcessSnapshot? _selectedProcess;
    private string _searchText = string.Empty;
    private string _statusText = "Pronto";
    private bool _isBusy;

    public MainViewModel()
    {
        Profiles = new ObservableCollection<MonitoringProfile>
        {
            new("Todos", null, null, null, null, null, null, null),
            new("Alta CPU", null, null, null, null, null, 20, null),
            new("Alta Memoria", null, null, null, null, null, null, 512),
            new("Assinados", null, null, null, true, null, null, null),
            new("Navegadores", "chrome", null, null, null, null, null, null)
        };

        SelectedProfile = Profiles[0];
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ProcessSnapshot> Processes { get; } = new();
    public ObservableCollection<ObservableProcessTreeNode> ProcessTree { get; } = new();
    public ObservableCollection<ThreadSnapshot> SelectedThreads { get; } = new();
    public ObservableCollection<ModuleSnapshot> SelectedModules { get; } = new();
    public ObservableCollection<NetworkConnectionSnapshot> SelectedConnections { get; } = new();
    public ObservableCollection<MemoryRegionSnapshot> SelectedMemoryRegions { get; } = new();
    public ObservableCollection<ProcessEvent> SelectedEvents { get; } = new();
    public ObservableCollection<ProcessSample> SelectedTimeline { get; } = new();
    public ObservableCollection<AlertNotification> Alerts { get; } = new();
    public ObservableCollection<MonitoringProfile> Profiles { get; }

    public ICommand RefreshCommand { get; }

    public MonitoringProfile? SelectedProfile { get; set; }

    public ProcessSnapshot? SelectedProcess
    {
        get => _selectedProcess;
        set
        {
            if (Set(ref _selectedProcess, value))
            {
                RefreshDetails();
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value))
            {
                ApplyEnvelope();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                (RefreshCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public int ProcessCount => Processes.Count;
    public double TotalCpu => Processes.Sum(x => x.CpuPercent);
    public double TotalMemoryMb => Processes.Sum(x => x.WorkingSetMb);
    public double TotalGpu => Processes.Sum(x => x.GpuPercent);

    public async Task InitializeAsync() => await RefreshAsync();

    public async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            StatusText = "Capturando processos, threads, rede e memoria...";
            _lastEnvelope = await _inspector.CaptureAsync(SelectedProfile);

            foreach (var snapshot in _lastEnvelope.Processes)
            {
                _timelineStore.Append(snapshot, _lastEnvelope.Timestamp);
            }

            ApplyEnvelope();
            StatusText = $"Atualizado em {_lastEnvelope.Timestamp:HH:mm:ss} com {_lastEnvelope.Processes.Count} processos.";
        }
        catch (Exception ex)
        {
            StatusText = $"Falha na captura: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void SelectTreeNode(ObservableProcessTreeNode? node)
    {
        if (node is null)
        {
            return;
        }

        SelectedProcess = Processes.FirstOrDefault(x => x.ProcessId == node.ProcessId);
    }

    public string ExportJson() => _lastEnvelope is null ? string.Empty : _exporter.ToJson(_lastEnvelope);
    public string ExportCsv() => _lastEnvelope is null ? string.Empty : _exporter.ToCsv(_lastEnvelope);
    public string ExportHtml() => _lastEnvelope is null ? string.Empty : _exporter.ToHtml(_lastEnvelope);

    public void ApplySelectedProfile()
    {
        ApplyEnvelope();
    }

    private void ApplyEnvelope()
    {
        if (_lastEnvelope is null)
        {
            return;
        }

        var currentSelectedProcessId = SelectedProcess?.ProcessId;
        IEnumerable<ProcessSnapshot> filtered = _lastEnvelope.Processes;

        if (SelectedProfile is not null)
        {
            filtered = filtered.Where(SelectedProfile.Matches);
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            filtered = filtered.Where(x =>
                x.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                x.ProcessId.ToString().Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                (x.Company?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (x.UserName?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (x.CommandLine?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        ReplaceWith(Processes, filtered.OrderByDescending(x => x.CpuPercent).ToList());
        ReplaceWith(ProcessTree, ProcessTreeBuilder.Build(Processes).Select(x => new ObservableProcessTreeNode(x)).ToList());
        ReplaceWith(Alerts, _lastEnvelope.Alerts.OrderByDescending(x => x.Timestamp).ToList());

        SelectedProcess = Processes.FirstOrDefault(x => x.ProcessId == currentSelectedProcessId) ?? Processes.FirstOrDefault();
        OnPropertyChanged(nameof(ProcessCount));
        OnPropertyChanged(nameof(TotalCpu));
        OnPropertyChanged(nameof(TotalMemoryMb));
        OnPropertyChanged(nameof(TotalGpu));
    }

    private void RefreshDetails()
    {
        ReplaceWith(SelectedThreads, SelectedProcess?.Threads ?? Array.Empty<ThreadSnapshot>());
        ReplaceWith(SelectedModules, SelectedProcess?.Modules ?? Array.Empty<ModuleSnapshot>());
        ReplaceWith(SelectedConnections, SelectedProcess?.Connections ?? Array.Empty<NetworkConnectionSnapshot>());
        ReplaceWith(SelectedMemoryRegions, SelectedProcess?.MemoryRegions ?? Array.Empty<MemoryRegionSnapshot>());
        ReplaceWith(SelectedEvents, SelectedProcess?.Events ?? Array.Empty<ProcessEvent>());

        var samples = SelectedProcess is null
            ? Array.Empty<ProcessSample>()
            : _timelineStore.GetSamples(SelectedProcess.ProcessId);
        ReplaceWith(SelectedTimeline, samples);
        OnPropertyChanged(nameof(SelectedProcess));
    }

    private static void ReplaceWith<T>(ObservableCollection<T> collection, IEnumerable<T> items)
    {
        collection.Clear();
        foreach (var item in items)
        {
            collection.Add(item);
        }
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public async void Execute(object? parameter) => await _execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

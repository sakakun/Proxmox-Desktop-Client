using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxmoxDesktop.Api;
using ProxmoxDesktop.Api.Models;
using ProxmoxDesktop.Services;

namespace ProxmoxDesktop.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ApiClient      _api;
    private readonly PeriodicTimer  _refreshTimer;
    private readonly PeriodicTimer  _ticketTimer;
    private CancellationTokenSource _cts = new();
    private Dictionary<int, string> _previousStatuses = [];

    [ObservableProperty] private ObservableCollection<MachineData> machines         = [];
    [ObservableProperty] private ObservableCollection<NodeGroup>   groupedMachines  = [];
    [ObservableProperty] private ObservableCollection<MachineData> filteredMachines = [];
    [ObservableProperty] private ObservableCollection<string>      nodeList         = [];
    [ObservableProperty] private bool    isLoading;
    [ObservableProperty] private string  searchQuery      = string.Empty;
    [ObservableProperty] private string? statusMessage;
    [ObservableProperty] private bool    isGroupedByNode;
    [ObservableProperty] private string? selectedNode;

    // Dashboard stats
    [ObservableProperty] private int totalCount;
    [ObservableProperty] private int runningCount;
    [ObservableProperty] private int stoppedCount;
    [ObservableProperty] private int vmCount;
    [ObservableProperty] private int lxcCount;

    partial void OnSearchQueryChanged(string _)   => ApplyFilter();
    partial void OnIsGroupedByNodeChanged(bool _) => ApplyFilter();
    partial void OnSelectedNodeChanged(string? _) => ApplyFilter();

    public MainViewModel(ApiClient api)
    {
        _api = api;
        _refreshTimer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        _ticketTimer  = new PeriodicTimer(TimeSpan.FromMinutes(90));
        _ = Task.Run(async () => { while (await _refreshTimer.WaitForNextTickAsync(_cts.Token)) await RefreshAsync(_cts.Token); });
        _ = Task.Run(async () => { while (await _ticketTimer .WaitForNextTickAsync(_cts.Token)) await _api.RenewTicketAsync(_cts.Token); });
    }

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        IsLoading = true; StatusMessage = null;
        try
        {
            var all = await _api.GetAllMachinesAsync(ct);
            if (_previousStatuses.Count > 0)
                foreach (var m in all)
                    if (_previousStatuses.TryGetValue(m.Vmid, out var prev) && prev != m.Status)
                        NotificationService.NotifyStateChange(m.Name, m.Vmid, prev, m.Status);
            _previousStatuses = all.ToDictionary(m => m.Vmid, m => m.Status);

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                Machines.Clear();
                foreach (var m in all) Machines.Add(m);

                var nodes = all.Select(m => m.NodeName).Distinct().OrderBy(n => n).ToList();
                NodeList.Clear();
                foreach (var n in nodes) NodeList.Add(n);

                TotalCount   = all.Count;
                RunningCount = all.Count(m => m.IsRunning);
                StoppedCount = all.Count(m => !m.IsRunning);
                VmCount      = all.Count(m => !m.IsLxc);
                LxcCount     = all.Count(m => m.IsLxc);
            });
            ApplyFilter();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusMessage = $"Load error: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    public async Task PowerActionAsync(PowerActionArgs args)
    {
        var r = await _api.PowerActionAsync(args.Machine, args.Action, args.Action == "suspend" && args.Extra);
        StatusMessage = r switch { PowerResult.Forbidden => "Permission denied.", PowerResult.Error => "Power action failed.", _ => null };
        await Task.Delay(2000);
        await RefreshAsync();
    }

    [RelayCommand] public async Task OpenConsoleAsync(ConsoleArgs args) { var url = await _api.GetConsoleUrlAsync(args.Machine, args.ConsoleType); if (url is not null) OnOpenConsole?.Invoke(args.Machine, url); }
    [RelayCommand] public async Task OpenSpiceAsync(MachineData machine) { var cfg = await _api.GetSpiceConfigAsync(machine); if (cfg is not null) OnOpenSpice?.Invoke(cfg); }
    [RelayCommand] public void Logout() => OnLogout?.Invoke();
    [RelayCommand] public void ClearNodeFilter() => SelectedNode = null;

    private void ApplyFilter()
    {
        var q = SearchQuery.Trim().ToLowerInvariant();
        var source = Machines.AsEnumerable();

        if (!string.IsNullOrEmpty(SelectedNode))
            source = source.Where(m => m.NodeName == SelectedNode);

        if (!string.IsNullOrEmpty(q))
            source = source.Where(m =>
                m.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                m.Vmid.ToString().Contains(q) ||
                m.NodeName.Contains(q, StringComparison.OrdinalIgnoreCase));

        var filtered = source.ToList();
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            FilteredMachines = new ObservableCollection<MachineData>(filtered);
            GroupedMachines  = new ObservableCollection<NodeGroup>(
                filtered.GroupBy(m => m.NodeName)
                        .OrderBy(g => g.Key)
                        .Select(g => new NodeGroup(g.Key, new ObservableCollection<MachineData>(g.OrderBy(m => m.Vmid)))));
        });
    }

    public event Action<MachineData, string>? OnOpenConsole;
    public event Action<SpiceObject>?         OnOpenSpice;
    public event Action?                      OnLogout;

    public void Dispose() { _cts.Cancel(); _refreshTimer.Dispose(); _ticketTimer.Dispose(); _api.Dispose(); }
}

public record PowerActionArgs(MachineData Machine, string Action, bool Extra = false);
public record ConsoleArgs(MachineData Machine, string ConsoleType);

public sealed class NodeGroup(string nodeName, ObservableCollection<MachineData> machines)
{
    public string NodeName   { get; } = nodeName;
    public ObservableCollection<MachineData> Machines { get; } = machines;
    public int    Count        => Machines.Count;
    public int    RunningCount => Machines.Count(m => m.IsRunning);
    public string Summary      => $"{RunningCount}/{Count} running";
}

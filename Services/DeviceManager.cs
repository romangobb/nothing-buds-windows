// Remembered-device store + auto-connect + multi-device active selection.
// No manual selection on restart: remembered MACs are dialed automatically.

using NothingBuds.Bluetooth;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace NothingBuds.Services;

public sealed record RememberedDevice(string Mac, string Name, string BaseModel, DateTime LastConnected);

public sealed class DeviceManager : INotifyPropertyChanged, IDisposable
{
    private static readonly string StoreDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NothingBuds");
    private static readonly string StoreFile = Path.Combine(StoreDir, "devices.json");

    private readonly List<RememberedDevice> _remembered = new();
    public IReadOnlyList<RememberedDevice> Remembered => _remembered.AsReadOnly();

    public ObservableCollection<EarDevice> ConnectedDevices { get; } = new();

    private EarDevice? _active;
    public EarDevice? ActiveDevice { get => _active; private set => SetField(ref _active, value); }

    private string _state = "Idle";
    public string State { get => _state; private set => SetField(ref _state, value); }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? Changed;

    private System.Threading.Timer? _watchdog;
    private readonly SemaphoreSlim _watchdogGate = new(1, 1);
    private bool _disposed;

    public DeviceManager() { Load(); }

    private void OnLinkLost(EarDevice dev)
    {
        Changed?.Invoke(); // refresh status/controls immediately
        _ = ReconnectSoonAsync(dev.Mac);
    }

    private void StartWatchdog()
    {
        _watchdog ??= new System.Threading.Timer(
            _ => _ = WatchdogTickAsync(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    private async Task ReconnectSoonAsync(string mac)
    {
        await Task.Delay(2500);
        if (_disposed || !IsRemembered(mac)) return;
        var live = ConnectedDevices.FirstOrDefault(d => d.Mac == mac);
        if (live != null && live.Connected) return;
        var rec = _remembered.FirstOrDefault(d => d.Mac == mac);
        if (rec != null) await TryConnectAsync(rec, CancellationToken.None, 5000);
    }

    private async Task WatchdogTickAsync()
    {
        if (_disposed || !await _watchdogGate.WaitAsync(0)) return;
        try
        {
            try
            {
                foreach (var r in _remembered.ToList())
                {
                    var live = ConnectedDevices.FirstOrDefault(d => d.Mac == r.Mac);
                    if (live != null && live.Connected) continue;
                    await TryConnectAsync(r, CancellationToken.None, 4000);
                }
                UpdateState();
            }
            catch (Exception ex) { Log.Info($"Watchdog: {ex.Message}"); }
        }
        finally { _watchdogGate.Release(); }
    }

    private void UpdateState()
    {
        int live = ConnectedDevices.Count(d => d.Connected);
        if (_remembered.Count == 0) State = "No remembered devices — use Add device";
        else if (live == ConnectedDevices.Count && live == _remembered.Count && live > 0)
            State = $"Connected ({live})";
        else if (live > 0) State = $"Connected ({live}/{_remembered.Count}) — retrying…";
        else if (ConnectedDevices.Count > 0) State = "Reconnecting…";
        else State = "No remembered devices reachable — retrying…";
    }

    // ----- persistence -----
    private void Load()
    {
        try
        {
            if (File.Exists(StoreFile))
            {
                var json = File.ReadAllText(StoreFile);
                var arr = JsonSerializer.Deserialize<List<RememberedDevice>>(json);
                if (arr != null)
                    foreach (var d in arr)
                    {
                        string model = string.IsNullOrEmpty(d.BaseModel)
                            ? DeviceCatalog.Resolve(d.Name).ModelId : d.BaseModel;
                        _remembered.Add(new RememberedDevice(
                            BluetoothEndPoint.Normalize(d.Mac), d.Name, model, d.LastConnected));
                    }
            }
        }
        catch { }
    }
    private void Save()
    {
        try
        {
            Directory.CreateDirectory(StoreDir);
            File.WriteAllText(StoreFile, JsonSerializer.Serialize(_remembered,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public void Remember(string mac, string name, string baseModel = "B172")
    {
        mac = BluetoothEndPoint.Normalize(mac);
        _remembered.RemoveAll(d => d.Mac == mac);
        _remembered.Add(new RememberedDevice(mac, name, baseModel, DateTime.UtcNow));
        Save(); Changed?.Invoke();
    }
    public void Forget(string mac)
    {
        mac = BluetoothEndPoint.Normalize(mac);
        _remembered.RemoveAll(d => d.Mac == mac);
        var live = ConnectedDevices.FirstOrDefault(d => d.Mac == mac);
        if (live != null)
        {
            if (ActiveDevice == live) ActiveDevice = ConnectedDevices.FirstOrDefault(d => d != live);
            ConnectedDevices.Remove(live);
            live.Dispose();
        }
        Save(); Changed?.Invoke();
    }
    public bool IsRemembered(string mac)
        => _remembered.Any(d => d.Mac == BluetoothEndPoint.Normalize(mac));

    // ----- auto-connect (no manual selection) -----
    public async Task AutoConnectAsync(CancellationToken ct = default)
    {
        StartWatchdog();
        Log.Info($"AutoConnect start, remembered={_remembered.Count}");
        if (_remembered.Count == 0) { State = "No remembered devices — use Add device"; return; }
        State = $"Connecting to {_remembered.Count} remembered device(s)…";
        var ordered = _remembered.OrderByDescending(d => d.LastConnected).ToList();
        var tasks = ordered.Select(r => TryConnectAsync(r, ct)).ToArray();
        await Task.WhenAll(tasks);
        Log.Info($"AutoConnect done, connected={ConnectedDevices.Count}");
        PickActive();
        UpdateState();
        Changed?.Invoke();
    }

    private void PickActive()
    {
        var live = ConnectedDevices.Where(d => d.Connected).ToList();
        if (live.Count == 0) return;
        if (ActiveDevice != null && ActiveDevice.Connected) return;
        var lastMac = _remembered.OrderByDescending(d => d.LastConnected).First().Mac;
        ActiveDevice = live.FirstOrDefault(d => d.Mac == lastMac) ?? live.First();
    }

    private async Task TryConnectAsync(RememberedDevice r, CancellationToken ct, int timeoutMs = 8000)
    {
        var existing = ConnectedDevices.FirstOrDefault(d => d.Mac == r.Mac);
        if (existing != null)
        {
            if (existing.Connected) return;
            // Drop the dead shell so a fresh device takes its place
            App.Current.Dispatcher.Invoke(() => ConnectedDevices.Remove(existing));
            if (ActiveDevice == existing) ActiveDevice = null;
            existing.Dispose();
        }
        var dev = new EarDevice(r.Mac, r.Name);
        dev.ApplyModel(r.BaseModel);
        dev.ConnectionLost += OnLinkLost;
        try
        {
            await dev.ConnectAsync(ct, timeoutMs);
            App.Current.Dispatcher.Invoke(() =>
            {
                if (!ConnectedDevices.Any(d => d.Mac == r.Mac))
                    ConnectedDevices.Add(dev);
            });
            Touch(r.Mac);
            UpdateModel(r.Mac, dev.BaseModel);
            PickActive();
            UpdateState();
            Log.Info($"Connected {r.Mac} ({dev.Name}) fw={dev.Firmware} L={dev.BatteryLeft} R={dev.BatteryRight} C={dev.BatteryCase} anc={dev.Anc} eq={dev.Listening}");
        }
        catch (Exception ex) { Log.Info($"Connect failed {r.Mac}: {ex.Message}"); dev.Dispose(); }
        Changed?.Invoke();
    }

    private void Touch(string mac)
    {
        int i = _remembered.FindIndex(d => d.Mac == mac);
        if (i >= 0)
        {
            var d = _remembered[i];
            _remembered[i] = d with { LastConnected = DateTime.UtcNow };
            Save();
        }
    }

    private void UpdateModel(string mac, string modelId)
    {
        int i = _remembered.FindIndex(d => d.Mac == mac);
        if (i >= 0 && _remembered[i].BaseModel != modelId)
        {
            _remembered[i] = _remembered[i] with { BaseModel = modelId };
            Save();
        }
    }

    // ----- one-time add flow: scan paired + probe -----
    public async Task<List<ScannedDevice>> ScanForNothingDevicesAsync(
        Action<string>? progress = null, CancellationToken ct = default)
    {
        var paired = PairedScanner.GetPairedDevices();
        progress?.Invoke($"Found {paired.Count} paired Bluetooth device(s), probing…");
        var found = new List<ScannedDevice>();
        var gate = new SemaphoreSlim(4);
        var tasks = paired.Select(async p =>
        {
            await gate.WaitAsync(ct);
            try
            {
                if (await NothingProbe.IsNothingDeviceAsync(p.Mac))
                    lock (found) found.Add(p);
            }
            finally { gate.Release(); }
        }).ToArray();
        await Task.WhenAll(tasks);
        progress?.Invoke($"Found {found.Count} Nothing/CMF device(s)");
        return found;
    }

    public async Task<EarDevice?> AddAndConnectAsync(ScannedDevice scanned, CancellationToken ct = default)
    {
        StartWatchdog();
        Remember(scanned.Mac, scanned.Name);
        var dev = new EarDevice(scanned.Mac, scanned.Name);
        dev.ConnectionLost += OnLinkLost;
        try
        {
            await dev.ConnectAsync(ct);
            App.Current.Dispatcher.Invoke(() => ConnectedDevices.Add(dev));
            ActiveDevice = dev;
            Touch(dev.Mac);
            State = $"Connected ({ConnectedDevices.Count})";
            Changed?.Invoke();
            return dev;
        }
        catch (Exception ex)
        {
            dev.Dispose();
            State = $"Connect failed: {ex.Message}";
            return null;
        }
    }

    public void SetActive(string mac)
    {
        var dev = ConnectedDevices.FirstOrDefault(d => d.Mac == BluetoothEndPoint.Normalize(mac));
        if (dev != null) { ActiveDevice = dev; Touch(dev.Mac); Changed?.Invoke(); }
    }

    public async Task RefreshActiveAsync()
    {
        var a = ActiveDevice;
        if (a == null) return;
        if (!a.Connected)
        {
            // Refresh on a dead link = reconnect now instead of silently doing nothing
            var rec = _remembered.FirstOrDefault(d => d.Mac == a.Mac);
            if (rec != null) await TryConnectAsync(rec, CancellationToken.None, 6000);
            return;
        }
        try { await a.RefreshAllAsync(); }
        catch (Exception ex) { Log.Info($"Refresh failed: {ex.Message}"); }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? prop = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
        return true;
    }

    public void Dispose()
    {
        _disposed = true;
        try { _watchdog?.Dispose(); } catch { }
        foreach (var d in ConnectedDevices) d.Dispose();
    }
}

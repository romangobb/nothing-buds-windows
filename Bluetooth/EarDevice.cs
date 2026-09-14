// High-level CMF Buds Pro 2 (B172) device: owns an RfcommClient,
// runs the ear-web init sequence, parses responses into bindable state.

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;

namespace NothingBuds.Bluetooth;

public sealed class EarDevice : INotifyPropertyChanged, IDisposable
{
    private readonly RfcommClient _rf = new();
    private readonly SynchronizationContext? _ctx = SynchronizationContext.Current;

    public string Mac { get; }
    public string Name { get; set; }
    public string BaseModel { get; set; } = "B172";

    private bool _connected;
    public bool Connected { get => _connected; private set => Set(ref _connected, value); }

    private int? _batteryLeft, _batteryRight, _batteryCase;
    public int? BatteryLeft { get => _batteryLeft; private set => Set(ref _batteryLeft, value); }
    public int? BatteryRight { get => _batteryRight; private set => Set(ref _batteryRight, value); }
    public int? BatteryCase { get => _batteryCase; private set => Set(ref _batteryCase, value); }
    private bool _chargingLeft, _chargingRight, _chargingCase;
    public bool ChargingLeft { get => _chargingLeft; private set => Set(ref _chargingLeft, value); }
    public bool ChargingRight { get => _chargingRight; private set => Set(ref _chargingRight, value); }
    public bool ChargingCase { get => _chargingCase; private set => Set(ref _chargingCase, value); }

    private string _firmware = "";
    public string Firmware { get => _firmware; private set => Set(ref _firmware, value); }

    private AncMode _anc = AncMode.High;
    public AncMode Anc { get => _anc; private set { if (Set(ref _anc, value)) OnPropertyChanged(nameof(AncLabel)); } }
    public string AncLabel => _anc.ToString();

    private ListeningPreset _listening = ListeningPreset.Dirac;
    public ListeningPreset Listening
    {
        get => _listening;
        private set { if (Set(ref _listening, value)) OnPropertyChanged(nameof(ListeningLabel)); }
    }
    public string ListeningLabel => ListeningLabels.Label(_listening);

    private bool _bassEnabled;
    public bool BassEnabled { get => _bassEnabled; private set => Set(ref _bassEnabled, value); }
    private int _bassLevel = 3;
    public int BassLevel { get => _bassLevel; private set => Set(ref _bassLevel, value); }

    private bool _inEar = true;
    public bool InEar { get => _inEar; private set => Set(ref _inEar, value); }
    private bool _latency;
    public bool Latency { get => _latency; private set => Set(ref _latency, value); }

    private string _status = "Disconnected";
    public string Status { get => _status; private set => Set(ref _status, value); }

    private string _gestureSummary = "";
    public string GestureSummary { get => _gestureSummary; private set => Set(ref _gestureSummary, value); }

    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>Raised when the RFCOMM link drops (buds left, taken by phone, case closed).</summary>
    public event Action<EarDevice>? ConnectionLost;

    public EarDevice(string mac, string name)
    {
        Mac = BluetoothEndPoint.Normalize(mac);
        Name = name;
        _rf.FrameReceived += OnFrame;
        _rf.Disconnected += () =>
        {
            bool was = Connected;
            Connected = false;
            Status = "Reconnecting…";
            if (was)
            {
                Services.Log.Info($"Link lost {Mac}");
                ConnectionLost?.Invoke(this);
            }
        };
    }

    private void SetUi(Action a)
    {
        try
        {
            var disp = System.Windows.Application.Current?.Dispatcher;
            if (disp != null && !disp.CheckAccess()) { disp.BeginInvoke(a); return; }
        }
        catch { }
        if (_ctx != null && SynchronizationContext.Current != _ctx)
        { try { _ctx.Post(_ => a(), null); return; } catch { } }
        a();
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? prop = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(prop);
        return true;
    }
    private void OnPropertyChanged(string? prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));

    public async Task ConnectAsync(CancellationToken ct = default, int timeoutMs = 8000)
    {
        Status = "Connecting…";
        await Task.Run(() => _rf.Connect(Mac, 15, timeoutMs), ct);
        Connected = true;
        Status = "Connected";
        await RefreshAllAsync(ct);
    }

    public void Disconnect()
    {
        _rf.FrameReceived -= OnFrame;
        _rf.Disconnect();
        Connected = false;
        Status = "Disconnected";
    }

    /// <summary>Send that degrades to "reconnecting" instead of throwing on a dead link.</summary>
    private bool Fire(ushort command, byte[]? payload = null)
    {
        try
        {
            if (payload == null) _rf.Send(command);
            else _rf.Send(command, payload);
            return true;
        }
        catch (Exception ex)
        {
            if (Connected)
            {
                Connected = false;
                Status = "Reconnecting…";
                Services.Log.Info($"Send failed {Mac}: {ex.GetType().Name}; will retry");
                ConnectionLost?.Invoke(this);
            }
            return false;
        }
    }

    /// <summary>ear-web initDevice() order with small gaps (live-verified).</summary>
    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        if (!Connected) return;
        Status = "Syncing…";
        var steps = new ushort[]
        {
            Cmd.Battery, Cmd.ListeningRead, Cmd.Firmware, Cmd.InEarRead,
            Cmd.LatencyRead, Cmd.GetGesture, Cmd.AncRead, Cmd.AdvancedEqRead, Cmd.BassRead,
        };
        foreach (ushort cmd in steps)
        {
            if (!Connected) return;
            Fire(cmd);
            try { await Task.Delay(120, ct); } catch (TaskCanceledException) { return; }
        }
        try { await Task.Delay(300, ct); } catch (TaskCanceledException) { }
        if (Connected) Status = "Connected";
    }

    private void OnFrame(ushort cmd, byte[] p)
    {
        SetUi(() =>
        {
            switch (cmd)
            {
                case Cmd.RespBattery:
                case Cmd.EventBattery:
                    ParseBattery(p);
                    break;
                case Cmd.RespFirmware:
                    Firmware = Encoding.ASCII.GetString(p).Trim('\0');
                    break;
                case Cmd.RespListening:
                case Cmd.RespLegacyEq:
                    if (p.Length >= 1) Listening = (ListeningPreset)p[0];
                    break;
                case Cmd.RespAnc:
                case Cmd.EventAnc:
                    if (p.Length >= 2) Anc = AncWire.FromWire(p[1]);
                    else if (p.Length >= 1) Anc = AncWire.FromWire(p[0]);
                    break;
                case Cmd.RespBass:
                    if (p.Length >= 2) { BassEnabled = p[0] != 0; BassLevel = Math.Clamp(p[1] / 2, 1, 5); }
                    break;
                case Cmd.RespLatency:
                    if (p.Length >= 1) Latency = p[0] == 1;
                    break;
                case Cmd.RespInEar:
                    if (p.Length >= 3) InEar = p[2] != 0;
                    break;
                case Cmd.RespGesture:
                    GestureSummary = DescribeGestures(p);
                    break;
                case Cmd.EventEarFit:
                    break; // handled via dialog callback if needed
                case Cmd.RespAdvancedEq:
                    break;
            }
        });
    }

    private void ParseBattery(byte[] p)
    {
        if (p.Length < 1) return;
        int count = p[0];
        int? l = null, r = null, c = null;
        bool cl = false, cr = false, cc = false;
        for (int i = 0; i < count; i++)
        {
            int o = 1 + i * 2;
            if (o + 1 >= p.Length) break;
            byte id = p[o], v = p[o + 1];
            int lvl = v & 0x7F; bool chg = (v & 0x80) != 0;
            switch (id)
            {
                case 0x02: l = lvl; cl = chg; break;
                case 0x03: r = lvl; cr = chg; break;
                case 0x04: c = lvl; cc = chg; break;
            }
        }
        // Only overwrite slots present in this report (case absent while worn)
        if (l.HasValue) { BatteryLeft = l; ChargingLeft = cl; }
        if (r.HasValue) { BatteryRight = r; ChargingRight = cr; }
        if (c.HasValue) { BatteryCase = c; ChargingCase = cc; }
    }

    private static string DescribeGestures(byte[] p)
    {
        if (p.Length < 1) return "";
        int n = p[0];
        return $"{n} mappings";
    }

    // ---- setters (fire-and-forget, buds echo state back) ----
    public Task SetAncAsync(AncMode mode)
    {
        Anc = mode; OnPropertyChanged(nameof(AncLabel));
        Fire(Cmd.SetAnc, new byte[] { 0x01, AncWire.ToWire(mode), 0x00 });
        return Task.Delay(150);
    }
    public Task SetListeningAsync(ListeningPreset preset)
    {
        Listening = preset; OnPropertyChanged(nameof(ListeningLabel));
        Fire(Cmd.SetListening, new byte[] { (byte)preset, 0x00 });
        return Task.Delay(150);
    }
    public Task SetBassAsync(bool enabled, int level)
    {
        BassEnabled = enabled; BassLevel = Math.Clamp(level, 1, 5);
        Fire(Cmd.SetBass, new byte[] { (byte)(enabled ? 1 : 0), (byte)(BassLevel * 2) });
        return Task.Delay(150);
    }
    public Task SetLatencyAsync(bool on)
    {
        Latency = on;
        Fire(Cmd.SetLatency, on ? new byte[] { 0x01, 0x00 } : new byte[] { 0x02, 0x00 });
        return Task.Delay(150);
    }
    public Task SetInEarAsync(bool on)
    {
        InEar = on;
        Fire(Cmd.SetInEar, new byte[] { 0x01, 0x01, (byte)(on ? 1 : 0) });
        return Task.Delay(150);
    }
    public Task RingAsync(bool left, bool on)
    {
        Fire(Cmd.Ring, new byte[] { (byte)(left ? 0x02 : 0x03), (byte)(on ? 0x01 : 0x00) });
        return Task.Delay(150);
    }
    public Task RingStopAsync() => RingAsync(true, false);
    public Task LaunchEarFitTestAsync()
    {
        Fire(Cmd.EarFitTest, new byte[] { 0x01 });
        return Task.Delay(150);
    }

    public void Dispose() => _rf.Dispose();
}

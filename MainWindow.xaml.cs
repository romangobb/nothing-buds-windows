using NothingBuds.Bluetooth;
using NothingBuds.Services;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace NothingBuds;

public partial class MainWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private readonly DeviceManager _devices;
    private EarDevice? _bound;
    private bool _syncing;
    private List<ScannedDevice> _lastScan = new();

    public MainWindow(DeviceManager devices)
    {
        _devices = devices;
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            // Dark title bar to match the app + Windows dark theme
            try
            {
                int dark = 1;
                DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, 4);
            }
            catch { }
        };

        EqCombo.ItemsSource = Enum.GetValues<ListeningPreset>()
            .Select(p => ListeningLabels.Label(p)).ToList();

        try { AutoStartCheck.IsChecked = AutoStart.IsEnabled(); } catch { }

        _devices.Changed += RefreshAll;
        _devices.ConnectedDevices.CollectionChanged += (_, _) =>
            Dispatcher.Invoke(RefreshAll);
        Closing += (_, e) =>
        {
            e.Cancel = true;
            Hide(); // background tray app: X hides, Quit exits
        };
        ScanResults.MouseDoubleClick += ScanResults_DoubleClick;

        RefreshAll();
    }

    private void RefreshAll()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(RefreshAll); return; }

        // Multi-device switch: only visible with 2+ connected (mobile-app style)
        var connected = _devices.ConnectedDevices.ToList();
        DeviceSwitcherPanel.Visibility =
            connected.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;
        _syncing = true;
        try
        {
            DeviceCombo.ItemsSource = null;
            DeviceCombo.ItemsSource = connected;
            DeviceCombo.SelectedItem = _devices.ActiveDevice;
        }
        finally { _syncing = false; }

        RememberedList.ItemsSource = null;
        RememberedList.ItemsSource =
            _devices.Remembered.Select(r => $"{r.Name}  ({r.Mac})").ToList();

        var active = _devices.ActiveDevice;
        if (_bound != active)
        {
            if (_bound != null) _bound.PropertyChanged -= Active_Changed;
            _bound = active;
            if (_bound != null) _bound.PropertyChanged += Active_Changed;
        }
        UpdateFromDevice();
    }

    private void Active_Changed(object? sender, PropertyChangedEventArgs e) =>
        Dispatcher.Invoke(UpdateFromDevice);

    private void UpdateFromDevice()
    {
        var d = _devices.ActiveDevice;
        _syncing = true;
        try
        {
            bool live = d?.Connected == true;
            DeviceControls.IsEnabled = live;
            if (d == null)
            {
                DeviceNameText.Text = "My devices";
                StatusText.Text = _devices.State;
                FirmwareText.Text = "Firmware: —";
                BattLRun.Text = BattRRun.Text = "—";
                BattBarL.Value = BattBarR.Value = 0;
                BattCaseText.Text = "";
                AncSub.Text = "—";
                BassSub.Text = "—";
                return;
            }
            DeviceNameText.Text = d.Name;
            StatusText.Text = live ? $"Connected  ·  {d.Mac}" : "Disconnected — retrying…";
            FirmwareText.Text = string.IsNullOrEmpty(d.Firmware) ? "Firmware: —" : $"Firmware: {d.Firmware}";
            BattLRun.Text = Batt(d.BatteryLeft, d.ChargingLeft);
            BattRRun.Text = Batt(d.BatteryRight, d.ChargingRight);
            BattBarL.Value = d.BatteryLeft ?? 0;
            BattBarR.Value = d.BatteryRight ?? 0;
            BattCaseText.Text = d.BatteryCase.HasValue
                ? $"Case {d.BatteryCase}%{(d.ChargingCase ? " ⚡" : "")}" : "";
            SetAnc(d.Anc);
            AncSub.Text = AncSubtitle(d.Anc);
            EqCombo.SelectedIndex = (int)d.Listening;
            BassEnable.IsChecked = d.BassEnabled;
            BassSlider.Value = d.BassLevel;
            BassLabel.Text = d.BassLevel.ToString();
            BassSub.Text = d.BassEnabled ? $"On · Level {d.BassLevel}" : "Off";
            InEarCheck.IsChecked = d.InEar;
            LatencyCheck.IsChecked = d.Latency;
        }
        finally { _syncing = false; }
    }

    private static string Batt(int? v, bool chg) =>
        !v.HasValue ? "—" : $"{v}%{(chg ? " ⚡" : "")}";

    private static string AncSubtitle(AncMode m) => m switch
    {
        AncMode.Off => "Off",
        AncMode.Transparency => "Transparency",
        AncMode.Low => "On · Low",
        AncMode.Mid => "On · Mid",
        AncMode.High => "On · High",
        AncMode.Adaptive => "On · Adaptive",
        _ => m.ToString(),
    };

    private static bool IsNcFamily(AncMode m) =>
        m is AncMode.Low or AncMode.Mid or AncMode.High or AncMode.Adaptive;

    private void SetAnc(AncMode m)
    {
        AncNc.IsChecked = IsNcFamily(m);
        AncTr.IsChecked = m == AncMode.Transparency;
        AncOff.IsChecked = m == AncMode.Off;
        LvlLow.IsChecked = m == AncMode.Low;
        LvlMid.IsChecked = m == AncMode.Mid;
        LvlHigh.IsChecked = m == AncMode.High;
        LvlAdapt.IsChecked = m == AncMode.Adaptive;
        AncLevelRow.IsEnabled = IsNcFamily(m);
        AncLevelRow.Opacity = IsNcFamily(m) ? 1 : 0.35;
    }

    private async void AncMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncing || _devices.ActiveDevice == null) return;
        if (sender is not System.Windows.Controls.RadioButton rb) return;
        if (!rb.IsChecked.GetValueOrDefault()) return;
        AncMode target = (rb.CommandParameter as string) switch
        {
            "TR" => AncMode.Transparency,
            "OFF" => AncMode.Off,
            // NC circle keeps current strength, defaulting to High
            _ => IsNcFamily(_devices.ActiveDevice.Anc) ? _devices.ActiveDevice.Anc : AncMode.High,
        };
        await _devices.ActiveDevice.SetAncAsync(target);
    }

    private async void AncLevel_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncing || _devices.ActiveDevice == null) return;
        if (sender is not System.Windows.Controls.RadioButton rb || rb.Tag is not string tag) return;
        if (!rb.IsChecked.GetValueOrDefault()) return;
        await _devices.ActiveDevice.SetAncAsync((AncMode)int.Parse(tag));
    }

    private void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // While the list is open, arrow-key highlight moves must not switch devices.
        if (_syncing || DeviceCombo.IsDropDownOpen) return;
        if (DeviceCombo.SelectedItem is EarDevice dev)
            _devices.SetActive(dev.Mac);
    }

    // Commit-on-close for both dropdowns: Esc (or no change) resyncs the UI,
    // anything else commits. Prevents highlight-walking from firing actions.
    private bool _comboEsc;
    private int _eqOpenIndex = -1;
    private object? _devOpenItem;

    private void Combo_EscMark(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape) _comboEsc = true;
    }

    private void EqCombo_Opened(object sender, EventArgs e)
    {
        _eqOpenIndex = EqCombo.SelectedIndex;
        _comboEsc = false;
    }

    private async void EqCombo_Closed(object? sender, EventArgs e)
    {
        if (_syncing || _devices.ActiveDevice == null) { _comboEsc = false; return; }
        try
        {
            if (_comboEsc || EqCombo.SelectedIndex < 0 || EqCombo.SelectedIndex == _eqOpenIndex)
            {
                // Cancel: snap the UI back to the buds' real state
                _syncing = true;
                try { EqCombo.SelectedIndex = (int)_devices.ActiveDevice.Listening; }
                finally { _syncing = false; }
                return;
            }
            await _devices.ActiveDevice.SetListeningAsync((ListeningPreset)EqCombo.SelectedIndex);
        }
        finally { _comboEsc = false; }
    }

    private void DeviceCombo_Opened(object sender, EventArgs e)
    {
        _devOpenItem = DeviceCombo.SelectedItem;
        _comboEsc = false;
    }

    private void DeviceCombo_Closed(object? sender, EventArgs e)
    {
        if (_syncing) { _comboEsc = false; return; }
        try
        {
            if (_comboEsc || Equals(DeviceCombo.SelectedItem, _devOpenItem))
            {
                _syncing = true;
                try { DeviceCombo.SelectedItem = _devices.ActiveDevice; }
                finally { _syncing = false; }
                return;
            }
            if (DeviceCombo.SelectedItem is EarDevice dev)
                _devices.SetActive(dev.Mac);
        }
        finally { _comboEsc = false; }
    }

    private async void EqCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Ignored while open (committed in EqCombo_Closed); closed-state
        // arrow changes still apply immediately.
        if (_syncing || EqCombo.IsDropDownOpen || _devices.ActiveDevice == null) return;
        if (EqCombo.SelectedIndex < 0) return;
        await _devices.ActiveDevice.SetListeningAsync((ListeningPreset)EqCombo.SelectedIndex);
    }

    private async void Bass_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing || _devices.ActiveDevice == null) return;
        bool en = BassEnable.IsChecked.GetValueOrDefault();
        int lvl = (int)BassSlider.Value;
        await _devices.ActiveDevice.SetBassAsync(en, lvl);
        BassLabel.Text = lvl.ToString();
        BassSub.Text = en ? $"On · Level {lvl}" : "Off";
    }

    private System.Threading.CancellationTokenSource? _bassCts;
    private async void BassSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing || _devices?.ActiveDevice == null || BassLabel == null || BassSlider == null) return;
        int lvl = (int)BassSlider.Value;
        BassLabel.Text = lvl.ToString();
        _bassCts?.Cancel();
        var cts = _bassCts = new System.Threading.CancellationTokenSource();
        try
        {
            await Task.Delay(350, cts.Token);
            bool en = BassEnable.IsChecked.GetValueOrDefault();
            await _devices.ActiveDevice.SetBassAsync(en, lvl);
            BassSub.Text = en ? $"On · Level {lvl}" : "Off";
        }
        catch (TaskCanceledException) { }
    }

    private async void InEar_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing || _devices.ActiveDevice == null) return;
        await _devices.ActiveDevice.SetInEarAsync(InEarCheck.IsChecked.GetValueOrDefault());
    }

    private async void Latency_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing || _devices.ActiveDevice == null) return;
        await _devices.ActiveDevice.SetLatencyAsync(LatencyCheck.IsChecked.GetValueOrDefault());
    }

    private void AutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        AutoStart.SetEnabled(AutoStartCheck.IsChecked.GetValueOrDefault());
    }

    private async void RingL_Click(object sender, RoutedEventArgs e)
    {
        if (_devices.ActiveDevice != null) await _devices.ActiveDevice.RingAsync(left: true, on: true);
    }
    private async void RingR_Click(object sender, RoutedEventArgs e)
    {
        if (_devices.ActiveDevice != null) await _devices.ActiveDevice.RingAsync(left: false, on: true);
    }
    private async void RingStop_Click(object sender, RoutedEventArgs e)
    {
        if (_devices.ActiveDevice != null) await _devices.ActiveDevice.RingStopAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) =>
        await _devices.RefreshActiveAsync();

    private async void EarFit_Click(object sender, RoutedEventArgs e)
    {
        if (_devices.ActiveDevice == null) return;
        await _devices.ActiveDevice.LaunchEarFitTestAsync();
        System.Windows.MessageBox.Show("Ear tip fit test started — check the buds prompts. Results arrive as events.",
            "NothingBuds", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
    }

    private async void AddDevice_Click(object sender, RoutedEventArgs e)
    {
        AddDeviceButton.IsEnabled = false;
        ScanStatus.Text = "Scanning paired Bluetooth devices…";
        try
        {
            _lastScan = await _devices.ScanForNothingDevicesAsync(s => ScanStatus.Text = s);
            if (_lastScan.Count == 0)
            {
                ScanStatus.Text = "No Nothing/CMF buds found. Pair them in Windows Bluetooth settings first (case open, setup button 2s), then scan again.";
                ScanResults.Visibility = Visibility.Collapsed;
            }
            else
            {
                ScanStatus.Text = $"Found {_lastScan.Count}: double-click to remember + connect.";
                ScanResults.ItemsSource = _lastScan.Select(s => $"{s.Name}  ({s.Mac})").ToList();
                ScanResults.Visibility = Visibility.Visible;
            }
        }
        finally { AddDeviceButton.IsEnabled = true; }
    }

    private async void ScanResults_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        int i = ScanResults.SelectedIndex;
        if (i < 0 || i >= _lastScan.Count) return;
        var pick = _lastScan[i];
        ScanStatus.Text = $"Connecting to {pick.Name}…";
        var dev = await _devices.AddAndConnectAsync(pick);
        ScanStatus.Text = dev != null ? $"Added {pick.Name} — will auto-connect next launch." : "Connect failed.";
        ScanResults.Visibility = Visibility.Collapsed;
    }

    private void RemoveDevice_Click(object sender, RoutedEventArgs e)
    {
        int i = RememberedList.SelectedIndex;
        if (i < 0) return;
        var all = _devices.Remembered.ToList();
        if (i < all.Count) _devices.Forget(all[i].Mac);
    }
}

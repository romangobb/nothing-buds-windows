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
            var p = d.Profile;
            DeviceNameText.Text = p.MarketingName;
            StatusText.Text = live ? $"Connected  ·  {d.Mac}" : "Disconnected — retrying…";
            FirmwareText.Text = string.IsNullOrEmpty(d.Firmware) ? "Firmware: —" : $"Firmware: {d.Firmware}";
            BattLRun.Text = Batt(d.BatteryLeft, d.ChargingLeft);
            BattRRun.Text = Batt(d.BatteryRight, d.ChargingRight);
            BattBarL.Value = d.BatteryLeft ?? 0;
            BattBarR.Value = d.BatteryRight ?? 0;
            BattCaseText.Text = d.BatteryCase.HasValue
                ? $"Case {d.BatteryCase}%{(d.ChargingCase ? " ⚡" : "")}" : "";

            // Feature gating per model profile.
            NoiseCard.Visibility = p.HasAnc ? Visibility.Visible : Visibility.Collapsed;
            BassCard.Visibility = p.HasBass ? Visibility.Visible : Visibility.Collapsed;
            InEarCheck.Visibility = p.HasInEar ? Visibility.Visible : Visibility.Collapsed;
            PersonalAncCheck.Visibility = p.HasPersonalAnc ? Visibility.Visible : Visibility.Collapsed;

            UpdateHeroImages(d);
            SetAnc(d.Anc);
            AncSub.Text = AncSubtitle(d.Anc);
            SyncEqCombo(d);
            BassEnable.IsChecked = d.BassEnabled;
            PaintBass(d.BassLevel, d.BassEnabled);
            BassSub.Text = d.BassEnabled ? $"On · Level {d.BassLevel}" : "Off";
            InEarCheck.IsChecked = d.InEar;
            PersonalAncCheck.IsChecked = d.PersonalAnc;
            LatencyCheck.IsChecked = d.Latency;
        }
        finally { _syncing = false; }
    }

    private string _dotsFor = "";
    private void UpdateHeroImages(EarDevice d)
    {
        var p = d.Profile;
        bool single = p.SingleImage;
        BudPairGrid.Visibility = (p.ImagePrefix != null && !single) ? Visibility.Visible : Visibility.Collapsed;
        BudImgSingle.Visibility = (p.ImagePrefix != null && single) ? Visibility.Visible : Visibility.Collapsed;
        if (p.ImagePrefix == null) { ColorDots.Children.Clear(); _dotsFor = ""; return; }
        if (!single)
        {
            SetImage(BudImgL, d.ImageLeft);
            SetImage(BudImgR, d.ImageRight);
        }
        else SetImage(BudImgSingle, d.ImageSingle);
        string key = d.Mac + "|" + p.ModelId + "|" + d.SelectedColor;
        if (key != _dotsFor) { BuildColorDots(d); _dotsFor = key; }
    }

    private static void SetImage(System.Windows.Controls.Image img, string? rel)
    {
        try
        {
            if ((img.Tag as string) == rel) return;
            img.Tag = rel;
            img.Source = rel == null ? null
                : new System.Windows.Media.Imaging.BitmapImage(
                    new Uri($"pack://application:,,,/{rel}"));
        }
        catch { img.Source = null; }
    }

    private void BuildColorDots(EarDevice d)
    {
        ColorDots.Children.Clear();
        var colors = d.Profile.Colors;
        if (colors.Length <= 1) return;
        foreach (string c in colors)
        {
            string color = c; // closure copy
            var btn = new System.Windows.Controls.Button
            {
                Width = 26, Height = 26, Margin = new Thickness(2, 0, 2, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = color,
                MinHeight = 0, Padding = new Thickness(0),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
            };
            // Flat template: skip the global pill-button chrome.
            var root = new FrameworkElementFactory(typeof(System.Windows.Controls.Border));
            root.SetValue(System.Windows.Controls.Border.BackgroundProperty,
                System.Windows.Media.Brushes.Transparent);
            var presenter = new FrameworkElementFactory(typeof(System.Windows.Controls.ContentPresenter));
            presenter.SetValue(System.Windows.Controls.ContentPresenter.HorizontalAlignmentProperty,
                System.Windows.HorizontalAlignment.Center);
            presenter.SetValue(System.Windows.Controls.ContentPresenter.VerticalAlignmentProperty,
                System.Windows.VerticalAlignment.Center);
            root.AppendChild(presenter);
            btn.Template = new System.Windows.Controls.ControlTemplate(typeof(System.Windows.Controls.Button))
            {
                VisualTree = root,
            };
            var ring = new System.Windows.Controls.Border
            {
                Width = 24, Height = 24, CornerRadius = new System.Windows.CornerRadius(12),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(color == d.SelectedColor ? 2 : (NeedsOutline(color) ? 1 : 0)),
                BorderBrush = color == d.SelectedColor
                    ? System.Windows.Media.Brushes.White
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6e, 0x72, 0x76)),
                Child = new System.Windows.Shapes.Ellipse
                {
                    Width = 16, Height = 16, Fill = ColorDotBrush(color),
                },
            };
            btn.Content = ring;
            btn.Click += (_, _) =>
            {
                var dev = _devices.ActiveDevice;
                if (dev == null) return;
                dev.SelectedColor = color;
                _devices.RememberColor(dev.Mac, color);
                UpdateHeroImages(dev);
            };
            ColorDots.Children.Add(btn);
        }
    }

    private static System.Windows.Media.Brush ColorDotBrush(string color) =>
        color.ToLowerInvariant().Replace(" ", "") switch
        {
            "black" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0x00, 0x00)),
            "darkgrey" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3a, 0x3d, 0x40)),
            "white" => System.Windows.Media.Brushes.WhiteSmoke,
            "orange" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xe8, 0x6a, 0x1f)),
            "blue" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3a, 0x6e, 0xa5)),
            "green" or "lightgreen" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9d, 0xbe, 0x8c)),
            "yellow" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xe8, 0xc5, 0x1f)),
            "grey" or "gray" or "lightgrey" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xb9, 0xbd, 0xc2)),
            _ => System.Windows.Media.Brushes.Gray,
        };

    /// <summary>Dark fills need a permanent hairline or they melt into the card.</summary>
    private static bool NeedsOutline(string color) =>
        color.ToLowerInvariant().Replace(" ", "") switch
        {
            "black" or "darkgrey" => true,
            _ => false,
        };

    private void SyncEqCombo(EarDevice d)
    {
        var labels = d.EqOptions.Select(o => o.Label).ToList();
        var cur = EqCombo.ItemsSource as System.Collections.IList;
        bool same = cur != null && cur.Count == labels.Count &&
                    labels.Zip(cur.Cast<object>(), (a, b) => a == (b as string)).All(x => x);
        if (!same)
        {
            EqCombo.ItemsSource = labels;
            EqCombo.SelectedIndex = labels.Count > 0 ? 0 : -1;
        }
        int idx = d.EqOptions.ToList().FindIndex(o => o.Value == d.EqValue);
        if (idx >= 0) EqCombo.SelectedIndex = idx;
    }

    private int EqComboValue()
    {
        var d = _devices.ActiveDevice;
        int i = EqCombo.SelectedIndex;
        if (d == null || i < 0 || i >= d.EqOptions.Count) return -1;
        return d.EqOptions[i].Value;
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
    private int _eqOpenValue = -1;
    private object? _devOpenItem;

    private void Combo_EscMark(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape) _comboEsc = true;
    }

    private void EqCombo_Opened(object sender, EventArgs e)
    {
        _eqOpenValue = EqComboValue();
        _comboEsc = false;
    }

    private async void EqCombo_Closed(object? sender, EventArgs e)
    {
        var d = _devices.ActiveDevice;
        if (_syncing || d == null) { _comboEsc = false; return; }
        try
        {
            int v = EqComboValue();
            if (_comboEsc || v < 0 || v == _eqOpenValue || v == d.EqValue)
            {
                // Cancel: snap the UI back to the buds' real state
                _syncing = true;
                try { SyncEqCombo(d); }
                finally { _syncing = false; }
                return;
            }
            await d.SetEqAsync(v);
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
        int v = EqComboValue();
        if (v < 0) return;
        await _devices.ActiveDevice.SetEqAsync(v);
    }

    private async void PersonalAnc_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing || _devices.ActiveDevice == null) return;
        await _devices.ActiveDevice.SetPersonalAncAsync(PersonalAncCheck.IsChecked.GetValueOrDefault());
    }

    private void PaintBass(int level, bool enabled)
    {
        level = Math.Clamp(level, 1, 5);
        double w = BassTrack.ActualWidth;
        BassFill.Width = w > 0 ? Math.Max(w * level / 5.0, 22) : 22;
        BassTrack.Opacity = enabled ? 1 : 0.35;
    }

    private void BassTrack_Resize(object sender, SizeChangedEventArgs e)
    {
        var d = _devices.ActiveDevice;
        if (d != null) PaintBass(d.BassLevel, d.BassEnabled);
    }

    private async void Bass_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing || _devices.ActiveDevice == null) return;
        bool en = BassEnable.IsChecked.GetValueOrDefault();
        int lvl = _devices.ActiveDevice.BassLevel;
        await _devices.ActiveDevice.SetBassAsync(en, lvl);
        PaintBass(lvl, en);
        BassSub.Text = en ? $"On · Level {lvl}" : "Off";
    }

    private bool _bassDrag;
    private System.Threading.CancellationTokenSource? _bassCts;

    private void BassTrack_Press(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _bassDrag = true;
        BassTrack.CaptureMouse();
        BassTrack_Set(e);
        e.Handled = true;
    }

    private void BassTrack_Move(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_bassDrag) BassTrack_Set(e);
    }

    private void BassTrack_Release(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _bassDrag = false;
        BassTrack.ReleaseMouseCapture();
    }

    private void BassTrack_Set(System.Windows.Input.MouseEventArgs e)
    {
        if (_syncing || _devices.ActiveDevice == null) return;
        double frac = e.GetPosition(BassTrack).X / Math.Max(BassTrack.ActualWidth, 1);
        // Dots sit on level boundaries; a dot tap belongs to its level.
        // Epsilon counters FP noise (0.4*5 can read 2.0000000004 -> ceiling 3).
        int lvl = Math.Clamp((int)Math.Ceiling(frac * 5 - 1e-9), 1, 5);
        bool en = BassEnable.IsChecked.GetValueOrDefault();
        PaintBass(lvl, en);
        BassSub.Text = en ? $"On · Level {lvl}" : "Off";
        _bassCts?.Cancel();
        var cts = _bassCts = new System.Threading.CancellationTokenSource();
        var dev = _devices.ActiveDevice;
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250, cts.Token);
                await dev.SetBassAsync(en, lvl);
            }
            catch (TaskCanceledException) { }
        });
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

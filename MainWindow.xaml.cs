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

        try { AutoStartCheck.IsChecked = AutoStart.IsEnabled(); } catch { }

        _devices.Changed += RefreshAll;
        _devices.ConnectedDevices.CollectionChanged += (_, _) =>
            Dispatcher.Invoke(RefreshAll);
        Closing += (_, e) =>
        {
            e.Cancel = true;
            ShowInTaskbar = false;
            Hide(); // background tray app: X hides, Quit exits
        };
        // Panel behavior: taskbar entry only while active. Focus loss,
        // minimize, or X hides to tray; tray reopens (and refreshes).
        Activated += (_, _) =>
        {
            ShowInTaskbar = true;
            RefreshIfStale();
        };
        Deactivated += (_, _) =>
        {
            ShowInTaskbar = false;
            Hide();
        };
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
                ShowInTaskbar = false;
                Hide();
            }
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
            SyncEqGrid(d);
            UpdateEqLayout(d);
            BassEnable.IsChecked = d.BassEnabled;
            PaintBass(d.BassLevel, d.BassEnabled);
            BassSub.Text = d.BassEnabled ? $"On · Level {d.BassLevel}" : "Off";
            RingL.Content = d.RingingLeft ? "Stop left" : "Ring left";
            RingR.Content = d.RingingRight ? "Stop right" : "Ring right";
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

    private string _eqGridKey = "";

    private void SyncEqGrid(EarDevice d)
    {
        var opts = d.EqOptions;
        string key = d.Mac + "|" + d.Profile.ModelId + "|" + (int)d.ActiveEq + "|" + opts.Count;
        if (key != _eqGridKey)
        {
            _eqGridKey = key;
            // First preset lives in the title row, right of "Equaliser".
            var hg = new System.Windows.Controls.Grid();
            // Bottom 2 + template 2 + button 4 = 8: same gap as between grid rows.
            hg.Margin = new System.Windows.Thickness(0, 0, 0, 2);
            hg.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
                { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
            hg.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
                { Width = System.Windows.GridLength.Auto });
            var title = new System.Windows.Controls.TextBlock
            {
                Text = "Equaliser",
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                // Explicit: the global TextBlock style (Inter) beats the
                // GroupBox header's inherited NType, which shrank the title.
                FontFamily = (System.Windows.Media.FontFamily)FindResource("NType"),
                FontSize = 19,
                Foreground = System.Windows.Media.Brushes.White,
            };
            var head = MakeEqPreset(opts[0]);
            head.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
            head.Margin = new System.Windows.Thickness(0);
            _eqHead = head;
            hg.Children.Add(title);
            hg.Children.Add(head);
            System.Windows.Controls.Grid.SetColumn(head, 1);
            EqCard.Header = hg;

            EqPresetBox.Children.Clear();
            bool hasCustomFooter = opts.Count > 0 && opts[^1].Value == d.CustomValue;
            var cells = opts.Skip(1).Take(opts.Count - (hasCustomFooter ? 2 : 1)).ToList();
            // Custom is a standard-size cell completing the last row
            // (right of Classical), not a full-width footer.
            if (hasCustomFooter) cells.Add(opts[^1]);
            if (cells.Count > 0)
            {
                var ug = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3 };
                foreach (var o in cells) ug.Children.Add(MakeEqPreset(o));
                EqPresetBox.Children.Add(ug);
            }
        }
        UpdateEqHeadWidth();
        foreach (var rb in EqPresetRadios())
            rb.IsChecked = rb.Tag is int v && v == d.EqValue;
    }

    private System.Windows.Controls.RadioButton? _eqHead;

    /// <summary>Title-row pill spans 2 grid cells: 2 buttons + the gap
    /// between them (2*cell - 2*button-margin).</summary>
    private void UpdateEqHeadWidth()
    {
        if (_eqHead == null) return;
        double box = EqPresetBox.ActualWidth;
        if (box > 0) _eqHead.Width = box * 2 / 3 - 8;
    }

    private void EqPresetBox_Resize(object sender, SizeChangedEventArgs e) =>
        UpdateEqHeadWidth();

    private System.Collections.Generic.IEnumerable<System.Windows.Controls.RadioButton> EqPresetRadios()
    {
        if (EqCard.Header is System.Windows.Controls.Grid hg)
            foreach (var rb in hg.Children.OfType<System.Windows.Controls.RadioButton>())
                yield return rb;
        foreach (var rb in EqPresetBox.Children.OfType<System.Windows.Controls.RadioButton>())
            yield return rb;
        foreach (var ug in EqPresetBox.Children.OfType<System.Windows.Controls.Primitives.UniformGrid>())
            foreach (var rb in ug.Children.OfType<System.Windows.Controls.RadioButton>())
                yield return rb;
    }

    private System.Windows.Controls.RadioButton MakeEqPreset(EqOption o)
    {
        var rb = new System.Windows.Controls.RadioButton
        {
            Content = o.Label,
            Tag = o.Value,
            GroupName = "EQPRESET",
            Style = (System.Windows.Style)FindResource("AncLevel"),
            FontFamily = (System.Windows.Media.FontFamily)FindResource("Inter"),
            FontSize = 12,
            Margin = new Thickness(4),
            MinHeight = 40,
        };
        rb.Checked += EqPreset_Checked;
        return rb;
    }

    private async void EqPreset_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncing || _devices.ActiveDevice == null) return;
        if (sender is not System.Windows.Controls.RadioButton rb) return;
        if (!rb.IsChecked.GetValueOrDefault() || rb.Tag is not int v) return;
        var dev = _devices.ActiveDevice;
        await dev.SetEqAsync(v);
        if (v == dev.CustomValue) await dev.FireGetCustomEq();
    }

    private void UpdateEqLayout(EarDevice d)
    {
        bool custom = d.IsCustomMode && d.CustomSupported;
        EqSliders.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        System.Windows.Controls.Grid.SetRowSpan(EqCard, custom ? 2 : 1);
        System.Windows.Controls.Grid.SetColumnSpan(FindCard, custom ? 1 : 2);
        FindCard.Margin = custom
            ? new Thickness(0, 14, 7, 0)
            : new Thickness(0, 14, 0, 0);
        FindButtons.Columns = custom ? 1 : 3;
        FindButtons.Rows = custom ? 3 : 1;
        if (custom)
        {
            _eqB = d.CustomBass;
            _eqM = d.CustomMid;
            _eqT = d.CustomTreble;
            RefreshEqFaders();
        }
    }

    private static string FmtEq(double v) => (v > 0 ? "+" : "") + v.ToString("0");

    // --- Custom-EQ vertical faders: same physics/design as the Ultra Bass
    // bar, rotated: press/drag anywhere on the track, snap to integer
    // -6..+6, white fill from the zero middle, white knob circle at the
    // value end (bare circle at 0, line length zero), debounced commit.
    private int _eqB, _eqM, _eqT;
    private const double EqKnob = 11.0;
    private const double EqR = 8.5;

    private (System.Windows.Controls.Grid track, System.Windows.Controls.Canvas dots,
             System.Windows.Controls.Border fill) EqParts(string tag) => tag switch
    {
        "M" => (EqTrackMid, EqDotsMid, EqFillMid),
        "T" => (EqTrackTreble, EqDotsTreble, EqFillTreble),
        _ => (EqTrackBass, EqDotsBass, EqFillBass),
    };

    private int EqVal(string tag) => tag switch { "M" => _eqM, "T" => _eqT, _ => _eqB };

    private void RefreshEqFaders()
    {
        EqValBass.Text = FmtEq(_eqB);
        EqValMid.Text = FmtEq(_eqM);
        EqValTreble.Text = FmtEq(_eqT);
        PaintEq("B"); PaintEq("M"); PaintEq("T");
    }

    private void PaintEq(string tag)
    {
        var (track, _, fill) = EqParts(tag);
        double h = track.ActualHeight;
        double w = track.ActualWidth;
        if (h <= 0) return;
        int v = EqVal(tag);
        double y0 = h / 2;
        double travel = y0 - EqR / 3 - EqKnob / 2;
        double yc = y0 - v / 6.0 * travel;
        double top = Math.Min(yc, y0) - EqKnob / 2;
        double bottom = Math.Max(yc, y0) + EqKnob / 2;
        fill.Height = bottom - top;
        fill.Margin = new Thickness(0, top, 0, 0);
        PaintEqDots(tag, w, y0, travel);
    }

    private readonly System.Collections.Generic.Dictionary<string,
        System.Collections.Generic.List<System.Windows.Shapes.Ellipse>> _eqDots = new();

    /// <summary>Muted step dots at -6..-1/+1..+6 (no zero dot: the fill's
    /// rounded caps always cover the middle). Fill paints over dots it
    /// reaches, same as the bass bar.</summary>
    private void PaintEqDots(string tag, double w, double y0, double travel)
    {
        var (_, canvas, _) = EqParts(tag);
        if (!_eqDots.TryGetValue(tag, out var list))
        {
            list = new System.Collections.Generic.List<System.Windows.Shapes.Ellipse>();
            var muted = (System.Windows.Media.Brush)FindResource("MutedBrush");
            for (int i = 0; i < 12; i++)
            {
                var dot = new System.Windows.Shapes.Ellipse
                {
                    Width = 4, Height = 4, Fill = muted,
                };
                list.Add(dot);
                canvas.Children.Add(dot);
            }
            _eqDots[tag] = list;
        }
        double left = Math.Max((w - 4) / 2, 0);
        int i2 = 0;
        for (int v = -6; v <= 6 && i2 < list.Count; v++)
        {
            if (v == 0) continue;
            double yc = y0 - v / 6.0 * travel;
            System.Windows.Controls.Canvas.SetLeft(list[i2], left);
            System.Windows.Controls.Canvas.SetTop(list[i2], yc - 2);
            i2++;
        }
    }

    private void EqTrack_Resize(object sender, SizeChangedEventArgs e) =>
        RefreshEqFaders();

    private void EqTrack_Press(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is System.Windows.UIElement el)
        {
            el.CaptureMouse();
            if (sender is FrameworkElement fe && fe.Tag is string tag)
                EqTrack_Set(tag, e);
            e.Handled = true;
        }
    }

    private void EqTrack_Move(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is System.Windows.UIElement el && el.IsMouseCaptured &&
            sender is FrameworkElement fe && fe.Tag is string tag)
            EqTrack_Set(tag, e);
    }

    private void EqTrack_Release(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is System.Windows.UIElement el && el.IsMouseCaptured)
            el.ReleaseMouseCapture();
    }

    private void EqTrack_Set(string tag, System.Windows.Input.MouseEventArgs e)
    {
        if (_syncing || _devices.ActiveDevice == null) return;
        var (track, _, _) = EqParts(tag);
        double h = Math.Max(track.ActualHeight, 1);
        double y = e.GetPosition(track).Y;
        double y0 = h / 2;
        double travel = Math.Max(y0 - EqR / 3 - EqKnob / 2, 1);
        int v = Math.Clamp((int)Math.Round((y0 - y) / travel * 6), -6, 6);
        Services.Log.Info($"Eq tap {tag} y={y:F1} h={h:F1} -> v={v}");
        switch (tag)
        {
            case "M": _eqM = v; break;
            case "T": _eqT = v; break;
            default: _eqB = v; break;
        }
        RefreshEqFaders();
        CommitEqDebounced();
    }

    private System.Threading.CancellationTokenSource? _eqCts;
    private void CommitEqDebounced()
    {
        _eqCts?.Cancel();
        var cts = _eqCts = new System.Threading.CancellationTokenSource();
        var dev = _devices.ActiveDevice;
        if (dev == null) return;
        int b = _eqB, m = _eqM, t = _eqT;
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(300, cts.Token);
                await dev.SetCustomEqAsync(b, m, t);
            }
            catch (TaskCanceledException) { }
        });
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

    // Commit-on-close for the device dropdown: Esc (or no change) resyncs
    // the UI, anything else commits. Prevents highlight-walking from firing.
    private bool _comboEsc;
    private object? _devOpenItem;

    private void Combo_EscMark(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape) _comboEsc = true;
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

    private async void PersonalAnc_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing || _devices.ActiveDevice == null) return;
        await _devices.ActiveDevice.SetPersonalAncAsync(PersonalAncCheck.IsChecked.GetValueOrDefault());
    }

    // Track geometry: height 34 -> R = 17. The fill is inset R/3 on both ends
    // so its rounded ends stay concentric with the track's curves.
    private const double BassR = 17.0;

    private void PaintBass(int level, bool enabled)
    {
        level = Math.Clamp(level, 1, 5);
        double w = BassTrack.ActualWidth;
        double span = w - 2 * BassR / 3;
        BassFill.Width = w > 0 ? Math.Max(span * level / 5.0, 22) : 22;
        BassTrack.Opacity = enabled ? 1 : 0.35;
        PaintBassDots(w);
    }

    private readonly List<System.Windows.Shapes.Ellipse> _bassDots = new();

    /// <summary>Step dots at 40/60/80/100%, each shifted R left so fill
    /// edges never bisect them.</summary>
    private void PaintBassDots(double w)
    {
        if (_bassDots.Count == 0 && BassDots != null)
        {
            var muted = (System.Windows.Media.Brush)FindResource("MutedBrush");
            for (int i = 0; i < 4; i++)
            {
                var dot = new System.Windows.Shapes.Ellipse
                {
                    Width = 8, Height = 8, Fill = muted,
                };
                _bassDots.Add(dot);
                BassDots.Children.Add(dot);
            }
        }
        if (w <= 0) return;
        double[] fracs = { 0.4, 0.6, 0.8, 1.0 };
        double top = Math.Max((BassTrack.ActualHeight - 8) / 2, 0);
        for (int i = 0; i < _bassDots.Count && i < fracs.Length; i++)
        {
            System.Windows.Controls.Canvas.SetLeft(_bassDots[i], w * fracs[i] - BassR - 4);
            System.Windows.Controls.Canvas.SetTop(_bassDots[i], top);
        }
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
        double w = Math.Max(BassTrack.ActualWidth, 1);
        double x = e.GetPosition(BassTrack).X;
        double frac = (x - BassR / 3) / (w - 2 * BassR / 3);
        frac = Math.Clamp(frac, 0, 1);
        // Dots sit on level boundaries; a dot tap belongs to its level.
        // Epsilon counters FP noise (0.4*5 can read 2.0000000004 -> ceiling 3).
        int lvl = Math.Clamp((int)Math.Ceiling(frac * 5 - 1e-9), 1, 5);
        Services.Log.Info($"Bass tap x={x:F1} w={w:F1} frac={frac:F3} -> lvl={lvl}");
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
        bool on = LatencyCheck.IsChecked.GetValueOrDefault();
        await _devices.ActiveDevice.SetLatencyAsync(on);
        _devices.RememberLatency(_devices.ActiveDevice.Mac, on);
    }

    private void AutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        AutoStart.SetEnabled(AutoStartCheck.IsChecked.GetValueOrDefault());
    }

    private async void RingL_Click(object sender, RoutedEventArgs e)
    {
        var dev = _devices.ActiveDevice;
        if (dev == null) return;
        // Toggle this side, like the web/mobile app (Stop silences both).
        await dev.RingAsync(left: true, on: !dev.RingingLeft);
    }
    private async void RingR_Click(object sender, RoutedEventArgs e)
    {
        var dev = _devices.ActiveDevice;
        if (dev == null) return;
        await dev.RingAsync(left: false, on: !dev.RingingRight);
    }
    private async void RingStop_Click(object sender, RoutedEventArgs e)
    {
        if (_devices.ActiveDevice != null) await _devices.ActiveDevice.RingStopAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) =>
        await _devices.RefreshActiveAsync();

    // F5 always refreshes now; foreground activation refreshes at most
    // every 5s so alt-tabbing doesn't spam the buds with GET bursts.
    private DateTime _lastFgRefresh = DateTime.MinValue;

    private async void RefreshIfStale()
    {
        if ((DateTime.UtcNow - _lastFgRefresh).TotalSeconds < 5) return;
        _lastFgRefresh = DateTime.UtcNow;
        await _devices.RefreshActiveAsync();
    }

    private async void Window_PreviewKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.F5)
        {
            e.Handled = true;
            _lastFgRefresh = DateTime.UtcNow;
            await _devices.RefreshActiveAsync();
        }
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

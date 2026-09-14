// App lifecycle: single MainWindow hidden to tray, auto-connect on start,
// left-click tray icon reopens window centered. Close (X) hides, Exit quits.

using NothingBuds.Services;

namespace NothingBuds;

public partial class App : System.Windows.Application
{
    private System.Windows.Forms.NotifyIcon? _tray;
    private MainWindow? _main;
    public DeviceManager Devices { get; } = new();

    protected override async void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        Log.Info("OnStartup");
        DispatcherUnhandledException += (_, ev) =>
        {
            Log.Info("UI crash: " + ev.Exception);
            ev.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
            Log.Info("Fatal: " + ev.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, ev) =>
        { Log.Info("Task crash: " + ev.Exception); ev.SetObserved(); };
        ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;

        _main = new MainWindow(Devices);
        _main.Hide();

        SetupTray();

        bool minimized = e.Args.Contains("--minimized");
        if (!minimized) ShowCentered();

        // Auto-connect remembered devices (no manual selection screen)
        _ = Task.Run(async () =>
        {
            await Devices.AutoConnectAsync();
            Dispatcher.Invoke(() =>
            {
                UpdateTrayText();
                if (Devices.ConnectedDevices.Count == 0 && !minimized)
                    ShowCentered(); // first run: show Add-device UI
            });
        });
        Devices.Changed += () => Dispatcher.Invoke(UpdateTrayText);
        await Task.CompletedTask;
    }

    private void SetupTray()
    {
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = MakeTrayIcon(),
            Text = "NothingBuds",
            Visible = true,
        };
        _tray.Click += (_, ev) =>
        {
            if (ev is System.Windows.Forms.MouseEventArgs me && me.Button == System.Windows.Forms.MouseButtons.Left)
                Dispatcher.Invoke(ShowCentered);
        };
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowCentered);
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => Dispatcher.Invoke(ShowCentered));
        menu.Items.Add("Refresh", null, async (_, _) => await Devices.RefreshActiveAsync());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Dispatcher.Invoke(Quit));
        _tray.ContextMenuStrip = menu;
    }

    private void UpdateTrayText()
    {
        if (_tray == null) return;
        var a = Devices.ActiveDevice;
        _tray.Text = a != null && a.Connected
            ? $"NothingBuds - {a.Name} (L:{a.BatteryLeft?.ToString() ?? "-"}% R:{a.BatteryRight?.ToString() ?? "-"}%)"
            : "NothingBuds - disconnected";
        if (_tray.Text.Length >= 64) _tray.Text = _tray.Text[..63];
    }

    public void ShowCentered()
    {
        if (_main == null) return;
        if (!_main.IsVisible) _main.Show();
        _main.WindowState = System.Windows.WindowState.Normal;
        _main.ShowInTaskbar = true;
        _main.Activate();
        // Center on the screen containing the cursor (multi-monitor safe)
        var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
        var area = screen.WorkingArea;
        var src = System.Windows.PresentationSource.FromVisual(_main);
        double scaleX = src?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        double scaleY = src?.CompositionTarget?.TransformToDevice.M22 ?? 1;
        _main.Left = area.Left / scaleX + (area.Width / scaleX - _main.Width) / 2;
        _main.Top = area.Top / scaleY + (area.Height / scaleY - _main.Height) / 2;
        _main.Topmost = true; _main.Topmost = false;
        _main.Focus();
    }

    public void HideToTray() => _main?.Hide();

    /// <summary>Nothing-style tray glyph: ink tile, paper "N", signal-red dot.</summary>
    private static System.Drawing.Icon MakeTrayIcon()
    {
        var bmp = new System.Drawing.Bitmap(32, 32);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(System.Drawing.Color.Transparent);
            using var ink = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(6, 8, 10));
            g.FillRectangle(ink, 0, 0, 32, 32);
            using var paper = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(240, 242, 242));
            using var font = new System.Drawing.Font(System.Drawing.FontFamily.GenericSansSerif,
                17, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
            var sf = new System.Drawing.StringFormat
            {
                Alignment = System.Drawing.StringAlignment.Center,
                LineAlignment = System.Drawing.StringAlignment.Center
            };
            g.DrawString("N", font, paper, new System.Drawing.RectangleF(0, 1, 32, 30), sf);
            using var red = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(215, 25, 33));
            g.FillEllipse(red, 23, 3, 6, 6);
        }
        IntPtr h = bmp.GetHicon();
        try { return System.Drawing.Icon.FromHandle(h); }
        finally { /* handle owned by Icon */ }
    }

    private void Quit()
    {
        if (_tray != null) _tray.Visible = false;
        Devices.Dispose();
        _tray?.Dispose();
        Shutdown();
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        if (_tray != null) _tray.Visible = false;
        Devices.Dispose();
        base.OnExit(e);
    }
}

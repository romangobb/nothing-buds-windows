using Microsoft.Win32;

namespace NothingBuds.Services;

/// <summary>Run-on-login via HKCU Run key. No admin needed.</summary>
public static class AutoStart
{
    private const string ValueName = "NothingBuds";
    private static string ExePath =>
        Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run");
            object? v = key?.GetValue(ValueName);
            return v is string s && s.Contains("NothingBuds", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static void SetEnabled(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key == null) return;
            if (on)
            {
                string exe = ExePath;
                if (!string.IsNullOrEmpty(exe))
                    key.SetValue(ValueName, $"\"{exe}\" --minimized");
            }
            else
            {
                if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName);
            }
        }
        catch { }
    }
}

using Microsoft.Win32;

namespace Sempulse.Services;

// "Start with Windows": a per-user Run entry pointing at this exe. Nothing is written unless the user turns it on.
public static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "Sempulse";

    static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Name) is string value && string.Equals(value, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(Name, Command);
        else key.DeleteValue(Name, throwOnMissingValue: false);
    }
}

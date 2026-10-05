using Microsoft.Win32;

namespace HomeAssistant.Desktop.Services;

/// <summary>
/// Run-at-logon registration through the per-user Run key.
///
/// The Run key is used rather than a scheduled task or the startup folder because it needs
/// no elevation and no shortcut file to keep in sync with the executable's location.
/// </summary>
public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "HomeAssistantDesktop";

    private static string ExecutablePath => Environment.ProcessPath ?? string.Empty;

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return false;
            }

            if (enabled)
            {
                var path = ExecutablePath;
                if (string.IsNullOrEmpty(path))
                {
                    return false;
                }

                // --minimized so a logon launch goes straight to the tray.
                key.SetValue(ValueName, $"\"{path}\" --minimized", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}

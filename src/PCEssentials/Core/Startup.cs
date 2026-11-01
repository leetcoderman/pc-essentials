using System.Diagnostics;
using Microsoft.Win32;

namespace PCEssentials.Core;

/// Start with Windows, either through the per-user Run key or, for CPU temperatures, as a logon
/// task with highest privileges (no UAC prompt at each sign-in).
public static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "PCEssentials";
    public static string ExePath => Environment.ProcessPath ?? "";

    /// Start-with-Windows points at an exe path; when the app has moved, an existing entry follows it.
    public static void KeepPathCurrent(bool isAdmin)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            if (key?.GetValue(Name) is string value && !value.Contains(ExePath, StringComparison.OrdinalIgnoreCase))
                SetRunKey(true);
            if (isAdmin && AdminTaskEnabled) SetAdminTask(true);
        }
        catch (Exception ex) { Log.Error("startup path", ex); }
    }

    public static bool RunKeyEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Name) is string;
        }
    }

    public static void SetRunKey(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(Name, $"\"{ExePath}\"");
        else key.DeleteValue(Name, false);
    }

    public static bool AdminTaskEnabled => RunSchtasks($"/Query /TN \"{Name}\"") == 0;

    /// Needs the app to be running elevated already.
    public static bool SetAdminTask(bool enabled)
    {
        var code = enabled
            ? RunSchtasks($"/Create /TN \"{Name}\" /TR \"\\\"{ExePath}\\\"\" /SC ONLOGON /RL HIGHEST /F")
            : RunSchtasks($"/Delete /TN \"{Name}\" /F");
        return code == 0;
    }

    private static int RunSchtasks(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            });
            if (p is null) return -1;
            p.WaitForExit(10_000);
            return p.ExitCode;
        }
        catch { return -1; }
    }

    /// Relaunches elevated. Returns false if the user declined the UAC prompt.
    public static bool RestartAsAdmin()
    {
        try
        {
            Process.Start(new ProcessStartInfo(ExePath, "--after-restart") { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch { return false; }
    }

    public static bool PawnIoInstalled
    {
        get
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
            return key is not null;
        }
    }
}

using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
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
    /// An elevated start also rewrites the admin task, so a task made by an older version picks up
    /// the current settings.
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

    /// Needs the app to be running elevated already. The task replaces the Run key, so the app
    /// doesn't also start a second time without admin rights.
    public static bool SetAdminTask(bool enabled)
    {
        if (!enabled) return RunSchtasks($"/Delete /TN \"{Name}\" /F") == 0;
        // Created from XML because plain /Create leaves Task Scheduler's defaults, which stop the
        // app after 72 hours and keep it from starting on battery.
        var xml = Path.Combine(Path.GetTempPath(), $"{Name}-task.xml");
        try
        {
            File.WriteAllText(xml, TaskXml(), Encoding.Unicode);
            if (RunSchtasks($"/Create /TN \"{Name}\" /XML \"{xml}\" /F") != 0) return false;
        }
        finally { try { File.Delete(xml); } catch { } }
        SetRunKey(false);
        return true;
    }

    private static string TaskXml()
    {
        var user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Enabled>true</Enabled>
              </Settings>
              <Actions Context="Author"><Exec><Command>{SecurityElement.Escape(ExePath)}</Command></Exec></Actions>
            </Task>
            """;
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

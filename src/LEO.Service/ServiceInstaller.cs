using System.Diagnostics;
using System.Security.Principal;
using Leo.Core;

namespace Leo.Service;

/// <summary>
/// `LEO.Service.exe install [--start]` / `LEO.Service.exe uninstall`.
/// Dev convenience until the WiX MSI (M8) takes over service registration.
/// </summary>
internal static class ServiceInstaller
{
    public static int Run(string verb, string[] options)
    {
        if (!IsElevated())
        {
            Console.Error.WriteLine("LEO: this command needs an elevated (Administrator) prompt.");
            return 5; // ERROR_ACCESS_DENIED
        }

        return verb switch
        {
            "install" => Install(start: options.Contains("--start")),
            "uninstall" => Uninstall(),
            _ => 1,
        };
    }

    private static int Install(bool start)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot resolve executable path.");
        var name = LeoInfo.ServiceName;

        // Runs as LocalSystem, auto-start at boot (LEO.md §7).
        var rc = Sc($"create {name} binPath= \"\\\"{exe}\\\"\" start= auto obj= LocalSystem DisplayName= \"{LeoInfo.ServiceDisplayName}\"",
            quietOn: 1073);
        if (rc == 1073) // ERROR_SERVICE_EXISTS → just repoint it
        {
            rc = Sc($"config {name} binPath= \"\\\"{exe}\\\"\" start= auto obj= LocalSystem DisplayName= \"{LeoInfo.ServiceDisplayName}\"");
        }
        if (rc != 0) return rc;

        Sc($"description {name} \"{LeoInfo.ServiceDescription}\"");
        // Recovery: restart after 5s, 10s, 30s; reset the failure count daily (LEO.md §4.1).
        Sc($"failure {name} reset= 86400 actions= restart/5000/restart/10000/restart/30000");
        Sc($"failureflag {name} 1");

        Console.WriteLine($"LEO: service '{name}' installed -> {exe}");
        return start ? Sc($"start {name}") : 0;
    }

    private static int Uninstall()
    {
        var name = LeoInfo.ServiceName;
        Sc($"stop {name}");
        for (var i = 0; i < 20 && QueryRunning(name); i++) Thread.Sleep(250);
        var rc = Sc($"delete {name}");
        if (rc is 0 or 1060) // 1060 = does not exist
        {
            Console.WriteLine($"LEO: service '{name}' removed.");
            return 0;
        }
        return rc;
    }

    private static bool QueryRunning(string name)
    {
        var (rc, output) = Exec("sc.exe", $"query {name}");
        return rc == 0 && (output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase)
                           || output.Contains("STOP_PENDING", StringComparison.OrdinalIgnoreCase));
    }

    private static int Sc(string arguments, int? quietOn = null)
    {
        var (rc, output) = Exec("sc.exe", arguments);
        if (rc != 0 && rc != quietOn) Console.Error.WriteLine($"sc {arguments.Split(' ')[0]} -> {rc}: {output.Trim()}");
        return rc;
    }

    private static (int ExitCode, string Output) Exec(string file, string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo(file, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }

    private static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

using System.Diagnostics;
using System.Security.Principal;
using Leo.Core;

namespace Leo.Service;

/// <summary>
/// `LEO.Service.exe install [--start]` / `uninstall`: dev convenience for tools\dev-service.ps1 (the MSI registers
/// the service itself). `cleanup` and `remove-dev-install` are run by the MSI (LEO.md §24.2, §24.5).
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
            "cleanup" => Setup.UninstallCleanup.Run(Console.Out),
            "remove-dev-install" => Setup.DevInstallRemover.Run(Console.Out),
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

    private static int Uninstall() => RemoveService(Console.Out);

    /// <summary>Stop (the service's stop path hands metrics back, §5.9), delete, then LEO's own firewall group.</summary>
    internal static int RemoveService(TextWriter output)
    {
        var name = LeoInfo.ServiceName;
        Sc($"stop {name}");
        for (var i = 0; i < 20 && QueryRunning(name); i++) Thread.Sleep(250);
        var rc = Sc($"delete {name}");
        if (rc is 0 or 1060) // 1060 = does not exist
        {
            output.WriteLine($"LEO: service '{name}' removed.");
            try
            {
                var n = Interop.FirewallRules.RemoveAll(); // only rules in LEO's own firewall group (LEO.md §21)
                if (n > 0) output.WriteLine($"LEO: removed {n} LEO firewall rule(s).");
            }
            catch (Exception ex) { output.WriteLine($"LEO: firewall cleanup failed: {ex.Message}"); }
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

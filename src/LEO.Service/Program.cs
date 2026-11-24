using Leo.Core;
using Leo.Core.Config;
using Leo.Service;
using Leo.Service.Logging;
using Leo.Service.Networking;
using Leo.Service.Wifi;

// Verbs: (none)              run — as a service under the SCM, or as a console app when started interactively
//        install [--start]   register the Windows service (admin required; dev only, the MSI registers its own)
//        uninstall           stop and remove the Windows service (admin required; dev only)
//        cleanup             full uninstall: put networking back, delete ALL settings and data (run by the MSI, §24.5)
//        remove-dev-install  remove a dev-deployed service before the MSI installs its own (run by the MSI, §24.2)

// DLLs only from System32 and this (admin-only writable) folder, never the current directory or PATH (LEO.md §24.6).
Leo.Core.Security.DllSearch.Restrict();

if (args.Length > 0 && args[0] is "install" or "uninstall" or "cleanup" or "remove-dev-install")
{
    return ServiceInstaller.Run(args[0], args.Skip(1).ToArray());
}

if (!MachineDataSecurity.Apply())
{
    // Someone else owns (or redirected) ProgramData\UNTERSTELLEN and it couldn't be repaired: refuse to write there.
    // Windows networking stays untouched while the service isn't running (LEO.md §5.9).
    Console.Error.WriteLine("LEO: the data folder isn't safe to use; the service won't start.");
    return 1;
}
Leo.Ipc.PipeTransport.Warmup();

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = LeoInfo.ServiceName);
builder.Logging.AddLeoFileLogger(LeoPaths.MachineLogs, "service");

builder.Services.AddSingleton(new ConfigStore(LeoPaths.ConfigFile));
builder.Services.AddSingleton<EngineStatusProvider>();
builder.Services.AddSingleton<NotificationHub>();
builder.Services.AddSingleton<NetworkInventory>();
builder.Services.AddSingleton<NetworkManager>();
builder.Services.AddSingleton<WifiManager>();
builder.Services.AddSingleton<Leo.Service.SplitTunnel.MihomoEngine>();
builder.Services.AddSingleton<Leo.Service.SplitTunnel.ChildProcessTracker>();
builder.Services.AddSingleton<VpnMonitor>();
builder.Services.AddSingleton(sp => new Leo.Service.SplitTunnel.SplitTunnelManager(
    sp.GetRequiredService<ConfigStore>(), sp.GetRequiredService<NetworkManager>(), sp.GetRequiredService<Leo.Service.SplitTunnel.MihomoEngine>(),
    sp.GetRequiredService<Leo.Service.SplitTunnel.ChildProcessTracker>(), sp.GetRequiredService<NotificationHub>(),
    sp.GetRequiredService<ILogger<Leo.Service.SplitTunnel.SplitTunnelManager>>(), sp.GetRequiredService<VpnMonitor>()));
builder.Services.AddSingleton(sp => new Leo.Service.Profiles.ProfileManager(
    sp.GetRequiredService<ConfigStore>(), sp.GetRequiredService<NetworkManager>(), sp.GetRequiredService<NotificationHub>(),
    sp.GetRequiredService<ILogger<Leo.Service.Profiles.ProfileManager>>(), sp.GetRequiredService<WifiManager>(),
    sp.GetRequiredService<Leo.Service.SplitTunnel.SplitTunnelManager>()));
builder.Services.AddSingleton(sp => new Leo.Service.Stats.StatsCollector(
    sp.GetRequiredService<NetworkManager>(), sp.GetRequiredService<ConfigStore>(),
    sp.GetRequiredService<ILogger<Leo.Service.Stats.StatsCollector>>(),
    sp.GetRequiredService<Leo.Service.SplitTunnel.SplitTunnelManager>(), sp.GetRequiredService<Leo.Service.SplitTunnel.ChildProcessTracker>()));

builder.Services.AddHostedService<EngineHeartbeat>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NetworkManager>());
builder.Services.AddHostedService(sp =>
{
    var wifi = sp.GetRequiredService<WifiManager>();
    var networks = sp.GetRequiredService<NetworkManager>();
    wifi.ConnectionChanged += (_, _) => networks.RequestRescan();
    networks.Changed += (_, snap) => wifi.PrimaryAdapterId = snap.PrimaryAdapterId;
    return wifi;
});
builder.Services.AddHostedService(sp => sp.GetRequiredService<VpnMonitor>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<Leo.Service.SplitTunnel.SplitTunnelManager>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<Leo.Service.Profiles.ProfileManager>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<Leo.Service.Stats.StatsCollector>());
builder.Services.AddHostedService<ControlPipeServer>();

builder.Build().Run();
return 0;

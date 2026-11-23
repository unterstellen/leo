using Leo.Core;
using Leo.Core.Config;
using Leo.Service;
using Leo.Service.Logging;
using Leo.Service.Networking;
using Leo.Service.Wifi;

// Verbs: (none)              run — as a service under the SCM, or as a console app when started interactively
//        install [--start]   register the Windows service (admin required)
//        uninstall           stop and remove the Windows service (admin required)
if (args.Length > 0 && args[0] is "install" or "uninstall")
{
    return ServiceInstaller.Run(args[0], args.Skip(1).ToArray());
}

MachineDataSecurity.Apply();
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
builder.Services.AddSingleton<Leo.Service.SplitTunnel.SplitTunnelManager>();
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
builder.Services.AddHostedService(sp => sp.GetRequiredService<Leo.Service.SplitTunnel.SplitTunnelManager>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<Leo.Service.Profiles.ProfileManager>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<Leo.Service.Stats.StatsCollector>());
builder.Services.AddHostedService<ControlPipeServer>();

builder.Build().Run();
return 0;

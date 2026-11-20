using Leo.Core;
using Leo.Service;
using Leo.Service.Logging;

// Verbs: (none)              run — as a service under the SCM, or as a console app when started interactively
//        install [--start]   register the Windows service (admin required)
//        uninstall           stop and remove the Windows service (admin required)
if (args.Length > 0 && args[0] is "install" or "uninstall")
{
    return ServiceInstaller.Run(args[0], args.Skip(1).ToArray());
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = LeoInfo.ServiceName);
builder.Logging.AddLeoFileLogger(LeoPaths.MachineLogs, "service");
builder.Services.AddSingleton<EngineStatusProvider>();
builder.Services.AddHostedService<ControlPipeServer>();
builder.Services.AddHostedService<EngineHeartbeat>();

builder.Build().Run();
return 0;

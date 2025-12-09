using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SimpleWindowsScreentime.Service;
using SimpleWindowsScreentime.Shared;
using SimpleWindowsScreentime.Shared.Configuration;
using SimpleWindowsScreentime.Shared.Security;
using SimpleWindowsScreentime.Shared.Time;

var builder = Host.CreateApplicationBuilder(args);

// Configure as Windows Service
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = Constants.ServiceName;
});

// Register shared services
builder.Services.AddSingleton<ConfigManager>();
builder.Services.AddSingleton<PinManager>();
builder.Services.AddSingleton<ScheduleChecker>();
builder.Services.AddSingleton<RecoveryManager>();
builder.Services.AddSingleton<UnlockManager>();

// Register service components
builder.Services.AddSingleton<BlockerProcessManager>();
builder.Services.AddSingleton<IpcServer>();
builder.Services.AddSingleton<NtpSyncService>();
builder.Services.AddSingleton<SelfHealingService>();
builder.Services.AddSingleton<SessionMonitor>();

// Register the main worker
builder.Services.AddHostedService<ScreenTimeWorker>();

var host = builder.Build();
host.Run();

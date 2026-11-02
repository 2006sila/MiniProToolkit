using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MiniProToolkit.Core;
using MiniProToolkit.Hardware;

namespace MiniProToolkit.Guardian;

public static class Program
{
    public static Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddWindowsService(options =>
        {
            options.ServiceName = "MiniProToolkit Guardian";
        });
        builder.Services.AddSingleton<GuardianWorkerOptions>();
        builder.Services.AddSingleton<GuardianRecoveryCoordinator>(_ =>
            new GuardianRecoveryCoordinator(TimeSpan.FromSeconds(8)));
        builder.Services.AddHostedService<GuardianWorker>();
        return builder.Build().RunAsync();
    }
}

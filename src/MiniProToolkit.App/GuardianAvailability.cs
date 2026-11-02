using System.ServiceProcess;

namespace MiniProToolkit.App;

internal static class GuardianAvailability
{
    public const string ServiceName = "MiniProToolkitGuardian";

    public static bool IsRunning()
    {
        try
        {
            using var service = new ServiceController(ServiceName);
            return service.Status == ServiceControllerStatus.Running;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}

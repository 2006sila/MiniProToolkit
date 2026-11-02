using System.Diagnostics;

namespace MiniProToolkit.App;

internal static class GuardianProcessIdentity
{
    public static DateTimeOffset CurrentStartUtc
    {
        get
        {
            using var process = Process.GetCurrentProcess();
            return process.StartTime.ToUniversalTime();
        }
    }
}

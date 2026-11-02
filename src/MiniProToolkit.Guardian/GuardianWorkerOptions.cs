namespace MiniProToolkit.Guardian;

public sealed class GuardianWorkerOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);
    public string LeasePath { get; init; } = MiniProToolkit.Core.GuardianLeaseStore.DefaultPath;
    public string LogPath { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "MiniProToolkit",
        "guardian.log");
}

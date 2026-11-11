namespace Riftway.Asar;

// The packer never serves an app's archives: the ArchiveIntegrity fuse is off here.
internal static class AppArchiveIntegrity
{
    internal sealed record Policy(bool Enabled);

    internal static Policy Current { get; } = new(false);

    internal static string? Check(Policy policy, string archivePath, string headerHash) => null;
}

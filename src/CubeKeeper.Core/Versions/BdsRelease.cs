namespace CubeKeeper.Core.Versions;

public enum ReleaseChannel
{
    Stable,
    Preview,
}

/// <summary>Una versión publicada de BDS para Linux y su enlace de descarga.</summary>
public sealed record BdsRelease(BdsVersion Version, Uri DownloadUrl, ReleaseChannel Channel, string Source);

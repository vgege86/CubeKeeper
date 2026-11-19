namespace CubeKeeper.Infrastructure.Persistence;

public enum AccessRequestStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
}

/// <summary>Alguien intentó entrar durante el modo inscripción sin estar aprobado.</summary>
public sealed class AccessRequest
{
    public long Id { get; set; }

    public required string Xuid { get; set; }

    public required string Name { get; set; }

    public DateTimeOffset RequestedAt { get; set; }

    /// <summary>Veces que lo ha intentado mientras estaba pendiente.</summary>
    public int Attempts { get; set; } = 1;

    public AccessRequestStatus Status { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }
}

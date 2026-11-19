namespace CubeKeeper.Infrastructure.Persistence;

/// <summary>Normas de juego de un jugador (horarios y límite diario).</summary>
public sealed class PlayerRulesEntity
{
    public required string Xuid { get; set; }

    public bool Enabled { get; set; }

    public int? DailyMinutes { get; set; }

    /// <summary>Franjas en JSON (<see cref="CubeKeeper.Core.Family.PlayerRules.SerializeSchedule"/>).</summary>
    public string ScheduleJson { get; set; } = "[]";

    public int ExtraMinutes { get; set; }

    public DateOnly? ExtraDate { get; set; }
}

/// <summary>Una acción de administración (o automática de CubeKeeper).</summary>
public sealed class AuditEntry
{
    public long Id { get; set; }

    public DateTimeOffset At { get; set; }

    /// <summary>Usuario del panel, o «CubeKeeper» para las automáticas.</summary>
    public required string Actor { get; set; }

    /// <summary>Código de la acción, por ejemplo «server.start» o «access.approve».</summary>
    public required string Action { get; set; }

    public string? Target { get; set; }

    public string? Details { get; set; }
}

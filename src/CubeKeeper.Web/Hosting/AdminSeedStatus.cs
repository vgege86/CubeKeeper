namespace CubeKeeper.Web.Hosting;

/// <summary>Resultado de crear o restablecer el administrador al arrancar, para enseñarlo en la pantalla de login.</summary>
public sealed class AdminSeedStatus
{
    public const int MinPasswordLength = 10;

    /// <summary>Motivo por el que no se pudo crear o restablecer el administrador, o null si fue bien.</summary>
    public string? Problem { get; set; }

    /// <summary>Se ha restablecido la contraseña en este arranque (hay que quitar CUBEKEEPER_ADMIN_RESET).</summary>
    public bool WasReset { get; set; }
}

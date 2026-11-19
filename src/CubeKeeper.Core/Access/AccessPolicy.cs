namespace CubeKeeper.Core.Access;

public enum AccessMode
{
    /// <summary>Puede entrar cualquiera con cuenta Microsoft (allow-list=false).</summary>
    Open,
    /// <summary>Solo los aprobados en allowlist.json (allow-list=true).</summary>
    ApprovedOnly,
}

public enum JoinAction
{
    /// <summary>Puede quedarse.</summary>
    Allow,
    /// <summary>Está bloqueado: expulsar.</summary>
    KickBlocked,
    /// <summary>Modo inscripción y no está aprobado: guardar la solicitud y expulsar.</summary>
    RecordRequestAndKick,
    /// <summary>
    /// Solo aprobados, sin inscripción, y aun así ha entrado (lista desactivada a mano o cambiando de modo): expulsar.
    /// </summary>
    KickNotApproved,
}

/// <summary>Qué hacer con un jugador que acaba de conectarse.</summary>
public static class AccessPolicy
{
    public static JoinAction Decide(string? xuid, AccessMode mode, bool enrollmentActive, Func<string, bool> isApproved, Func<string, bool> isBlocked)
    {
        ArgumentNullException.ThrowIfNull(isApproved);
        ArgumentNullException.ThrowIfNull(isBlocked);
        if (string.IsNullOrEmpty(xuid))
        {
            // Sin XUID (online-mode=false) no se puede identificar a nadie: solo se deja en modo abierto.
            return mode == AccessMode.Open && !enrollmentActive ? JoinAction.Allow : JoinAction.KickNotApproved;
        }
        if (isBlocked(xuid))
        {
            return JoinAction.KickBlocked;
        }
        if (mode == AccessMode.Open || isApproved(xuid))
        {
            return JoinAction.Allow;
        }
        return enrollmentActive ? JoinAction.RecordRequestAndKick : JoinAction.KickNotApproved;
    }

    /// <summary>Nombre entre comillas para comandos como <c>kick</c> (los gamertags pueden llevar espacios o #).</summary>
    public static string QuoteName(string name) => "\"" + name.Replace("\"", string.Empty, StringComparison.Ordinal) + "\"";

    /// <summary>Texto seguro para comandos (sin saltos de línea ni comillas).</summary>
    public static string SafeText(string text)
    {
        var chars = text.Where(c => c != '"').Select(c => char.IsControl(c) ? ' ' : c).ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}

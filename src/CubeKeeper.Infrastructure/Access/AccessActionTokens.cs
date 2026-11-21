using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace CubeKeeper.Infrastructure.Access;

/// <summary>Lo que permite hacer un enlace de un aviso: decidir la solicitud de un jugador.</summary>
public sealed record AccessActionTicket(string Xuid, string Name, DateTimeOffset ExpiresAt);

/// <summary>
/// Enlaces de un solo uso para aprobar o rechazar una solicitud de acceso desde los botones del aviso de ntfy, sin
/// abrir el panel. Cada enlace lleva un secreto aleatorio de 256 bits, caduca a los 30 minutos y se gasta al usarlo
/// (cualquiera de los dos botones). Viven en memoria: al reiniciar la app dejan de valer.
/// </summary>
public sealed class AccessActionTokens(TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, AccessActionTicket> _tickets = new(StringComparer.Ordinal);

    public string Issue(string xuid, string name)
    {
        Prune();
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        _tickets[token] = new AccessActionTicket(xuid, name, time.GetUtcNow() + Lifetime);
        return token;
    }

    /// <summary>Gasta el enlace: devuelve la solicitud si era válido y no había caducado; si no, null.</summary>
    public AccessActionTicket? Redeem(string token)
    {
        if (string.IsNullOrEmpty(token) || !_tickets.TryRemove(token, out var ticket))
        {
            return null;
        }
        // Al decidir sobre un jugador, los demás enlaces suyos (de avisos repetidos) también dejan de valer.
        foreach (var (other, t) in _tickets)
        {
            if (t.Xuid == ticket.Xuid)
            {
                _tickets.TryRemove(other, out _);
            }
        }
        return ticket.ExpiresAt > time.GetUtcNow() ? ticket : null;
    }

    private void Prune()
    {
        var now = time.GetUtcNow();
        foreach (var (token, t) in _tickets)
        {
            if (t.ExpiresAt <= now)
            {
                _tickets.TryRemove(token, out _);
            }
        }
    }
}

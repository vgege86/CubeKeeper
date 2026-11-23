using System.Globalization;

namespace CubeKeeper.Core.Resources;

/// <summary>
/// Lectura de /proc/net/tcp y /proc/net/tcp6: qué sockets TCP usan un puerto local. Sirve para saber si BDS está
/// escuchando en <c>server-port</c> y si quedan conexiones recientes (TIME_WAIT) que le impedirían abrirlo.
/// </summary>
/// <remarks>
/// BDS (NetherNet) abre el puerto TCP de la negociación sin SO_REUSEADDR: si al arrancar queda alguna conexión
/// reciente en ese puerto (por ejemplo, de MCXboxBroadcast, que consulta el servidor cada 30 s), no consigue
/// abrirlo, no lo dice y sigue arrancado sin que nadie pueda entrar. Linux retiene esas conexiones unos 60 s.
/// </remarks>
public static class TcpSockets
{
    /// <summary>Estado TCP_LISTEN en /proc/net/tcp.</summary>
    public const int Listen = 0x0A;

    /// <summary>Estado TCP_TIME_WAIT en /proc/net/tcp.</summary>
    public const int TimeWait = 0x06;

    /// <summary>Estados (en hexadecimal en el archivo) de los sockets cuyo puerto local es <paramref name="port"/>.</summary>
    public static IReadOnlyList<int> StatesOnLocalPort(string procNetTcp, int port)
    {
        ArgumentNullException.ThrowIfNull(procNetTcp);
        var states = new List<int>();
        foreach (var raw in procNetTcp.Split('\n'))
        {
            // «  sl  local_address rem_address   st …»: la primera línea es la cabecera.
            var fields = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 4 || !fields[0].EndsWith(':'))
            {
                continue;
            }
            var local = fields[1];
            var colon = local.LastIndexOf(':');
            if (colon < 0 ||
                !int.TryParse(local.AsSpan(colon + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var localPort) ||
                !int.TryParse(fields[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var state))
            {
                continue;
            }
            if (localPort == port)
            {
                states.Add(state);
            }
        }
        return states;
    }

    /// <summary>Hay algún socket escuchando en el puerto.</summary>
    public static bool IsListening(IEnumerable<int> states) => states.Contains(Listen);

    /// <summary>Hay sockets en el puerto que no escuchan (conexiones abiertas o recientes): impiden a BDS abrirlo.</summary>
    public static int Busy(IEnumerable<int> states) => states.Count(s => s != Listen);
}

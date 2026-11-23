using CubeKeeper.Core.Resources;

namespace CubeKeeper.Infrastructure.Process;

/// <summary>Estado de un puerto TCP local (para no arrancar BDS con el puerto ocupado y vigilar que lo abre).</summary>
public interface ITcpPortProbe
{
    /// <summary>Estados de los sockets TCP con ese puerto local, o null si no se puede saber (no es Linux).</summary>
    IReadOnlyList<int>? States(int port);
}

/// <summary>Lee /proc/net/tcp y /proc/net/tcp6. Con la red del host (como en TrueNAS), son los del NAS.</summary>
public sealed class ProcTcpPortProbe : ITcpPortProbe
{
    private static readonly string[] Files = ["/proc/net/tcp", "/proc/net/tcp6"];

    public IReadOnlyList<int>? States(int port)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }
        var states = new List<int>();
        var any = false;
        foreach (var file in Files)
        {
            try
            {
                states.AddRange(TcpSockets.StatesOnLocalPort(File.ReadAllText(file), port));
                any = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // tcp6 no existe si el kernel no tiene IPv6.
            }
        }
        return any ? states : null;
    }
}

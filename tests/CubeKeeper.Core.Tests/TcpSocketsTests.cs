using CubeKeeper.Core.Resources;

namespace CubeKeeper.Core.Tests;

public class TcpSocketsTests
{
    // 19132 = 0x4ABC. Un LISTEN en IPv4, un TIME_WAIT (06) de una consulta reciente y otro puerto que no cuenta.
    private const string Tcp = """
          sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode
           0: 00000000:4ABC 00000000:0000 0A 00000000:00000000 00:00000000 00000000   568        0 1 1 0000000000000000 100 0 0 10 0
           1: 1501A8C0:4ABC 1601A8C0:D2F0 06 00000000:00000000 03:00000F5A 00000000     0        0 0 3 0000000000000000
           2: 00000000:223D 00000000:0000 0A 00000000:00000000 00:00000000 00000000   568        0 2 1 0000000000000000 100 0 0 10 0
        """;

    private const string Tcp6 = """
          sl  local_address                         remote_address                        st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode
           0: 00000000000000000000000000000000:4ABC 00000000000000000000000000000000:0000 06 00000000:00000000 03:00000F5A 00000000     0        0 0 3 0000000000000000
        """;

    [Fact]
    public void Finds_states_on_the_local_port()
    {
        var states = TcpSockets.StatesOnLocalPort(Tcp, 19132);
        Assert.Equal([TcpSockets.Listen, TcpSockets.TimeWait], states);
        Assert.True(TcpSockets.IsListening(states));
        Assert.Equal(1, TcpSockets.Busy(states));
    }

    [Fact]
    public void Reads_ipv6_and_ignores_header_and_other_ports()
    {
        var states = TcpSockets.StatesOnLocalPort(Tcp6, 19132);
        Assert.Equal([TcpSockets.TimeWait], states);
        Assert.False(TcpSockets.IsListening(states));
        Assert.Empty(TcpSockets.StatesOnLocalPort(Tcp, 19133));
        Assert.Empty(TcpSockets.StatesOnLocalPort("basura\n\n", 19132));
    }
}

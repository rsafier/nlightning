namespace NLightning.Testing.Cluster.Tests.Reach;

using Cluster.Reach;

public class TcpConnectionTableTests
{
    // A bitcoind pod: ZMQ raw block (28332 = 0x6EAC) listening, one subscriber on it, RPC listening, and one IPv6 socket
    private const string Table =
        """
          sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode
           0: 00000000:6EAC 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 1 1 0000000000000000 100 0 0 10 0
           1: 0A2A0007:6EAC 01C2A8C0:D431 01 00000000:00000000 00:00000000 00000000  1000        0 2 1 0000000000000000 20 4 30 10 -1
           2: 00000000:47FB 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 3 1 0000000000000000 100 0 0 10 0
           3: 0A2A0007:6EAC 01C2A8C0:D432 06 00000000:00000000 00:00000000 00000000  1000        0 4 1 0000000000000000 20 4 30 10 -1
          sl  local_address                         remote_address                        st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode
           0: 0000000000000000FFFF00000A2A0007:6EAD 0000000000000000FFFF000001C2A8C0:D433 01 00000000:00000000 00:00000000 00000000  1000        0 5 1 0000000000000000 20 4 30 10 -1
        """;

    [Fact]
    public void Given_ProcNetTcp_When_Parsed_Then_EverySocketIsReadAndHeadersSkipped()
    {
        // Act
        var entries = TcpConnectionTable.Parse(Table);

        // Assert
        Assert.Equal(5, entries.Count);
        Assert.Equal(new TcpSocketEntry(28332, 0, "0A"), entries[0]);
        Assert.Equal(new TcpSocketEntry(28332, 54321, "01"), entries[1]);
        Assert.True(entries[1].IsEstablished);
        Assert.Equal(18427, entries[2].LocalPort);
        Assert.Equal(new TcpSocketEntry(28333, 54323, "01"), entries[4]);
    }

    [Fact]
    public void Given_Sockets_When_Counted_Then_OnlyEstablishedOnesOfThePortCount()
    {
        // Act
        var entries = TcpConnectionTable.Parse(Table);

        // Assert: the listener and the TIME_WAIT (06) socket on 28332 do not count
        Assert.Equal(1, TcpConnectionTable.CountEstablished(entries, 28332));
        Assert.Equal(1, TcpConnectionTable.CountEstablished(entries, 28333));
        Assert.Equal(0, TcpConnectionTable.CountEstablished(entries, 18443));
    }

    [Fact]
    public void Given_GarbageAndAnEmptyTable_When_Parsed_Then_NothingIsReturned()
    {
        // Act / Assert
        Assert.Empty(TcpConnectionTable.Parse(string.Empty));
        Assert.Empty(TcpConnectionTable.Parse("not a table\n 0: xyz abc 01"));
    }
}
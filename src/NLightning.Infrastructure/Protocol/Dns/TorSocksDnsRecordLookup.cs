using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Infrastructure.Protocol.Dns;

using Domain.Node.Options;
using Transport.Tor;

/// <summary>
/// <see cref="ITorDnsRecordLookup"/>: the BOLT 10 seeds asked through Tor (NL-571). One DNS-over-TCP exchange per
/// query (RFC 1035 §4.2.2: a 2-byte length, then the message) to the resolver of
/// <c>Node:Bootstrap:TorNameServer</c>, reached through the SOCKS5 port (<see cref="ITorSocksDialer"/>) like a peer
/// connection: Tor's own resolution does A/AAAA only, while the seeds answer SRV. With stream isolation every query
/// rides its own circuit.
/// </summary>
/// <remarks>
/// Transport failures (no SOCKS port, Tor's refusal, a closed or malformed answer) come back as
/// <see cref="DnsLookupStatus.Other"/>; only the caller's cancellation throws. The connection closes after one
/// exchange — a stub resolver's usage, and a query's latency through Tor dwarfs a new circuit. One query waits at
/// least Tor's <c>ConnectTimeout</c> (a circuit first, like any connection through Tor), never less than
/// <c>Node:Bootstrap:QueryTimeout</c>.
/// </remarks>
internal sealed class TorSocksDnsRecordLookup : ITorDnsRecordLookup
{
    private readonly ITorSocksDialer _dialer;
    private readonly ILogger _logger;
    private readonly string? _host;
    private readonly int _port;
    private readonly TimeSpan _timeout;
    private int _nextId;

    public TorSocksDnsRecordLookup(ITorSocksDialer dialer, IOptions<NodeOptions> nodeOptions,
                                   ILogger<TorSocksDnsRecordLookup> logger)
    {
        _dialer = dialer;
        _logger = logger;
        var options = nodeOptions.Value;
        _timeout = options.Tor.ConnectTimeout > options.Bootstrap.QueryTimeout
            ? options.Tor.ConnectTimeout
            : options.Bootstrap.QueryTimeout;
        NameServer = options.Bootstrap.TorNameServer.Trim();
        IsAvailable = options.Tor.IsEnabled
                   && BootstrapOptions.TryParseTorNameServer(options.Bootstrap.TorNameServer, out _host, out _port);
    }

    /// <inheritdoc />
    public bool IsAvailable { get; }

    /// <inheritdoc />
    public string NameServer { get; }

    /// <inheritdoc />
    public async Task<DnsLookupResponse> QueryAsync(string name, DnsRecordKind kind, CancellationToken ct)
    {
        if (!IsAvailable)
            return DnsLookupResponse.Of(DnsLookupStatus.Other);

        var id = (ushort)Interlocked.Increment(ref _nextId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);
        try
        {
            using var client = await _dialer.ConnectAsync(_host!, _port, timeout.Token);
            var stream = client.GetStream();

            var query = DnsTcpMessage.BuildQuery(id, name, kind);
            var framed = new byte[query.Length + 2];
            BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)query.Length);
            query.CopyTo(framed, 2);
            await stream.WriteAsync(framed, timeout.Token);

            // The reply is one 2-byte length, then the message (never fragmented across exchanges here)
            var header = new byte[2];
            await stream.ReadExactlyAsync(header, timeout.Token);
            var reply = new byte[BinaryPrimitives.ReadUInt16BigEndian(header)];
            if (reply.Length == 0)
                throw new IOException("Empty DNS reply");

            await stream.ReadExactlyAsync(reply, timeout.Token);
            return DnsTcpMessage.TryParseResponse(reply, id, kind, out var response)
                ? response
                : LogAndFail($"malformed or unsolicited reply ({reply.Length} bytes)");
        }
        catch (ArgumentException e)
        {
            return LogAndFail(e.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return DnsLookupResponse.Of(DnsLookupStatus.Timeout);
        }
        catch (Exception e)
        {
            return LogAndFail(e is Socks5Exception or Domain.Exceptions.ConnectionException
                ? e.Message
                : $"{e.GetType().Name}: {e.Message}");
        }

        DnsLookupResponse LogAndFail(string reason)
        {
            _logger.LogDebug("Seed resolver {Server}: {Name} {Kind} failed: {Reason}", NameServer, name, kind, reason);
            return DnsLookupResponse.Of(DnsLookupStatus.Other);
        }
    }
}
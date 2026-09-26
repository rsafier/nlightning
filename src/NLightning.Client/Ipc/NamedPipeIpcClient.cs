using System.Buffers;
using System.IO.Pipes;
using MessagePack;
using NLightning.Domain.Channels.ValueObjects;

namespace NLightning.Client.Ipc;

using Domain.Bitcoin.Enums;
using Domain.Client.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.ValueObjects;
using Transport.Ipc;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

public sealed class NamedPipeIpcClient : IAsyncDisposable
{
    private readonly string _namedPipeFilePath;
    private readonly string _cookieFilePath;
    private readonly string _server;

    public NamedPipeIpcClient(string namedPipeFilePath, string cookieFilePath, string server = ".")
    {
        _namedPipeFilePath = namedPipeFilePath;
        _cookieFilePath = cookieFilePath;
        _server = server;
    }

    public async Task<NodeInfoIpcResponse> GetNodeInfoAsync(CancellationToken ct = default)
    {
        var req = new NodeInfoIpcRequest();
        var payload = MessagePackSerializer.Serialize(req, cancellationToken: ct);
        var env = new IpcEnvelope
        {
            Version = 1,
            Command = ClientCommand.NodeInfo,
            CorrelationId = Guid.NewGuid(),
            AuthToken = await GetAuthTokenAsync(ct),
            Payload = payload,
            Kind = 0
        };

        var respEnv = await SendAsync(env, ct);
        if (respEnv.Kind != IpcEnvelopeKind.Error)
            return MessagePackSerializer.Deserialize<NodeInfoIpcResponse>(respEnv.Payload, cancellationToken: ct);

        var err = MessagePackSerializer.Deserialize<IpcError>(respEnv.Payload, cancellationToken: ct);
        throw new InvalidOperationException($"IPC error {err.Code}: {err.Message}");
    }

    public async Task<ConnectPeerIpcResponse> ConnectPeerAsync(string address, CancellationToken ct = default)
    {
        var req = new ConnectPeerIpcRequest
        {
            Address = new PeerAddressInfo(address)
        };
        var payload = MessagePackSerializer.Serialize(req, cancellationToken: ct);
        var env = new IpcEnvelope
        {
            Version = 1,
            Command = ClientCommand.ConnectPeer,
            CorrelationId = Guid.NewGuid(),
            AuthToken = await GetAuthTokenAsync(ct),
            Payload = payload,
            Kind = 0
        };

        var respEnv = await SendAsync(env, ct);
        if (respEnv.Kind != IpcEnvelopeKind.Error)
            return MessagePackSerializer.Deserialize<ConnectPeerIpcResponse>(respEnv.Payload, cancellationToken: ct);

        var err = MessagePackSerializer.Deserialize<IpcError>(respEnv.Payload, cancellationToken: ct);
        throw new InvalidOperationException($"IPC error {err.Code}: {err.Message}");
    }

    public async Task<ListPeersIpcResponse> ListPeersAsync(CancellationToken ct = default)
    {
        var req = new ListPeersIpcRequest();
        var payload = MessagePackSerializer.Serialize(req, cancellationToken: ct);
        var env = new IpcEnvelope
        {
            Version = 1,
            Command = ClientCommand.ListPeers,
            CorrelationId = Guid.NewGuid(),
            AuthToken = await GetAuthTokenAsync(ct),
            Payload = payload,
            Kind = IpcEnvelopeKind.Request
        };

        var respEnv = await SendAsync(env, ct);
        if (respEnv.Kind != IpcEnvelopeKind.Error)
            return MessagePackSerializer.Deserialize<ListPeersIpcResponse>(respEnv.Payload, cancellationToken: ct);

        var err = MessagePackSerializer.Deserialize<IpcError>(respEnv.Payload, cancellationToken: ct);
        throw new InvalidOperationException($"IPC error {err.Code}: {err.Message}");
    }

    public async Task<ListChannelsIpcResponse> ListChannelsAsync(string? peerId, CancellationToken ct = default)
    {
        var req = new ListChannelsIpcRequest
        {
            PeerId = string.IsNullOrWhiteSpace(peerId) ? null : new CompactPubKey(Convert.FromHexString(peerId))
        };
        var payload = MessagePackSerializer.Serialize(req, cancellationToken: ct);
        var env = new IpcEnvelope
        {
            Version = 1,
            Command = ClientCommand.ListChannels,
            CorrelationId = Guid.NewGuid(),
            AuthToken = await GetAuthTokenAsync(ct),
            Payload = payload,
            Kind = IpcEnvelopeKind.Request
        };

        var respEnv = await SendAsync(env, ct);
        if (respEnv.Kind != IpcEnvelopeKind.Error)
            return MessagePackSerializer.Deserialize<ListChannelsIpcResponse>(respEnv.Payload, cancellationToken: ct);

        var err = MessagePackSerializer.Deserialize<IpcError>(respEnv.Payload, cancellationToken: ct);
        throw new InvalidOperationException($"IPC error {err.Code}: {err.Message}");
    }

    public async Task<GetAddressIpcResponse> GetAddressAsync(string? addressTypeString, CancellationToken ct = default)
    {
        var req = new GetAddressIpcRequest { AddressType = ParseAddressType(addressTypeString) };
        var payload = MessagePackSerializer.Serialize(req, cancellationToken: ct);
        var env = new IpcEnvelope
        {
            Version = 1,
            Command = ClientCommand.GetAddress,
            CorrelationId = Guid.NewGuid(),
            AuthToken = await GetAuthTokenAsync(ct),
            Payload = payload,
            Kind = IpcEnvelopeKind.Request
        };

        var respEnv = await SendAsync(env, ct);
        if (respEnv.Kind != IpcEnvelopeKind.Error)
            return MessagePackSerializer.Deserialize<GetAddressIpcResponse>(respEnv.Payload, cancellationToken: ct);

        var err = MessagePackSerializer.Deserialize<IpcError>(respEnv.Payload, cancellationToken: ct);
        throw new InvalidOperationException($"IPC error {err.Code}: {err.Message}");
    }

    public async Task<WalletBalanceIpcResponse> GetWalletBalance(CancellationToken ct)
    {
        var req = new WalletBalanceIpcRequest();
        var payload = MessagePackSerializer.Serialize(req, cancellationToken: ct);
        var env = new IpcEnvelope
        {
            Version = 1,
            Command = ClientCommand.WalletBalance,
            CorrelationId = Guid.NewGuid(),
            AuthToken = await GetAuthTokenAsync(ct),
            Payload = payload,
            Kind = 0
        };

        var respEnv = await SendAsync(env, ct);
        if (respEnv.Kind != IpcEnvelopeKind.Error)
            return MessagePackSerializer.Deserialize<WalletBalanceIpcResponse>(respEnv.Payload, cancellationToken: ct);

        var err = MessagePackSerializer.Deserialize<IpcError>(respEnv.Payload, cancellationToken: ct);
        throw new InvalidOperationException($"IPC error {err.Code}: {err.Message}");
    }

    public async Task<OpenChannelIpcResponse> OpenChannelAsync(string nodeInfo, string amountSats,
                                                               string? pushSats = null,
                                                               CancellationToken ct = default,
                                                               bool isPublic = false)
    {
        var req = new OpenChannelIpcRequest
        {
            NodeInfo = nodeInfo,
            Amount = LightningMoney.Satoshis(Convert.ToInt64(amountSats)),
            PushAmount = pushSats is null ? null : LightningMoney.Satoshis(Convert.ToInt64(pushSats)),
            IsPublic = isPublic
        };
        var payload = MessagePackSerializer.Serialize(req, cancellationToken: ct);
        var env = new IpcEnvelope
        {
            Version = 1,
            Command = ClientCommand.OpenChannel,
            CorrelationId = Guid.NewGuid(),
            AuthToken = await GetAuthTokenAsync(ct),
            Payload = payload,
            Kind = 0
        };

        var respEnv = await SendAsync(env, ct);
        if (respEnv.Kind != IpcEnvelopeKind.Error)
            return MessagePackSerializer.Deserialize<OpenChannelIpcResponse>(respEnv.Payload, cancellationToken: ct);

        var err = MessagePackSerializer.Deserialize<IpcError>(respEnv.Payload, cancellationToken: ct);
        throw new InvalidOperationException($"IPC error {err.Code}: {err.Message}");
    }

    public async Task<OpenChannelSubscriptionIpcResponse> OpenChannelSubscriptionAsync(
        ChannelId channelId, CancellationToken ct = default)
    {
        var req = new OpenChannelSubscriptionIpcRequest
        {
            ChannelId = channelId
        };
        var payload = MessagePackSerializer.Serialize(req, cancellationToken: ct);
        var env = new IpcEnvelope
        {
            Version = 1,
            Command = ClientCommand.OpenChannelSubscription,
            CorrelationId = Guid.NewGuid(),
            AuthToken = await GetAuthTokenAsync(ct),
            Payload = payload,
            Kind = 0
        };

        var respEnv = await SendAsync(env, ct);
        if (respEnv.Kind != IpcEnvelopeKind.Error)
            return MessagePackSerializer.Deserialize<OpenChannelSubscriptionIpcResponse>(
                respEnv.Payload, cancellationToken: ct);

        var err = MessagePackSerializer.Deserialize<IpcError>(respEnv.Payload, cancellationToken: ct);
        throw new InvalidOperationException($"IPC error {err.Code}: {err.Message}");
    }

    /// <summary>
    /// Creates an invoice (ClientCommand 9).
    /// </summary>
    /// <param name="amount">The requested amount, or null for an any-amount invoice.</param>
    /// <param name="description">BOLT 11 <c>d</c>; may be empty.</param>
    /// <param name="expirySeconds">BOLT 11 <c>x</c>, or null for the node default.</param>
    /// <param name="ct">Cancels the call.</param>
    public Task<CreateInvoiceIpcResponse> CreateInvoiceAsync(LightningMoney? amount, string description,
                                                             uint? expirySeconds, CancellationToken ct = default)
    {
        var req = new CreateInvoiceIpcRequest
        {
            Amount = amount,
            Description = description,
            ExpirySeconds = expirySeconds
        };
        return SendRequestAsync<CreateInvoiceIpcRequest, CreateInvoiceIpcResponse>(ClientCommand.CreateInvoice, req,
                                                                                    ct);
    }

    /// <summary>
    /// Pays a BOLT 11 invoice and waits for the outcome (ClientCommand 10).
    /// </summary>
    /// <param name="bolt11">The invoice.</param>
    /// <param name="amount">The amount when the invoice has none.</param>
    /// <param name="timeoutSeconds">How long the daemon waits for the outcome and retries, or null for its default.
    /// </param>
    /// <param name="maxFeeMsat">The per-call fee limit in msat, or null for the daemon's default (NL-270).</param>
    /// <param name="maxParts">The most HTLCs in flight at once (1 never splits), or null for the daemon's default.
    /// </param>
    /// <param name="ct">Cancels the call (the payment itself keeps going in the daemon).</param>
    public Task<PayInvoiceIpcResponse> PayInvoiceAsync(string bolt11, LightningMoney? amount, uint? timeoutSeconds,
                                                       ulong? maxFeeMsat = null, uint? maxParts = null,
                                                       CancellationToken ct = default)
    {
        var req = new PayInvoiceIpcRequest
        {
            Bolt11 = bolt11,
            Amount = amount,
            TimeoutSeconds = timeoutSeconds,
            MaxFee = maxFeeMsat is { } fee ? LightningMoney.MilliSatoshis(fee) : null,
            MaxParts = maxParts
        };
        return SendRequestAsync<PayInvoiceIpcRequest, PayInvoiceIpcResponse>(ClientCommand.PayInvoice, req, ct);
    }

    /// <summary>
    /// Starts the cooperative close of a channel and waits for the closing transaction (ClientCommand 13).
    /// </summary>
    /// <param name="channelId">The channel.</param>
    /// <param name="feeRatePerKw">The feerate of our closing fee estimate, or null for the daemon's.</param>
    /// <param name="noFeeRange">Negotiate without <c>fee_range</c>.</param>
    /// <param name="waitSeconds">How long the daemon waits for the closing transaction, or null for its default.</param>
    /// <param name="ct">Cancels the call (the close itself goes on in the daemon).</param>
    public Task<CloseChannelIpcResponse> CloseChannelAsync(ChannelId channelId, uint? feeRatePerKw, bool noFeeRange,
                                                           uint? waitSeconds, CancellationToken ct = default)
    {
        var req = new CloseChannelIpcRequest
        {
            ChannelId = channelId,
            FeeRatePerKw = feeRatePerKw,
            NoFeeRange = noFeeRange,
            WaitSeconds = waitSeconds
        };
        return SendRequestAsync<CloseChannelIpcRequest, CloseChannelIpcResponse>(ClientCommand.CloseChannel, req,
                                                                                 ct);
    }

    /// <summary>
    /// Fails a channel and broadcasts our latest commitment (ClientCommand 14).
    /// </summary>
    public Task<ForceCloseChannelIpcResponse> ForceCloseChannelAsync(ChannelId channelId,
                                                                     CancellationToken ct = default)
    {
        var req = new ForceCloseChannelIpcRequest { ChannelId = channelId };
        return SendRequestAsync<ForceCloseChannelIpcRequest, ForceCloseChannelIpcResponse>(
            ClientCommand.ForceCloseChannel, req, ct);
    }

    /// <summary>
    /// Lists the on-chain resolution of closed channels (ClientCommand 15).
    /// </summary>
    /// <param name="channelId">Only this channel, when set.</param>
    /// <param name="includeClosed">Also the channels already closed.</param>
    /// <param name="ct">Cancels the call.</param>
    public Task<PendingSweepsIpcResponse> PendingSweepsAsync(ChannelId? channelId, bool includeClosed,
                                                             CancellationToken ct = default)
    {
        var req = new PendingSweepsIpcRequest { ChannelId = channelId, IncludeClosed = includeClosed };
        return SendRequestAsync<PendingSweepsIpcRequest, PendingSweepsIpcResponse>(ClientCommand.PendingSweeps, req,
                                                                                   ct);
    }

    /// <summary>
    /// Whether the node's chain processing is halted and what it refuses meanwhile (ClientCommand 16, NL-216).
    /// </summary>
    public Task<ChainStatusIpcResponse> ChainStatusAsync(CancellationToken ct = default) =>
        SendRequestAsync<ChainStatusIpcRequest, ChainStatusIpcResponse>(ClientCommand.ChainStatus,
                                                                         new ChainStatusIpcRequest(), ct);

    /// <summary>
    /// Lists a page of our invoices, newest first (ClientCommand 11).
    /// </summary>
    public Task<ListInvoicesIpcResponse> ListInvoicesAsync(int skip, int take, CancellationToken ct = default)
    {
        var req = new ListInvoicesIpcRequest { Skip = skip, Take = take };
        return SendRequestAsync<ListInvoicesIpcRequest, ListInvoicesIpcResponse>(ClientCommand.ListInvoices, req, ct);
    }

    /// <summary>
    /// Lists a page of our outgoing payments, newest first (ClientCommand 12).
    /// </summary>
    public Task<ListPaymentsIpcResponse> ListPaymentsAsync(int skip, int take, CancellationToken ct = default)
    {
        var req = new ListPaymentsIpcRequest { Skip = skip, Take = take };
        return SendRequestAsync<ListPaymentsIpcRequest, ListPaymentsIpcResponse>(ClientCommand.ListPayments, req, ct);
    }

    /// <summary>
    /// Parses the `getaddress` argument. With no argument, the <see cref="GetAddressIpcRequest"/> default is used.
    /// </summary>
    internal static AddressType ParseAddressType(string? addressTypeString)
    {
        if (string.IsNullOrWhiteSpace(addressTypeString))
            return new GetAddressIpcRequest().AddressType;

        return addressTypeString.ToLowerInvariant() switch
        {
            "p2tr" => AddressType.P2Tr,
            "p2wpkh" => AddressType.P2Wpkh,
            "all" => AddressType.P2Tr | AddressType.P2Wpkh,
            _ => throw new ArgumentOutOfRangeException(nameof(addressTypeString), addressTypeString,
                                                       "Address has to be `p2tr`, `p2wpkh`, or `all`.")
        };
    }

    private async Task<TResponse> SendRequestAsync<TRequest, TResponse>(ClientCommand command, TRequest request,
                                                                       CancellationToken ct)
    {
        var env = new IpcEnvelope
        {
            Version = 1,
            Command = command,
            CorrelationId = Guid.NewGuid(),
            AuthToken = await GetAuthTokenAsync(ct),
            Payload = MessagePackSerializer.Serialize(request, cancellationToken: ct),
            Kind = IpcEnvelopeKind.Request
        };

        var respEnv = await SendAsync(env, ct);
        if (respEnv.Kind != IpcEnvelopeKind.Error)
            return MessagePackSerializer.Deserialize<TResponse>(respEnv.Payload, cancellationToken: ct);

        var err = MessagePackSerializer.Deserialize<IpcError>(respEnv.Payload, cancellationToken: ct);
        throw new InvalidOperationException($"IPC error {err.Code}: {err.Message}");
    }

    private async Task<IpcEnvelope> SendAsync(IpcEnvelope envelope, CancellationToken ct)
    {
        await using var client =
            new NamedPipeClientStream(_server, _namedPipeFilePath, PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            await client.ConnectAsync(TimeSpan.FromSeconds(2), ct);
        }
        catch (TimeoutException)
        {
            throw new IOException(
                "Could not connect to NLightning node IPC pipe. Ensure the node is running and listening for IPC.");
        }

        // Send request
        var bytes = MessagePackSerializer.Serialize(envelope, cancellationToken: ct);
        var lenPrefix = BitConverter.GetBytes(bytes.Length);
        await client.WriteAsync(lenPrefix, ct);
        await client.WriteAsync(bytes, ct);
        await client.FlushAsync(ct);

        // Read response length
        var header = new byte[4];
        await ReadExactAsync(client, header, ct);
        var respLen = BitConverter.ToInt32(header, 0);
        if (respLen is <= 0 or > 10_000_000)
            throw new IOException("Invalid IPC response length.");

        // Read payload
        var respBuf = ArrayPool<byte>.Shared.Rent(respLen);
        try
        {
            await ReadExactAsync(client, respBuf.AsMemory(0, respLen), ct);
            var env = MessagePackSerializer.Deserialize<IpcEnvelope>(respBuf.AsMemory(0, respLen),
                                                                     cancellationToken: ct);
            return env;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(respBuf);
        }
    }

    private static async Task ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], ct);
            if (read == 0) throw new EndOfStreamException();
            total += read;
        }
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
        => await ReadExactAsync(stream, buffer.AsMemory(), ct);

    private async Task<string> GetAuthTokenAsync(CancellationToken ct)
    {
        if (!File.Exists(_cookieFilePath))
            throw new IOException(
                "Authentication cookie file not found. Ensure the node is running and the cookie file path is correct.");

        var content = await File.ReadAllTextAsync(_cookieFilePath, ct);
        return content.Trim();
    }

    public ValueTask DisposeAsync()
        => ValueTask.CompletedTask;
}
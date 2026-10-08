using System.Data;
using System.Data.Common;
using System.Text.Json;
using NBitcoin;
using NLightning.Domain.Bitcoin.Enums;
using NLightning.Infrastructure.Bitcoin.Networks;
using SignedTransaction = NLightning.Domain.Bitcoin.ValueObjects.SignedTransaction;

namespace NLightning.Infrastructure.RemoteSigning;

public sealed partial class NativeSignerAuthority
{
    private static void InitializeWalletApprovals(DbConnection connection)
    {
        Run(connection, null, "CREATE TABLE IF NOT EXISTS NativeWalletApprovals (NodeId TEXT NOT NULL, RequestId TEXT NOT NULL, Fingerprint TEXT NOT NULL, Expires BIGINT NOT NULL, Content TEXT NOT NULL, PRIMARY KEY (NodeId, RequestId))");
        Run(connection, null, "CREATE TABLE IF NOT EXISTS NativeWalletInputClaims (NodeId TEXT NOT NULL, TransactionId TEXT NOT NULL, OutputIndex BIGINT NOT NULL, RequestId TEXT NOT NULL, PRIMARY KEY (NodeId, TransactionId, OutputIndex))");
    }

    /// <summary>
    /// Owner-only exact withdrawal approval. Input claims and both approval records commit together and are retained
    /// after signing; abandoning or replacing a claim requires a separately validated lifecycle.
    /// </summary>
    public void ApproveWalletIntent(NativeSignerBinding binding, NativeSignerIntent intent,
                                    NativeWalletApproval approval, DateTimeOffset expires)
    {
        ValidateIntent(intent);
        intent = intent with { Payload = intent.Payload.ToArray() };
        approval = JsonSerializer.Deserialize<NativeWalletApproval>(JsonSerializer.Serialize(approval))
            ?? throw new ArgumentException("Owner wallet approval is missing.");
        ValidateWalletApproval(binding, intent, approval);
        if (expires.ToUnixTimeSeconds() <= _clock.GetUtcNow().ToUnixTimeSeconds())
            throw new ArgumentException("Wallet approval has expired.");
        // Serialize now so caller-owned arrays cannot alter already installed metadata.
        var fingerprint = Fingerprint(binding, intent);
        var content = JsonSerializer.Serialize(approval);
        using var connection = Open();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        ReadState(connection, transaction, binding);
        Run(connection, transaction, "INSERT INTO NativeWalletApprovals (NodeId, RequestId, Fingerprint, Expires, Content) VALUES (@node, @request, @fingerprint, @expires, @content)",
            ("node", binding.NodeId), ("request", intent.RequestId), ("fingerprint", fingerprint),
            ("expires", expires.ToUnixTimeSeconds()), ("content", content));
        foreach (var input in approval.Inputs)
            Run(connection, transaction, "INSERT INTO NativeWalletInputClaims (NodeId, TransactionId, OutputIndex, RequestId) VALUES (@node, @txid, @index, @request)",
                ("node", binding.NodeId), ("txid", input.TransactionId), ("index", (long)input.OutputIndex), ("request", intent.RequestId));
        Run(connection, transaction, "INSERT INTO NativeSignerApprovals (NodeId, RequestId, Fingerprint, Expires) VALUES (@node, @request, @fingerprint, @expires)",
            ("node", binding.NodeId), ("request", intent.RequestId), ("fingerprint", fingerprint),
            ("expires", expires.ToUnixTimeSeconds()));
        transaction.Commit();
    }

    public NativeWalletApprovedIntent GetWalletApproval(NativeSignerBinding binding, NativeSignerIntent intent)
    {
        ValidateIntent(intent);
        if (FindExecutionReadScope() is { } scope)
            return ReadWalletApproval(scope.Connection, scope.Transaction, binding, intent);
        using var connection = Open();
        return ReadWalletApproval(connection, null, binding, intent);
    }

    private static NativeWalletApprovedIntent ReadWalletApproval(DbConnection connection, DbTransaction? transaction,
                                                                 NativeSignerBinding binding, NativeSignerIntent intent)
    {
        // Fenced execution reads in its owning transaction; external readers own an independent connection.
        ReadState(connection, transaction, binding);
        string fingerprint;
        long expires;
        NativeWalletApproval approval;
        using (var command = Command(connection, transaction,
                   "SELECT Fingerprint, Expires, Content FROM NativeWalletApprovals WHERE NodeId = @node AND RequestId = @request",
                   ("node", binding.NodeId), ("request", intent.RequestId)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new UnauthorizedAccessException("Exact owner wallet approval is required.");
            fingerprint = reader.GetString(0);
            expires = reader.GetInt64(1);
            approval = JsonSerializer.Deserialize<NativeWalletApproval>(reader.GetString(2))
                ?? throw new InvalidDataException("Owner wallet approval is missing.");
        }
        if (fingerprint != Fingerprint(binding, intent))
            throw new UnauthorizedAccessException("Wallet request differs from its immutable owner approval.");
        ValidateWalletApproval(binding, intent, approval);
        using (var command = Command(connection, transaction,
                   "SELECT TransactionId, OutputIndex FROM NativeWalletInputClaims WHERE NodeId = @node AND RequestId = @request",
                   ("node", binding.NodeId), ("request", intent.RequestId)))
        using (var reader = command.ExecuteReader())
        {
            var remaining = approval.Inputs.Select(input => (input.TransactionId, input.OutputIndex)).ToHashSet();
            while (reader.Read())
                if (!remaining.Remove((reader.GetString(0), checked((uint)reader.GetInt64(1)))))
                    throw new InvalidDataException("Wallet input claims differ from owner approval.");
            if (remaining.Count != 0) throw new InvalidDataException("Wallet input claims are incomplete.");
        }
        return new NativeWalletApprovedIntent(binding, intent.RequestId, fingerprint, expires, approval);
    }

    private static void ValidateWalletApproval(NativeSignerBinding binding, NativeSignerIntent intent,
                                                NativeWalletApproval approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        if (intent.Operation != SignerOperations.SignWalletTransaction3 || approval.ReservationId == Guid.Empty
         || approval.Inputs is not { Count: > 0 and <= 1024 } || approval.Spending is null
         || approval.Spending.Destinations is not { Count: > 0 and <= 1024 }
         || approval.Spending.MaximumFeeSatoshis < 0
         || approval.Spending.Destinations.Any(output => output is null || output.AmountSatoshis <= 0
             || output.ScriptPubKey is not { Length: > 0 and <= 10000 }))
            throw new ArgumentException("Wallet approval requires a supported purpose and bounded spending terms.");
        var inputs = new HashSet<(string, uint)>();
        foreach (var input in approval.Inputs)
        {
            if (input is null || input.Derivation is null
             || input.Derivation.AddressType is not (AddressType.P2Wpkh or AddressType.P2Tr)
             || input.TransactionId is not { Length: 64 } || !input.TransactionId.All(Uri.IsHexDigit)
             || uint256.Parse(input.TransactionId).ToString() != input.TransactionId
             || !inputs.Add((input.TransactionId, input.OutputIndex)))
                throw new ArgumentException("Wallet approval input or derivation is invalid or duplicated.");
        }
        var arguments = SignerWire.Decode(intent.Payload);
        if (arguments.Length != SignerOperations.ArgumentCount(intent.Operation)
         || SignerWire.Read<Guid>(arguments[1]) != approval.ReservationId)
            throw new ArgumentException("Wallet request does not match its approved reservation.");
        var unsigned = SignerWire.Read<SignedTransaction>(arguments[0]);
        var tx = Transaction.Load(unsigned.RawTxBytes, NBitcoinNetworkResolver.Resolve(binding.Network));
        var proposed = tx.Inputs.Select(input => (input.PrevOut.Hash.ToString(), input.PrevOut.N)).ToArray();
        if (proposed.Length != inputs.Count || proposed.Distinct().Count() != proposed.Length
         || !inputs.SetEquals(proposed))
            throw new ArgumentException("Wallet request does not match the complete approved input set.");
    }
}
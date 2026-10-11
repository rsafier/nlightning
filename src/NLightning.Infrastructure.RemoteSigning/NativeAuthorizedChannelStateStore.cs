using System.Data;
using System.Data.Common;
using System.Text.Json;
using NLightning.Domain.Bitcoin.Transactions.Enums;
using NLightning.Domain.Bitcoin.ValueObjects;
using NLightning.Domain.Channels.Commitments;
using NLightning.Domain.Channels.ValueObjects;
using NLightning.Domain.Crypto.ValueObjects;

namespace NLightning.Infrastructure.RemoteSigning;

public sealed record NativeChannelParty(ulong DustLimitSatoshis, ulong ReserveSatoshis,
                                       ulong HtlcMinimumMsat, ushort MaximumHtlcs,
                                       ulong MaximumInFlightMsat, ushort ToSelfDelay);

/// <summary>Owner-installed channel terms. These are never enrolled through the node signing RPC.</summary>
public sealed record NativeChannelEnrollment(NativeSignerBinding Binding, ChannelId ChannelId,
                                            TxId FundingTransactionId, ushort FundingOutputIndex,
                                            ulong FundingSatoshis, uint ChannelKeyIndex,
                                            ChannelBasepoints LocalBasepoints, ChannelBasepoints RemoteBasepoints,
                                            CompactPubKey RemoteNodePublicKey, bool IsInitiator,
                                            NativeChannelParty Local, NativeChannelParty Remote,
                                            bool HasAnchors, uint MinimumFeeratePerKw,
                                            uint MaximumFeeratePerKw, ulong? MaximumDustExposureMsat = null);

/// <summary>Exact authorized commitment content, before Bitcoin output rounding and dust trimming.</summary>
public sealed record NativeAuthorizedCommitment(CommitmentSide Holder, ulong Number, ulong LocalMsat,
                                               ulong RemoteMsat, uint FeeratePerKw,
                                               IReadOnlyList<SpecHtlc> Htlcs,
                                               CompactPubKey? RemotePerCommitmentPoint);

public interface INativeAuthorizedChannelStateStore
{
    NativeChannelEnrollment GetEnrollment(NativeSignerBinding binding, ChannelId channelId);
    NativeAuthorizedCommitment GetCommitment(NativeSignerBinding binding, ChannelId channelId,
                                            CommitmentSide holder, ulong number);
}

/// <summary>
/// Immutable owner-plane enrollment and commitment approvals in an independently administered database.
/// Automatic protocol-state advancement must validate peer messages before installing further commitments.
/// </summary>
public sealed class NativeAuthorizedChannelStateStore(Func<DbConnection> connectionFactory)
    : INativeAuthorizedChannelStateStore
{
    public void Initialize()
    {
        using var connection = Open();
        Run(connection, null, "CREATE TABLE IF NOT EXISTS NativeChannelEnrollments (NodeId TEXT NOT NULL, ChannelId TEXT NOT NULL, Enrollment TEXT NOT NULL, PRIMARY KEY (NodeId, ChannelId))");
        Run(connection, null, "CREATE TABLE IF NOT EXISTS NativeChannelCommitments (NodeId TEXT NOT NULL, ChannelId TEXT NOT NULL, Holder INTEGER NOT NULL, Number BIGINT NOT NULL, Content TEXT NOT NULL, PRIMARY KEY (NodeId, ChannelId, Holder, Number))");
    }

    public void Enroll(NativeChannelEnrollment enrollment)
    {
        using var connection = Open();
        Run(connection, null, "INSERT INTO NativeChannelEnrollments (NodeId, ChannelId, Enrollment) VALUES (@node, @channel, @content)",
            ("node", enrollment.Binding.NodeId), ("channel", enrollment.ChannelId.ToString()),
            ("content", JsonSerializer.Serialize(enrollment, SignerWire.Options)));
    }

    /// <summary>Owner-only exact-state approval. A node cannot assert balances, HTLCs or counters through this port.</summary>
    public void ApproveCommitment(NativeSignerBinding binding, ChannelId channelId,
                                  NativeAuthorizedCommitment commitment)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        _ = Enrollment(connection, transaction, binding, channelId);
        Run(connection, transaction, "INSERT INTO NativeChannelCommitments (NodeId, ChannelId, Holder, Number, Content) VALUES (@node, @channel, @holder, @number, @content)",
            ("node", binding.NodeId), ("channel", channelId.ToString()), ("holder", (int)commitment.Holder),
            ("number", checked((long)commitment.Number)),
            ("content", JsonSerializer.Serialize(commitment, SignerWire.Options)));
        transaction.Commit();
    }

    public NativeChannelEnrollment GetEnrollment(NativeSignerBinding binding, ChannelId channelId)
    {
        using var connection = Open();
        return Enrollment(connection, null, binding, channelId);
    }

    public NativeAuthorizedCommitment GetCommitment(NativeSignerBinding binding, ChannelId channelId,
                                                    CommitmentSide holder, ulong number)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        _ = Enrollment(connection, transaction, binding, channelId);
        using var command = Command(connection, transaction,
            "SELECT Content FROM NativeChannelCommitments WHERE NodeId = @node AND ChannelId = @channel AND Holder = @holder AND Number = @number",
            ("node", binding.NodeId), ("channel", channelId.ToString()), ("holder", (int)holder),
            ("number", checked((long)number)));
        var content = command.ExecuteScalar() as string
            ?? throw new UnauthorizedAccessException("This commitment has no independent signer authorization.");
        return JsonSerializer.Deserialize<NativeAuthorizedCommitment>(content, SignerWire.Options)
            ?? throw new InvalidDataException("Authorized channel state is missing.");
    }

    private static NativeChannelEnrollment Enrollment(DbConnection connection, DbTransaction? transaction,
                                                      NativeSignerBinding binding, ChannelId channelId)
    {
        using var command = Command(connection, transaction,
            "SELECT Enrollment FROM NativeChannelEnrollments WHERE NodeId = @node AND ChannelId = @channel",
            ("node", binding.NodeId), ("channel", channelId.ToString()));
        var content = command.ExecuteScalar() as string
            ?? throw new UnauthorizedAccessException("Channel is not enrolled for this node.");
        var enrollment = JsonSerializer.Deserialize<NativeChannelEnrollment>(content, SignerWire.Options)
            ?? throw new InvalidDataException("Channel enrollment is missing.");
        if (enrollment.Binding != binding || enrollment.ChannelId != channelId)
            throw new UnauthorizedAccessException("Channel enrollment belongs to another signing authority.");
        return enrollment;
    }

    private DbConnection Open()
    {
        var connection = connectionFactory();
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    private static void Run(DbConnection connection, DbTransaction? transaction, string sql,
                            params (string Name, object Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        command.ExecuteNonQuery();
    }

    private static DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql,
                                     params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@" + name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        return command;
    }
}
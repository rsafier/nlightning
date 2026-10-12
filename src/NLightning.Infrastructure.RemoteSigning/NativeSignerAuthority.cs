using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using NLightning.Domain.Signing;

namespace NLightning.Infrastructure.RemoteSigning;

/// <summary>Enrollment is installed by the owner administration plane, never supplied as authority by a node.</summary>
public sealed record NativeSignerBinding(string NodeId, string OwnerId, string SignerId, string Network,
                                         string PublicKey)
{
    public static NativeSignerBinding FromContext(NodeSigningContext context)
    {
        context.Validate();
        return new NativeSignerBinding(context.NodeId, context.OwnerId, context.SignerId, context.Network,
            context.NodePublicKey.ToString());
    }
}

/// <summary>Exact intent binds the owner approval to the complete typed operation payload, including its fee limits.</summary>
public sealed record NativeSignerIntent(string RequestId, uint Operation, byte[] Payload)
{
    public static NativeSignerIntent FromRequest(Signing.Contracts.SigningRequest request) =>
        new(request.RequestId, request.Operation, request.Payload.ToByteArray());
}
public sealed record NativeSignerExecution(string WriterId, long Epoch, string Checkpoint,
                                           string SignerCheckpoint = NativeSignerAuthority.InitialCheckpoint);
public sealed record NativeSignerAuthorityResult(byte[] Response, string Checkpoint, bool Replayed,
                                                string SignerCheckpoint = NativeSignerAuthority.InitialCheckpoint);

/// <summary>
/// Trusted transactional authority. The database and owner administration credentials must be independent of
/// node credentials and unavailable to node rollback. A local database alone does not provide that guarantee.
/// </summary>
public sealed partial class NativeSignerAuthority(Func<DbConnection> connectionFactory, TimeProvider? clock = null)
    : INativeWalletApprovalStore
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    [ThreadStatic] private static ExecutionReadScope? s_executionReadScope;
    public const string InitialCheckpoint = "initial";

    public void Initialize()
    {
        using var connection = Open();
        Run(connection, null, "CREATE TABLE IF NOT EXISTS NativeSignerAuthorities (NodeId TEXT PRIMARY KEY, Binding TEXT NOT NULL, Epoch BIGINT NOT NULL, WriterId TEXT NOT NULL, Checkpoint TEXT NOT NULL, SignerCheckpoint TEXT NOT NULL)");
        Run(connection, null, "CREATE TABLE IF NOT EXISTS NativeSignerApprovals (NodeId TEXT NOT NULL, RequestId TEXT NOT NULL, Fingerprint TEXT NOT NULL, Expires BIGINT NOT NULL, PRIMARY KEY (NodeId, RequestId))");
        Run(connection, null, "CREATE TABLE IF NOT EXISTS NativeSignerReceipts (NodeId TEXT NOT NULL, RequestId TEXT NOT NULL, Fingerprint TEXT NOT NULL, Response TEXT NOT NULL, Checkpoint TEXT NOT NULL, PRIMARY KEY (NodeId, RequestId))");
        InitializeWalletApprovals(connection);
    }

    /// <summary>Owner-only provisioning. Re-enrollment cannot replace an existing identity or its safety history.</summary>
    public void Enroll(NativeSignerBinding binding, string signerCheckpoint = InitialCheckpoint)
    {
        ValidateBinding(binding);
        using var connection = Open();
        Run(connection, null, "INSERT INTO NativeSignerAuthorities (NodeId, Binding, Epoch, WriterId, Checkpoint, SignerCheckpoint) VALUES (@node, @binding, 0, '', @checkpoint, @signercheckpoint)",
            ("node", binding.NodeId), ("binding", JsonSerializer.Serialize(binding)), ("checkpoint", InitialCheckpoint), ("signercheckpoint", signerCheckpoint));
    }

    /// <summary>Owner-only transfer; expected epoch prevents two supervisors both acquiring the same generation.</summary>
    public NativeSignerExecution AcquireWriter(NativeSignerBinding binding, long expectedEpoch, string writerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(writerId);
        using var connection = Open();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var state = ReadState(connection, transaction, binding);
        if (state.Epoch != expectedEpoch || expectedEpoch == long.MaxValue)
            throw new InvalidOperationException("Writer epoch changed or exhausted.");
        if (Run(connection, transaction, "UPDATE NativeSignerAuthorities SET Epoch = @next, WriterId = @writer WHERE NodeId = @node AND Epoch = @epoch AND Checkpoint = @checkpoint",
                ("next", expectedEpoch + 1), ("writer", writerId), ("node", binding.NodeId),
                ("epoch", expectedEpoch), ("checkpoint", state.Checkpoint)) != 1)
            throw new InvalidOperationException("Writer ownership changed.");
        transaction.Commit();
        return new NativeSignerExecution(writerId, expectedEpoch + 1, state.Checkpoint, state.SignerCheckpoint);
    }

    /// <summary>
    /// Loads the current checkpoints for an already installed writer after verifying its local safety history.
    /// This does not acquire ownership, reset history or reconcile a divergent signer journal.
    /// </summary>
    public NativeSignerExecution LoadCurrentExecution(NativeSignerBinding binding, string writerId, long epoch,
                                                       Func<string> readLocalCheckpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(writerId);
        if (epoch <= 0) throw new ArgumentOutOfRangeException(nameof(epoch));
        ArgumentNullException.ThrowIfNull(readLocalCheckpoint);
        using var connection = Open();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var state = ReadState(connection, transaction, binding);
        var execution = new NativeSignerExecution(writerId, epoch, state.Checkpoint, state.SignerCheckpoint);
        FenceWriter(connection, transaction, binding, execution, readLocalCheckpoint);
        transaction.Commit();
        return execution;
    }

    /// <summary>Owner-only authorization for an immutable operation, not a caller-supplied spending allowance.</summary>
    public void Approve(NativeSignerBinding binding, NativeSignerIntent intent, DateTimeOffset expires)
    {
        ValidateIntent(intent);
        if (expires <= _clock.GetUtcNow()) throw new ArgumentException("Approval has expired.");
        using var connection = Open();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        ReadState(connection, transaction, binding);
        Run(connection, transaction, "INSERT INTO NativeSignerApprovals (NodeId, RequestId, Fingerprint, Expires) VALUES (@node, @request, @fingerprint, @expires)",
            ("node", binding.NodeId), ("request", intent.RequestId), ("fingerprint", Fingerprint(binding, intent)),
            ("expires", expires.ToUnixTimeSeconds()));
        transaction.Commit();
    }

    /// <summary>
    /// The callback must perform signer-owned semantic validation before key use. It executes while the authority
    /// row is fenced by a transactional write. Commits are ordered before or after ownership transfer;
    /// response delivery and publication require separate fencing at their own authoritative boundary.
    /// An exact completed receipt is replayable without executing the callback again or requiring renewed approval.
    /// </summary>
    public NativeSignerAuthorityResult Execute(NativeSignerBinding binding, NativeSignerExecution execution,
                                               NativeSignerIntent intent, Func<byte[]> validateAndExecute,
                                               Func<string>? signerCheckpoint = null, Action? validateReplay = null,
                                               Action<byte[]>? validateReplayResponse = null)
    {
        ValidateIntent(intent);
        using var connection = Open();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var state = FenceWriter(connection, transaction, binding, execution, signerCheckpoint);
        using var readScope = new ExecutionReadScope(this, connection, transaction);
        var fingerprint = Fingerprint(binding, intent);
        using (var receipt = Command(connection, transaction,
                   "SELECT Fingerprint, Response, Checkpoint FROM NativeSignerReceipts WHERE NodeId = @node AND RequestId = @request",
                   ("node", binding.NodeId), ("request", intent.RequestId)))
        using (var reader = receipt.ExecuteReader())
        {
            if (reader.Read())
            {
                if (reader.GetString(0) != fingerprint) throw new InvalidOperationException("Request identity was reused.");
                var result = new NativeSignerAuthorityResult(Convert.FromBase64String(reader.GetString(1)), state.Checkpoint, true, state.SignerCheckpoint);
                reader.Close();
                validateReplay?.Invoke();
                validateReplayResponse?.Invoke(result.Response.ToArray());
                transaction.Commit();
                return result;
            }
        }
        if (state.Checkpoint != execution.Checkpoint)
            throw new InvalidOperationException("Signer checkpoint is stale; reconcile before signing.");
        using (var approval = Command(connection, transaction,
                   "SELECT Fingerprint, Expires FROM NativeSignerApprovals WHERE NodeId = @node AND RequestId = @request",
                   ("node", binding.NodeId), ("request", intent.RequestId)))
        using (var reader = approval.ExecuteReader())
        {
            if (!reader.Read() || reader.GetString(0) != fingerprint || reader.GetInt64(1) <= _clock.GetUtcNow().ToUnixTimeSeconds())
                throw new UnauthorizedAccessException("An unexpired owner approval for this exact intent is required.");
        }
        var response = validateAndExecute();
        var nextSignerCheckpoint = signerCheckpoint?.Invoke() ?? state.SignerCheckpoint;
        var checkpoint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new { Previous = state.Checkpoint, fingerprint, Response = Convert.ToBase64String(response) })));
        Run(connection, transaction, "INSERT INTO NativeSignerReceipts (NodeId, RequestId, Fingerprint, Response, Checkpoint) VALUES (@node, @request, @fingerprint, @response, @checkpoint)",
            ("node", binding.NodeId), ("request", intent.RequestId), ("fingerprint", fingerprint),
            ("response", Convert.ToBase64String(response)), ("checkpoint", checkpoint));
        if (Run(connection, transaction, "UPDATE NativeSignerAuthorities SET Checkpoint = @next, SignerCheckpoint = @signercheckpoint WHERE NodeId = @node AND Epoch = @epoch AND WriterId = @writer AND Checkpoint = @previous",
                ("next", checkpoint), ("signercheckpoint", nextSignerCheckpoint), ("node", binding.NodeId), ("epoch", execution.Epoch),
                ("writer", execution.WriterId), ("previous", state.Checkpoint)) != 1)
            throw new InvalidOperationException("Authority changed before signer commit.");
        transaction.Commit();
        return new NativeSignerAuthorityResult(response, checkpoint, false, nextSignerCheckpoint);
    }

    /// <summary>Reads reconciliation state under the same execution fence; this callback must not sign or mutate safety state.</summary>
    public T RequireCurrentWriter<T>(NativeSignerBinding binding, NativeSignerExecution execution,
                                      Func<string> signerCheckpoint, Func<T> readReconciliation)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        FenceWriter(connection, transaction, binding, execution, signerCheckpoint);
        var result = readReconciliation();
        transaction.Commit();
        return result;
    }

    private static AuthorityState FenceWriter(DbConnection connection, DbTransaction transaction,
                                              NativeSignerBinding binding, NativeSignerExecution execution,
                                              Func<string>? signerCheckpoint)
    {
        var state = ReadState(connection, transaction, binding);
        if (state.Epoch != execution.Epoch || state.WriterId != execution.WriterId)
            throw new InvalidOperationException("Stale writer cannot execute or recover a signer request.");
        // Obtain a write fence before any private-key operation; transfer is ordered before or after this commit.
        if (Run(connection, transaction, "UPDATE NativeSignerAuthorities SET WriterId = @writer WHERE NodeId = @node AND Epoch = @epoch AND WriterId = @writer",
                ("writer", execution.WriterId), ("node", binding.NodeId), ("epoch", execution.Epoch)) != 1)
            throw new InvalidOperationException("Writer ownership changed.");
        if (execution.SignerCheckpoint != state.SignerCheckpoint
         || signerCheckpoint is not null && signerCheckpoint() != state.SignerCheckpoint)
            throw new InvalidOperationException("Local signer history does not match independent authority.");
        return state;
    }

    internal static string Fingerprint(NativeSignerBinding binding, NativeSignerIntent intent) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { binding, intent })));

    private static void ValidateIntent(NativeSignerIntent intent)
    {
        if (!Guid.TryParseExact(intent.RequestId, "N", out _)) throw new ArgumentException("Intent requires a stable GUID request identity.");
        if (intent.Payload.Length > RemoteSignerOptions.MaxMessageBytes) throw new ArgumentException("Intent exceeds the signer message limit.");
    }

    private static void ValidateBinding(NativeSignerBinding binding)
    {
        NodeSigningContext.ValidateIdentifier(binding.NodeId, nameof(binding.NodeId));
        NodeSigningContext.ValidateIdentifier(binding.OwnerId, nameof(binding.OwnerId));
        NodeSigningContext.ValidateIdentifier(binding.SignerId, nameof(binding.SignerId));
        _ = Domain.Protocol.ValueObjects.BitcoinNetwork.Resolve(binding.Network);
        _ = new Domain.Crypto.ValueObjects.CompactPubKey(Convert.FromHexString(binding.PublicKey));
    }

    private static AuthorityState ReadState(DbConnection connection, DbTransaction? transaction, NativeSignerBinding binding)
    {
        ValidateBinding(binding);
        using var command = Command(connection, transaction,
            "SELECT Binding, Epoch, WriterId, Checkpoint, SignerCheckpoint FROM NativeSignerAuthorities WHERE NodeId = @node", ("node", binding.NodeId));
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetString(0) != JsonSerializer.Serialize(binding))
            throw new UnauthorizedAccessException("Node, owner, signer, network or public identity does not match enrollment.");
        return new AuthorityState(reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4));
    }

    private DbConnection Open()
    {
        var connection = connectionFactory();
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    private static int Run(DbConnection connection, DbTransaction? transaction, string sql,
                           params (string Name, object Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        return command.ExecuteNonQuery();
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

    private ExecutionReadScope? FindExecutionReadScope()
    {
        for (var scope = s_executionReadScope; scope is not null; scope = scope.Previous)
            if (ReferenceEquals(scope.Authority, this)) return scope;
        return null;
    }

    // Callbacks execute synchronously. Do not flow a live database transaction into asynchronous child work.
    private sealed class ExecutionReadScope : IDisposable
    {
        public NativeSignerAuthority Authority { get; }
        public DbConnection Connection { get; }
        public DbTransaction Transaction { get; }
        public ExecutionReadScope? Previous { get; }

        public ExecutionReadScope(NativeSignerAuthority authority, DbConnection connection, DbTransaction transaction)
        {
            Authority = authority;
            Connection = connection;
            Transaction = transaction;
            Previous = s_executionReadScope;
            s_executionReadScope = this;
        }

        public void Dispose() => s_executionReadScope = Previous;
    }

    private sealed record AuthorityState(long Epoch, string WriterId, string Checkpoint, string SignerCheckpoint);
}
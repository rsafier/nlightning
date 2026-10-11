using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NLightning.Domain.Channels.ValueObjects;
using NLightning.Domain.Node.Options;
using NLightning.Domain.Protocol.Constants;
using NLightning.Infrastructure.Bitcoin.Builders;
using NLightning.Infrastructure.Bitcoin.Managers;
using NLightning.Infrastructure.Bitcoin.Services;
using NLightning.Infrastructure.Bitcoin.Signers;
using NLightning.Infrastructure.RemoteSigning;
using NLightning.Infrastructure.Repositories.Memory;
using NLightning.Signing.Contracts;

namespace NLightning.RemoteSigning.Tests;

public sealed class NativeAuthorizedSignerExecutorTests
{
    [Fact]
    public void Given_IndependentValidatorRejects_When_NodeIsAuthenticated_Then_JournalDoesNotChange()
    {
        using var fixture = new Fixture();
        var intent = fixture.ApprovedRequest();
        var before = fixture.Journal.GetCheckpointDigest();
        var executor = fixture.Executor(new Validator { Reject = true });
        Assert.Throws<UnauthorizedAccessException>(() => executor.Execute(intent, "writer-a", 1,
            () => throw new Exception("Must not execute")));
        Assert.Equal(before, fixture.Journal.GetCheckpointDigest());
    }

    [Fact]
    public void Given_JournalCommitBeforeAuthorityFailure_When_NewRequestArrives_Then_ExecutorRemainsBlocked()
    {
        using var fixture = new Fixture();
        using (var connection = fixture.Connection())
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER FailReceipt BEFORE INSERT ON NativeSignerReceipts BEGIN SELECT RAISE(ABORT, 'injected authority commit failure'); END";
            command.ExecuteNonQuery();
        }
        var intent = fixture.ApprovedRequest();
        var executor = fixture.Executor(new Validator());
        Assert.Throws<SqliteException>(() => executor.Execute(intent, "writer-a", 1, () => fixture.MutateJournal(intent)));
        var next = fixture.ApprovedRequest();
        Assert.Throws<InvalidOperationException>(() => executor.Execute(next, "writer-a", 1,
            () => throw new Exception("Must not execute")));
        Assert.Throws<InvalidDataException>(() => fixture.Executor(new Validator()));
    }

    [Fact]
    public void Given_CompletedRequest_When_WriterTransfers_Then_StaleReplayCannotReturnTheReceipt()
    {
        using var fixture = new Fixture();
        var intent = fixture.ApprovedRequest();
        var executor = fixture.Executor(new Validator());
        var saved = executor.Execute(intent, "writer-a", 1, () => fixture.MutateJournal(intent));
        var nextWriter = fixture.Authority.AcquireWriter(fixture.Binding, 1, "writer-b");
        Assert.Throws<InvalidOperationException>(() => executor.Execute(intent, "writer-a", 1,
            () => throw new Exception("Must not execute")));
        var resumed = new NativeAuthorizedSignerExecutor(fixture.Authority, fixture.Binding, fixture.Journal,
            nextWriter, new Validator { Reject = true }, fixture.Checkpoints);
        var result = resumed.Execute(intent, "writer-b", 2, () => throw new Exception("Must not execute"));
        Assert.True(result.Replayed);
        Assert.Equal(saved.Response, result.Response);
    }

    [Fact]
    public void Given_WrongAuthenticatedWriter_When_Executing_Then_NoPrivateOperationOccurs()
    {
        using var fixture = new Fixture();
        var executor = fixture.Executor(new Validator());
        Assert.Throws<UnauthorizedAccessException>(() => executor.Execute(fixture.ApprovedRequest(), "writer-b", 1,
            () => throw new Exception("Must not execute")));
    }

    [Fact]
    public void Given_RetiredChannel_When_AuthorityReceiptIsReplayed_Then_LocalInvalidationBlocksTheResult()
    {
        using var fixture = new Fixture();
        var executor = fixture.Executor(new Validator());
        var original = fixture.ApprovedRequest();
        executor.Execute(original, "writer-a", 1, () => fixture.MutateJournal(original));
        var retired = fixture.ApprovedRequest(SignerOperations.UnregisterChannel);
        executor.Execute(retired, "writer-a", 1, () => fixture.MutateJournal(retired));
        Assert.Equal(RequestOutcome.Invalidated, fixture.Journal.Reconcile(original).Outcome);
        Assert.Throws<InvalidOperationException>(() => executor.Execute(original, "writer-a", 1,
            () => throw new Exception("Must not execute")));
    }

    [Fact]
    public void Given_AuxiliarySafetyStoreRolledBack_When_MainJournalStillMatches_Then_ExecutionIsBlocked()
    {
        var initial = System.Security.Cryptography.SHA256.HashData("initial auxiliary history"u8);
        var auxiliary = initial;
        using var fixture = new Fixture(() => auxiliary);
        var executor = fixture.Executor(new Validator());
        var original = fixture.ApprovedRequest();
        executor.Execute(original, "writer-a", 1, () =>
        {
            auxiliary = System.Security.Cryptography.SHA256.HashData("consumed nonce"u8);
            return fixture.MutateJournal(original);
        });
        var main = fixture.Journal.GetCheckpointDigest();
        auxiliary = initial;
        var next = fixture.ApprovedRequest();
        Assert.Throws<InvalidOperationException>(() => executor.Execute(next, "writer-a", 1,
            () => throw new Exception("Must not execute")));
        Assert.Equal(main, fixture.Journal.GetCheckpointDigest());
    }

    [Fact]
    public void Given_WriterTransfer_When_Reconciling_Then_StaleWriterCannotReadReceipt()
    {
        using var fixture = new Fixture();
        var executor = fixture.Executor(new Validator());
        var request = fixture.ApprovedRequest();
        executor.Execute(request, "writer-a", 1, () => fixture.MutateJournal(request));
        fixture.Authority.AcquireWriter(fixture.Binding, 1, "writer-b");
        var read = false;
        Assert.Throws<InvalidOperationException>(() => executor.AuthorizeReconciliation(request, "writer-a", 1, () =>
        { read = true; return fixture.Journal.Reconcile(request); }));
        Assert.False(read);
    }

    internal sealed class Validator : INativeSignerRequestValidator
    {
        public bool Reject { get; init; }
        public void ValidateReplay(NativeSignerBinding binding, NativeSignerIntent intent) { }
        public void Validate(NativeSignerBinding binding, NativeSignerIntent intent)
        {
            if (Reject) throw new UnauthorizedAccessException("Independent validation rejected node state.");
        }
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "rs-authorized-" + Guid.NewGuid().ToString("N"));
        private readonly SecureKeyManager _keys;
        private readonly LocalLightningSigner _signer;
        private readonly UtxoMemoryRepository _wallet = new();
        private readonly NativeSignerExecution _execution;
        public DurableSignerState Journal { get; }
        public NativeSignerAuthority Authority { get; }
        public NativeSignerBinding Binding { get; }
        public NativeSignerSafetyCheckpointSet Checkpoints { get; }

        public Fixture(Func<byte[]>? auxiliary = null)
        {
            Directory.CreateDirectory(_directory);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            _keys = SecureKeyManager.FromSeed(Enumerable.Repeat((byte)1, 32).ToArray(), NetworkConstants.Regtest, _ => { });
            _signer = new LocalLightningSigner(new FundingOutputBuilder(), new KeyDerivationService(),
                NullLogger<LocalLightningSigner>.Instance, new NodeOptions { BitcoinNetwork = NetworkConstants.Regtest },
                _keys, _wallet);
            Journal = new DurableSignerState(_signer, Path.Combine(_directory, "journal"), "regtest");
            Binding = new NativeSignerBinding("node-a", "owner-a", "signer-a", "regtest", _signer.GetNodePublicKey().ToString());
            Checkpoints = auxiliary is null ? new NativeSignerSafetyCheckpointSet(Binding, Journal)
                : new NativeSignerSafetyCheckpointSet(Binding, Journal, new NativeSignerCheckpointSource("auxiliary", auxiliary));
            Authority = new NativeSignerAuthority(Connection);
            Authority.Initialize();
            Authority.Enroll(Binding, Checkpoints.GetCheckpoint());
            _execution = Authority.AcquireWriter(Binding, 0, "writer-a");
        }

        public SqliteConnection Connection() => new("Data Source=" + Path.Combine(_directory, "authority.db"));
        public NativeAuthorizedSignerExecutor Executor(Validator validator) => new(Authority, Binding, Journal, _execution, validator, Checkpoints);
        public const string Token = "authorized-native-signer-test-token-0000000000000000";
        public const string WriterToken = "writer-a-native-execution-credential-000000000000000";
        public SignerRpcService Service(bool reject = false, NativeSignerExecution? execution = null,
                                       string writerToken = WriterToken, bool installWriterVerifier = true) => new(_signer, _keys, _wallet, new RemoteSignerOptions
                                       {
                                           SocketPath = Path.Combine(_directory, "signer.sock"),
                                           AuthToken = Token,
                                           Network = Binding.Network,
                                           NodeId = Binding.NodeId,
                                           OwnerId = Binding.OwnerId,
                                           SignerId = Binding.SignerId
                                       }, Journal, authorizedExecutor: new NativeAuthorizedSignerExecutor(Authority, Binding, Journal,
            execution ?? _execution, new Validator { Reject = reject }, Checkpoints),
            writerCredentials: installWriterVerifier
                ? new NativeSignerWriterCredential(Binding, execution ?? _execution, writerToken) : null);
        public SigningRequest ApprovedRequest(uint operation = SignerOperations.MarkDataLoss) => Request(operation, true);
        public SigningRequest Request(uint operation = SignerOperations.MarkDataLoss, bool approve = false)
        {
            var request = new SigningRequest
            {
                Version = 1,
                RequestId = Guid.NewGuid().ToString("N"),
                Operation = operation,
                Payload = ByteString.CopyFrom(SignerWire.Encode([new ChannelId(new byte[32])])),
                NodeId = Binding.NodeId,
                OwnerId = Binding.OwnerId,
                SignerId = Binding.SignerId,
                Network = Binding.Network
            };
            if (approve) Authority.Approve(Binding, NativeSignerIntent.FromRequest(request), DateTimeOffset.UtcNow.AddMinutes(5));
            return request;
        }
        public byte[] MutateJournal(SigningRequest request)
        {
            var result = Journal.ExecuteRequest(request, SignerWire.Decode(request.Payload.ToByteArray()),
                () => throw new Exception("State change uses journal-owned dispatch."));
            return new SigningResponse { Payload = ByteString.CopyFrom(SignerWire.Encode(result)) }.ToByteArray();
        }
        public void Dispose()
        {
            Journal.Dispose();
            _keys.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
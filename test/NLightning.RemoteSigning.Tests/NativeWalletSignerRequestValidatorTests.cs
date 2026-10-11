using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using NLightning.Domain.Bitcoin.Enums;
using NLightning.Domain.Bitcoin.Wallet.Models;
using NLightning.Domain.Channels.ValueObjects;
using NLightning.Domain.Money;
using NLightning.Domain.Node.Options;
using NLightning.Domain.Protocol.Constants;
using NLightning.Infrastructure.Bitcoin.Builders;
using NLightning.Infrastructure.Bitcoin.Managers;
using NLightning.Infrastructure.Bitcoin.Services;
using NLightning.Infrastructure.Bitcoin.Signers;
using NLightning.Infrastructure.RemoteSigning;
using NLightning.Infrastructure.Repositories.Memory;
using NLightning.Signing.Contracts;
using SignedTransaction = NLightning.Domain.Bitcoin.ValueObjects.SignedTransaction;
using TxId = NLightning.Domain.Bitcoin.ValueObjects.TxId;

namespace NLightning.RemoteSigning.Tests;

public sealed class NativeWalletSignerRequestValidatorTests
{
    [Theory]
    [InlineData(AddressType.P2Wpkh, SqliteCacheMode.Private)]
    [InlineData(AddressType.P2Tr, SqliteCacheMode.Private)]
    [InlineData(AddressType.P2Wpkh, SqliteCacheMode.Shared)]
    [InlineData(AddressType.P2Tr, SqliteCacheMode.Shared)]
    public void Given_IndependentApprovalAndEvidence_When_Signing_Then_SignatureVerifiesAgainstTheRealPreviousOutput(AddressType addressType,
                                                                                                                   SqliteCacheMode cacheMode)
    {
        using var fixture = new Fixture(addressType, cacheMode);
        var request = fixture.Request();
        fixture.Approve(request);
        var result = fixture.Executor().Execute(request, "writer-a", 1, () => fixture.Sign(request));
        var signed = fixture.Signed(result.Response);
        var tx = Transaction.Load(signed.RawTxBytes, Network.RegTest);
        var validation = tx.CreateValidator([new TxOut(Money.Satoshis(1000), new Script(fixture.Script))]);
        Assert.Null(validation.ValidateInput(0).Error);
        Assert.Equal(1, fixture.PrivateCalls);
        Assert.Equal(1, fixture.Evidence.Batches);
        Assert.Equal(1, fixture.Evidence.Completions);
        var replay = fixture.Executor().Execute(request, "writer-a", 1,
            () => throw new Exception("Replay must not sign."));
        Assert.True(replay.Replayed);
        Assert.Equal(result.Response, replay.Response);
        Assert.Equal(1, fixture.PrivateCalls);
        Assert.Equal(1, fixture.Evidence.Batches);
    }

    [Fact]
    public void Given_OnlyGenericExactApproval_When_Signing_Then_NoWalletContextOrKeysAreUsed()
    {
        using var fixture = new Fixture();
        var request = fixture.Request();
        fixture.Authority.Approve(fixture.Binding, NativeSignerIntent.FromRequest(request), fixture.Clock.GetUtcNow().AddMinutes(1));
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Executor().Execute(request, "writer-a", 1, () => fixture.Sign(request)));
        Assert.Equal(0, fixture.PrivateCalls);
        Assert.Equal(0, fixture.Evidence.Batches);
    }

    [Theory]
    [InlineData("amount")]
    [InlineData("fractional-amount")]
    [InlineData("address-index")]
    [InlineData("change-flag")]
    [InlineData("address-type")]
    [InlineData("address")]
    [InlineData("account")]
    [InlineData("derivation-override")]
    [InlineData("channel-lock")]
    [InlineData("used-input")]
    [InlineData("missing-utxo")]
    [InlineData("duplicate-utxo")]
    [InlineData("missing-reservation")]
    [InlineData("duplicate-reservation")]
    [InlineData("wrong-reservation")]
    [InlineData("txid")]
    [InlineData("destination")]
    [InlineData("fee")]
    [InlineData("external-inputs")]
    [InlineData("silent-payment")]
    public void Given_ExactPayloadApproval_When_SnapshotOrSpendingSemanticsAreFabricated_Then_KeysAndWalletAreUntouched(string attack)
    {
        using var fixture = new Fixture();
        var request = fixture.Request(attack);
        fixture.Approve(request);
        var before = fixture.Journal.GetCheckpointDigest();
        Assert.ThrowsAny<Exception>(() => fixture.Executor().Execute(request, "writer-a", 1, () => fixture.Sign(request)));
        Assert.Equal(0, fixture.PrivateCalls);
        Assert.Equal(before, fixture.Journal.GetCheckpointDigest());
        Assert.False(fixture.Wallet.TryGetUtxo(fixture.InputId, 0, out _));
    }

    [Theory]
    [InlineData("spent")]
    [InlineData("owner")]
    [InlineData("amount")]
    [InlineData("script")]
    [InlineData("tip-change")]
    public void Given_ApprovedSnapshot_When_IndependentEvidenceRejectsIt_Then_NoSigningOccurs(string attack)
    {
        using var fixture = new Fixture();
        var request = fixture.Request();
        fixture.Approve(request);
        fixture.Evidence.Attack = attack;
        Assert.ThrowsAny<Exception>(() => fixture.Executor().Execute(request, "writer-a", 1, () => fixture.Sign(request)));
        Assert.Equal(0, fixture.PrivateCalls);
    }

    [Theory]
    [InlineData(SqliteCacheMode.Private)]
    [InlineData(SqliteCacheMode.Shared)]
    public void Given_CompletedSpend_When_InputIsSpentApprovalExpiresAndWriterRestarts_Then_ExactReceiptReplaysWithoutEvidenceOrSigning(SqliteCacheMode cacheMode)
    {
        using var fixture = new Fixture(cacheMode: cacheMode);
        var request = fixture.Request();
        fixture.Approve(request);
        var completed = fixture.Executor().Execute(request, "writer-a", 1, () => fixture.Sign(request));
        fixture.Evidence.Attack = "spent";
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        fixture.RestartJournal();
        var calls = fixture.Evidence.Batches;
        var replay = fixture.Executor().Execute(request, "writer-a", 1,
            () => throw new Exception("Replay must not sign."));
        Assert.True(replay.Replayed);
        Assert.Equal(completed.Response, replay.Response);
        Assert.Equal(calls, fixture.Evidence.Batches);
        Assert.Equal(1, fixture.PrivateCalls);
        Assert.Equal(completed.Checkpoint, replay.Checkpoint);
    }

    [Theory]
    [InlineData(SqliteCacheMode.Private)]
    [InlineData(SqliteCacheMode.Shared)]
    public void Given_CompletedLocalReceipt_When_IndependentReceiptBytesDiffer_Then_ReplayRejectsBeforeReturningOrCommitting(SqliteCacheMode cacheMode)
    {
        using var fixture = new Fixture(cacheMode: cacheMode);
        var request = fixture.Request();
        fixture.Approve(request);
        var executor = fixture.Executor();
        executor.Execute(request, "writer-a", 1, () => fixture.Sign(request));
        using (var connection = fixture.Connection())
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE NativeSignerReceipts SET Response = @response WHERE NodeId = @node AND RequestId = @request";
            command.Parameters.AddWithValue("response", Convert.ToBase64String(new byte[] { 1, 2, 3 }));
            command.Parameters.AddWithValue("node", fixture.Binding.NodeId);
            command.Parameters.AddWithValue("request", request.RequestId);
            command.ExecuteNonQuery();
            command.Parameters.Clear();
            command.CommandText = "CREATE TABLE ReplayWriterAudit (Marker INTEGER NOT NULL)";
            command.ExecuteNonQuery();
            command.CommandText = "CREATE TRIGGER AuditReplayWriter AFTER UPDATE ON NativeSignerAuthorities BEGIN INSERT INTO ReplayWriterAudit (Marker) VALUES (1); END";
            command.ExecuteNonQuery();
        }
        var before = fixture.Journal.GetCheckpointDigest();
        Assert.Throws<InvalidDataException>(() => executor.Execute(request, "writer-a", 1,
            () => throw new Exception("Replay must not sign.")));
        Assert.Equal(before, fixture.Journal.GetCheckpointDigest());
        Assert.Equal(1, fixture.PrivateCalls);
        Assert.Equal(request.RequestId, fixture.Authority.GetWalletApproval(fixture.Binding,
            NativeSignerIntent.FromRequest(request)).RequestId);
        using var verification = fixture.Connection();
        verification.Open();
        using var audit = verification.CreateCommand();
        audit.CommandText = "SELECT COUNT(*) FROM ReplayWriterAudit";
        Assert.Equal(0L, (long)audit.ExecuteScalar()!);
    }

    [Fact]
    public void Given_UnexpiredApprovalThatLaterExpires_When_FirstSigningStarts_Then_FreshExecutionDoesNotUseEvidenceOrKeys()
    {
        using var fixture = new Fixture();
        var request = fixture.Request();
        fixture.Approve(request);
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Executor().Execute(request, "writer-a", 1, () => fixture.Sign(request)));
        Assert.Equal(0, fixture.PrivateCalls);
        Assert.Equal(0, fixture.Evidence.Batches);
    }

    [Theory]
    [InlineData(SqliteCacheMode.Private)]
    [InlineData(SqliteCacheMode.Shared)]
    public void Given_ApprovalExpiresDuringIndependentEvidence_When_ValidationCompletes_Then_NoWalletContextOrKeysAreUsed(SqliteCacheMode cacheMode)
    {
        // Arrange
        using var fixture = new Fixture(cacheMode: cacheMode);
        var request = fixture.Request();
        fixture.Approve(request);
        var before = fixture.Journal.GetCheckpointDigest();
        fixture.Evidence.BeforeCompletion = () => fixture.Clock.Advance(TimeSpan.FromMinutes(2));

        // Act
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Executor().Execute(request, "writer-a", 1,
            () => fixture.Sign(request)));

        // Assert
        Assert.Equal(1, fixture.Evidence.Batches);
        Assert.Equal(1, fixture.Evidence.Completions);
        Assert.Equal(0, fixture.PrivateCalls);
        Assert.Equal(before, fixture.Journal.GetCheckpointDigest());
        Assert.False(fixture.Wallet.TryGetUtxo(fixture.InputId, 0, out _));
        Assert.Equal(request.RequestId, fixture.Authority.GetWalletApproval(fixture.Binding,
            NativeSignerIntent.FromRequest(request)).RequestId);
    }

    [Fact]
    public void Given_ApprovedUninstalledDerivation_When_Validating_Then_TheOwnerRecordCannotSupplyKeys()
    {
        using var fixture = new Fixture();
        var request = fixture.Request("address-index");
        fixture.Authority.ApproveWalletIntent(fixture.Binding, NativeSignerIntent.FromRequest(request),
            new NativeWalletApproval(fixture.Reservation,
                [new NativeWalletApprovedInput(fixture.InputId.ToString(), 0, fixture.Locator with { Index = 99 })],
                new NativeWalletSpendingIntent([new NativeApprovedOutput(fixture.Destination, 500)], 10)),
            fixture.Clock.GetUtcNow().AddMinutes(1));
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Executor().Execute(request, "writer-a", 1, () => fixture.Sign(request)));
        Assert.Equal(0, fixture.PrivateCalls);
        Assert.Equal(0, fixture.Evidence.Batches);
    }

    [Theory]
    [InlineData(SignerOperations.SignWalletTransaction)]
    [InlineData(SignerOperations.SignWalletTransaction2)]
    [InlineData(SignerOperations.SignNodeMessage)]
    public void Given_UnimplementedPurpose_When_ValidatingOrReplaying_Then_DefaultDenialPrecedesEvidence(uint operation)
    {
        using var fixture = new Fixture();
        var intent = NativeSignerIntent.FromRequest(fixture.Request()) with { Operation = operation };
        Assert.Throws<NotSupportedException>(() => fixture.Validator.Validate(fixture.Binding, intent));
        Assert.Throws<NotSupportedException>(() => fixture.Validator.ValidateReplay(fixture.Binding, intent));
        Assert.Equal(0, fixture.Evidence.Batches);
    }

    [Fact]
    public void Given_ClaimedInput_When_AnotherRequestIsApproved_Then_SecondApprovalAndItsMetadataRollBack()
    {
        using var fixture = new Fixture();
        var original = fixture.Request();
        fixture.Approve(original);
        var other = fixture.Request();
        Assert.Throws<SqliteException>(() => fixture.Approve(other));
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Authority.GetWalletApproval(fixture.Binding, NativeSignerIntent.FromRequest(other)));
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Authority.Execute(fixture.Binding, fixture.Execution(),
            NativeSignerIntent.FromRequest(other), () => throw new Exception("Second approval must not survive.")));
        Assert.Equal(original.RequestId, fixture.Authority.GetWalletApproval(fixture.Binding, NativeSignerIntent.FromRequest(original)).RequestId);
    }

    [Fact]
    public void Given_ExistingGenericApproval_When_TypedApprovalInsertFails_Then_NoInputClaimOrTypedMetadataSurvives()
    {
        using var fixture = new Fixture();
        var original = fixture.Request();
        fixture.Authority.Approve(fixture.Binding, NativeSignerIntent.FromRequest(original), fixture.Clock.GetUtcNow().AddMinutes(1));
        Assert.Throws<SqliteException>(() => fixture.Approve(original));
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Authority.GetWalletApproval(fixture.Binding, NativeSignerIntent.FromRequest(original)));
        var next = fixture.Request();
        fixture.Approve(next);
        Assert.Equal(next.RequestId, fixture.Authority.GetWalletApproval(fixture.Binding, NativeSignerIntent.FromRequest(next)).RequestId);
    }

    [Fact]
    public void Given_InstalledOwnerMetadata_When_CallerMutatesItsArrays_Then_StoredApprovalAndRegistryRemainUnchanged()
    {
        using var fixture = new Fixture();
        var request = fixture.Request();
        var destination = fixture.Destination.ToArray();
        var inputs = new[] { new NativeWalletApprovedInput(fixture.InputId.ToString(), 0, fixture.Locator) };
        var approval = new NativeWalletApproval(fixture.Reservation, inputs,
            new NativeWalletSpendingIntent([new NativeApprovedOutput(destination, 500)], 10));
        fixture.Authority.ApproveWalletIntent(fixture.Binding, NativeSignerIntent.FromRequest(request), approval, fixture.Clock.GetUtcNow().AddMinutes(1));
        destination[0] ^= 1;
        inputs[0] = inputs[0] with { Derivation = fixture.Locator with { Index = 99 } };
        var returned = fixture.Registry.GetScript(fixture.Binding, fixture.Locator);
        returned[0] ^= 1;
        fixture.Validator.Validate(fixture.Binding, NativeSignerIntent.FromRequest(request));
        Assert.Equal(fixture.Script, fixture.Registry.GetScript(fixture.Binding, fixture.Locator));
    }

    [Fact]
    public void Given_ApprovedRequest_When_PayloadOrOwnerChanges_Then_ApprovalCannotBeRelabeled()
    {
        using var fixture = new Fixture();
        var request = fixture.Request();
        fixture.Approve(request);
        var changed = request.Clone();
        changed.Payload = ByteString.CopyFrom(SignerWire.Encode([1]));
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Validator.Validate(fixture.Binding, NativeSignerIntent.FromRequest(changed)));
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Validator.Validate(fixture.Binding with { OwnerId = "other-owner" }, NativeSignerIntent.FromRequest(request)));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "native-wallet-purpose-" + Guid.NewGuid().ToString("N"));
        private readonly SecureKeyManager _keys;
        private readonly LocalLightningSigner _signer;
        private readonly SqliteCacheMode _cacheMode;
        public Clock Clock { get; } = new();
        public NativeSignerBinding Binding { get; }
        public NativeSignerAuthority Authority { get; }
        public DurableSignerState Journal { get; private set; }
        public NativeSignerSafetyCheckpointSet Checkpoints { get; private set; }
        public NativeSignerWalletScriptRegistry Registry { get; }
        public NativeWalletSignerRequestValidator Validator { get; }
        public ChainEvidence Evidence { get; }
        public UtxoMemoryRepository Wallet { get; } = new();
        public Guid Reservation { get; } = Guid.NewGuid();
        public TxId InputId { get; } = new(uint256.Parse(new string('a', 64)).ToBytes());
        public NativeWalletKeyLocator Locator { get; }
        public byte[] Script { get; }
        public byte[] Destination { get; } = NBitcoin.Script.FromHex("0014" + new string('2', 40)).ToBytes();
        public int PrivateCalls { get; private set; }

        public Fixture(AddressType type = AddressType.P2Wpkh, SqliteCacheMode cacheMode = SqliteCacheMode.Private)
        {
            _cacheMode = cacheMode;
            Directory.CreateDirectory(_directory);
            _keys = SecureKeyManager.FromSeed(Enumerable.Repeat((byte)1, 32).ToArray(), NetworkConstants.Regtest, _ => { });
            Binding = new NativeSignerBinding("node-a", "owner-a", "signer-a", "regtest", _keys.GetNodePubKey().ToString());
            Locator = new NativeWalletKeyLocator(0, false, type);
            Registry = new NativeSignerWalletScriptRegistry(Binding, _keys, [Locator, new NativeWalletKeyLocator(1, true, type)]);
            Script = Registry.GetScript(Binding, Locator);
            _signer = new LocalLightningSigner(new FundingOutputBuilder(), new KeyDerivationService(),
                NullLogger<LocalLightningSigner>.Instance, new NodeOptions { BitcoinNetwork = NetworkConstants.Regtest }, _keys, Wallet);
            Journal = new DurableSignerState(_signer, Path.Combine(_directory, "journal"), "regtest");
            Checkpoints = new NativeSignerSafetyCheckpointSet(Binding, Journal);
            Authority = new NativeSignerAuthority(Connection, Clock);
            Authority.Initialize();
            Authority.Enroll(Binding, Checkpoints.GetCheckpoint());
            Authority.AcquireWriter(Binding, 0, "writer-a");
            Evidence = new ChainEvidence(Binding, InputId.ToString(), Script);
            Validator = new NativeWalletSignerRequestValidator(Authority, Registry, Evidence, Clock);
        }

        public SqliteConnection Connection() => new(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(_directory, "authority.db"), Cache = _cacheMode }.ToString());
        public NativeSignerExecution Execution() => Authority.LoadCurrentExecution(Binding, "writer-a", 1, Checkpoints.GetCheckpoint);
        public NativeAuthorizedSignerExecutor Executor() => new(Authority, Binding, Journal, Execution(), Validator, Checkpoints);
        public void Approve(SigningRequest request) => Authority.ApproveWalletIntent(Binding, NativeSignerIntent.FromRequest(request),
            new NativeWalletApproval(Reservation, [new NativeWalletApprovedInput(InputId.ToString(), 0, Locator)],
                new NativeWalletSpendingIntent([new NativeApprovedOutput(Destination, 500)], 10)), Clock.GetUtcNow().AddMinutes(1));

        public SigningRequest Request(string? attack = null)
        {
            var transaction = Transaction.Create(Network.RegTest);
            transaction.Inputs.Add(new TxIn(new OutPoint(uint256.Parse(InputId.ToString()), 0)));
            transaction.Outputs.Add(new TxOut(Money.Satoshis(500), new Script(attack == "destination" ? Script : Destination)));
            transaction.Outputs.Add(new TxOut(Money.Satoshis(attack == "fee" ? 480 : 490),
                new Script(Registry.GetScript(Binding, Locator with { Index = 1, IsChange = true }))));
            var addressScript = attack == "address" ? Destination : Script;
            var address = new WalletAddressModel(attack == "address-type" ? AddressType.P2Tr : Locator.AddressType,
                attack == "address-index" ? 99u : 0u, attack == "change-flag",
                new Script(addressScript).GetDestinationAddress(Network.RegTest)!.ToString())
            { AccountIndex = attack == "account" ? 1u : 0u, DerivationIndex = attack == "derivation-override" ? 0u : null };
            var utxo = new UtxoModel(InputId, 0, LightningMoney.MilliSatoshis(attack == "amount" ? 900_000UL
                : attack == "fractional-amount" ? 1_000_001UL : 1_000_000UL), 100, address);
            if (attack == "silent-payment")
                utxo = new UtxoModel(new SilentPaymentOutputModel(InputId, 0, new byte[32], new byte[32], null,
                    1000, 100, new NLightning.Domain.Crypto.ValueObjects.Hash(new byte[32])));
            if (attack == "channel-lock") utxo.LockedToChannelId = new ChannelId(new byte[32]);
            if (attack == "used-input") utxo.UsedInTransactionId = InputId;
            var reservation = new FeeReservation(InputId, 0, attack == "wrong-reservation" ? Guid.NewGuid() : Reservation);
            var snapshot = new WalletSnapshot(attack == "missing-utxo" ? [] : attack == "duplicate-utxo" ? [utxo, utxo] : [utxo],
                attack == "missing-reservation" ? [] : attack == "duplicate-reservation" ? [reservation, reservation] : [reservation]);
            var external = attack == "external-inputs"
                ? new[] { new SpentOutput(InputId, 0, LightningMoney.Satoshis(1000), new NLightning.Domain.Bitcoin.ValueObjects.BitcoinScript(Script)) }
                : [];
            return new SigningRequest
            {
                Version = 1,
                RequestId = Guid.NewGuid().ToString("N"),
                Operation = SignerOperations.SignWalletTransaction3,
                NodeId = Binding.NodeId,
                OwnerId = Binding.OwnerId,
                SignerId = Binding.SignerId,
                Network = Binding.Network,
                Payload = ByteString.CopyFrom(SignerWire.Encode([new SignedTransaction(attack == "txid" ? InputId
                    : new TxId(transaction.GetHash().ToBytes()), transaction.ToBytes()), Reservation, external, snapshot]))
            };
        }

        public byte[] Sign(SigningRequest request)
        {
            PrivateCalls++;
            var args = SignerWire.Decode(request.Payload.ToByteArray());
            SignerWire.Read<WalletSnapshot>(args[3]).Apply(Wallet);
            var result = Journal.ExecuteRequest(request, args, () => throw new Exception("Journal owns dispatch."));
            return new SigningResponse { Payload = ByteString.CopyFrom(SignerWire.Encode(result)) }.ToByteArray();
        }

        public SignedTransaction Signed(byte[] response)
        {
            var args = SignerWire.Decode(SigningResponse.Parser.ParseFrom(response).Payload.ToByteArray());
            Assert.True(SignerWire.Read<bool>(args[0]));
            return SignerWire.Read<SignedTransaction>(args[1]);
        }

        public void RestartJournal()
        {
            Journal.Dispose();
            if (Wallet.TryGetUtxo(InputId, 0, out var utxo)) Wallet.Spend(utxo);
            Wallet.ReleaseFeeReservation(Reservation);
            Journal = new DurableSignerState(_signer, Path.Combine(_directory, "journal"), "regtest");
            Checkpoints = new NativeSignerSafetyCheckpointSet(Binding, Journal);
        }

        public void Dispose()
        {
            Journal.Dispose();
            _keys.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(_directory, true);
        }
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class ChainEvidence(NativeSignerBinding binding, string txid, byte[] script) : IAuthenticatedNativeChainEvidence
    {
        public string? Attack { get; set; }
        public int Batches { get; private set; }
        public int Completions { get; private set; }
        public Action? BeforeCompletion { get; set; }
        public void RequireFresh(NativeSignerBinding requested) { Assert.Equal(binding, requested); Batches++; }
        public NativeWalletInputEvidence GetOutput(NativeSignerBinding requested, string transactionId, uint index) =>
            new(txid, 0, Attack == "amount" ? 900 : 1000, Attack == "script" ? [] : script,
                Attack == "owner" ? "other-owner" : binding.OwnerId, binding.NodeId, Attack != "spent");
        public void RequireUnchanged(NativeSignerBinding requested)
        {
            Completions++;
            BeforeCompletion?.Invoke();
            if (Attack == "tip-change") throw new InvalidOperationException("Chain tip changed before signing.");
        }
    }
}
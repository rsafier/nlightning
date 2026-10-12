using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;
using NLightning.Domain.Bitcoin.Transactions.Enums;
using NLightning.Domain.Bitcoin.Transactions.Factories;
using NLightning.Domain.Bitcoin.Transactions.Outputs;
using NLightning.Domain.Bitcoin.ValueObjects;
using NLightning.Domain.Channels.Commitments;
using NLightning.Domain.Channels.Enums;
using NLightning.Domain.Channels.ValueObjects;
using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Domain.Money;
using NLightning.Domain.Node.Options;
using NLightning.Domain.Protocol.Constants;
using NLightning.Infrastructure.Bitcoin.Builders;
using NLightning.Infrastructure.Bitcoin.Managers;
using NLightning.Infrastructure.Bitcoin.Services;
using NLightning.Infrastructure.Bitcoin.Signers;
using NLightning.Infrastructure.RemoteSigning;
using NLightning.Infrastructure.Repositories.Memory;
using TxId = NLightning.Domain.Bitcoin.ValueObjects.TxId;

namespace NLightning.RemoteSigning.Tests;

public sealed class NativeChannelTransactionValidatorTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "nltg-channel-authority-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly SecureKeyManager _keys = SecureKeyManager.FromSeed(Enumerable.Repeat((byte)1, 32).ToArray(), NetworkConstants.Regtest, _ => { });
    private readonly SecureKeyManager _peerKeys = SecureKeyManager.FromSeed(Enumerable.Repeat((byte)2, 32).ToArray(), NetworkConstants.Regtest, _ => { });
    private readonly LocalLightningSigner _signer;
    private readonly LocalLightningSigner _peerSigner;
    private readonly NativeSignerBinding _binding;
    private readonly NativeAuthorizedChannelStateStore _store;
    private readonly FundingEvidence _chain = new();
    private readonly NativeChannelTransactionValidator _validator;
    private readonly ChannelId _channel = new(Enumerable.Repeat((byte)3, 32).ToArray());

    public NativeChannelTransactionValidatorTests()
    {
        var options = new NodeOptions { BitcoinNetwork = NetworkConstants.Regtest };
        _signer = Signer(_keys, options);
        _peerSigner = Signer(_peerKeys, options);
        _binding = new NativeSignerBinding("node-a", "owner-a", "signer-a", "regtest", _keys.GetNodePubKey().ToString());
        _store = Store();
        _store.Initialize();
        _validator = new NativeChannelTransactionValidator(_signer,
            new CommitmentTransactionModelFactory(new CommitmentKeyDerivationService(new KeyDerivationService(), _signer), _signer),
            new CommitmentTransactionBuilder(Options.Create(options)), _chain, _store);
    }

    [Theory]
    [InlineData(CommitmentSide.Local, false)]
    [InlineData(CommitmentSide.Remote, false)]
    [InlineData(CommitmentSide.Local, true)]
    [InlineData(CommitmentSide.Remote, true)]
    public void ReconstructedProtocolOutputsPreserveAuthorizedFractionalMsatAndTrimmedHtlcs(CommitmentSide holder, bool anchors)
    {
        Enroll(holder, anchors);
        var persisted = Store().GetCommitment(_binding, _channel, holder, 0);
        Assert.Equal(600_000_001UL, persisted.LocalMsat);
        Assert.Equal(349_899_999UL, persisted.RemoteMsat);
        Assert.Equal(50_000_001UL, persisted.Htlcs[0].AmountMsat);
        Assert.Equal(99_999UL, persisted.Htlcs[1].AmountMsat);
        var expected = _validator.Reconstruct(_binding, _channel, holder, 0);
        _validator.ValidateCommitment(_binding, _channel, holder, 0, expected);
        var transaction = Transaction.Load(expected.RawTxBytes, Network.RegTest);
        Assert.Single(transaction.Inputs);
        Assert.True(transaction.Outputs.Sum(output => output.Value.Satoshi) < 1_000_000);
        Assert.Equal(anchors ? 5 : 3, transaction.Outputs.Count);
    }

    [Theory]
    [InlineData("amount")]
    [InlineData("destination")]
    [InlineData("funding")]
    [InlineData("sequence")]
    [InlineData("locktime")]
    [InlineData("version")]
    [InlineData("witness")]
    [InlineData("extra-output")]
    public void AlteringAnySignedTransactionContentIsRejectedAgainstIndependentReconstruction(string change)
    {
        Enroll(CommitmentSide.Remote);
        var expected = _validator.Reconstruct(_binding, _channel, CommitmentSide.Remote, 0);
        var tx = Transaction.Load(expected.RawTxBytes, Network.RegTest);
        switch (change)
        {
            case "amount": tx.Outputs[0].Value += Money.Satoshis(1); break;
            case "destination": tx.Outputs[0].ScriptPubKey = Script.Empty; break;
            case "funding": tx.Inputs[0].PrevOut = new OutPoint(uint256.One, 1); break;
            case "sequence": tx.Inputs[0].Sequence = new Sequence(1); break;
            case "locktime": tx.LockTime = new LockTime(0); break;
            case "version": tx.Version = 3; break;
            case "witness": tx.Inputs[0].WitScript = new WitScript([new byte[32]]); break;
            case "extra-output": tx.Outputs.Add(Money.Satoshis(1), Script.Empty); break;
        }
        Assert.Throws<UnauthorizedAccessException>(() => _validator.ValidateCommitment(_binding, _channel,
            CommitmentSide.Remote, 0, new SignedTransaction(tx.GetHash().ToBytes(), tx.ToBytes())));
        _validator.ValidateCommitment(_binding, _channel, CommitmentSide.Remote, 0, expected);
    }

    [Theory]
    [InlineData("amount")]
    [InlineData("script")]
    [InlineData("spent")]
    [InlineData("stale")]
    public void FabricatedOrStaleFundingEvidenceCannotAuthorizeACommitment(string change)
    {
        Enroll(CommitmentSide.Local);
        _chain.Output = change switch
        {
            "amount" => _chain.Output! with { AmountSatoshis = 999_999 },
            "script" => _chain.Output! with { ScriptPubKey = [0x51] },
            "spent" => _chain.Output! with { Unspent = false },
            _ => _chain.Output
        };
        _chain.Fresh = change != "stale";
        Assert.Throws<UnauthorizedAccessException>(() => _validator.Reconstruct(_binding, _channel, CommitmentSide.Local, 0));
    }

    [Fact]
    public void NodeRegistrationCannotFabricateRevocationAdvancement()
    {
        var enrollment = Enroll(CommitmentSide.Local);
        var registration = new ChannelSigningInfo(enrollment.FundingTransactionId, enrollment.FundingOutputIndex,
            LightningMoney.Satoshis(enrollment.FundingSatoshis), enrollment.LocalBasepoints.FundingPubKey,
            enrollment.RemoteBasepoints.FundingPubKey, enrollment.ChannelKeyIndex,
            enrollment.RemoteBasepoints.HtlcBasepoint);
        _validator.ValidateInitialRegistration(_binding, _channel, registration);
        Assert.Throws<UnauthorizedAccessException>(() => _validator.ValidateInitialRegistration(_binding,
            _channel, registration with { FundingSatoshis = enrollment.FundingSatoshis }));
        Assert.Throws<UnauthorizedAccessException>(() => _validator.ValidateInitialRegistration(_binding,
            _channel, registration with { FundingSatoshis = registration.FundingSatoshis + 1 }));
        Assert.Throws<UnauthorizedAccessException>(() => _validator.ValidateInitialRegistration(_binding,
            _channel, registration with { LocalCommitmentNumber = 1 }));
        Assert.Throws<UnauthorizedAccessException>(() => _validator.ValidateInitialRegistration(_binding,
            _channel, registration with { BroadcastSignedCommitmentNumber = 0 }));
    }

    [Fact]
    public void OwnerBindingAndUnapprovedCommitmentNumbersCannotSelectAnotherState()
    {
        Enroll(CommitmentSide.Local);
        Assert.Throws<UnauthorizedAccessException>(() => _validator.Reconstruct(
            _binding with { OwnerId = "owner-b" }, _channel, CommitmentSide.Local, 0));
        Assert.Throws<UnauthorizedAccessException>(() => _validator.Reconstruct(_binding, _channel, CommitmentSide.Local, 1));
    }

    [Fact]
    public void ConflictingOwnerEnrollmentAndCommitmentContentCannotReplaceExistingHistory()
    {
        var enrollment = Enroll(CommitmentSide.Local);
        Assert.Throws<SqliteException>(() => Store().Enroll(enrollment with { FundingSatoshis = 2_000_000 }));
        var existing = Store().GetCommitment(_binding, _channel, CommitmentSide.Local, 0);
        Assert.Throws<SqliteException>(() => Store().ApproveCommitment(_binding, _channel,
            existing with { LocalMsat = existing.LocalMsat + 1 }));
        Assert.Equal(existing.LocalMsat, Store().GetCommitment(_binding, _channel, CommitmentSide.Local, 0).LocalMsat);
    }

    [Fact]
    public void EvenOwnerInstalledStateMustConserveEveryMillisatoshi()
    {
        Enroll(CommitmentSide.Local);
        _store.ApproveCommitment(_binding, _channel, new NativeAuthorizedCommitment(
            CommitmentSide.Local, 1, 1_000_000_001, 0, 2500, [], null));
        Assert.Throws<UnauthorizedAccessException>(() => _validator.Reconstruct(_binding, _channel, CommitmentSide.Local, 1));
    }

    private NativeChannelEnrollment Enroll(CommitmentSide holder, bool anchors = false)
    {
        var local = _signer.GetChannelBasepoints(0);
        var peer = _peerSigner.GetChannelBasepoints(0);
        var party = new NativeChannelParty(546, 10_000, 1, 30, 1_000_000_000, 144);
        var enrollment = new NativeChannelEnrollment(_binding, _channel,
            new TxId(Enumerable.Repeat((byte)4, 32).ToArray()), 0, 1_000_000, 0,
            local, peer, _peerKeys.GetNodePubKey(), true, party, party, anchors, 1000, 5000);
        _store.Enroll(enrollment);
        _store.ApproveCommitment(_binding, _channel, new NativeAuthorizedCommitment(holder, 0,
            600_000_001, 349_899_999, 2500,
            [new SpecHtlc(HtlcDirection.Outgoing, 0, 50_000_001, new Hash(new byte[32]), 100),
             new SpecHtlc(HtlcDirection.Incoming, 0, 99_999, new Hash(Enumerable.Repeat((byte)5, 32).ToArray()), 110)],
            holder == CommitmentSide.Remote ? _peerSigner.GetPerCommitmentPoint(0, 0) : null));
        var funding = new FundingOutputBuilder().Build(new FundingOutputInfo(LightningMoney.Satoshis(1_000_000),
            local.FundingPubKey, peer.FundingPubKey));
        _chain.Output = new NativeWalletInputEvidence(enrollment.FundingTransactionId.ToString(), 0,
            1_000_000, funding.BitcoinScriptPubKey, "joint", "joint", true);
        return enrollment;
    }

    private NativeAuthorizedChannelStateStore Store() => new(() => new SqliteConnection("Data Source=" + _path));
    private static LocalLightningSigner Signer(SecureKeyManager keys, NodeOptions options) => new(
        new FundingOutputBuilder(), new KeyDerivationService(), NullLogger<LocalLightningSigner>.Instance,
        options, keys, new UtxoMemoryRepository());

    public void Dispose()
    {
        _keys.Dispose();
        _peerKeys.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _path, _path + "-wal", _path + "-shm" })
            if (File.Exists(path)) File.Delete(path);
    }

    private sealed class FundingEvidence : IAuthenticatedNativeChainEvidence
    {
        public NativeWalletInputEvidence? Output { get; set; }
        public bool Fresh { get; set; } = true;
        public void RequireFresh(NativeSignerBinding binding)
        {
            if (!Fresh) throw new UnauthorizedAccessException("Independent chain evidence is stale.");
        }
        public NativeWalletInputEvidence GetOutput(NativeSignerBinding binding, string transactionId, uint outputIndex) =>
            Output ?? throw new UnauthorizedAccessException("Funding proof missing.");
    }
}
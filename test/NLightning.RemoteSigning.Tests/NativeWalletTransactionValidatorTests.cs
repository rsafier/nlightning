using NBitcoin;
using NLightning.Infrastructure.RemoteSigning;

namespace NLightning.RemoteSigning.Tests;

public sealed class NativeWalletTransactionValidatorTests
{
    private static readonly NativeSignerBinding s_binding = new("node-a", "owner-a", "signer-a", "regtest", "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798");
    private static readonly Script s_owned = Script.FromHex("00141111111111111111111111111111111111111111");
    private static readonly Script s_destination = Script.FromHex("00142222222222222222222222222222222222222222");
    private static readonly Script s_attacker = Script.FromHex("00143333333333333333333333333333333333333333");
    private static readonly string s_txId = new string('a', 64);

    [Fact]
    public void Given_AuthenticatedInputAndExactIntent_When_Validating_Then_ChangeAndFeeAreAccepted()
    {
        Validator(new Evidence()).Validate(s_binding, TransactionBytes(), Intent());
    }

    [Theory]
    [InlineData("destination")]
    [InlineData("amount")]
    [InlineData("fee")]
    [InlineData("duplicate-input")]
    public void Given_ApprovedSpend_When_TransactionIsAltered_Then_ItIsRejected(string attack)
    {
        var transaction = Transaction.Create(Network.RegTest);
        transaction.Inputs.Add(new TxIn(new OutPoint(uint256.Parse(s_txId), 0)));
        if (attack == "duplicate-input") transaction.Inputs.Add(new TxIn(new OutPoint(uint256.Parse(s_txId), 0)));
        transaction.Outputs.Add(new TxOut(Money.Satoshis(attack == "amount" ? 501 : 500), attack == "destination" ? s_attacker : s_destination));
        transaction.Outputs.Add(new TxOut(Money.Satoshis(attack == "fee" ? 400 : 490), s_owned));
        Assert.ThrowsAny<Exception>(() => Validator(new Evidence()).Validate(s_binding, transaction.ToBytes(), Intent()));
    }

    [Theory]
    [InlineData("spent")]
    [InlineData("owner")]
    [InlineData("node")]
    [InlineData("script")]
    [InlineData("amount")]
    [InlineData("outpoint")]
    public void Given_NodeClaimingWalletFunds_When_IndependentEvidenceDisagrees_Then_SpendingFails(string attack)
    {
        var previous = new Evidence().GetOutput(s_binding, s_txId, 0);
        previous = attack switch
        {
            "spent" => previous with { Unspent = false },
            "owner" => previous with { OwnerId = "owner-b" },
            "node" => previous with { NodeId = "node-b" },
            "script" => previous with { ScriptPubKey = s_attacker.ToBytes() },
            "amount" => previous with { AmountSatoshis = 900 },
            _ => previous with { OutputIndex = 1 }
        };
        Assert.Throws<UnauthorizedAccessException>(() => Validator(new Evidence(previous)).Validate(s_binding, TransactionBytes(), Intent()));
    }

    [Fact]
    public void Given_LostChainFreshness_When_Validating_Then_OutputLookupIsNotAttempted()
    {
        var evidence = new Evidence { Fresh = false };
        Assert.Throws<InvalidOperationException>(() => Validator(evidence).Validate(s_binding, TransactionBytes(), Intent()));
        Assert.Equal(0, evidence.Lookups);
    }

    private static NativeWalletTransactionValidator Validator(Evidence evidence) => new(evidence,
        (_, script) => script.AsSpan().SequenceEqual(s_owned.ToBytes()));
    private static NativeWalletSpendingIntent Intent() => new([new NativeApprovedOutput(s_destination.ToBytes(), 500)], 10);
    private static byte[] TransactionBytes()
    {
        var transaction = Transaction.Create(Network.RegTest);
        transaction.Inputs.Add(new TxIn(new OutPoint(uint256.Parse(s_txId), 0)));
        transaction.Outputs.Add(new TxOut(Money.Satoshis(500), s_destination));
        transaction.Outputs.Add(new TxOut(Money.Satoshis(490), s_owned));
        return transaction.ToBytes();
    }

    private sealed class Evidence(NativeWalletInputEvidence? output = null) : IAuthenticatedNativeChainEvidence
    {
        public bool Fresh { get; init; } = true;
        public int Lookups { get; private set; }
        public void RequireFresh(NativeSignerBinding binding)
        {
            if (!Fresh) throw new InvalidOperationException("Authenticated chain evidence is stale.");
        }
        public NativeWalletInputEvidence GetOutput(NativeSignerBinding binding, string transactionId, uint outputIndex)
        {
            Lookups++;
            return output ?? new NativeWalletInputEvidence(s_txId, 0, 1000, s_owned.ToBytes(), "owner-a", "node-a", true);
        }
    }
}
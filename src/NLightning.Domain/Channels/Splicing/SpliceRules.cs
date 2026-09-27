namespace NLightning.Domain.Channels.Splicing;

using Models;
using Node;
using Protocol.Payloads;

/// <summary>
/// The BOLT 2 splice rules as pure checks (splicing plan SP1-D-T1: SP-S-01/02, SP-R-01/02, SP-TX-01..05, D14). Each
/// check returns null when the rule holds, else the violated requirement and the action BOLT 2 prescribes.
/// </summary>
/// <remarks>Signatures only in the SP1 contracts; lane SP1-D implements and table-tests them.</remarks>
public static class SpliceRules
{
    /// <summary>D14: both <c>option_quiesce</c> (34/35) and <c>option_splice</c> (62/63) are negotiated.</summary>
    public static bool IsNegotiated(FeatureSet negotiatedFeatures) =>
        throw new NotImplementedException("Lane SP1-D (SP1-D-T1)");

    /// <summary>SP-S-01/02: may we send <c>splice_init</c> with <paramref name="contributionSatoshis"/>?</summary>
    public static SpliceRuleViolation? CheckSendInit(SpliceConditions conditions, long contributionSatoshis) =>
        throw new NotImplementedException("Lane SP1-D (SP1-D-T1)");

    /// <summary>SP-R-01: the peer's <c>splice_init</c> (<paramref name="feerateAcceptable"/>: our feerate policy
    /// accepts its <c>funding_feerate_perkw</c>, else <c>tx_abort</c>).</summary>
    public static SpliceRuleViolation? CheckReceiveInit(SpliceConditions conditions, SpliceInitPayload payload,
                                                        bool feerateAcceptable) =>
        throw new NotImplementedException("Lane SP1-D (SP1-D-T1)");

    /// <summary>SP-R-02: the peer's <c>splice_ack</c> (<paramref name="initSent"/>: our <c>splice_init</c> waits for
    /// it).</summary>
    public static SpliceRuleViolation? CheckReceiveAck(SpliceConditions conditions, SpliceAckPayload payload,
                                                       bool initSent) =>
        throw new NotImplementedException("Lane SP1-D (SP1-D-T1)");
}
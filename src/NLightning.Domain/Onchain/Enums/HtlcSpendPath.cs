namespace NLightning.Domain.Onchain.Enums;

/// <summary>
/// Which BOLT 3 script path a witness spending an HTLC output used, read from the witness shape alone.
/// </summary>
public enum HtlcSpendPath : byte
{
    /// <summary>Not an HTLC output witness this parser knows.</summary>
    Unknown = 0,

    /// <summary>HTLC-success transaction input: <c>0 &lt;remotehtlcsig&gt; &lt;localhtlcsig&gt; &lt;payment_preimage&gt;
    /// &lt;script&gt;</c> (received output of the holder's commitment).</summary>
    HtlcSuccessTransaction = 1,

    /// <summary>HTLC-timeout transaction input: <c>0 &lt;remotehtlcsig&gt; &lt;localhtlcsig&gt; &lt;&gt; &lt;script&gt;</c>
    /// (offered output of the holder's commitment).</summary>
    HtlcTimeoutTransaction = 2,

    /// <summary>Direct claim with the preimage by the counterparty of the commitment: <c>&lt;remotehtlcsig&gt;
    /// &lt;payment_preimage&gt; &lt;script&gt;</c> (offered output).</summary>
    PreimageClaim = 3,

    /// <summary>Direct claim after the timeout by the counterparty of the commitment: <c>&lt;remotehtlcsig&gt; &lt;&gt;
    /// &lt;script&gt;</c> (received output).</summary>
    TimeoutClaim = 4,

    /// <summary>Penalty: <c>&lt;revocation_sig&gt; &lt;revocationpubkey&gt; &lt;script&gt;</c>.</summary>
    Revocation = 5
}
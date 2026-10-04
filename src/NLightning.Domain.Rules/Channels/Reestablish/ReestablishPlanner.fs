namespace NLightning.Domain.Channels.Reestablish

open System
open System.Collections.Generic
open NLightning.Domain.Channels.Enums
open NLightning.Domain.Protocol.Models
open NLightning.Domain.Rules

/// <summary>
/// The pure BOLT 2 "Message Retransmission" rules (plan §3.11, N7-T1): the numbers we announce in our
/// <c>channel_reestablish</c> and what the peer's <c>channel_reestablish</c> means for us: retransmit, resume, fail, or
/// data loss.
/// </summary>
/// <remarks>
/// Notation: L = our current commitment number, R = the peer's current commitment number, X/Y/S = the peer's
/// <c>next_commitment_number</c>, <c>next_revocation_number</c> and <c>your_last_per_commitment_secret</c>. Checks,
/// in order: next_funding (SP-RE-03, B2-RE-25); X = 0 fails and broadcasts (B2-RE-14); Y &gt; L is data loss when S is
/// our secret Y - 1 (B2-RE-23), else a failure; S must be our secret Y - 1 (B2-RE-24); Y = L - 1 resends our last
/// <c>revoke_and_ack</c> (else B2-RE-21); X = R + 1 resends a signed commitment verbatim, X = R + 2 means the peer has
/// it (else B2-RE-19); X = 1 and L = 0 resends <c>channel_ready</c> unless a splice TLV is present (B2-RE-15,
/// SP-RE-05); retransmissions keep their original order (B2-RE-20); <c>my_current_funding_locked</c> (SP-RE-04).
/// The C# version's remarks (git history) keep the deviation notes.
/// </remarks>
[<RequireQualifiedAccess>]
module ReestablishPlanner =

    /// <summary>The length of <c>your_last_per_commitment_secret</c>.</summary>
    [<Literal>]
    let SecretLength = 32

    /// <summary>Our <c>next_funding</c> (SP-RE-01): the latest interactive transaction when we sent
    /// <c>commitment_signed</c> for it and did not receive <c>tx_signatures</c>; bit 0 when we lack the peer's.</summary>
    let GetOwnNextFunding (local: ReestablishLocalState) : ReestablishFundingField | null =
        ArgumentNullException.ThrowIfNull local

        match local.LatestInteractiveTx with
        | NonNull latest when latest.CommitmentSignedSent && not latest.TxSignaturesReceived ->
            let flags =
                if latest.CommitmentSignedReceived then 0uy else ReestablishFundingField.CommitmentSignedFlag

            ReestablishFundingField(latest.TxId, flags)
        | _ -> null

    /// <summary>Our <c>my_current_funding_locked</c> (SP-RE-02), with <c>option_splice</c> only: the last splice we
    /// sent <c>splice_locked</c> for, else the funding once <c>channel_ready</c> was sent; bit 0 on a public channel
    /// when we hold no <c>announcement_signatures</c> of the peer for that txid.</summary>
    let GetOwnFundingLocked (local: ReestablishLocalState) : ReestablishFundingField | null =
        ArgumentNullException.ThrowIfNull local

        match local.Splice with
        | Null -> null
        | NonNull splice ->
            let txId =
                if splice.LastSpliceLockedSent.HasValue then ValueSome splice.LastSpliceLockedSent.Value
                elif splice.ChannelReadySent then ValueSome splice.CurrentFundingTxId
                else ValueNone

            match txId with
            | ValueNone -> null
            | ValueSome txId ->
                let ask = splice.AnnounceChannel && not (Seq.contains txId splice.AnnouncementSignaturesReceivedFor)
                ReestablishFundingField(txId, (if ask then ReestablishFundingField.AnnouncementSignaturesFlag else 0uy))

    /// <summary>Our <c>channel_reestablish</c> (B2-RE-08..11): L + 1, R, the peer's secret R - 1 (none when R = 0),
    /// our point L, and the two funding TLVs.</summary>
    let CreateOwn (local: ReestablishLocalState) : OwnReestablish =
        ArgumentNullException.ThrowIfNull local
        let l, r = local.LocalCommitmentNumber, local.RemoteCommitmentNumber
        let lastSecret = if r = 0UL then Nullable() else Nullable(r - 1UL)
        OwnReestablish(Checked.(+) l 1UL, r, lastSecret, l, GetOwnNextFunding local, GetOwnFundingLocked local)

    /// S must be all zeroes when Y is 0, else our secret Y - 1.
    let private secretMatches
        (y: uint64)
        (secret: ReadOnlyMemory<byte>)
        (isOurSecret: Func<uint64, ReadOnlyMemory<byte>, bool>)
        =
        secret.Length = SecretLength
        && (if y = 0UL then
                not (MemoryExtensions.ContainsAnyExcept(secret.Span, 0uy))
            else
                y - 1UL <= PerCommitmentIndex.MaxCommitmentNumber && isOurSecret.Invoke(y - 1UL, secret))

    /// SP-RE-05: whether our message or the peer's carries a funding TLV for a splice transaction.
    let private carriesSpliceFunding (local: ReestablishLocalState) (peer: PeerReestablish) peerNamesLatest =
        let latest = Option.ofObj local.LatestInteractiveTx
        let ourLatestIsSplice = latest |> Option.exists _.IsSplice

        if ourLatestIsSplice && not (isNull (GetOwnNextFunding local)) then
            true
        else
            match local.Splice with
            | Null -> false
            | NonNull splice ->
                let namesSplice (field: ReestablishFundingField | null) =
                    match field with
                    | NonNull f -> splice.IsSplice f.TxId
                    | Null -> false

                namesSplice (GetOwnFundingLocked local)
                || namesSplice peer.MyCurrentFundingLocked
                || ((not (isNull peer.NextFunding) || peer.HasNextFunding)
                    && not (peerNamesLatest && latest |> Option.exists (fun t -> not t.IsSplice)))

    type private NextFundingPlan = { X: uint64; Steps: ReestablishStep list; NamesLatest: bool }

    /// next_funding (SP-RE-03, B2-RE-25): finish the signing steps of our latest interactive transaction; a different
    /// txid while we set next_funding too fails the channel, anything else is forgotten with tx_abort.
    let private planNextFunding (local: ReestablishLocalState) (peer: PeerReestablish) x =
        let r = local.RemoteCommitmentNumber
        let none = { X = x; Steps = []; NamesLatest = false }

        match local.LatestInteractiveTx, peer.NextFunding with
        | NonNull latest, NonNull next when latest.TxId = next.TxId ->
            // bolt02/splicing-test.md still asks for our commitment_signed with X = R instead of the flag
            let legacy =
                x = r
                && latest.CommitmentSignedSent
                && not latest.TxSignaturesReceived
                && not local.HasRemoteNextCommit

            let steps =
                if latest.TxSignaturesReceived then
                    [ ReestablishStep.NextFundingTxSignatures ]
                else
                    [
                        if (next.IsBit0Set || legacy) && latest.CommitmentSignedSent then
                            ReestablishStep.NextFundingCommitmentSigned
                        if
                            (latest.CommitmentSignedReceived && latest.SendsTxSignaturesFirst)
                            || latest.TxSignaturesSent
                        then
                            ReestablishStep.NextFundingTxSignatures
                    ]

            Ok
                {
                    X = (if legacy then Checked.(+) r 1UL else x)
                    Steps = steps
                    NamesLatest = true
                }
        | _, NonNull next ->
            match GetOwnNextFunding local with
            | NonNull own ->
                Error(ReestablishPlan.Failed("SP-RE-03", $"next_funding {next.TxId} differs from ours {own.TxId}"))
            | Null -> Ok { none with Steps = [ ReestablishStep.TxAbort ] }
        | _, Null when peer.HasNextFunding -> Ok { none with Steps = [ ReestablishStep.TxAbort ] }
        | _ -> Ok none

    /// <summary>Judges the peer's <c>channel_reestablish</c>.</summary>
    /// <param name="local">Our side (after <c>RevertUncommitted</c>).</param>
    /// <param name="peer">The peer's message.</param>
    /// <param name="isOurSecret">True when the 32 bytes are our per-commitment secret for the given number.</param>
    let Plan
        (local: ReestablishLocalState, peer: PeerReestablish, isOurSecret: Func<uint64, ReadOnlyMemory<byte>, bool>)
        =
        ArgumentNullException.ThrowIfNull local
        ArgumentNullException.ThrowIfNull peer
        ArgumentNullException.ThrowIfNull isOurSecret
        let l, r, y = local.LocalCommitmentNumber, local.RemoteCommitmentNumber, peer.NextRevocationNumber
        let secret = peer.YourLastPerCommitmentSecret
        let fail id reason (steps: ReestablishStep list) = Error(ReestablishPlan.Failed(id, reason, List.toArray steps))

        result {
            let! funding = planNextFunding local peer peer.NextCommitmentNumber
            let x, early = funding.X, funding.Steps

            do!
                if x = 0UL then
                    Error(ReestablishPlan.Failed("B2-RE-14", "next_commitment_number is 0", List.toArray early, true))
                else
                    Ok()

            // Data loss first: the peer expects a revocation we never made and proves it with our own secret
            let! resendRevokeAndAck =
                if y > l && secretMatches y secret isOurSecret then
                    Error(
                        ReestablishPlan.LostData
                            $"The peer expects revocation {y} but our current commitment is {l}, and it knows our secret {y - 1UL}"
                    )
                elif y > l then
                    fail "B2-RE-21" $"next_revocation_number {y} is ahead of our commitment {l} without proof" early
                elif not (secretMatches y secret isOurSecret) then
                    let expected = if y = 0UL then "(zeroes)" else string (y - 1UL)
                    fail "B2-RE-24" $"your_last_per_commitment_secret is not our secret {expected}" early
                elif y = l then
                    Ok false
                elif l > 0UL && y = l - 1UL then
                    Ok true
                else
                    fail "B2-RE-21" $"next_revocation_number {y} does not fit our commitment {l}" early

            let! resendCommitDiff =
                if x = Checked.(+) r 1UL then
                    // The peer is missing the commitment_signed we sent for R + 1, if we sent one
                    if local.HasRemoteNextCommit && not local.HasSentCommitDiff then
                        fail "B2-RE-18" $"commitment_signed {x} must be retransmitted but it is not stored" early
                    else
                        Ok local.HasRemoteNextCommit
                elif local.HasRemoteNextCommit && x = Checked.(+) r 2UL then
                    Ok false // it will resend its revoke_and_ack
                else
                    let expected = if local.HasRemoteNextCommit then $"{r + 1UL} or {r + 2UL}" else $"{r + 1UL}"
                    fail "B2-RE-19" $"next_commitment_number {x}, expected {expected}" early

            // SP-RE-04 (option_splice only): my_current_funding_locked naming a pending splice whose splice_locked we
            // lack is processed as that splice_locked; its bit 0 asks for our announcement_signatures of that funding
            let peerSpliceLocked, announce =
                match local.Splice, peer.MyCurrentFundingLocked with
                | NonNull splice, NonNull locked ->
                    let lockedNow =
                        match splice.PendingSplices |> Seq.tryFind (fun p -> p.TxId = locked.TxId) with
                        | Some p when not p.SpliceLockedReceived -> Some locked.TxId
                        | _ -> None

                    let ready = Seq.contains locked.TxId splice.AnnouncementSignaturesReadyFor || lockedNow.IsSome
                    lockedNow, locked.IsBit0Set && splice.AnnounceChannel && ready
                | _ -> None, false

            let steps =
                [
                    yield! early
                    if x = 1UL && l = 0UL && not (carriesSpliceFunding local peer funding.NamesLatest) then
                        ReestablishStep.ChannelReady
                    // Same relative order as first sent: the one sent last goes last
                    match resendRevokeAndAck, resendCommitDiff, local.LastSent with
                    | true, true, LastSentCommitmentMessage.RevokeAndAck ->
                        yield! [ ReestablishStep.CommitDiff; ReestablishStep.RevokeAndAck ]
                    | true, true, _ -> yield! [ ReestablishStep.RevokeAndAck; ReestablishStep.CommitDiff ]
                    | true, false, _ -> ReestablishStep.RevokeAndAck
                    | false, true, _ -> ReestablishStep.CommitDiff
                    | false, false, _ -> ()
                    if local.HasUnsignedLocalUpdates then
                        ReestablishStep.UnsignedUpdates
                    if announce then
                        ReestablishStep.AnnouncementSignatures
                ]

            return
                ReestablishPlan(
                    ReestablishOutcome.Resume,
                    List.toArray steps,
                    PeerSpliceLocked = Option.toNullable peerSpliceLocked
                )
        }
        |> unwrap

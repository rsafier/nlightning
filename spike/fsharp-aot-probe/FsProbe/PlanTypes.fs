namespace FsProbe.Reestablish

type Step =
    | TxAbort
    | ChannelReady
    | RevokeAndAck
    | CommitDiff
    | UnsignedUpdates
    | NextFundingCommitmentSigned
    | NextFundingTxSignatures
    | AnnouncementSignatures

/// Each outcome carries only the fields that mean something for it.
type Plan =
    | Resume of steps: Step list * peerSpliceLocked: byte[] voption
    | Fail of requirementId: string * reason: string * steps: Step list * mustBroadcast: bool
    | DataLoss of reason: string

module PlanUse =
    /// Exhaustive: adding a case to Plan makes this a compile warning (FS0025), an error with TreatWarningsAsErrors.
    let describe plan =
        match plan with
        | Resume(steps, _) -> $"resume {List.length steps}"
        | Fail(id, reason, _, broadcast) -> $"fail [{id}] {reason} broadcast={broadcast}"
        | DataLoss reason -> $"data loss: {reason}"

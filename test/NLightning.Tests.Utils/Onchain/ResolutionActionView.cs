namespace NLightning.Tests.Utils.Onchain;

using Domain.Onchain.Enums;
using Domain.Onchain.Models;

/// <summary>The case of a <see cref="ResolutionAction"/>, for table-driven assertions.</summary>
public enum ResolutionActionKind : byte
{
    Wait = 1,
    BroadcastHtlcTimeoutTx = 2,
    BroadcastHtlcSuccessTx = 3,
    Sweep = 4,
    RaiseFulfilled = 5,
    RaiseFailed = 6,
    AlertLostFunds = 7
}

/// <summary>
/// A flat view of the <see cref="ResolutionAction"/> union for the planner tests, which assert the fields the action
/// had before the C# 15 pilot (NL-1086) made it a union; product code matches on the cases instead.
/// </summary>
public static class ResolutionActionView
{
    extension(ResolutionAction action)
    {
        public ResolutionActionKind Kind => action switch
        {
            ResolutionAction.Wait => ResolutionActionKind.Wait,
            ResolutionAction.BroadcastHtlcTimeoutTx => ResolutionActionKind.BroadcastHtlcTimeoutTx,
            ResolutionAction.BroadcastHtlcSuccessTx => ResolutionActionKind.BroadcastHtlcSuccessTx,
            ResolutionAction.Sweep => ResolutionActionKind.Sweep,
            ResolutionAction.RaiseFulfilled => ResolutionActionKind.RaiseFulfilled,
            ResolutionAction.RaiseFailed => ResolutionActionKind.RaiseFailed,
            ResolutionAction.AlertLostFunds => ResolutionActionKind.AlertLostFunds
        };

        public string RequirementId => action switch
        {
            ResolutionAction.Wait wait => wait.RequirementId,
            ResolutionAction.BroadcastHtlcTimeoutTx timeout => timeout.RequirementId,
            ResolutionAction.BroadcastHtlcSuccessTx success => success.RequirementId,
            ResolutionAction.Sweep sweep => sweep.RequirementId,
            ResolutionAction.RaiseFulfilled fulfilled => fulfilled.RequirementId,
            ResolutionAction.RaiseFailed failed => failed.RequirementId,
            ResolutionAction.AlertLostFunds alert => alert.RequirementId
        };

        public uint? WaitUntilHeight => action is ResolutionAction.Wait wait ? wait.UntilHeight : null;

        public SweepSpendKind? SpendKind => action is ResolutionAction.Sweep sweep ? sweep.SpendKind : null;

        public bool OnSecondLevel => action is ResolutionAction.Sweep { OnSecondLevel: true };

        public uint? DeadlineHeight => action switch
        {
            ResolutionAction.Sweep sweep => sweep.DeadlineHeight,
            ResolutionAction.BroadcastHtlcSuccessTx success => success.DeadlineHeight,
            _ => null
        };

        public byte[]? Preimage => action switch
        {
            ResolutionAction.Sweep sweep => sweep.Preimage,
            ResolutionAction.BroadcastHtlcSuccessTx success => success.Preimage,
            ResolutionAction.RaiseFulfilled fulfilled => fulfilled.Preimage,
            _ => null
        };
    }

    extension(OutputResolutionPlan plan)
    {
        /// <summary>True when an action of <paramref name="kind"/> is asked for.</summary>
        public bool Has(ResolutionActionKind kind) => plan.Actions.Any(a => a.Kind == kind);

        /// <summary>The first action of <paramref name="kind"/>, or a default (empty) union.</summary>
        public ResolutionAction Get(ResolutionActionKind kind) => plan.Actions.FirstOrDefault(a => a.Kind == kind);
    }
}
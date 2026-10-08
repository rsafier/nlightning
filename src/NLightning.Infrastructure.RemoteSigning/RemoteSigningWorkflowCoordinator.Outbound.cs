using NLightning.Domain.Bitcoin.ValueObjects;
using NLightning.Domain.Signing.Recovery;

namespace NLightning.Infrastructure.RemoteSigning;

public sealed partial class RemoteSigningWorkflowCoordinator
{
    public SignedTransaction? ReplayFundingOrPrepare(ISigningWorkflowScope workflow)
    {
        if (workflow is not WorkflowScope active || !ReferenceEquals(_active.Value, active)
         || active.Kind != SigningWorkflowKind.Funding)
            throw Blocked("Funding recovery requires its activated funding workflow.");
        return active.SavedRequests.Count == 0 ? null : ReplayFunding(workflow);
    }
}
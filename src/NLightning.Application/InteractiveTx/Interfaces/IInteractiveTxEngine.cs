namespace NLightning.Application.InteractiveTx.Interfaces;

using Domain.Protocol.InteractiveTx;
using Domain.Protocol.InteractiveTx.Models;

/// <summary>
/// Creates the negotiation engine the <see cref="InteractiveTxDriver"/> runs (BOLT 2 "Interactive Transaction
/// Construction"). The default is <see cref="DomainInteractiveTxEngine"/>, a thin adapter over the pure Domain
/// <see cref="InteractiveTxSession"/> (lane IT-A); the seam exists so the driver's own behaviour (persistence, host
/// callbacks, <c>tx_abort</c> echo, RBF bookkeeping) can be tested without it.
/// </summary>
public interface IInteractiveTxEngine
{
    /// <summary>A new negotiation (<see cref="InteractiveTxSession.Create"/>).</summary>
    IInteractiveTxNegotiation Create(InteractiveTxSessionParameters parameters);

    /// <summary>Resumes a stored negotiation (<see cref="InteractiveTxSession.Restore"/>).</summary>
    IInteractiveTxNegotiation Restore(InteractiveTxSessionModel model, InteractiveTxSessionParameters parameters);
}
namespace NLightning.Domain.Client.Requests;

/// <summary>
/// Asks how the node follows the chain (<c>ClientCommand.ChainStatus</c>, NL-216): whether block processing is halted
/// and what the node refuses while it is.
/// </summary>
public sealed class ChainStatusClientRequest;
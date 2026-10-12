namespace NLightning.Infrastructure.Transport.Tor;

/// <summary>
/// The onion service's proof-of-work defenses as <c>ADD_ONION</c> takes them (Tor 0.4.9 or newer:
/// <c>PoWDefensesEnabled=</c>, optional <c>PoWQueueRate=</c> and <c>PoWQueueBurst=</c>; rend-spec-v3 §7.3). Built from
/// <c>Node:Tor:OnionServicePoW*</c> (NL-573).
/// </summary>
/// <param name="Enabled">Whether the defenses are on.</param>
/// <param name="QueueRate">The suggested queue rate, when tuned.</param>
/// <param name="QueueBurst">The queue burst, when tuned.</param>
public sealed record TorOnionPoWOptions(bool Enabled, uint? QueueRate = null, uint? QueueBurst = null);
namespace NLightning.Domain.Protocol.Onion.Enums;

/// <summary>
/// The onion layer whose shared secret authenticated a trampoline payment's failure at the origin node.
/// </summary>
public enum TrampolineFailureLayer
{
    /// <summary>
    /// The outer (payment) onion: the erring node is a hop of the route to the first trampoline node, that node
    /// itself answering for its outer layer only, or a node the first trampoline node reports for its own route.
    /// </summary>
    Outer = 0,

    /// <summary>
    /// The trampoline onion: the erring node is a trampoline hop (an intermediate trampoline node or the recipient).
    /// </summary>
    Trampoline = 1
}
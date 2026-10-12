using System.Diagnostics.CodeAnalysis;

namespace NLightning.Tests.Utils.Vectors;

/// <summary>
/// BOLT 7 query replies captured in Docker (plan G3-T4) from Core Lightning (<see cref="Bolt7Vectors.ClnVersion"/>,
/// <c>Integration.Tests/Docker/Gossip/Capture/ClnGossipQueryCaptureTests</c>): CLN's <c>reply_channel_range</c> to
/// <c>query_channel_range(0, tip + 1)</c> with <c>query_option</c> = timestamps | checksums, for its one public channel
/// (108x1x0, CLN to a second CLN), and the two <c>channel_update</c>s of that channel CLN sent in the same session. The
/// reply's <c>checksums_tlv</c> is CLN's CRC32C of each update without its signature and timestamp: the interop vector
/// for our <c>ChannelUpdateChecksum</c>.
/// </summary>
[ExcludeFromCodeCoverage]
public static class Bolt7QueryVectors
{
    /// <summary>The captured <c>reply_channel_range</c> (264) payload, without the type.</summary>
    public static readonly Bolt7CapturedMessage ClnReplyChannelRange = new(
        264, Bolt7Vectors.ClnVersion,
        "06226e46111a0b59caaf126043eb5bbf28c34f3a5e332a1fc7b2b73cf188910f00000000000000730100090000006c00000100000109"
      + "006ab7fd226ab7fd220308def5017680cb93d6");

    /// <summary>The <c>channel_update</c> of direction 0 (<c>node_id_1</c>) of the channel in the reply.</summary>
    public static readonly Bolt7CapturedMessage ClnUpdateDirection0 = new(
        258, Bolt7Vectors.ClnVersion,
        "3c0a2675d2bf7c42a29fec1f85bec13dec1debfc77465f5ec4a6ab6aaf87824f03ab67bc665b1946d902ba0d4486486424fbd5be9b94f7"
      + "a0540a74438340598806226e46111a0b59caaf126043eb5bbf28c34f3a5e332a1fc7b2b73cf188910f00006c00000100006ab7fd220100"
      + "00060000000000000000000000010000000a000000003b023380");

    /// <summary>The <c>channel_update</c> of direction 1 (<c>node_id_2</c>) of the channel in the reply.</summary>
    public static readonly Bolt7CapturedMessage ClnUpdateDirection1 = new(
        258, Bolt7Vectors.ClnVersion,
        "22263f557e1160a4929f2c506c998ce2d73557d3d7272cfd7dec0b2e50d029150d9dcaf61ffd2b5a2455be3975b5eaf20989d6266f1517"
      + "dc9b65c7d3ee68cbba06226e46111a0b59caaf126043eb5bbf28c34f3a5e332a1fc7b2b73cf188910f00006c00000100006ab7fd220101"
      + "00060000000000000000000000010000000a000000003b023380");
}
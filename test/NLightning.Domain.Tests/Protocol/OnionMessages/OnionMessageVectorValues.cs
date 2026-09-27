namespace NLightning.Domain.Tests.Protocol.OnionMessages;

/// <summary>
/// Values of the official BOLT 4 vector <c>bolt04/blinded-onion-message-onion-test.json</c> (copied verbatim from
/// <c>test/NLightning.Integration.Tests/BOLT4/Vectors/</c>, upstream commit in its <c>README.md</c>).
/// </summary>
internal static class OnionMessageVectorValues
{
    /// <summary>
    /// <c>route.first_node_id</c> (Alice).
    /// </summary>
    public const string FirstNodeIdHex =
        "02eec7245d6b7d2ccb30380bfbe2a3648cd7a942653f5aa340edcea1f283686619";

    /// <summary>
    /// <c>route.first_path_key</c>.
    /// </summary>
    public const string FirstPathKeyHex =
        "031195a8046dcbb8e17034bca630065e7a0982e4e36f6f7e5a8d4554e4846fcd99";

    /// <summary>
    /// <c>route.hops[0].blinded_node_id</c>.
    /// </summary>
    public const string Hop0BlindedNodeIdHex =
        "02d1c3d73f8cac67e7c5b6ec517282d5ba0a52b06a29ec92ff01e12decf76003c1";

    /// <summary>
    /// <c>route.hops[0].encrypted_recipient_data</c>.
    /// </summary>
    public const string Hop0EncryptedRecipientDataHex =
        "49531cf38d3280b7f4af6d6461a2b32e3df50acfd35176fc61422a1096eed4dfc3806f29bf74320f712a61c766e7f7caac0c"
      + "42f86040125fbaeec0c7613202b206dbdd31fda56394367b66a711bfd7d5bedbe20bed1b";

    /// <summary>
    /// <c>route.hops[1].blinded_node_id</c>.
    /// </summary>
    public const string Hop1BlindedNodeIdHex =
        "03f1465ca5cf3ec83f16f9343d02e6c24b76993a93e1dea2398f3147a9be893d7a";

    /// <summary>
    /// <c>route.hops[1].encrypted_recipient_data</c>.
    /// </summary>
    public const string Hop1EncryptedRecipientDataHex =
        "adf6771d3983b7f543d1b3d7a12b440b2bd3e1b3b8d6ec1023f6dec4f0e7548a6f57f6dbe9573b0a0f24f7c5773a7dd7a7bd"
      + "b6bd0ee686d759f5";

    /// <summary>
    /// <c>route.hops[2].blinded_node_id</c>.
    /// </summary>
    public const string Hop2BlindedNodeIdHex =
        "035dbc0493aa4e7eea369d6a06e8013fd03e66a5eea91c455ed65950c4942b624b";

    /// <summary>
    /// <c>route.hops[2].encrypted_recipient_data</c>.
    /// </summary>
    public const string Hop2EncryptedRecipientDataHex =
        "d8903df7a79ac799a0b59f4ba22f6a599fa32e7ff1a8325fc22b88d278ce3e4840af02adfb82d6145a189ba50c2219c9e435"
      + "1e634d198e0849ac";

    /// <summary>
    /// <c>route.hops[3].blinded_node_id</c>.
    /// </summary>
    public const string Hop3BlindedNodeIdHex =
        "0237bf019fa0fbecde8b4a1c7b197c9c1c76f9a23d67dd55bb5e42e1f50bb771a6";

    /// <summary>
    /// <c>route.hops[3].encrypted_recipient_data</c>.
    /// </summary>
    public const string Hop3EncryptedRecipientDataHex =
        "bdc03f088764c6224c8f939e321bf096f363b2092db381fc8787f891c8e6dc9284991b98d2a63d9f91fe563065366dd406cd"
      + "8e112cdaaa80d0e6";

    /// <summary>
    /// The <c>route</c> as a BOLT 4 <c>blinded_path</c> on the wire (built from the fields above: the vector has no
    /// wire form of it).
    /// </summary>
    public const string RouteWireHex =
        "02eec7245d6b7d2ccb30380bfbe2a3648cd7a942653f5aa340edcea1f283686619031195a8046dcbb8e17034bca630065e7a"
      + "0982e4e36f6f7e5a8d4554e4846fcd990402d1c3d73f8cac67e7c5b6ec517282d5ba0a52b06a29ec92ff01e12decf76003c1"
      + "005649531cf38d3280b7f4af6d6461a2b32e3df50acfd35176fc61422a1096eed4dfc3806f29bf74320f712a61c766e7f7ca"
      + "ac0c42f86040125fbaeec0c7613202b206dbdd31fda56394367b66a711bfd7d5bedbe20bed1b03f1465ca5cf3ec83f16f934"
      + "3d02e6c24b76993a93e1dea2398f3147a9be893d7a003aadf6771d3983b7f543d1b3d7a12b440b2bd3e1b3b8d6ec1023f6de"
      + "c4f0e7548a6f57f6dbe9573b0a0f24f7c5773a7dd7a7bdb6bd0ee686d759f5035dbc0493aa4e7eea369d6a06e8013fd03e66"
      + "a5eea91c455ed65950c4942b624b003ad8903df7a79ac799a0b59f4ba22f6a599fa32e7ff1a8325fc22b88d278ce3e4840af"
      + "02adfb82d6145a189ba50c2219c9e4351e634d198e0849ac0237bf019fa0fbecde8b4a1c7b197c9c1c76f9a23d67dd55bb5e"
      + "42e1f50bb771a6003abdc03f088764c6224c8f939e321bf096f363b2092db381fc8787f891c8e6dc9284991b98d2a63d9f91"
      + "fe563065366dd406cd8e112cdaaa80d0e6";

    /// <summary>
    /// Dave's <c>onionmsg_tlv</c> (<c>decrypt.hops[3].tlvs</c>): type 1 = "hello", then his
    /// <c>encrypted_recipient_data</c>.
    /// </summary>
    public const string DaveOnionMessageTlvHex =
        "010568656c6c6f043abdc03f088764c6224c8f939e321bf096f363b2092db381fc8787f891c8e6dc9284991b98d2a63d9f91"
      + "fe563065366dd406cd8e112cdaaa80d0e6";
}
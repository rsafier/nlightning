namespace NLightning.Domain.Tests.Protocol.OnionMessages;

/// <summary>
/// Independent <c>blinded_path</c> wire bytes: the <c>offer_paths</c> (TLV 16) values of the official
/// <c>bolt12/offers-test.json</c> (lightning/bolts master), copied verbatim from each case's <c>fields</c> entry.
/// </summary>
/// <remarks>
/// The file's "Malformed: ... in blinded_path" cases are not used: their TLV 16 has no length byte (the value follows
/// the type directly), so they fail the TLV framing before a <c>blinded_path</c> is read. "Second offer_path is empty"
/// has sound framing and is kept.
/// </remarks>
public static class OffersTestVectorValues
{
    /// <summary>
    /// <c>first_node_id</c> of "with blinded path via Bob (0x424242...), path_key 020202...".
    /// </summary>
    public const string BobNodeIdHex = "0324653eac434488002cc06bbfb7f10fe18991e35f9fe4302dbea6d2353dc0ab1c";

    /// <summary>
    /// The <c>path_key</c> and every <c>blinded_node_id</c> of the file's paths: 33 bytes of 0x02.
    /// </summary>
    public const string Point02Hex = "020202020202020202020202020202020202020202020202020202020202020202";

    /// <summary>
    /// "with blinded path via Bob (0x424242...), path_key 020202...": one path, node id introduction, hops
    /// [id=02.., enc=0x00*16], [id=02.., enc=0x11*8].
    /// </summary>
    public const string NodeIdIntroductionPathHex =
        "0324653eac434488002cc06bbfb7f10fe18991e35f9fe4302dbea6d2353dc0ab1c0202020202020202020202020202020202"
      + "0202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202"
      + "0010000000000000000000000000000000000202020202020202020202020202020202020202020202020202020202020202"
      + "0200081111111111111111";

    /// <summary>
    /// "same, with blinded path first_node_id using sciddir": <c>first_node_id</c> is direction 0 of SCID 0x0x42.
    /// </summary>
    public const string ScidIntroductionPathHex =
        "00000000000000002a0202020202020202020202020202020202020202020202020202020202020202020202020202020202"
      + "0202020202020202020202020202020202020202020202020202001000000000000000000000000000000000020202020202"
      + "02020202020202020202020202020202020202020202020202020200081111111111111111";

    /// <summary>
    /// "... and with second blinded path via 1x2x3 (direction 1), path_key 020202...": two paths back to back, the
    /// second introduced by direction 1 of SCID 1x2x3 with hops [id=02.., enc=0x00*16], [id=02.., enc=0x22*8].
    /// </summary>
    public const string TwoPathsHex =
        "0324653eac434488002cc06bbfb7f10fe18991e35f9fe4302dbea6d2353dc0ab1c0202020202020202020202020202020202"
      + "0202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202"
      + "0010000000000000000000000000000000000202020202020202020202020202020202020202020202020202020202020202"
      + "0200081111111111111111010000010000020003020202020202020202020202020202020202020202020202020202020202"
      + "0202020202020202020202020202020202020202020202020202020202020202020202020200100000000000000000000000"
      + "000000000002020202020202020202020202020202020202020202020202020202020202020200082222222222222222";

    /// <summary>
    /// "Second offer_path is empty" (invalid): the second path is direction 1 of SCID 1x2x3, a path key and
    /// <c>num_hops</c> 0.
    /// </summary>
    public const string SecondPathEmptyHex =
        "0324653eac434488002cc06bbfb7f10fe18991e35f9fe4302dbea6d2353dc0ab1c0202020202020202020202020202020202"
      + "0202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202"
      + "0010000000000000000000000000000000000202020202020202020202020202020202020202020202020202020202020202"
      + "0200081111111111111111010000010000020003020202020202020202020202020202020202020202020202020202020202"
      + "02020200";
}
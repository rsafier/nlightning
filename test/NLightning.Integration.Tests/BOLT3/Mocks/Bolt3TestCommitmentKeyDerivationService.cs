namespace NLightning.Integration.Tests.BOLT3.Mocks;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Interfaces;

/// <summary>
/// Returns the BOLT 3 Appendix C commitment keys of node A's commitment (the "local" node of the vectors), whichever
/// side asks: as node A for its local commitment, or as node B for its remote commitment (node A's).
/// </summary>
public class Bolt3TestCommitmentKeyDerivationService : ICommitmentKeyDerivationService
{
    /// <summary>
    /// Appendix C <c>local_per_commitment_point</c> (x_local_per_commitment_secret * G, also Appendix E).
    /// </summary>
    public static readonly CompactPubKey LocalPerCommitmentPoint =
        Convert.FromHexString("025f7117a78150fe2ef97db7cfc83bd57b2e2c0d0dd25eaf467a4a1c2a45ce1486");

    private readonly CompactPubKey _emptyCompactPubKey =
        new([
            0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00
        ]);

    public CommitmentKeys DeriveLocalCommitmentKeys(uint localChannelKeyIndex, ChannelBasepoints localBasepoints,
                                                    ChannelBasepoints remoteBasepoints, ulong commitmentNumber)
    {
        return NodeACommitmentKeys(LocalPerCommitmentPoint);
    }

    public CommitmentKeys DeriveRemoteCommitmentKeys(ChannelBasepoints localBasepoints,
                                                     ChannelBasepoints remoteBasepoints,
                                                     CompactPubKey remotePerCommitmentPoint)
    {
        return NodeACommitmentKeys(remotePerCommitmentPoint);
    }

    private CommitmentKeys NodeACommitmentKeys(CompactPubKey perCommitmentPoint)
    {
        return new CommitmentKeys(
            _emptyCompactPubKey,
            Convert.FromHexString("03fd5960528dc152014952efdb702a88f71e3c1653b2314431701ec77e57fde83c"),
            Convert.FromHexString("0212a140cd0c6539d07cd08dfe09984dec3251ea808b892efeac3ede9402bf2b19"),
            Convert.FromHexString("030d417a46946384f88d5f3337267c5e579765875dc4daca813e21734b140639e7"),
            Convert.FromHexString("0394854aa6eab5b2a8122cc726e9dded053a2184d88256816826d6231c068d4a5b"),
            perCommitmentPoint);
    }
}
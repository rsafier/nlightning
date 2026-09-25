namespace NLightning.Domain.Protocol.Models;

using Crypto.ValueObjects;

/// <summary>
/// One bucket of the BOLT 3 compact per-commitment secret storage (the peer's shachain): the secret with index
/// <see cref="Index"/> kept in bucket <see cref="Bucket"/> (the number of trailing zero bits of the index, 0..48).
/// </summary>
/// <param name="Bucket">The bucket, <c>where_to_put_secret(Index)</c>.</param>
/// <param name="Index">The BOLT 3 secret index (counts down from 2^48 - 1).</param>
/// <param name="Secret">The per-commitment secret revealed by the peer.</param>
public readonly record struct ShachainEntry(int Bucket, ulong Index, Secret Secret)
{
    /// <summary>
    /// The number of buckets: one per possible count of trailing zero bits of a 48-bit index, plus the seed bucket.
    /// </summary>
    public const int BucketCount = 49;

    /// <summary>
    /// BOLT 3 <c>where_to_put_secret</c>: the number of trailing zero bits of <paramref name="index"/> (48 for 0).
    /// </summary>
    public static int GetBucket(ulong index)
    {
        for (var b = 0; b < BucketCount - 1; b++)
        {
            if (((index >> b) & 1) == 1)
                return b;
        }

        return BucketCount - 1;
    }
}
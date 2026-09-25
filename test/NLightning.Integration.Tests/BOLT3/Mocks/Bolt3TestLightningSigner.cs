using Microsoft.Extensions.Logging;
using NBitcoin;
using NLightning.Tests.Utils.Vectors;

#pragma warning disable CS8625 // Cannot convert null literal to non-nullable reference type.

namespace NLightning.Integration.Tests.BOLT3.Mocks;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Node.Options;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Crypto.Functions;
using Infrastructure.Bitcoin.Services;
using Infrastructure.Bitcoin.Signers;
using CompactSignature = Domain.Crypto.ValueObjects.CompactSignature;

/// <summary>
/// A <see cref="LocalLightningSigner"/> whose funding key and <c>htlc_basepoint_secret</c> are the BOLT 3 Appendix C
/// secrets of one of the two vector nodes (node A, the "local" node of the vectors, by default).
/// </summary>
public class Bolt3TestLightningSigner : LocalLightningSigner, ILightningSigner
{
    /// <summary>Appendix C <c>remote_funding_privkey</c> (node B).</summary>
    public static readonly Key NodeBFundingPrivkey =
        new(Convert.FromHexString("1552dfba4f6cf29a62a0af13c8d6981d36d0ef8d61ba10fb0fe90da7634d7e13"));

    /// <summary>Appendix C <c>local_payment_basepoint_secret</c>, also node A's HTLC basepoint secret.</summary>
    public static readonly Key NodeAHtlcBasepointSecret =
        new(Convert.FromHexString("1111111111111111111111111111111111111111111111111111111111111111"));

    /// <summary>Appendix C <c>remote_payment_basepoint_secret</c>, also node B's HTLC basepoint secret.</summary>
    public static readonly Key NodeBHtlcBasepointSecret =
        new(Convert.FromHexString("4444444444444444444444444444444444444444444444444444444444444444"));

    private readonly Key _fundingKey;
    private readonly Key _htlcBasepointSecret;

    public Bolt3TestLightningSigner(NodeOptions nodeOptions, ILogger<LocalLightningSigner> logger,
                                    bool asNodeB = false)
        : base(new FundingOutputBuilder(), new KeyDerivationService(new Secp256K1Math()), logger, nodeOptions, null,
               null)
    {
        _fundingKey = asNodeB ? NodeBFundingPrivkey : Bolt3AppendixCVectors.NodeAFundingPrivkey;
        _htlcBasepointSecret = asNodeB ? NodeBHtlcBasepointSecret : NodeAHtlcBasepointSecret;
    }

    public new ChannelBasepoints GetChannelBasepoints(uint channelKeyIndex)
    {
        return new ChannelBasepoints();
    }

    public new ChannelBasepoints GetChannelBasepoints(ChannelId channelId)
    {
        return new ChannelBasepoints();
    }

    public new void RegisterChannel(ChannelId channelId, ChannelSigningInfo signingInfo)
    {
        base.RegisterChannel(channelId, signingInfo);
    }

    public new void ValidateSignature(ChannelId channelId, CompactSignature signature,
                                      SignedTransaction unsignedTransaction)
    {
        base.ValidateSignature(channelId, signature, unsignedTransaction);
    }

    protected override Key GenerateFundingPrivateKey(uint channelKeyIndex)
    {
        return new Key(_fundingKey.ToBytes());
    }

    protected override Key GetHtlcBasepointSecret(uint channelKeyIndex)
    {
        return new Key(_htlcBasepointSecret.ToBytes());
    }
}
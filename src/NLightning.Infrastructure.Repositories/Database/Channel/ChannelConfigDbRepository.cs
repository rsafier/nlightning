using NLightning.Domain.Bitcoin.ValueObjects;
using NLightning.Domain.Channels.Interfaces;
using NLightning.Domain.Channels.ValueObjects;
using NLightning.Domain.Enums;
using NLightning.Domain.Money;
using NLightning.Infrastructure.Persistence.Contexts;
using NLightning.Infrastructure.Persistence.Entities.Channel;

namespace NLightning.Infrastructure.Repositories.Database.Channel;

public class ChannelConfigDbRepository(NLightningDbContext context)
    : BaseDbRepository<ChannelConfigEntity>(context), IChannelConfigDbRepository
{
    public void Add(ChannelId channelId, ChannelParams config)
    {
        var configEntity = MapDomainToEntity(channelId, config);
        Insert(configEntity);
    }

    public void Update(ChannelId channelId, ChannelParams config)
    {
        var configEntity = MapDomainToEntity(channelId, config);
        base.Update(configEntity);
    }

    public Task DeleteAsync(ChannelId channelId)
    {
        return DeleteByIdAsync(channelId);
    }

    public async Task<ChannelParams?> GetByChannelIdAsync(ChannelId channelId)
    {
        var configEntity = await GetByIdAsync(channelId);

        return configEntity == null ? null : MapEntityToDomain(configEntity);
    }

    internal static ChannelConfigEntity MapDomainToEntity(ChannelId channelId, ChannelParams config)
    {
        return new ChannelConfigEntity
        {
            ChannelId = channelId,
            FeeRatePerKwSatoshis = config.FeeRateAmountPerKw.Satoshi,
            MinimumDepth = config.MinimumDepth,
            OptionAnchorOutputs = config.OptionAnchorOutputs,
            UseScidAlias = (byte)config.UseScidAlias,

            LocalChannelReserveAmountSats = SatoshisOrZero(config.Local.ChannelReserveAmount),
            LocalDustLimitAmountSats = SatoshisOrZero(config.Local.DustLimitAmount),
            LocalHtlcMinimumMsat = MilliSatoshisOrZero(config.Local.HtlcMinimumAmount),
            LocalMaxAcceptedHtlcs = config.Local.MaxAcceptedHtlcs,
            LocalMaxHtlcValueInFlightMsat = MilliSatoshisOrZero(config.Local.MaxHtlcValueInFlight),
            LocalToSelfDelay = config.Local.ToSelfDelay,
            LocalUpfrontShutdownScript = config.Local.UpfrontShutdownScript,

            RemoteChannelReserveAmountSats = SatoshisOrZero(config.Remote.ChannelReserveAmount),
            RemoteDustLimitAmountSats = SatoshisOrZero(config.Remote.DustLimitAmount),
            RemoteHtlcMinimumMsat = MilliSatoshisOrZero(config.Remote.HtlcMinimumAmount),
            RemoteMaxAcceptedHtlcs = config.Remote.MaxAcceptedHtlcs,
            RemoteMaxHtlcValueInFlightMsat = MilliSatoshisOrZero(config.Remote.MaxHtlcValueInFlight),
            RemoteToSelfDelay = config.Remote.ToSelfDelay,
            RemoteUpfrontShutdownScript = config.Remote.UpfrontShutdownScript
        };
    }

    internal static ChannelParams MapEntityToDomain(ChannelConfigEntity entity)
    {
        var local = new ChannelParty(LightningMoney.Satoshis(entity.LocalDustLimitAmountSats),
                                     LightningMoney.Satoshis(entity.LocalChannelReserveAmountSats),
                                     LightningMoney.MilliSatoshis(entity.LocalHtlcMinimumMsat),
                                     entity.LocalMaxAcceptedHtlcs,
                                     LightningMoney.MilliSatoshis(entity.LocalMaxHtlcValueInFlightMsat),
                                     entity.LocalToSelfDelay, ToScript(entity.LocalUpfrontShutdownScript));
        var remote = new ChannelParty(LightningMoney.Satoshis(entity.RemoteDustLimitAmountSats),
                                      LightningMoney.Satoshis(entity.RemoteChannelReserveAmountSats),
                                      LightningMoney.MilliSatoshis(entity.RemoteHtlcMinimumMsat),
                                      entity.RemoteMaxAcceptedHtlcs,
                                      LightningMoney.MilliSatoshis(entity.RemoteMaxHtlcValueInFlightMsat),
                                      entity.RemoteToSelfDelay, ToScript(entity.RemoteUpfrontShutdownScript));

        return new ChannelParams(local, remote, LightningMoney.Satoshis(entity.FeeRatePerKwSatoshis),
                                 entity.MinimumDepth, entity.OptionAnchorOutputs,
                                 (FeatureSupport)entity.UseScidAlias);
    }

    // A default ChannelParty (e.g. the peer's side before accept_channel) has null amounts
    private static long SatoshisOrZero(LightningMoney? amount) => amount?.Satoshi ?? 0;
    private static ulong MilliSatoshisOrZero(LightningMoney? amount) => amount?.MilliSatoshi ?? 0;

    private static BitcoinScript? ToScript(byte[]? script) => script is null ? (BitcoinScript?)null : new BitcoinScript(script);
}
namespace NLightning.Domain.Client.Responses;

using Bitcoin.Wallet.Models;

public sealed record WalletHistoryClientResponse(WalletHistoryRescanState? State);
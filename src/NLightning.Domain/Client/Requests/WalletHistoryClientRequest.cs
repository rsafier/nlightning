namespace NLightning.Domain.Client.Requests;

public sealed record WalletHistoryClientRequest(uint? FromHeight = null, uint? ToHeight = null,
    bool AllowPartial = false, uint AddressCount = 30, bool Cancel = false);
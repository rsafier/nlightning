namespace NLightning.Infrastructure.VlsSigning;

public static class VlsOperations
{
    public const uint Identity = 2000, ReserveIndex = 2001, Allocate = 2002, Basepoints = 2003, Point = 2004,
        Setup = 2005, SignRemote = 2006, ValidateHolder = 2007, Activate = 2008, RevokeHolder = 2009,
        ValidateRevocation = 2010, ForceClose = 2011, MutualClose = 2012, Ecdh = 2013, Invoice = 2014,
        WalletPublicKey = 2015, WalletSign = 2016, PaymentPreimages = 2017, AuthorizeKeysend = 2018,
        AuthorizeInvoice = 2019, PublicAccount = 2020, ChannelUpdate = 2021, NodeAnnouncement = 2022, MarkDataLoss = 2023, BroadcastStatus = 2024, VerifyBroadcastMark = 2025;
}
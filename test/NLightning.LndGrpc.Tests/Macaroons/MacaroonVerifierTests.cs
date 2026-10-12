using System.Net;

namespace NLightning.LndGrpc.Tests.Macaroons;

using LndGrpc.Macaroons;

/// <summary>
/// LND's macaroon rules (<c>macaroons.Service.CheckMacAuth</c> with the bakery checker): the three default macaroons
/// against the per-method permission table, and every refusal — forged, tampered, expired, foreign root key, unknown
/// storage id, unrecognized or third-party caveats, IP locks.
/// </summary>
public class MacaroonVerifierTests
{
    private const string GetInfo = "/lnrpc.Lightning/GetInfo";
    private const string AddInvoice = "/lnrpc.Lightning/AddInvoice";
    private const string SignMessage = "/lnrpc.Lightning/SignMessage";
    private const string WalletBalance = "/lnrpc.Lightning/WalletBalance";

    private static readonly IPAddress s_loopback = IPAddress.Loopback;
    private readonly FixedTimeProvider _time = new(new DateTimeOffset(2029, 6, 1, 0, 0, 0, TimeSpan.Zero));

    private MacaroonVerifier Verifier => new(MacaroonTests.RootKey, _time);

    [Theory]
    [InlineData("admin", GetInfo, MacaroonCheck.Allowed)]
    [InlineData("admin", AddInvoice, MacaroonCheck.Allowed)]
    [InlineData("admin", SignMessage, MacaroonCheck.Allowed)]
    [InlineData("readonly", GetInfo, MacaroonCheck.Allowed)]
    [InlineData("readonly", "/lnrpc.Lightning/ListInvoices", MacaroonCheck.Allowed)]
    [InlineData("readonly", "/lnrpc.Lightning/VerifyMessage", MacaroonCheck.Allowed)]
    [InlineData("readonly", AddInvoice, MacaroonCheck.PermissionDenied)]
    [InlineData("readonly", SignMessage, MacaroonCheck.PermissionDenied)]
    [InlineData("readonly", "/lnrpc.Lightning/OpenChannelSync", MacaroonCheck.PermissionDenied)]
    [InlineData("invoice", AddInvoice, MacaroonCheck.Allowed)]
    [InlineData("invoice", "/lnrpc.Lightning/LookupInvoice", MacaroonCheck.Allowed)]
    [InlineData("invoice", WalletBalance, MacaroonCheck.Allowed)]
    [InlineData("invoice", GetInfo, MacaroonCheck.PermissionDenied)]
    [InlineData("invoice", "/lnrpc.Lightning/ListChannels", MacaroonCheck.PermissionDenied)]
    public void Given_ADefaultMacaroon_When_ChecksAMethod_Then_LndsTableDecides(string kind, string method,
                                                                               MacaroonCheck expected)
    {
        // Arrange
        var ops = kind switch
        {
            "admin" => LndPermissions.Admin,
            "readonly" => LndPermissions.Read,
            _ => LndPermissions.Invoice
        };
        var macaroon = LndMacaroonFiles.NewMacaroon(MacaroonTests.RootKey, ops).Serialize();

        // Act
        var result = Verifier.Check(macaroon, LndPermissions.ForMethod(method)!, method, s_loopback);

        // Assert
        Assert.Equal(expected, result.Outcome);
    }

    [Fact]
    public void Given_AUriPermission_When_Checked_Then_OnlyThatMethodIsAllowed()
    {
        // Arrange
        var macaroon = LndMacaroonFiles.NewMacaroon(MacaroonTests.RootKey, [new MacaroonOp("uri", GetInfo)])
                                       .Serialize();

        // Act / Assert
        Assert.Equal(MacaroonCheck.Allowed,
                     Verifier.Check(macaroon, LndPermissions.ForMethod(GetInfo)!, GetInfo, s_loopback).Outcome);
        Assert.Equal(MacaroonCheck.PermissionDenied,
                     Verifier.Check(macaroon, LndPermissions.ForMethod(WalletBalance)!, WalletBalance, s_loopback)
                             .Outcome);
    }

    [Fact]
    public void Given_GosTimeBeforeVector_When_CheckedBeforeAndAfterExpiry_Then_OnlyBeforeIsAllowed()
    {
        // Arrange: the Go vector expires 2030-01-02T03:04:05Z and grants info:read
        var macaroon = Convert.FromHexString(MacaroonTests.TimeBeforeHex);
        var required = LndPermissions.ForMethod(GetInfo)!;

        // Act
        var before = Verifier.Check(macaroon, required, GetInfo, s_loopback);
        _time.Now = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var after = Verifier.Check(macaroon, required, GetInfo, s_loopback);

        // Assert
        Assert.Equal(MacaroonCheck.Allowed, before.Outcome);
        Assert.Equal(MacaroonCheck.Unauthenticated, after.Outcome);
        Assert.Contains("expired", after.Reason);
    }

    [Fact]
    public void Given_GosIpAddrVector_When_CheckedFromAnotherAddress_Then_Refused()
    {
        // Arrange: time-before 2030 and ipaddr 127.0.0.1
        var macaroon = Convert.FromHexString(MacaroonTests.IpAddrHex);
        var required = LndPermissions.ForMethod(GetInfo)!;

        // Act / Assert
        Assert.Equal(MacaroonCheck.Allowed, Verifier.Check(macaroon, required, GetInfo, s_loopback).Outcome);
        Assert.Equal(MacaroonCheck.Allowed,
                     Verifier.Check(macaroon, required, GetInfo, IPAddress.Loopback.MapToIPv6()).Outcome);
        Assert.Equal(MacaroonCheck.Unauthenticated,
                     Verifier.Check(macaroon, required, GetInfo, IPAddress.Parse("10.0.0.1")).Outcome);
        Assert.Equal(MacaroonCheck.Unauthenticated, Verifier.Check(macaroon, required, GetInfo, null).Outcome);
    }

    [Theory]
    [InlineData("iprange 10.0.0.0/8", "10.1.2.3", MacaroonCheck.Allowed)]
    [InlineData("iprange 10.0.0.0/8", "192.168.1.1", MacaroonCheck.Unauthenticated)]
    [InlineData("lnd-custom my-caveat value", "127.0.0.1", MacaroonCheck.Unauthenticated)]
    [InlineData("declared username bob", "127.0.0.1", MacaroonCheck.Unauthenticated)]
    [InlineData("allow info:read", "127.0.0.1", MacaroonCheck.Unauthenticated)]
    [InlineData("time-before not-a-time", "127.0.0.1", MacaroonCheck.Unauthenticated)]
    [InlineData("whatever", "127.0.0.1", MacaroonCheck.Unauthenticated)]
    public void Given_ACaveat_When_Checked_Then_OnlyUnderstoodAndSatisfiedOnesPass(string condition, string peer,
                                                                                  MacaroonCheck expected)
    {
        // Arrange
        var macaroon = LndMacaroonFiles.NewMacaroon(MacaroonTests.RootKey, LndPermissions.Admin)
                                       .AddFirstPartyCaveat(condition).Serialize();

        // Act
        var result = Verifier.Check(macaroon, LndPermissions.ForMethod(GetInfo)!, GetInfo, IPAddress.Parse(peer));

        // Assert
        Assert.Equal(expected, result.Outcome);
    }

    [Fact]
    public void Given_AForgedOrForeignMacaroon_When_Checked_Then_Unauthenticated()
    {
        // Arrange
        var required = LndPermissions.ForMethod(GetInfo)!;
        var foreign = LndMacaroonFiles.NewMacaroon(new byte[32], LndPermissions.Admin).Serialize();
        var tampered = LndMacaroonFiles.NewMacaroon(MacaroonTests.RootKey, LndPermissions.Read).Serialize();
        tampered[^1] ^= 1;

        // Act / Assert
        Assert.Equal(MacaroonCheck.Unauthenticated, Verifier.Check(foreign, required, GetInfo, s_loopback).Outcome);
        Assert.Equal(MacaroonCheck.Unauthenticated, Verifier.Check(tampered, required, GetInfo, s_loopback).Outcome);
        Assert.Equal(MacaroonCheck.Unauthenticated, Verifier.Check([], required, GetInfo, s_loopback).Outcome);
        Assert.Equal(MacaroonCheck.Unauthenticated,
                     Verifier.Check(Convert.FromHexString("0102"), required, GetInfo, s_loopback).Outcome);
    }

    [Fact]
    public void Given_AnotherStorageId_When_Checked_Then_MacaroonNotFound()
    {
        // Arrange: valid signature under our key, but for root key id "1"
        var id = new MacaroonId(new byte[16], "1"u8.ToArray(), LndPermissions.Admin);
        var macaroon = Macaroon.Create(MacaroonTests.RootKey, id.Encode(), "lnd").Serialize();

        // Act
        var result = Verifier.Check(macaroon, LndPermissions.ForMethod(GetInfo)!, GetInfo, s_loopback);

        // Assert
        Assert.Equal(MacaroonCheck.Unauthenticated, result.Outcome);
        Assert.Contains("not found", result.Reason);
    }

    [Fact]
    public void Given_AnOldBakeryIdVersion_When_Checked_Then_Refused()
    {
        // Arrange: a v2 macaroon whose id is not a bakery version 3 id
        var macaroon = Macaroon.Create(MacaroonTests.RootKey, "0-legacy"u8.ToArray(), "lnd").Serialize();

        // Act / Assert
        Assert.Equal(MacaroonCheck.Unauthenticated,
                     Verifier.Check(macaroon, LndPermissions.ForMethod(GetInfo)!, GetInfo, s_loopback).Outcome);
    }

    [Fact]
    public void Given_AThirdPartyCaveat_When_Checked_Then_Refused()
    {
        // Arrange: the Go vector's header, then a caveat with location "x", id "c" and verification id "v"
        var plain = Convert.FromHexString(MacaroonTests.PlainHex);
        var header = plain[..^35];
        var bytes = header.Concat(new byte[] { 0x01, 0x01, (byte)'x', 0x02, 0x01, (byte)'c', 0x04, 0x01, (byte)'v', 0x00 })
                          .Concat(plain[^35..]).ToArray();
        var macaroon = Macaroon.Deserialize(bytes);

        // Act
        var result = Verifier.Check(bytes, LndPermissions.ForMethod(GetInfo)!, GetInfo, s_loopback);

        // Assert
        Assert.False(macaroon.Caveats[0].IsFirstParty);
        Assert.Equal(MacaroonCheck.Unauthenticated, result.Outcome);
    }

    [Fact]
    public void Given_LndsTable_When_Read_Then_EveryWave1MethodIsListedWithLndsOps()
    {
        Assert.Equal([new MacaroonOp("info", "read")], LndPermissions.ForMethod(GetInfo));
        Assert.Equal([new MacaroonOp("invoices", "write")], LndPermissions.ForMethod(AddInvoice));
        Assert.Equal([new MacaroonOp("message", "write")], LndPermissions.ForMethod(SignMessage));
        Assert.Equal([new MacaroonOp("onchain", "write"), new MacaroonOp("offchain", "write")],
                     LndPermissions.ForMethod("/lnrpc.Lightning/OpenChannelSync"));
        Assert.Equal(4, LndPermissions.ForMethod("/lnrpc.Lightning/GetDebugInfo")!.Count);
        Assert.Equal(67, LndPermissions.Methods.Count);
        Assert.Null(LndPermissions.ForMethod("/lnrpc.Lightning/NoSuchMethod"));
        Assert.Equal(19, LndPermissions.Admin.Count);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
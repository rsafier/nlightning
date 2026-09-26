namespace NLightning.Domain.Tests.Channels.Closing;

using Domain.Channels.Closing;

/// <summary>
/// BOLT 2 <c>shutdown</c> script forms (B2-SHUT-S10, B2-SHUT-R02) and BOLT 3 dust thresholds (B3-DUST-01).
/// </summary>
public class ShutdownScriptValidatorTests
{
    public static TheoryData<string, string, bool, bool, bool> Scripts => new()
    {
        // name, hex, anySegwit, simpleClose, valid
        { "P2WPKH", "0014" + new string('a', 40), false, false, true },
        { "P2WSH", "0020" + new string('b', 64), false, false, true },
        { "P2WPKH wrong length push", "0015" + new string('a', 42), false, false, false },
        { "P2WSH short", "0020" + new string('b', 62), false, false, false },
        { "P2PKH", "76a914" + new string('c', 40) + "88ac", true, true, false },
        { "P2SH", "a914" + new string('d', 40) + "87", true, true, false },
        { "P2TR without anysegwit", "5120" + new string('e', 64), false, false, false },
        { "P2TR with anysegwit", "5120" + new string('e', 64), true, false, true },
        { "v16 push 2 with anysegwit", "6002" + "abcd", true, false, true },
        { "v1 push 40 with anysegwit", "5128" + new string('f', 80), true, false, true },
        { "v1 push 1 with anysegwit", "5101" + "ab", true, false, false },
        { "v1 push 41 with anysegwit", "5129" + new string('f', 82), true, false, false },
        { "v1 push length mismatch", "5120" + new string('e', 62), true, false, false },
        { "OP_RETURN 6 without simple_close", "6a06" + new string('0', 12), true, false, false },
        { "OP_RETURN 6 with simple_close", "6a06" + new string('0', 12), false, true, true },
        { "OP_RETURN 5 with simple_close", "6a05" + new string('0', 10), false, true, false },
        { "OP_RETURN 75 with simple_close", "6a4b" + new string('0', 150), false, true, true },
        { "OP_RETURN PUSHDATA1 76 with simple_close", "6a4c4c" + new string('0', 152), false, true, true },
        { "OP_RETURN PUSHDATA1 80 with simple_close", "6a4c50" + new string('0', 160), false, true, true },
        { "OP_RETURN PUSHDATA1 81 with simple_close", "6a4c51" + new string('0', 162), false, true, false },
        { "OP_RETURN PUSHDATA1 75 with simple_close", "6a4c4b" + new string('0', 150), false, true, false },
        { "empty", "", true, true, false }
    };

    [Theory]
    [MemberData(nameof(Scripts))]
    public void Given_Script_When_IsValid_Then_MatchesBolt2Forms(string name, string hex, bool anySegwit,
                                                                bool simpleClose, bool expected)
    {
        // Arrange
        var script = Convert.FromHexString(hex);

        // Act
        var valid = ShutdownScriptValidator.IsValid(script, anySegwit, simpleClose);

        // Assert
        Assert.True(expected == valid, name);
    }

    [Theory]
    [InlineData("76a914aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa88ac", 546UL)]
    [InlineData("a914aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa87", 540UL)]
    [InlineData("0014aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 294UL)]
    [InlineData("0020aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 330UL)]
    [InlineData("51024e73", 240UL)]
    [InlineData("5120aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 354UL)]
    [InlineData("6a06000000000000", 0UL)]
    [InlineData("deadbeef", 546UL)]
    public void Given_Script_When_GetDustThreshold_Then_Bolt3Value(string hex, ulong expected)
    {
        // Act
        var threshold = ShutdownScriptValidator.GetDustThresholdSat(Convert.FromHexString(hex));

        // Assert
        Assert.Equal(expected, threshold);
    }

    [Fact]
    public void Given_P2PkhScript_When_IsP2Pkh_Then_True()
    {
        // Arrange
        var script = Convert.FromHexString("76a914aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa88ac");

        // Act / Assert
        Assert.True(ShutdownScriptValidator.IsP2Pkh(script));
        Assert.False(ShutdownScriptValidator.IsP2Wpkh(script));
    }
}
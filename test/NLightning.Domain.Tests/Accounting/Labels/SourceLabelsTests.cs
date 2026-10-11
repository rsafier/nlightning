namespace NLightning.Domain.Tests.Accounting.Labels;

using Domain.Accounting.Constants;
using Domain.Accounting.Labels;
using Domain.Accounting.Services;

/// <summary>
/// The shared rules of the operator's label and tags at the source (NL-602 A3-T1, plan §9): label at most 256 UTF-8
/// bytes without control characters; at most 16 tags, key <c>[a-z0-9_.-]{1,32}</c>, value at most 128 UTF-8 bytes;
/// the canonical stored list and the event details.
/// </summary>
public class SourceLabelsTests
{
    [Fact]
    public void Given_ALabelAndUnsortedTags_When_Created_Then_TagsAreSortedAndCanonical()
    {
        // Act
        var labels = SourceLabels.Create("café order", ["project=alpha", "customer=a=b", "empty="]);

        // Assert
        Assert.Equal("café order", labels.Label);
        Assert.Equal(["customer", "empty", "project"], labels.Tags.Select(t => t.Key));
        Assert.Equal("a=b", labels.Tags[0].Value);
        Assert.Equal("", labels.Tags[1].Value);
        Assert.Equal("customer=a=b\nempty=\nproject=alpha", labels.CanonicalTags);
        Assert.Equal(["customer=a=b", "empty=", "project=alpha"], labels.TagStrings);
        Assert.False(labels.IsEmpty);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Given_NoLabelAndNoTags_When_Created_Then_None(string? label)
    {
        // Act
        var labels = SourceLabels.Create(label, null);

        // Assert
        Assert.Same(SourceLabels.None, labels);
        Assert.True(labels.IsEmpty);
        Assert.Null(labels.Label);
        Assert.Null(labels.CanonicalTags);
        Assert.Empty(labels.ToDetailPairs());
    }

    [Fact]
    public void Given_ALabelOfExactly256Bytes_When_Created_Then_Accepted_And257IsRefused()
    {
        // Arrange: "é" is 2 UTF-8 bytes
        var exact = new string('é', 128);
        var over = exact + "a";

        // Act
        var accepted = SourceLabels.TryCreate(exact, null, out _, out var acceptedError);
        var refused = SourceLabels.TryCreate(over, null, out var refusedLabels, out var refusedError);

        // Assert
        Assert.True(accepted, acceptedError);
        Assert.False(refused);
        Assert.Same(SourceLabels.None, refusedLabels);
        Assert.Contains("257 bytes", refusedError);
    }

    [Theory]
    [InlineData("line\nbreak")]
    [InlineData("tab\there")]
    [InlineData("nul\0")]
    [InlineData("c1\u0085")]
    public void Given_AControlCharacterInTheLabel_When_Created_Then_Refused(string label)
    {
        // Act
        var ok = SourceLabels.TryCreate(label, null, out _, out var error);

        // Assert
        Assert.False(ok);
        Assert.Contains("control characters", error);
    }

    [Fact]
    public void Given_ALoneSurrogate_When_Created_Then_Refused()
    {
        // Act
        var ok = SourceLabels.TryCreate("bad\uD800", null, out _, out var error);

        // Assert
        Assert.False(ok);
        Assert.Contains("not valid Unicode", error);
    }

    [Theory]
    [InlineData("noequals", "must be key=value")]
    [InlineData("=v", "must not be empty")]
    [InlineData("Upper=v", "may only hold")]
    [InlineData("with space=v", "may only hold")]
    [InlineData("k=line\nbreak", "control characters")]
    public void Given_ABrokenTag_When_Created_Then_RefusedWithTheRule(string tag, string expected)
    {
        // Act
        var ok = SourceLabels.TryCreate(null, [tag], out _, out var error);

        // Assert
        Assert.False(ok);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void Given_KeysAtTheirLimits_When_Created_Then_ThirtyTwoIsAcceptedAndThirtyThreeRefused()
    {
        // Arrange
        var key32 = "a-b_c.d" + new string('x', 25);
        var key33 = key32 + "y";

        // Act / Assert
        Assert.True(SourceLabels.TryCreate(null, [$"{key32}=1"], out _, out _));
        Assert.False(SourceLabels.TryCreate(null, [$"{key33}=1"], out _, out var error));
        Assert.Contains("longer than 32", error);
    }

    [Fact]
    public void Given_AValueOf129Bytes_When_Created_Then_Refused()
    {
        // Act
        var accepted = SourceLabels.TryCreate(null, ["k=" + new string('v', 128)], out _, out _);
        var refused = SourceLabels.TryCreate(null, ["k=" + new string('v', 129)], out _, out var error);

        // Assert
        Assert.True(accepted);
        Assert.False(refused);
        Assert.Contains("129 bytes", error);
    }

    [Fact]
    public void Given_SixteenAndSeventeenTags_When_Created_Then_OnlySixteenAreAccepted()
    {
        // Arrange
        var sixteen = Enumerable.Range(0, 16).Select(i => $"k{i:00}=v").ToArray();

        // Act
        var accepted = SourceLabels.TryCreate(null, sixteen, out var labels, out _);
        var refused = SourceLabels.TryCreate(null, [.. sixteen, "k16=v"], out _, out var error);

        // Assert
        Assert.True(accepted);
        Assert.Equal(16, labels.Tags.Count);
        Assert.False(refused);
        Assert.Contains($"At most {AccountingSchemaLimits.MaxTags} tags", error);
    }

    [Fact]
    public void Given_ARepeatedKey_When_Created_Then_Refused()
    {
        // Act
        var ok = SourceLabels.TryCreate(null, ["k=1", "k=2"], out _, out var error);

        // Assert
        Assert.False(ok);
        Assert.Contains("more than once", error);
    }

    [Fact]
    public void Given_SixteenLongTags_When_Created_Then_TheOneKibListLimitRefusesThem()
    {
        // Arrange: 16 x (2 + 1 + 100) bytes plus 15 separators > 1024
        var tags = Enumerable.Range(0, 16).Select(i => $"k{i:x}={new string('v', 100)}").ToArray();

        // Act
        var ok = SourceLabels.TryCreate(null, tags, out _, out var error);

        // Assert
        Assert.False(ok);
        Assert.Contains($"{AccountingSchemaLimits.TagsMaxBytes} bytes", error);
    }

    [Fact]
    public void Given_ABrokenRule_When_CreateIsCalled_Then_ArgumentException()
    {
        // Act / Assert
        var exception = Assert.Throws<ArgumentException>(() => SourceLabels.Create(null, ["K=v"]));
        Assert.Contains("may only hold", exception.Message);
    }

    [Fact]
    public void Given_StoredValues_When_Read_Then_TheSameLabelsComeBack()
    {
        // Arrange
        var created = SourceLabels.Create("rent", ["unit=4b", "month=2026-10"]);

        // Act
        var stored = SourceLabels.FromStored(created.Label, created.CanonicalTags);

        // Assert
        Assert.Equal("rent", stored.Label);
        Assert.Equal(created.CanonicalTags, stored.CanonicalTags);
        Assert.Same(SourceLabels.None, SourceLabels.FromStored(null, null));
        Assert.Same(SourceLabels.None, SourceLabels.FromStored("", ""));
    }

    [Fact]
    public void Given_AStoredListWithJunkLines_When_Read_Then_OnlyValidKeysAreKeptAndNothingThrows()
    {
        // Act
        var stored = SourceLabels.FromStored(null, "b=2\nnot a tag\n=x\nBad=1\na=1\nb=3");

        // Assert
        Assert.Equal("a=1\nb=2", stored.CanonicalTags);
    }

    [Fact]
    public void Given_Labels_When_WrittenToDetailsAndReadBack_Then_TheyRoundTrip()
    {
        // Arrange
        var labels = SourceLabels.Create("payroll october", ["employee=42", "dept=ops"]);

        // Act
        var details = AccountingDetailsCodec.Create([("kind", "bolt11"), .. labels.ToDetailPairs()]);
        var decoded = AccountingDetailsCodec.Decode(AccountingDetailsCodec.Encode(details));
        var read = SourceLabels.FromDetails(decoded);

        // Assert
        Assert.Equal("payroll october", decoded[AccountingDetailKeys.Label]);
        Assert.Equal("ops", decoded[AccountingDetailKeys.TagPrefix + "dept"]);
        Assert.Equal("42", decoded["tag.employee"]);
        Assert.Equal(labels.Label, read.Label);
        Assert.Equal(labels.CanonicalTags, read.CanonicalTags);
        Assert.Same(SourceLabels.None, SourceLabels.FromDetails(AccountingDetailsCodec.Create(("kind", "bolt11"))));
        Assert.Same(SourceLabels.None, SourceLabels.FromDetails(null));
    }

    [Fact]
    public void Given_ATag_When_Formatted_Then_KeyEqualsValue()
    {
        // Act / Assert
        Assert.Equal("k=v", new SourceTag("k", "v").ToString());
        Assert.True(SourceLabelRules.TryParseTag("k=v=w", out var tag, out _));
        Assert.Equal(new SourceTag("k", "v=w"), tag);
        Assert.True(SourceLabelRules.IsKeyCharacter('-'));
        Assert.False(SourceLabelRules.IsKeyCharacter('A'));
        Assert.Null(SourceLabelRules.ValidateLabel(null));
    }
}
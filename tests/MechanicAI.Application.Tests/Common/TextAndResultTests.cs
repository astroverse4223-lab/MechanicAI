using MechanicAI.Application.Common;

namespace MechanicAI.Application.Tests.Common;

public class TextTests
{
    [Theory]
    [InlineData(null, 5, "")]
    [InlineData("", 5, "")]
    [InlineData("short", 5, "short")]
    [InlineData("exactly10!", 10, "exactly10!")]
    [InlineData("this is too long", 8, "this is…")]
    public void Truncate_AddsEllipsisOnlyWhenNeeded(string? value, int max, string expected)
    {
        Assert.Equal(expected, Text.Truncate(value, max));
    }

    [Fact]
    public void Truncate_RespectsCustomEllipsisLength()
    {
        Assert.Equal("abc...", Text.Truncate("abcdefghij", 6, "..."));
        Assert.Equal(6, Text.Truncate("abcdefghij", 6, "...").Length);
    }

    [Fact]
    public void CollapseWhitespace_NormalizesRuns()
    {
        Assert.Equal("a b c", Text.CollapseWhitespace("  a \t\n b   c  "));
        Assert.Equal(string.Empty, Text.CollapseWhitespace(null));
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("a", 1)]
    [InlineData("abcd", 1)]
    [InlineData("abcde", 2)]
    public void EstimateTokens_RoundsUpAtFourCharsPerToken(string? value, int expected)
    {
        Assert.Equal(expected, Text.EstimateTokens(value));
    }

    [Fact]
    public void Sha256Hex_IsLowercaseHexOfUtf8()
    {
        const string expected = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
        Assert.Equal(expected, Text.Sha256Hex("abc"));
        Assert.Equal(expected, Text.Sha256Hex("abc"u8.ToArray()));
    }

    [Theory]
    [InlineData("FORD", "Ford")]
    [InlineData("  chevrolet ", "Chevrolet")]
    [InlineData("BMW", "BMW")]
    [InlineData("gmc", "GMC")]
    [InlineData("RAM", "Ram")]
    [InlineData("MERCEDES-BENZ", "Mercedes-Benz")]
    [InlineData("LAND ROVER", "Land Rover")]
    [InlineData("mclaren", "McLaren")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void NormalizeMake_HandlesSpecialCases(string? make, string expected)
    {
        Assert.Equal(expected, Text.NormalizeMake(make));
    }

    [Fact]
    public void TitleCase_LowercasesThenCapitalizes()
    {
        Assert.Equal("Grand Cherokee", Text.TitleCase("GRAND CHEROKEE"));
        Assert.Equal(string.Empty, Text.TitleCase("  "));
    }

    [Fact]
    public void Tokenize_KeepsTechnicalTokensTogether()
    {
        var tokens = Text.Tokenize("Check the 5.3L V8 A/F sensor, P0171 & B1-S1!").ToList();

        Assert.Equal(["check", "the", "5.3l", "v8", "a/f", "sensor", "p0171", "b1-s1"], tokens);
    }

    [Fact]
    public void Tokenize_EmptyForBlank()
    {
        Assert.Empty(Text.Tokenize(" "));
        Assert.Empty(Text.Tokenize(null));
    }
}

public class ResultTests
{
    [Fact]
    public void Success_HasNoError()
    {
        var result = Result.Success();
        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Null(result.Error);
    }

    [Fact]
    public void ErrorConvertsImplicitlyToFailure()
    {
        Result result = Error.Validation("bad");
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Validation, result.Error!.Kind);
        Assert.Equal("bad", result.Error.Message);
    }

    [Fact]
    public void GenericResult_ValueAndErrorConversions()
    {
        Result<int> ok = 42;
        Result<int> failed = Error.NotFound("Vehicle");

        Assert.True(ok.IsSuccess);
        Assert.Equal(42, ok.Value);
        Assert.True(ok.HasValue);
        Assert.True(failed.IsFailure);
        Assert.False(failed.HasValue);
        Assert.Equal("Vehicle was not found.", failed.Error!.Message);
        Assert.Equal(ErrorKind.NotFound, failed.Error.Kind);
    }

    [Fact]
    public void HasValue_IsFalseForNullSuccessValue()
    {
        var result = Result.Success<string?>(null);
        Assert.True(result.IsSuccess);
        Assert.False(result.HasValue);
    }

    [Fact]
    public void StaticFactories_ProduceExpectedKinds()
    {
        Assert.Equal(ErrorKind.Offline, Error.Offline("Web search").Kind);
        Assert.Contains("Web search requires an internet connection", Error.Offline("Web search").Message, StringComparison.Ordinal);
        Assert.Equal(ErrorKind.Cancelled, Error.Cancelled().Kind);
        Assert.Equal(ErrorKind.NotConfigured, Error.NotConfigured("x").Kind);
        Assert.Equal(ErrorKind.Validation, Result.Failure<int>(Error.Validation("v")).Error!.Kind);
    }

    [Fact]
    public void FromException_MapsExternalServiceException()
    {
        var ex = new ExternalServiceException("NHTSA", ErrorKind.RateLimited, "Too many requests.");

        var error = Error.FromException(ex);

        Assert.Equal(ErrorKind.RateLimited, error.Kind);
        Assert.Equal("Too many requests.", error.Message);
        Assert.Contains("NHTSA", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void FromException_MapsCancellation()
    {
        Assert.Equal(ErrorKind.Cancelled, Error.FromException(new OperationCanceledException()).Kind);
        Assert.Equal(ErrorKind.Cancelled, Error.FromException(new TaskCanceledException()).Kind);
    }

    [Fact]
    public void FromException_MapsArgumentExceptionToValidation()
    {
        var error = Error.FromException(new ArgumentException("Year is out of range."));
        Assert.Equal(ErrorKind.Validation, error.Kind);
        Assert.Equal("Year is out of range.", error.Message);
    }

    [Fact]
    public void FromException_HidesUnexpectedDetailsFromMessage()
    {
        var error = Error.FromException(new InvalidOperationException("secret internals"));

        Assert.Equal(ErrorKind.Unexpected, error.Kind);
        Assert.DoesNotContain("secret internals", error.Message, StringComparison.Ordinal);
        Assert.Contains("secret internals", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalServiceException_DefaultConstructorsAreUnexpected()
    {
        Assert.Equal(ErrorKind.Unexpected, new ExternalServiceException().Kind);
        var ex = new ExternalServiceException("boom", new IOException());
        Assert.Equal("boom", ex.UserMessage);
        Assert.IsType<IOException>(ex.InnerException);
    }
}

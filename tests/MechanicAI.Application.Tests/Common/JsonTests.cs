using MechanicAI.Application.Common;
using MechanicAI.Domain.Enums;

namespace MechanicAI.Application.Tests.Common;

public class JsonTests
{
    [Fact]
    public void ExtractJson_ReturnsBareObject()
    {
        Assert.Equal("{\"a\":1}", Json.ExtractJson("  {\"a\":1}  "));
    }

    [Fact]
    public void ExtractJson_UnwrapsMarkdownFence()
    {
        var text = "Here is the result:\n```json\n{\"causes\": [\"coil\"]}\n```\nLet me know.";
        Assert.Equal("{\"causes\": [\"coil\"]}", Json.ExtractJson(text));
    }

    [Fact]
    public void ExtractJson_UnwrapsFenceWithoutLanguage()
    {
        var text = "```\n[1, 2, 3]\n```";
        Assert.Equal("[1, 2, 3]", Json.ExtractJson(text));
    }

    [Fact]
    public void ExtractJson_FindsObjectInsideProse()
    {
        var text = "Sure! The answer is {\"ok\": true, \"n\": {\"x\": [1]}} and that's all.";
        Assert.Equal("{\"ok\": true, \"n\": {\"x\": [1]}}", Json.ExtractJson(text));
    }

    [Fact]
    public void ExtractJson_IgnoresBracesInsideStrings()
    {
        var text = "prefix {\"text\": \"a } brace and a \\\" quote {\", \"b\": 2} suffix }";
        Assert.Equal("{\"text\": \"a } brace and a \\\" quote {\", \"b\": 2}", Json.ExtractJson(text));
    }

    [Fact]
    public void ExtractJson_ReturnsFirstOfSeveralObjects()
    {
        Assert.Equal("{\"a\":1}", Json.ExtractJson("{\"a\":1} {\"b\":2}"));
    }

    [Fact]
    public void ExtractJson_ReturnsArrayWhenItComesFirst()
    {
        Assert.Equal("[{\"a\":1}]", Json.ExtractJson("list: [{\"a\":1}] trailing {\"b\":2}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no json here")]
    [InlineData("{\"unterminated\": [1, 2")]
    public void ExtractJson_ReturnsNullWhenNoCompleteJson(string? text)
    {
        Assert.Null(Json.ExtractJson(text));
    }

    [Fact]
    public void ExtractJson_FallsBackToScanningWhenFenceHasNoJson()
    {
        var text = "```\nnot json\n```\nthen {\"a\": 1}";
        Assert.Equal("{\"a\": 1}", Json.ExtractJson(text));
    }

    private sealed class Sample
    {
        public string? Name { get; set; }

        public SourceType Type { get; set; }

        public int Count { get; set; }
    }

    [Fact]
    public void Serialize_UsesCamelCaseStringEnumsAndOmitsNulls()
    {
        var json = Json.Serialize(new Sample { Type = SourceType.Forum, Count = 2 });
        Assert.Equal("{\"type\":\"Forum\",\"count\":2}", json);
    }

    [Fact]
    public void Deserialize_IsLenient()
    {
        var value = Json.Deserialize<Sample>("{ \"NAME\": \"x\", // comment\n \"type\": \"manufacturer\", \"count\": \"5\", }");

        Assert.NotNull(value);
        Assert.Equal("x", value.Name);
        Assert.Equal(SourceType.Manufacturer, value.Type);
        Assert.Equal(5, value.Count);
    }

    [Fact]
    public void Deserialize_ReturnsDefaultForBlank()
    {
        Assert.Null(Json.Deserialize<Sample>("  "));
    }

    [Fact]
    public void TryDeserialize_ReportsFailureForInvalidJson()
    {
        Assert.False(Json.TryDeserialize<Sample>("{not json", out var value));
        Assert.Null(value);
        Assert.True(Json.TryDeserialize<Sample>("{\"count\":1}", out var ok));
        Assert.Equal(1, ok!.Count);
    }
}

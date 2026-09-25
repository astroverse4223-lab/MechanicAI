using System.Globalization;
using System.Text;
using MechanicAI.Application.Abstractions;
using MechanicAI.Application.KnowledgeBase;

namespace MechanicAI.Application.Tests.KnowledgeBase;

public class DocumentChunkerTests
{
    private static ExtractedPage Page(int number, string text) => new(number, text, 612, 792, [], false);

    private static string BodyLines(int count, string prefix = "The engine control module monitors sensor inputs")
    {
        var sb = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"{prefix} and line {i:000} keeps going\n");
        }

        return sb.ToString();
    }

    [Fact]
    public void Chunk_ShortPageBecomesOneChunkWithHeading()
    {
        var documentId = Guid.NewGuid();
        const string text = "FUEL SYSTEM\nThe fuel pump supplies pressure to the rail through the filter.";

        var chunk = Assert.Single(DocumentChunker.Chunk(documentId, [Page(3, text)]));

        Assert.Equal(documentId, chunk.DocumentId);
        Assert.Equal(0, chunk.Ordinal);
        Assert.Equal(3, chunk.PageNumber);
        Assert.Equal("FUEL SYSTEM", chunk.Heading);
        Assert.Equal(text, chunk.Text);
        Assert.Equal(0, chunk.CharStart);
        Assert.Equal(text.Length, chunk.CharEnd);
        Assert.Equal((text.Length + 3) / 4, chunk.TokenEstimate);
    }

    [Fact]
    public void Chunk_SkipsBlankPagesAndTinyFragments()
    {
        var chunks = DocumentChunker.Chunk(Guid.NewGuid(), [Page(1, "   \n  "), Page(2, "tiny"), Page(3, string.Empty)]);

        Assert.Empty(chunks);
    }

    [Fact]
    public void Chunk_LongPageIsSplitIntoOverlappingBoundedChunks()
    {
        var text = BodyLines(80);

        var chunks = DocumentChunker.Chunk(Guid.NewGuid(), [Page(1, text)]);

        Assert.True(chunks.Count >= 3);
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Ordinal));
        foreach (var chunk in chunks)
        {
            Assert.InRange(chunk.Text.Length, 20, DocumentChunker.MaxChars);
            Assert.Equal(text[chunk.CharStart..chunk.CharEnd].Trim(), chunk.Text);
        }

        for (var i = 1; i < chunks.Count; i++)
        {
            Assert.True(chunks[i].CharStart < chunks[i - 1].CharEnd, "consecutive chunks overlap");
            Assert.True(chunks[i].CharStart > chunks[i - 1].CharStart, "chunks advance");
        }

        Assert.Equal(text.TrimEnd('\n').Length, chunks[^1].CharEnd);
    }

    [Fact]
    public void Chunk_TextWithoutLineBreaksIsStillBounded()
    {
        var sentence = "The oxygen sensor switches rich and lean around stoichiometry. ";
        var text = string.Concat(Enumerable.Repeat(sentence, 120));

        var chunks = DocumentChunker.Chunk(Guid.NewGuid(), [Page(1, text)]);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c =>
        {
            Assert.True(c.Text.Length <= DocumentChunker.MaxChars);
            Assert.Equal(text[c.CharStart..c.CharEnd].Trim(), c.Text);
        });
        Assert.Equal(0, chunks[0].CharStart);
        Assert.Equal(text.Length, chunks[^1].CharEnd);
    }

    [Fact]
    public void Chunk_NeverSpansPagesAndCarriesHeadingForward()
    {
        var page1 = "1.2 Fuel Pump Removal\n" + BodyLines(3);
        var page2 = BodyLines(3, "Disconnect the fuel pump electrical connector");

        var chunks = DocumentChunker.Chunk(Guid.NewGuid(), [Page(1, page1), Page(2, page2)]);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(1, chunks[0].PageNumber);
        Assert.Equal(2, chunks[1].PageNumber);
        Assert.Equal("1.2 Fuel Pump Removal", chunks[0].Heading);
        Assert.Equal("1.2 Fuel Pump Removal", chunks[1].Heading);
        Assert.Equal([0, 1], chunks.Select(c => c.Ordinal));
    }

    [Fact]
    public void Chunk_StartsNewChunkAtHeadingOnceContentIsSubstantial()
    {
        var text = "OVERVIEW\n" + BodyLines(10) + "DIAGNOSIS\n" + BodyLines(3);

        var chunks = DocumentChunker.Chunk(Guid.NewGuid(), [Page(1, text)]);

        Assert.Equal(["OVERVIEW", "DIAGNOSIS"], chunks.Select(c => c.Heading));
        Assert.StartsWith("DIAGNOSIS", chunks[1].Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("WIRING DIAGRAM", true)]
    [InlineData("Removal:", true)]
    [InlineData("1.2 Fuel Pump Removal", true)]
    [InlineData("IV. Specifications", true)]
    [InlineData("This is a normal sentence.", false)]
    [InlineData("lower case line without colon", false)]
    [InlineData("ab", false)]
    [InlineData("12345", false)]
    [InlineData("ENDS WITH COMMA,", false)]
    public void IsHeading_Heuristics(string line, bool expected)
    {
        Assert.Equal(expected, DocumentChunker.IsHeading(line));
    }
}

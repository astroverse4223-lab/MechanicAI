using System.Text.RegularExpressions;
using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Common;
using MechanicAI.Domain.Entities;

namespace MechanicAI.Application.KnowledgeBase;

/// <summary>
/// Splits extracted pages into overlapping passages (~1,200 characters) on line boundaries.
/// Chunks never span pages, so every passage has an exact page and character range for
/// citation and highlighting. The most recent heading is carried with each chunk.
/// </summary>
public static partial class DocumentChunker
{
    public const int TargetChars = 1200;
    public const int MaxChars = 1800;
    public const int OverlapChars = 200;

    public static IReadOnlyList<DocumentChunk> Chunk(Guid documentId, IReadOnlyList<ExtractedPage> pages)
    {
        var chunks = new List<DocumentChunk>();
        string? heading = null;
        var ordinal = 0;

        foreach (var page in pages)
        {
            var text = page.Text;
            if (string.IsNullOrWhiteSpace(text)) continue;

            var lines = SplitLines(text);
            var start = 0;
            while (start < lines.Count)
            {
                var chunkStartOffset = lines[start].Offset;
                var end = start;
                var length = 0;
                string? chunkHeading = heading;

                while (end < lines.Count)
                {
                    var line = lines[end];
                    if (IsHeading(line.Text))
                    {
                        // Start a new chunk at a heading if the current one already has content.
                        if (length >= TargetChars / 3 && end > start) break;
                        heading = Text.Truncate(line.Text.Trim(), 200);
                        chunkHeading ??= heading;
                        if (end == start) chunkHeading = heading;
                    }

                    length = line.Offset + line.Text.Length - chunkStartOffset;
                    end++;
                    if (length >= TargetChars) break;
                }

                // Hard cap for pages without line breaks.
                var chunkEndOffset = Math.Min(lines[end - 1].Offset + lines[end - 1].Text.Length, chunkStartOffset + MaxChars);
                var body = text[chunkStartOffset..chunkEndOffset].Trim();
                if (body.Length >= 20)
                {
                    chunks.Add(new DocumentChunk
                    {
                        DocumentId = documentId,
                        Ordinal = ordinal++,
                        PageNumber = page.PageNumber,
                        Heading = chunkHeading,
                        Text = body,
                        TokenEstimate = Text.EstimateTokens(body),
                        CharStart = chunkStartOffset,
                        CharEnd = chunkEndOffset,
                    });
                }

                if (end >= lines.Count) break;

                // Overlap: back up to the first line that starts within OverlapChars of the end.
                var next = end;
                while (next - 1 > start && chunkEndOffset - lines[next - 1].Offset < OverlapChars) next--;
                start = next <= start ? end : next;
            }
        }

        return chunks;
    }

    internal static bool IsHeading(string line)
    {
        var t = line.Trim();
        if (t.Length is < 3 or > 90) return false;
        if (t.EndsWith('.') || t.EndsWith(',')) return false;
        var letters = t.Count(char.IsLetter);
        if (letters < 3) return false;
        var upper = t.Count(char.IsUpper);
        if (upper / (double)letters > 0.75) return true;
        return NumberedHeadingRegex().IsMatch(t) || (t.EndsWith(':') && t.Length < 60);
    }

    private static List<(int Offset, string Text)> SplitLines(string text)
    {
        var lines = new List<(int, string)>();
        var offset = 0;
        foreach (var raw in text.Split('\n'))
        {
            if (raw.Length > MaxChars)
            {
                // Very long line (text without breaks): split into sentences-ish pieces.
                var pos = 0;
                while (pos < raw.Length)
                {
                    var len = Math.Min(TargetChars, raw.Length - pos);
                    var cut = raw.LastIndexOf(". ", pos + len - 1, len, StringComparison.Ordinal);
                    if (cut > pos + TargetChars / 2) len = cut - pos + 1;
                    lines.Add((offset + pos, raw.Substring(pos, len)));
                    pos += len;
                }
            }
            else if (raw.Trim().Length > 0)
            {
                lines.Add((offset, raw));
            }

            offset += raw.Length + 1;
        }

        return lines;
    }

    [GeneratedRegex(@"^(\d+(\.\d+){0,3}|[A-Z]\.|[IVX]+\.)\s+[A-Z][A-Za-z]")]
    private static partial Regex NumberedHeadingRegex();
}

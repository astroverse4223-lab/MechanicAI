using System.Text;
using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Common;
using MechanicAI.Domain.Entities;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.Exceptions;

namespace MechanicAI.Infrastructure.Documents;

/// <summary>
/// Extracts PDF text with word positions (via PdfPig). Pages are laid out in reading order
/// (Docstrum segmentation handles multi-column service manuals), and each word records its
/// character offset in the page text and a normalized bounding box, so a retrieved passage
/// can be highlighted on the rendered page. PdfPig never executes embedded JavaScript.
/// </summary>
public sealed class PdfTextExtractor : IDocumentTextExtractor
{
    public bool CanExtract(string extension) => extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    public Task<ExtractedDocument> ExtractAsync(string path, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run(() => Extract(path, progress, cancellationToken), cancellationToken);

    private static ExtractedDocument Extract(string path, IProgress<double>? progress, CancellationToken ct)
    {
        PdfDocument pdf;
        try
        {
            pdf = PdfDocument.Open(path, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
        }
        catch (PdfDocumentEncryptedException ex)
        {
            throw new ExternalServiceException("PDF", ErrorKind.Validation, "This PDF is password-protected. Remove the password and upload it again.", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ExternalServiceException("PDF", ErrorKind.InvalidResponse, "The PDF could not be opened. It may be damaged.", ex);
        }

        using (pdf)
        {
            var pages = new List<ExtractedPage>(pdf.NumberOfPages);
            for (var number = 1; number <= pdf.NumberOfPages; number++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var page = pdf.GetPage(number);
                    pages.Add(ExtractPage(page));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A single malformed page must not fail the whole document.
                    pages.Add(new ExtractedPage(number, string.Empty, 612, 792, [], false));
                }

                progress?.Report((double)number / pdf.NumberOfPages);
            }

            var title = pdf.Information.Title;
            return new ExtractedDocument(pages, string.IsNullOrWhiteSpace(title) ? null : title.Trim());
        }
    }

    internal static ExtractedPage ExtractPage(Page page)
    {
        var width = page.Width <= 0 ? 612 : page.Width;
        var height = page.Height <= 0 ? 792 : page.Height;
        var words = page.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
        var sb = new StringBuilder();
        var pageWords = new List<PageWord>(words.Count);

        void AddLine(IEnumerable<Word> lineWords)
        {
            var first = true;
            foreach (var word in lineWords)
            {
                if (!first) sb.Append(' ');
                first = false;
                var box = word.BoundingBox;
                pageWords.Add(new PageWord
                {
                    Text = word.Text,
                    Offset = sb.Length,
                    X = Clamp01(box.Left / width),
                    Y = Clamp01(1 - (box.Top / height)),
                    W = Clamp01(box.Width / width),
                    H = Clamp01(box.Height / height),
                });
                sb.Append(word.Text);
            }

            sb.Append('\n');
        }

        try
        {
            var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
            var ordered = UnsupervisedReadingOrderDetector.Instance.Get(blocks);
            foreach (var block in ordered)
            {
                foreach (var line in block.TextLines) AddLine(line.Words);
                sb.Append('\n');
            }
        }
        catch (Exception)
        {
            sb.Clear();
            pageWords.Clear();
            foreach (var line in GroupLines(words)) AddLine(line);
        }

        return new ExtractedPage(page.Number, sb.ToString().TrimEnd(), width, height, pageWords, false);
    }

    private static IEnumerable<List<Word>> GroupLines(List<Word> words)
    {
        var sorted = words.OrderByDescending(w => w.BoundingBox.Bottom).ThenBy(w => w.BoundingBox.Left).ToList();
        var line = new List<Word>();
        double? baseline = null;
        foreach (var w in sorted)
        {
            var tolerance = Math.Max(2, w.BoundingBox.Height * 0.5);
            if (baseline is { } b && Math.Abs(w.BoundingBox.Bottom - b) > tolerance)
            {
                yield return line.OrderBy(x => x.BoundingBox.Left).ToList();
                line = [];
            }

            line.Add(w);
            baseline = w.BoundingBox.Bottom;
        }

        if (line.Count > 0) yield return line.OrderBy(x => x.BoundingBox.Left).ToList();
    }

    private static double Clamp01(double v) => double.IsNaN(v) ? 0 : Math.Clamp(v, 0, 1);
}

/// <summary>Plain text and Markdown files, split into ~4,000-character sections for citation.</summary>
public sealed class PlainTextExtractor : IDocumentTextExtractor
{
    private const int SectionChars = 4000;

    public bool CanExtract(string extension) =>
        extension.ToLowerInvariant() is ".txt" or ".md" or ".markdown" or ".csv" or ".log";

    public async Task<ExtractedDocument> ExtractAsync(string path, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var text = await File.ReadAllTextAsync(path, cancellationToken);
        var pages = new List<ExtractedPage>();
        var number = 1;
        var start = 0;
        while (start < text.Length)
        {
            var end = Math.Min(text.Length, start + SectionChars);
            if (end < text.Length)
            {
                var paragraph = text.LastIndexOf("\n\n", end, Math.Min(end - start, 1500), StringComparison.Ordinal);
                if (paragraph > start) end = paragraph + 2;
            }

            pages.Add(new ExtractedPage(number++, text[start..end], 0, 0, [], false));
            start = end;
        }

        progress?.Report(1);
        var firstLine = text.Split('\n', 2)[0].TrimStart('#', ' ').Trim();
        return new ExtractedDocument(pages, firstLine.Length is > 3 and < 120 ? firstLine : null);
    }
}

/// <summary>Dispatches to the extractor that understands the file type.</summary>
public sealed class CompositeTextExtractor(IEnumerable<IDocumentTextExtractor> extractors) : IDocumentTextExtractor
{
    private readonly IReadOnlyList<IDocumentTextExtractor> _extractors = extractors.Where(e => e is not CompositeTextExtractor).ToList();

    public bool CanExtract(string extension) => _extractors.Any(e => e.CanExtract(extension));

    public Task<ExtractedDocument> ExtractAsync(string path, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(path);
        var extractor = _extractors.FirstOrDefault(e => e.CanExtract(extension))
                        ?? throw new ExternalServiceException("Documents", ErrorKind.Validation, $"Files of type {extension} cannot be indexed.");
        return extractor.ExtractAsync(path, progress, cancellationToken);
    }
}

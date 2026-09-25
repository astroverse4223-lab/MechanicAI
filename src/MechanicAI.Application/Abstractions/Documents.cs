using MechanicAI.Domain.Entities;

namespace MechanicAI.Application.Abstractions;

public sealed record ExtractedPage(
    int PageNumber,
    string Text,
    double Width,
    double Height,
    IReadOnlyList<PageWord> Words,
    bool FromOcr);

public sealed record ExtractedDocument(IReadOnlyList<ExtractedPage> Pages, string? Title)
{
    /// <summary>Pages that have essentially no text layer (scanned) and would benefit from OCR.</summary>
    public IEnumerable<int> PagesNeedingOcr => Pages.Where(p => p.Text.Trim().Length < 25).Select(p => p.PageNumber);
}

/// <summary>Extracts text (and word positions where available) from PDFs, text, and markdown files.</summary>
public interface IDocumentTextExtractor
{
    bool CanExtract(string extension);

    Task<ExtractedDocument> ExtractAsync(string path, IProgress<double>? progress, CancellationToken cancellationToken);
}

public sealed record OcrResult(string Text, double Width, double Height, IReadOnlyList<PageWord> Words);

/// <summary>
/// Optical character recognition. On Windows this uses the built-in Windows.Media.Ocr engine,
/// which runs entirely on the workstation.
/// </summary>
public interface IOcrEngine
{
    bool IsAvailable { get; }

    Task<OcrResult> RecognizeImageAsync(byte[] imageBytes, CancellationToken cancellationToken);

    Task<OcrResult?> RecognizePdfPageAsync(string pdfPath, int pageNumber, CancellationToken cancellationToken);
}

public sealed record RenderedImage(byte[] PngBytes, int PixelWidth, int PixelHeight);

/// <summary>Renders PDF pages to images (viewer, OCR, and vision-model input).</summary>
public interface IPdfPageRenderer
{
    bool IsAvailable { get; }

    Task<RenderedImage> RenderPageAsync(string pdfPath, int pageNumber, int targetWidth, CancellationToken cancellationToken);
}

public sealed record PreparedImage(byte[] Data, string MediaType, int Width, int Height);

public sealed record DecodedPixels(byte[] Bgra, int Width, int Height);

/// <summary>Decodes, downsizes, and re-encodes images before analysis.</summary>
public interface IImageProcessor
{
    bool IsAvailable { get; }

    Task<PreparedImage> PrepareForAnalysisAsync(byte[] data, int maxDimension, CancellationToken cancellationToken);

    Task<DecodedPixels> DecodePixelsAsync(byte[] data, int maxDimension, CancellationToken cancellationToken);
}

/// <summary>Reads 1D/2D barcodes (VIN labels use Code 39, Data Matrix, or QR).</summary>
public interface IBarcodeReader
{
    IReadOnlyList<string> Decode(DecodedPixels pixels);
}

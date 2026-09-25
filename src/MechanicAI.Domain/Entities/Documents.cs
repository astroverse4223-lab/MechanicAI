using MechanicAI.Domain.Common;
using MechanicAI.Domain.Enums;
using MechanicAI.Domain.Interfaces;

namespace MechanicAI.Domain.Entities;

/// <summary>An uploaded document in the technician's private knowledge base.</summary>
public class Document : Entity, IAggregateRoot, IVehicleScoped
{
    public string Title { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    /// <summary>File name inside the application's document store.</summary>
    public string StoredFileName { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public string Sha256 { get; set; } = string.Empty;

    public DocumentKind Kind { get; set; } = DocumentKind.Other;

    public string? Description { get; set; }

    public Guid? VehicleId { get; set; }

    /// <summary>Optional applicability, used to filter retrieval by vehicle.</summary>
    public string? Make { get; set; }

    public string? Model { get; set; }

    public int? YearFrom { get; set; }

    public int? YearTo { get; set; }

    public int PageCount { get; set; }

    public int ChunkCount { get; set; }

    public DocumentStatus Status { get; set; } = DocumentStatus.Pending;

    public string? StatusMessage { get; set; }

    public DateTime? IndexedUtc { get; set; }

    public string? EmbeddingModel { get; set; }

    public bool UsedOcr { get; set; }

    public List<string> Tags { get; set; } = [];

    public bool IsWiringDiagram => Kind == DocumentKind.WiringDiagram;

    public bool AppliesTo(int? year, string? make, string? model)
    {
        if (!string.IsNullOrWhiteSpace(Make) && !string.IsNullOrWhiteSpace(make) &&
            !string.Equals(Make, make, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(Model) && !string.IsNullOrWhiteSpace(model) &&
            !model.Contains(Model, StringComparison.OrdinalIgnoreCase) &&
            !Model.Contains(model, StringComparison.OrdinalIgnoreCase)) return false;
        if (year is { } y)
        {
            if (YearFrom is { } from && y < from) return false;
            if (YearTo is { } to && y > to) return false;
        }

        return true;
    }
}

/// <summary>Extracted text of one page, with word boxes for highlighting in the viewer.</summary>
public class DocumentPage : Entity
{
    public Guid DocumentId { get; set; }

    public int PageNumber { get; set; }

    public string Text { get; set; } = string.Empty;

    /// <summary>Page size in PDF points (or pixels for images).</summary>
    public double Width { get; set; }

    public double Height { get; set; }

    public bool FromOcr { get; set; }

    /// <summary>Word boxes with coordinates normalized to 0..1, origin top-left.</summary>
    public List<PageWord> Words { get; set; } = [];
}

public class PageWord
{
    public string Text { get; set; } = string.Empty;

    public double X { get; set; }

    public double Y { get; set; }

    public double W { get; set; }

    public double H { get; set; }

    /// <summary>Character offset of this word within <see cref="DocumentPage.Text"/>.</summary>
    public int Offset { get; set; }
}

/// <summary>A retrieval unit: a passage of a document with page provenance.</summary>
public class DocumentChunk : Entity
{
    public Guid DocumentId { get; set; }

    public int Ordinal { get; set; }

    public int PageNumber { get; set; }

    public int? PageEnd { get; set; }

    public string? Heading { get; set; }

    public string Text { get; set; } = string.Empty;

    public int TokenEstimate { get; set; }

    /// <summary>Character range within the starting page's text (for highlighting).</summary>
    public int CharStart { get; set; }

    public int CharEnd { get; set; }
}

/// <summary>An embedding vector for a chunk, produced by a specific model.</summary>
public class ChunkEmbedding : Entity
{
    public Guid ChunkId { get; set; }

    public Guid DocumentId { get; set; }

    public string Model { get; set; } = string.Empty;

    public int Dimensions { get; set; }

    public float[] Vector { get; set; } = [];
}

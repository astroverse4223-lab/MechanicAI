using MechanicAI.Domain.Common;
using MechanicAI.Domain.Interfaces;

namespace MechanicAI.Domain.Entities;

/// <summary>A recorded live-data capture from an OBD-II adapter (or the labeled simulator).</summary>
public class LiveDataSession : Entity, IAggregateRoot, IVehicleScoped
{
    public Guid? VehicleId { get; set; }

    public Guid? DiagnosticSessionId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string AdapterDescription { get; set; } = string.Empty;

    public string? Protocol { get; set; }

    /// <summary>VIN reported by the ECU (Mode 09 PID 02), if available.</summary>
    public string? EcuVin { get; set; }

    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;

    public DateTime? EndedUtc { get; set; }

    public List<string> Pids { get; set; } = [];

    public string? Notes { get; set; }

    /// <summary>True when captured from the built-in simulator. Always shown prominently.</summary>
    public bool IsSimulated { get; set; }

    public int SampleCount { get; set; }

    public string? AnalysisMarkdown { get; set; }
}

/// <summary>A single PID value at an offset from the capture start. High-volume table.</summary>
public class LiveDataSample
{
    public long Id { get; set; }

    public Guid SessionId { get; set; }

    public int OffsetMs { get; set; }

    public string Pid { get; set; } = string.Empty;

    public double Value { get; set; }
}

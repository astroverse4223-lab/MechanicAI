using MechanicAI.Domain.Common;
using MechanicAI.Domain.Enums;

namespace MechanicAI.Domain.Entities;

/// <summary>
/// Reference definition of a trouble code. Generic definitions come from the built-in
/// SAE J2012 reference; manufacturer-specific definitions may be added by data providers
/// or by the technician (and are labeled with their source).
/// </summary>
public class DtcDefinition : Entity
{
    public string Code { get; set; } = string.Empty;

    /// <summary>Null for SAE generic definitions; otherwise the make the definition applies to.</summary>
    public string? Manufacturer { get; set; }

    public DtcSystem System { get; set; }

    public string Subsystem { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public bool IsGeneric { get; set; } = true;

    /// <summary>Common symptoms (general; shown as unconfirmed possibilities).</summary>
    public List<string> Symptoms { get; set; } = [];

    /// <summary>Common causes (general; shown as unconfirmed possibilities).</summary>
    public List<string> Causes { get; set; } = [];

    public List<string> RelatedCodes { get; set; } = [];

    public List<string> SafetyTags { get; set; } = [];

    public string? Notes { get; set; }

    public string Source { get; set; } = string.Empty;

    public string ContentVersion { get; set; } = string.Empty;

    /// <summary>True when the entry was added by the technician rather than shipped content.</summary>
    public bool IsUserDefined { get; set; }
}

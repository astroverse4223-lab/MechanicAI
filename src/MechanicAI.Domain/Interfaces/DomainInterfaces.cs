namespace MechanicAI.Domain.Interfaces;

/// <summary>Marks the root entity of a consistency boundary (loaded and saved as a unit).</summary>
public interface IAggregateRoot;

/// <summary>
/// Development/demo records that ship with the app. They are always labeled in the UI
/// and never presented as verified real-world repair information.
/// </summary>
public interface ISampleData
{
    bool IsSample { get; set; }
}

/// <summary>Records that belong to a specific vehicle's history.</summary>
public interface IVehicleScoped
{
    Guid? VehicleId { get; }
}

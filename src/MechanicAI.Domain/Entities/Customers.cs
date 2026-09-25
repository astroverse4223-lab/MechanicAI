using MechanicAI.Domain.Common;
using MechanicAI.Domain.Interfaces;

namespace MechanicAI.Domain.Entities;

public class Customer : Entity, IAggregateRoot, ISampleData
{
    public string FirstName { get; set; } = string.Empty;

    public string LastName { get; set; } = string.Empty;

    public string? CompanyName { get; set; }

    public string? Phone { get; set; }

    public string? Email { get; set; }

    public string? AddressLine1 { get; set; }

    public string? AddressLine2 { get; set; }

    public string? City { get; set; }

    public string? Region { get; set; }

    public string? PostalCode { get; set; }

    public string? Notes { get; set; }

    public bool IsSample { get; set; }

    public List<Vehicle> Vehicles { get; set; } = [];

    public string DisplayName
    {
        get
        {
            var person = $"{FirstName} {LastName}".Trim();
            if (string.IsNullOrWhiteSpace(CompanyName)) return person;
            return string.IsNullOrWhiteSpace(person) ? CompanyName : $"{person} ({CompanyName})";
        }
    }
}

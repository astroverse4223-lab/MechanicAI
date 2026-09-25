using MechanicAI.Domain.Entities;
using MechanicAI.Domain.Enums;

namespace MechanicAI.Domain.Tests.Entities;

public class EstimateTests
{
    [Fact]
    public void Totals_SumLinesAndTaxOnlyTaxableLines()
    {
        var estimate = new Estimate
        {
            TaxRate = 0.0825m,
            Lines =
            [
                new EstimateLine { Kind = EstimateLineKind.Labor, Quantity = 1.5m, UnitPrice = 120m, Taxable = false },
                new EstimateLine { Kind = EstimateLineKind.Part, Quantity = 3m, UnitPrice = 33.335m },
                new EstimateLine { Kind = EstimateLineKind.Fee, Quantity = 1m, UnitPrice = 4.99m },
            ],
        };

        Assert.Equal(180m, estimate.Lines[0].Total);
        Assert.Equal(100.01m, estimate.Lines[1].Total); // 100.005 rounds away from zero
        Assert.Equal(285.00m, estimate.Subtotal);
        Assert.Equal(105.00m, estimate.TaxableTotal);
        Assert.Equal(8.66m, estimate.Tax);
        Assert.Equal(293.66m, estimate.Total);
    }

    [Fact]
    public void Tax_RoundsMidpointAwayFromZero()
    {
        var estimate = new Estimate
        {
            TaxRate = 0.0825m,
            Lines = [new EstimateLine { Quantity = 1m, UnitPrice = 10m }],
        };

        Assert.Equal(0.83m, estimate.Tax);
        Assert.Equal(10.83m, estimate.Total);
    }

    [Fact]
    public void EmptyEstimate_IsZero()
    {
        var estimate = new Estimate { TaxRate = 0.1m };

        Assert.Equal(0m, estimate.Subtotal);
        Assert.Equal(0m, estimate.Tax);
        Assert.Equal(0m, estimate.Total);
    }

    [Fact]
    public void NewLine_DefaultsToTaxableSinglePart()
    {
        var line = new EstimateLine();

        Assert.True(line.Taxable);
        Assert.Equal(1m, line.Quantity);
        Assert.Equal(EstimateLineKind.Part, line.Kind);
    }
}

public class CustomerTests
{
    [Theory]
    [InlineData("Jane", "Doe", null, "Jane Doe")]
    [InlineData("Jane", "Doe", "  ", "Jane Doe")]
    [InlineData("Jane", "Doe", "Acme Fleet", "Jane Doe (Acme Fleet)")]
    [InlineData("", "", "Acme Fleet", "Acme Fleet")]
    [InlineData("Jane", "", null, "Jane")]
    [InlineData("", "Doe", null, "Doe")]
    [InlineData("", "", null, "")]
    public void DisplayName_CombinesPersonAndCompany(string first, string last, string? company, string expected)
    {
        var customer = new Customer { FirstName = first, LastName = last, CompanyName = company };
        Assert.Equal(expected, customer.DisplayName);
    }
}

public class VehicleTests
{
    [Fact]
    public void DisplayName_JoinsYearMakeModelTrim()
    {
        var vehicle = new Vehicle { Year = 2017, Make = "Chevrolet", Model = "Silverado 1500", Trim = "LT" };
        Assert.Equal("2017 Chevrolet Silverado 1500 LT", vehicle.DisplayName);
    }

    [Fact]
    public void DisplayName_SkipsMissingParts()
    {
        Assert.Equal("Ford F-150", new Vehicle { Make = "Ford", Model = "F-150", Trim = " " }.DisplayName);
        Assert.Equal("2012", new Vehicle { Year = 2012 }.DisplayName);
    }

    [Fact]
    public void DisplayName_FallsBackWhenNothingIsKnown()
    {
        Assert.Equal("Unidentified vehicle", new Vehicle().DisplayName);
    }

    [Fact]
    public void Description_AppendsEngine()
    {
        var vehicle = new Vehicle { Year = 2018, Make = "Ford", Model = "F-150", Engine = "3.5L V6 EcoBoost" };
        Assert.Equal("2018 Ford F-150 3.5L V6 EcoBoost", vehicle.Description);
    }

    [Fact]
    public void Description_UsesDisplacementWhenNoEngineText()
    {
        var vehicle = new Vehicle { Year = 2016, Make = "Toyota", Model = "Camry", DisplacementLiters = 2.5m };
        Assert.Equal("2016 Toyota Camry 2.5L", vehicle.Description);
    }

    [Fact]
    public void Description_IsDisplayNameWithoutEngineInfo()
    {
        var vehicle = new Vehicle { Year = 2016, Make = "Toyota", Model = "Camry" };
        Assert.Equal(vehicle.DisplayName, vehicle.Description);
    }

    [Fact]
    public void Defaults_AreMilesAndManualSource()
    {
        var vehicle = new Vehicle();
        Assert.Equal(DistanceUnit.Miles, vehicle.MileageUnit);
        Assert.Equal(VehicleDataSource.Manual, vehicle.DataSource);
    }
}

public class DiagnosticSessionTests
{
    [Theory]
    [InlineData(DiagnosticSessionStatus.Intake, false)]
    [InlineData(DiagnosticSessionStatus.Testing, false)]
    [InlineData(DiagnosticSessionStatus.Verification, false)]
    [InlineData(DiagnosticSessionStatus.Completed, true)]
    [InlineData(DiagnosticSessionStatus.Abandoned, true)]
    public void IsClosed_OnlyForCompletedOrAbandoned(DiagnosticSessionStatus status, bool closed)
    {
        Assert.Equal(closed, new DiagnosticSession { Status = status }.IsClosed);
    }

    [Fact]
    public void AddStep_AssignsIncreasingSequenceAndTouchesSession()
    {
        var session = new DiagnosticSession();
        session.UpdatedUtc = DateTime.UtcNow.AddHours(-1);
        var nodeId = Guid.NewGuid();

        var first = session.AddStep(DiagnosticStepKind.SessionCreated, ActorKind.System, "Created");
        var second = session.AddStep(DiagnosticStepKind.DtcAdded, ActorKind.Technician, "Added P0302", "detail",
            EvidenceClass.TechnicianObservation, nodeId, actorName: "Sam");

        Assert.Equal(1, first.Sequence);
        Assert.Equal(2, second.Sequence);
        Assert.Equal(session.Id, second.SessionId);
        Assert.Equal("detail", second.Detail);
        Assert.Equal(EvidenceClass.TechnicianObservation, second.Evidence);
        Assert.Equal(nodeId, second.NodeId);
        Assert.Equal("Sam", second.ActorName);
        Assert.Equal(2, session.Steps.Count);
        Assert.True(session.UpdatedUtc > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void AddStep_ContinuesAfterHighestExistingSequence()
    {
        var session = new DiagnosticSession();
        session.Steps.Add(new DiagnosticStep { Sequence = 7 });
        session.Steps.Add(new DiagnosticStep { Sequence = 3 });

        var step = session.AddStep(DiagnosticStepKind.NoteAdded, ActorKind.Technician, "Note");

        Assert.Equal(8, step.Sequence);
    }

    [Fact]
    public void Causes_ReturnsOnlyCauseNodes()
    {
        var session = new DiagnosticSession
        {
            Nodes =
            [
                new DiagnosticNode { Kind = DiagnosticNodeKind.Root, Key = "root" },
                new DiagnosticNode { Kind = DiagnosticNodeKind.Category, Key = "category:ignition" },
                new DiagnosticNode { Kind = DiagnosticNodeKind.Cause, Key = "ignition-coil" },
                new DiagnosticNode { Kind = DiagnosticNodeKind.Cause, Key = "spark-plug" },
            ],
        };

        Assert.Equal(["ignition-coil", "spark-plug"], session.Causes.Select(c => c.Key));
    }

    [Fact]
    public void CompletedTests_AreCompletedOnlyInExecutionOrder()
    {
        var session = new DiagnosticSession
        {
            Tests =
            [
                new DiagnosticTest { Key = "c", Status = TestStatus.Completed, ExecutionOrder = 3 },
                new DiagnosticTest { Key = "skipped", Status = TestStatus.Skipped, ExecutionOrder = 1 },
                new DiagnosticTest { Key = "none", Status = TestStatus.Completed },
                new DiagnosticTest { Key = "a", Status = TestStatus.Completed, ExecutionOrder = 1 },
                new DiagnosticTest { Key = "available", Status = TestStatus.Available },
            ],
        };

        Assert.Equal(["a", "c", "none"], session.CompletedTests.Select(t => t.Key));
    }
}

public class DiagnosticTestEntityTests
{
    [Fact]
    public void SelectedOutcome_ResolvesByKey()
    {
        var test = new DiagnosticTest
        {
            Outcomes = [new TestOutcomeDefinition { Key = "pass" }, new TestOutcomeDefinition { Key = "fail" }],
        };

        Assert.Null(test.SelectedOutcome);
        test.SelectedOutcomeKey = "fail";
        Assert.Equal("fail", test.SelectedOutcome?.Key);
        test.SelectedOutcomeKey = "missing";
        Assert.Null(test.SelectedOutcome);
    }

    [Theory]
    [InlineData(true, false, TestResult.Pass)]
    [InlineData(false, false, TestResult.Fail)]
    [InlineData(null, false, TestResult.Pass)]
    [InlineData(false, true, TestResult.Inconclusive)]
    [InlineData(true, true, TestResult.Inconclusive)]
    public void OutcomeToResult_MapsNormalAndInconclusive(bool? normal, bool inconclusive, TestResult expected)
    {
        Assert.Equal(expected, new TestOutcomeDefinition { Normal = normal, Inconclusive = inconclusive }.ToResult());
    }
}

public class MiscEntityTests
{
    [Fact]
    public void NewEntity_HasVersion7IdAndUtcTimestamps()
    {
        var a = new Note();
        var b = new Note();

        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(7, a.Id.Version);
        Assert.Equal(DateTimeKind.Utc, a.CreatedUtc.Kind);
    }

    [Fact]
    public void Touch_UpdatesTimestamp()
    {
        var note = new Note { UpdatedUtc = DateTime.UtcNow.AddDays(-1) };
        note.Touch();
        Assert.True(note.UpdatedUtc > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void User_IsLockedOutUntilLockoutEnd()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(new User().IsLockedOut(now));
        Assert.True(new User { LockoutEndUtc = now.AddMinutes(5) }.IsLockedOut(now));
        Assert.False(new User { LockoutEndUtc = now }.IsLockedOut(now));
    }

    [Fact]
    public void RefreshToken_IsActiveUntilExpiredOrRevoked()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(new RefreshToken { ExpiresUtc = now.AddDays(1) }.IsActive(now));
        Assert.False(new RefreshToken { ExpiresUtc = now.AddDays(-1) }.IsActive(now));
        Assert.False(new RefreshToken { ExpiresUtc = now.AddDays(1), RevokedUtc = now }.IsActive(now));
    }

    [Theory]
    [InlineData(2015, "Ford", "F-150", true)]
    [InlineData(2015, "ford", "f-150 xlt", true)]
    [InlineData(2015, "Chevrolet", "F-150", false)]
    [InlineData(2015, "Ford", "Escape", false)]
    [InlineData(2008, "Ford", "F-150", false)]
    [InlineData(2021, "Ford", "F-150", false)]
    [InlineData(null, null, null, true)]
    public void Document_AppliesToVehicleFilter(int? year, string? make, string? model, bool expected)
    {
        var document = new Document { Make = "Ford", Model = "F-150", YearFrom = 2009, YearTo = 2020 };

        Assert.Equal(expected, document.AppliesTo(year, make, model));
    }

    [Fact]
    public void Document_WithoutApplicabilityAppliesToEverything()
    {
        Assert.True(new Document().AppliesTo(1999, "Honda", "Civic"));
        Assert.True(new Document { Kind = DocumentKind.WiringDiagram }.IsWiringDiagram);
        Assert.False(new Document().IsWiringDiagram);
    }
}

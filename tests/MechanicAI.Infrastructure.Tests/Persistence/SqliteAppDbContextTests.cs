using MechanicAI.Domain.Entities;
using MechanicAI.Domain.Enums;
using MechanicAI.Domain.ValueObjects;
using MechanicAI.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MechanicAI.Infrastructure.Tests.Persistence;

public sealed class SqliteAppDbContextTests : IAsyncLifetime, IDisposable
{
    private readonly TempDirectory _temp = new();

    public SqliteAppDbContextTests()
    {
        ConnectionString = new SqliteConnectionStringBuilder { DataSource = _temp.Combine("test.db"), Pooling = false }.ToString();
    }

    private string ConnectionString { get; }

    private SqliteAppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<SqliteAppDbContext>().UseSqlite(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Migrations_AreAppliedAndMatchTheModel()
    {
        await using var db = CreateContext();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges(), "The EF model has changes that are not captured in a SQLite migration.");
    }

    [Fact]
    public async Task VehicleAndDiagnosticSession_RoundTrip()
    {
        var vehicle = new Vehicle
        {
            Vin = "1FTFW1E59JFA00001",
            Year = 2018,
            Make = "Ford",
            Model = "F-150",
            Engine = "3.5L V6 EcoBoost",
            DisplacementLiters = 3.5m,
            Mileage = 98_000,
            DataSource = VehicleDataSource.NhtsaVpic,
        };
        var session = new DiagnosticSession
        {
            VehicleId = vehicle.Id,
            VehicleDescription = vehicle.Description,
            Title = "P0302 misfire",
            Complaint = "Shakes at idle",
            Symptoms = ["Rough idle", "Flashing MIL"],
            Conditions = ["When cold"],
            Status = DiagnosticSessionStatus.Testing,
            PlaybookKeys = ["misfire-single-cylinder"],
            ClarifyingQuestions = [new ClarifyingQuestion { Question = "Cold or hot?", Answer = "Cold", AskedBy = ActorKind.Ai }],
        };
        session.Dtcs.Add(new SessionDtc
        {
            SessionId = session.Id,
            Code = "P0302",
            Status = DtcStatus.Current,
            FreezeFrame = new Dictionary<string, string> { ["RPM"] = "720", ["ECT"] = "88 °C" },
        });
        var cause = new DiagnosticNode
        {
            SessionId = session.Id,
            Kind = DiagnosticNodeKind.Cause,
            Key = "ignition-coil",
            Title = "Failed ignition coil",
            Category = DiagnosticCategory.Ignition,
            PriorProbability = 0.3,
            Probability = 0.47,
            SafetyTags = [SafetyTags.IgnitionVoltage],
            Sources = [new SourceCitation { Label = "REF", Title = "Playbook", Type = SourceType.BuiltInReference }],
        };
        session.Nodes.Add(cause);
        session.Tests.Add(new DiagnosticTest
        {
            SessionId = session.Id,
            Key = "coil-swap",
            Title = "Swap the coil",
            Procedure = ["Swap coils 2 and 3", "Clear misfire counters"],
            PrimaryNodeId = cause.Id,
            Status = TestStatus.Completed,
            SelectedOutcomeKey = "moved",
            Result = TestResult.Fail,
            ExecutionOrder = 1,
            Outcomes =
            [
                new TestOutcomeDefinition { Key = "moved", Label = "Moved", Normal = false, Likelihoods = new() { ["ignition-coil"] = 0.95, ["*"] = 0.05 } },
                new TestOutcomeDefinition { Key = "stayed", Label = "Stayed", Normal = true, Likelihoods = new() { ["ignition-coil"] = 0.05, ["*"] = 0.95 } },
            ],
        });
        session.AddStep(DiagnosticStepKind.SessionCreated, ActorKind.Technician, "Session created", actorName: "Sam");

        await using (var db = CreateContext())
        {
            db.Vehicles.Add(vehicle);
            db.DiagnosticSessions.Add(session);
            await db.SaveChangesAsync();
        }

        await using (var db = CreateContext())
        {
            var loadedVehicle = await db.Vehicles.SingleAsync(v => v.Id == vehicle.Id);
            Assert.Equal("2018 Ford F-150 3.5L V6 EcoBoost", loadedVehicle.Description);
            Assert.Equal(3.5m, loadedVehicle.DisplacementLiters);
            Assert.Equal(VehicleDataSource.NhtsaVpic, loadedVehicle.DataSource);

            var loaded = await db.DiagnosticSessions
                .Include(s => s.Vehicle)
                .Include(s => s.Dtcs)
                .Include(s => s.Nodes)
                .Include(s => s.Tests)
                .Include(s => s.Steps)
                .AsSplitQuery()
                .SingleAsync(s => s.Id == session.Id);

            Assert.Equal(vehicle.Id, loaded.Vehicle?.Id);
            Assert.Equal("P0302 misfire", loaded.Title);
            Assert.Equal(DiagnosticSessionStatus.Testing, loaded.Status);
            Assert.Equal(["Rough idle", "Flashing MIL"], loaded.Symptoms);
            Assert.Equal(["When cold"], loaded.Conditions);
            Assert.Equal(["misfire-single-cylinder"], loaded.PlaybookKeys);
            var question = Assert.Single(loaded.ClarifyingQuestions);
            Assert.Equal("Cold", question.Answer);
            Assert.Equal(ActorKind.Ai, question.AskedBy);

            var dtc = Assert.Single(loaded.Dtcs);
            Assert.Equal("P0302", dtc.Code);
            Assert.Equal("88 °C", dtc.FreezeFrame["ECT"]);

            var node = Assert.Single(loaded.Causes);
            Assert.Equal(0.47, node.Probability);
            Assert.Equal([SafetyTags.IgnitionVoltage], node.SafetyTags);
            Assert.Equal(SourceType.BuiltInReference, Assert.Single(node.Sources).Type);

            var test = Assert.Single(loaded.Tests);
            Assert.Equal(TestResult.Fail, test.Result);
            Assert.Equal(["Swap coils 2 and 3", "Clear misfire counters"], test.Procedure);
            Assert.Equal(0.95, test.SelectedOutcome?.Likelihoods["ignition-coil"]);
            Assert.Equal(cause.Id, test.PrimaryNodeId);
            Assert.Single(loaded.CompletedTests);

            var step = Assert.Single(loaded.Steps);
            Assert.Equal(1, step.Sequence);
            Assert.Equal("Sam", step.ActorName);
        }
    }

    [Fact]
    public async Task SaveChanges_StampsTimestampsAndReadsBackUtc()
    {
        var vehicle = new Vehicle { Make = "Honda", Model = "Civic" };
        vehicle.UpdatedUtc = DateTime.UtcNow.AddYears(-1);
        var beforeInsert = DateTime.UtcNow;

        await using (var db = CreateContext())
        {
            db.Vehicles.Add(vehicle);
            await db.SaveChangesAsync();
        }

        Assert.True(vehicle.UpdatedUtc >= beforeInsert, "UpdatedUtc is stamped on insert");
        var created = vehicle.CreatedUtc;
        var inserted = vehicle.UpdatedUtc;

        await Task.Delay(20);
        await using (var db = CreateContext())
        {
            var loaded = await db.Vehicles.SingleAsync(v => v.Id == vehicle.Id);
            Assert.Equal(DateTimeKind.Utc, loaded.CreatedUtc.Kind);
            Assert.Equal(DateTimeKind.Utc, loaded.UpdatedUtc.Kind);
            Assert.Equal(created, loaded.CreatedUtc);

            loaded.Mileage = 150_000;
            await db.SaveChangesAsync();

            Assert.True(loaded.UpdatedUtc > inserted, "UpdatedUtc is stamped on update");
            Assert.Equal(created, loaded.CreatedUtc);
        }

        await using (var db = CreateContext())
        {
            var reloaded = await db.Vehicles.AsNoTracking().SingleAsync(v => v.Id == vehicle.Id);
            Assert.Equal(150_000, reloaded.Mileage);
            Assert.True(reloaded.UpdatedUtc > inserted);
        }
    }

    [Fact]
    public async Task SaveChanges_ConvertsLocalTimesToUtc()
    {
        var local = new DateTime(2026, 3, 1, 8, 30, 0, DateTimeKind.Local);
        var repair = new Repair { Title = "Replaced coil", PerformedUtc = local };

        await using (var db = CreateContext())
        {
            db.Repairs.Add(repair);
            await db.SaveChangesAsync();
        }

        await using (var db = CreateContext())
        {
            var loaded = await db.Repairs.AsNoTracking().SingleAsync(r => r.Id == repair.Id);
            Assert.Equal(DateTimeKind.Utc, loaded.PerformedUtc.Kind);
            Assert.Equal(local.ToUniversalTime(), loaded.PerformedUtc);
        }
    }

    [Fact]
    public async Task DeletingSession_CascadesToChildren()
    {
        var session = new DiagnosticSession { Title = "To delete" };
        session.Dtcs.Add(new SessionDtc { SessionId = session.Id, Code = "P0171" });
        session.AddStep(DiagnosticStepKind.SessionCreated, ActorKind.System, "Created");

        await using (var db = CreateContext())
        {
            db.DiagnosticSessions.Add(session);
            await db.SaveChangesAsync();
        }

        await using (var db = CreateContext())
        {
            var loaded = await db.DiagnosticSessions.Include(s => s.Dtcs).Include(s => s.Steps).SingleAsync(s => s.Id == session.Id);
            db.DiagnosticSessions.Remove(loaded);
            await db.SaveChangesAsync();
        }

        await using (var db = CreateContext())
        {
            Assert.False(await db.SessionDtcs.AnyAsync(d => d.SessionId == session.Id));
            Assert.False(await db.DiagnosticSteps.AnyAsync(d => d.SessionId == session.Id));
        }
    }
}

using MechanicAI.Application.Content;
using MechanicAI.Application.Diagnostics;
using MechanicAI.Domain.Entities;
using MechanicAI.Domain.Enums;

namespace MechanicAI.Application.Tests.Diagnostics;

public class DiagnosticTreeMaterializerTests
{
    private static BuiltTree Tree(IReadOnlyList<PlaybookDefinition> playbooks, params string[] dtcs) =>
        DiagnosticTreeBuilder.Build(PlaybookMatcher.Match(playbooks, dtcs, string.Empty), new TreeContext(dtcs, string.Empty, null));

    private static DiagnosticNode Node(DiagnosticSession session, string key) => session.Nodes.Single(n => n.Key == key);

    [Fact]
    public void Materialize_CreatesRootCategoriesCausesAndTests()
    {
        var session = new DiagnosticSession { Complaint = "Shakes at idle" };

        DiagnosticTreeMaterializer.Materialize(session, Tree(TestPlaybooks.All(), "P0302"), "P0302 misfire");

        var root = Assert.Single(session.Nodes, n => n.Kind == DiagnosticNodeKind.Root);
        Assert.Equal("P0302 misfire", root.Title);
        Assert.Equal("Shakes at idle", root.Description);
        Assert.Null(root.ParentId);

        var ignition = Node(session, "category:ignition");
        var vacuum = Node(session, "category:vacuum");
        Assert.Equal(root.Id, ignition.ParentId);
        Assert.Equal("Ignition", ignition.Title);

        var coil = Node(session, "ignition-coil");
        Assert.Equal(DiagnosticNodeKind.Cause, coil.Kind);
        Assert.Equal(ignition.Id, coil.ParentId);
        Assert.Equal(0.3, coil.PriorProbability, 9);
        Assert.Equal("Playbook: misfire", coil.OriginDetail);
        Assert.Equal(["ignition-voltage"], coil.SafetyTags);
        Assert.Equal(vacuum.Id, Node(session, "vacuum-leak").ParentId);
        Assert.Equal(["rotating"], Node(session, "vacuum-leak").SafetyTags);

        var unlisted = Node(session, DiagnosticMath.UnlistedCauseKey);
        Assert.Equal(root.Id, unlisted.ParentId);
        Assert.Equal(999, unlisted.SortOrder);
        Assert.Equal(EvidenceClass.AiInference, unlisted.Evidence);

        Assert.All(session.Nodes, n => Assert.Equal(session.Id, n.SessionId));
        Assert.Equal(["coil-swap", "smoke-test", "generic-test"], session.Tests.Select(t => t.Key));
    }

    [Fact]
    public void Materialize_PlacesTestsUnderTheirCauseOrCrossSystemCategory()
    {
        var session = new DiagnosticSession();

        DiagnosticTreeMaterializer.Materialize(session, Tree([TestPlaybooks.Misfire(), TestPlaybooks.Lean()], "P0302", "P0171"), "Root");

        Assert.Equal(Node(session, "ignition-coil").Id, session.Tests.Single(t => t.Key == "coil-swap").PrimaryNodeId);

        // Related causes span Vacuum and AirIntake, and "generic-test" has none: both go to the cross-system node.
        var general = Node(session, DiagnosticTreeMaterializer.GeneralCategoryKey);
        Assert.Equal("Cross-system checks", general.Title);
        Assert.Equal(general.Id, session.Tests.Single(t => t.Key == "smoke-test").PrimaryNodeId);
        Assert.Equal(general.Id, session.Tests.Single(t => t.Key == "generic-test").PrimaryNodeId);
    }

    [Fact]
    public void Materialize_IsIdempotent()
    {
        var session = new DiagnosticSession();
        var tree = Tree(TestPlaybooks.All(), "P0302");

        DiagnosticTreeMaterializer.Materialize(session, tree, "Root");
        var nodeIds = session.Nodes.Select(n => n.Id).ToList();
        var testIds = session.Tests.Select(t => t.Id).ToList();
        DiagnosticTreeMaterializer.Materialize(session, tree, "Root");

        Assert.Equal(nodeIds, session.Nodes.Select(n => n.Id));
        Assert.Equal(testIds, session.Tests.Select(t => t.Id));
    }

    [Fact]
    public void Materialize_RebuildKeepsTechnicianWorkAndDropsStaleSystemItems()
    {
        var session = new DiagnosticSession();
        DiagnosticTreeMaterializer.Materialize(session, Tree(TestPlaybooks.All(), "P0302"), "Root");

        var coilSwap = session.Tests.Single(t => t.Key == "coil-swap");
        coilSwap.Status = TestStatus.Completed;
        coilSwap.SelectedOutcomeKey = "moved";
        var technicianCause = new DiagnosticNode
        {
            SessionId = session.Id,
            Kind = DiagnosticNodeKind.Cause,
            Key = "tech-idea",
            Title = "Cracked intake boot",
            Category = DiagnosticCategory.AirIntake,
            Origin = ActorKind.Technician,
        };
        session.Nodes.Add(technicianCause);
        var technicianTest = new DiagnosticTest { SessionId = session.Id, Key = "tech-test", Origin = ActorKind.Technician, RelatedCauseKeys = ["tech-idea"] };
        session.Tests.Add(technicianTest);

        // Rebuild from a tree that no longer contains the misfire playbook.
        DiagnosticTreeMaterializer.Materialize(session, Tree([TestPlaybooks.Lean()], "P0171"), "Root");

        Assert.DoesNotContain(session.Nodes, n => n.Key is "ignition-coil" or "spark-plug" or "category:ignition");
        Assert.Contains(technicianCause, session.Nodes);
        Assert.Equal(Node(session, "category:airintake").Id, technicianCause.ParentId);

        Assert.Contains(coilSwap, session.Tests); // completed results survive
        Assert.Equal(Node(session, DiagnosticTreeMaterializer.GeneralCategoryKey).Id, coilSwap.PrimaryNodeId);
        Assert.Contains(technicianTest, session.Tests);
        Assert.Equal(technicianCause.Id, technicianTest.PrimaryNodeId);
        Assert.DoesNotContain(session.Tests, t => t.Key == "generic-test"); // untouched system test dropped
    }

    [Fact]
    public void Materialize_ResetsResultWhenSelectedOutcomeDisappears()
    {
        var session = new DiagnosticSession();
        DiagnosticTreeMaterializer.Materialize(session, Tree([TestPlaybooks.Lean()], "P0171"), "Root");
        var smoke = session.Tests.Single(t => t.Key == "smoke-test");
        smoke.Status = TestStatus.Completed;
        smoke.SelectedOutcomeKey = "partial";
        smoke.Result = TestResult.Fail;

        // The merged definition (misfire first) has no "partial" outcome.
        DiagnosticTreeMaterializer.Materialize(session, Tree([TestPlaybooks.Misfire(), TestPlaybooks.Lean()], "P0302", "P0171"), "Root");

        Assert.Same(smoke, session.Tests.Single(t => t.Key == "smoke-test"));
        Assert.Null(smoke.SelectedOutcomeKey);
        Assert.Null(smoke.Result);
        Assert.Equal(TestStatus.Available, smoke.Status);
        Assert.Equal(["found", "none"], smoke.Outcomes.Select(o => o.Key));
    }

    [Fact]
    public void Materialize_AiCauseProposedByPlaybookKeepsOriginAndTakesStrongerPrior()
    {
        var session = new DiagnosticSession();
        var aiCause = new DiagnosticNode
        {
            Kind = DiagnosticNodeKind.Cause,
            Key = "spark-plug",
            Title = "AI title",
            Origin = ActorKind.Ai,
            PriorProbability = 0.05,
            Category = DiagnosticCategory.Ignition,
        };
        session.Nodes.Add(aiCause);

        DiagnosticTreeMaterializer.Materialize(session, Tree(TestPlaybooks.All(), "P0302"), "Root");

        Assert.Same(aiCause, Node(session, "spark-plug"));
        Assert.Equal(ActorKind.Ai, aiCause.Origin);
        Assert.Equal("AI title", aiCause.Title);
        Assert.Equal(0.2, aiCause.PriorProbability, 9);
    }

    [Theory]
    [InlineData(DiagnosticCategory.AirIntake, "Air / Intake")]
    [InlineData(DiagnosticCategory.Hvac, "HVAC")]
    [InlineData(DiagnosticCategory.HighVoltage, "High Voltage")]
    [InlineData(DiagnosticCategory.Restraints, "Restraints (SRS)")]
    [InlineData(DiagnosticCategory.Fuel, "Fuel")]
    public void CategoryTitle_IsHumanReadable(DiagnosticCategory category, string expected)
    {
        Assert.Equal(expected, DiagnosticTreeMaterializer.CategoryTitle(category));
    }
}

using MechanicAI.Application.Diagnostics;
using MechanicAI.Application.Settings;
using MechanicAI.Domain.Entities;
using MechanicAI.Domain.Enums;
using static MechanicAI.Application.Tests.Diagnostics.DiagnosticMathTests;

namespace MechanicAI.Application.Tests.Diagnostics;

public class DiagnosticRecalculatorTests
{
    private static readonly DiagnosticsSettings Settings = new();

    private static DiagnosticNode Cause(string key, double prior) =>
        new() { Kind = DiagnosticNodeKind.Cause, Key = key, PriorProbability = prior };

    /// <summary>coil 0.3, plug 0.2, leak 0.1 (normalized to 0.47/0.313/0.157 + 0.06 unlisted).</summary>
    private static DiagnosticSession Session()
    {
        var session = new DiagnosticSession
        {
            Nodes =
            [
                new DiagnosticNode { Kind = DiagnosticNodeKind.Root, Key = "root" },
                Cause("coil", 0.3),
                Cause("plug", 0.2),
                Cause("leak", 0.1),
                Cause(DiagnosticMath.UnlistedCauseKey, 0),
            ],
            Tests =
            [
                new DiagnosticTest
                {
                    Key = "coil-swap",
                    EstimatedMinutes = 20,
                    Outcomes = [Outcome("moved", ("coil", 0.95), ("*", 0.05)), Outcome("stayed", ("coil", 0.05), ("*", 0.95))],
                },
                new DiagnosticTest
                {
                    Key = "plug-inspect",
                    EstimatedMinutes = 60,
                    Difficulty = 3,
                    Outcomes = [Outcome("worn", ("plug", 0.7), ("*", 0.3)), Outcome("ok", ("plug", 0.3), ("*", 0.7))],
                },
                new DiagnosticTest
                {
                    Key = "useless",
                    Outcomes = [Outcome("a", ("*", 0.5)), Outcome("b", ("*", 0.5))],
                },
                new DiagnosticTest
                {
                    Key = "contradicts-all",
                    Outcomes =
                    [
                        Outcome("weird", ("coil", 0.01), ("plug", 0.01), ("leak", 0.01)),
                        Outcome("normal", ("coil", 0.99), ("plug", 0.99), ("leak", 0.99)),
                    ],
                },
            ],
        };
        return session;
    }

    private static DiagnosticNode Node(DiagnosticSession s, string key) => s.Nodes.Single(n => n.Key == key);

    private static DiagnosticTest TestByKey(DiagnosticSession s, string key) => s.Tests.Single(t => t.Key == key);

    private static void Record(DiagnosticSession s, string testKey, string outcome, int order)
    {
        var test = TestByKey(s, testKey);
        test.Status = TestStatus.Completed;
        test.SelectedOutcomeKey = outcome;
        test.ExecutionOrder = order;
    }

    [Fact]
    public void Recalculate_WithoutEvidence_UsesNormalizedPriorsAndRecommendsBestTest()
    {
        var session = Session();

        var result = DiagnosticRecalculator.Recalculate(session, Settings);

        Assert.Equal(0.3 / 0.6 * 0.94, Node(session, "coil").Probability, 5);
        Assert.Equal(0.06, Node(session, DiagnosticMath.UnlistedCauseKey).Probability, 5);
        Assert.Empty(result.StatusChanges);
        Assert.All(session.Nodes.Where(n => n.Kind == DiagnosticNodeKind.Cause), n => Assert.Equal(CauseStatus.Open, n.Status));
        Assert.Equal("coil", result.LeadingCause?.Key);
        Assert.False(result.EvidenceOutsideTree);

        // The useless test carries no information and is not ranked.
        Assert.DoesNotContain(result.Ranking, r => r.Test.Key == "useless");
        var top = result.Ranking[0];
        Assert.Equal("coil-swap", top.Test.Key);
        Assert.Equal(TestStatus.Recommended, top.Test.Status);
        Assert.Equal(TestStatus.Available, TestByKey(session, "plug-inspect").Status);
        Assert.Equal<double?>(0, TestByKey(session, "useless").Score);
        Assert.True(top.Test.InformationGain > 0);
        Assert.Equal(top.InformationGain / DiagnosticMath.Cost(top.Test), top.Score, 9);
    }

    [Fact]
    public void Recalculate_StrongEvidenceIsolatesCauseAndRulesOthersOut()
    {
        var session = Session();
        Record(session, "coil-swap", "moved", 1);

        var result = DiagnosticRecalculator.Recalculate(session, Settings);

        var coil = Node(session, "coil");
        Assert.True(coil.Probability > Settings.IsolationThreshold);
        Assert.Equal(CauseStatus.Likely, coil.Status);
        Assert.NotNull(coil.StatusReason);
        Assert.Equal(CauseStatus.RuledOut, Node(session, "leak").Status); // posterior < 2% and fell by > 4x
        Assert.Equal(CauseStatus.Open, Node(session, "plug").Status);     // still above the rule-out threshold
        Assert.Equal(CauseStatus.Open, Node(session, DiagnosticMath.UnlistedCauseKey).Status);
        Assert.Contains(result.StatusChanges, c => c.Node.Key == "coil" && c.From == CauseStatus.Open && c.To == CauseStatus.Likely);
        Assert.Contains(result.StatusChanges, c => c.Node.Key == "leak" && c.To == CauseStatus.RuledOut);
        Assert.Equal("coil", result.LeadingCause?.Key);
        Assert.DoesNotContain(result.Ranking, r => r.Test.Key == "coil-swap");
        Assert.Equal(TestStatus.Completed, TestByKey(session, "coil-swap").Status);
        Assert.Equal(1.0, session.Nodes.Where(n => n.Kind == DiagnosticNodeKind.Cause).Sum(n => n.Probability), 4);
    }

    [Fact]
    public void Recalculate_RevertingEvidenceRestoresTheOriginalState()
    {
        var session = Session();
        DiagnosticRecalculator.Recalculate(session, Settings);
        var before = session.Nodes.ToDictionary(n => n.Key, n => (n.Probability, n.Status));

        Record(session, "coil-swap", "moved", 1);
        DiagnosticRecalculator.Recalculate(session, Settings);
        var coilSwap = TestByKey(session, "coil-swap");
        coilSwap.Status = TestStatus.Available;
        coilSwap.SelectedOutcomeKey = null;
        coilSwap.ExecutionOrder = null;
        DiagnosticRecalculator.Recalculate(session, Settings);

        Assert.All(session.Nodes, n => Assert.Equal(before[n.Key], (n.Probability, n.Status)));
    }

    [Fact]
    public void Recalculate_PositiveEvidenceMarksCauseSuspected()
    {
        var session = Session();
        Record(session, "plug-inspect", "worn", 1);

        DiagnosticRecalculator.Recalculate(session, Settings);

        var plug = Node(session, "plug");
        Assert.InRange(plug.Probability, 0.35, Settings.IsolationThreshold);
        Assert.Equal(CauseStatus.Suspected, plug.Status);
    }

    [Fact]
    public void Recalculate_EvidenceAgainstEveryCauseRaisesUnlisted()
    {
        var session = Session();
        Record(session, "contradicts-all", "weird", 1);

        var result = DiagnosticRecalculator.Recalculate(session, Settings);

        var unlisted = Node(session, DiagnosticMath.UnlistedCauseKey);
        Assert.True(unlisted.Probability >= 0.35);
        Assert.Equal(CauseStatus.Suspected, unlisted.Status);
        Assert.True(result.EvidenceOutsideTree);
        Assert.NotEqual(DiagnosticMath.UnlistedCauseKey, result.LeadingCause?.Key);
    }

    [Fact]
    public void Recalculate_ManualStatusesAreExcludedAndPreserved()
    {
        var session = Session();
        var coil = Node(session, "coil");
        coil.Status = CauseStatus.RuledOut;
        coil.IsManualStatus = true;
        var plug = Node(session, "plug");
        plug.Status = CauseStatus.Confirmed;

        var result = DiagnosticRecalculator.Recalculate(session, Settings);

        Assert.Equal(0, coil.Probability);
        Assert.Equal(CauseStatus.RuledOut, coil.Status);
        Assert.Equal(CauseStatus.Confirmed, plug.Status);
        Assert.Equal("plug", result.LeadingCause?.Key); // confirmed causes lead
        Assert.DoesNotContain(result.StatusChanges, c => c.Node == coil || c.Node == plug);
    }

    [Fact]
    public void RankTests_PrefersCheaperTestWithEqualGain()
    {
        var session = new DiagnosticSession
        {
            Tests =
            [
                new DiagnosticTest { Key = "slow", EstimatedMinutes = 90, Outcomes = [Outcome("a", ("x", 0.9)), Outcome("b", ("x", 0.1))] },
                new DiagnosticTest { Key = "fast", EstimatedMinutes = 5, Outcomes = [Outcome("a", ("x", 0.9)), Outcome("b", ("x", 0.1))] },
                new DiagnosticTest { Key = "skipped", Status = TestStatus.Skipped, Outcomes = [Outcome("a", ("x", 0.9)), Outcome("b", ("x", 0.1))] },
            ],
        };
        var belief = new Dictionary<string, double> { ["x"] = 0.5, ["y"] = 0.5 };

        var ranking = DiagnosticRecalculator.RankTests(session, belief);

        Assert.Equal(["fast", "slow"], ranking.Select(r => r.Test.Key));
        Assert.Equal(ranking[0].InformationGain, ranking[1].InformationGain, 9);
    }

    [Fact]
    public void CurrentBelief_ReadsCauseProbabilities()
    {
        var session = Session();
        DiagnosticRecalculator.Recalculate(session, Settings);

        var belief = DiagnosticRecalculator.CurrentBelief(session);

        Assert.Equal(4, belief.Count);
        Assert.Equal(Node(session, "coil").Probability, belief["coil"]);
    }
}

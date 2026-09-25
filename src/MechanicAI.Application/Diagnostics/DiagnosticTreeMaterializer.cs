using MechanicAI.Domain.Entities;
using MechanicAI.Domain.Enums;
using MechanicAI.Domain.ValueObjects;

namespace MechanicAI.Application.Diagnostics;

/// <summary>
/// Creates or rebuilds the node/test entities of a session from a built tree. Existing
/// entities are updated in place (so EF Core change tracking stays consistent):
/// technician- and AI-added causes/tests are kept, completed or skipped tests keep their
/// results, and manual cause statuses survive a rebuild.
/// </summary>
public static class DiagnosticTreeMaterializer
{
    public const string RootKey = "root";
    public const string GeneralCategoryKey = "category:general";

    public static void Materialize(DiagnosticSession session, BuiltTree tree, string rootTitle)
    {
        var keepNodes = new HashSet<DiagnosticNode>(ReferenceEqualityComparer.Instance);

        DiagnosticNode Upsert(Func<DiagnosticNode, bool> match, Func<DiagnosticNode> create)
        {
            var node = session.Nodes.FirstOrDefault(match);
            if (node is null)
            {
                node = create();
                session.Nodes.Add(node);
            }

            keepNodes.Add(node);
            return node;
        }

        var root = Upsert(n => n.Kind == DiagnosticNodeKind.Root, () => new DiagnosticNode
        {
            SessionId = session.Id,
            Kind = DiagnosticNodeKind.Root,
            Key = RootKey,
            Evidence = EvidenceClass.TechnicianObservation,
            Origin = ActorKind.Technician,
        });
        root.ParentId = null;
        root.Title = rootTitle;
        root.Description = session.Complaint;
        root.SortOrder = 0;

        var categorySort = 0;
        DiagnosticNode CategoryNode(DiagnosticCategory category)
        {
            var key = "category:" + category.ToString().ToLowerInvariant();
            var isNew = !session.Nodes.Any(n => n.Kind == DiagnosticNodeKind.Category && n.Key == key && keepNodes.Contains(n));
            var node = Upsert(n => n.Kind == DiagnosticNodeKind.Category && n.Key == key, () => new DiagnosticNode
            {
                SessionId = session.Id,
                Kind = DiagnosticNodeKind.Category,
                Key = key,
                Category = category,
                Origin = ActorKind.System,
            });
            node.ParentId = root.Id;
            node.Title = CategoryTitle(category);
            if (isNew) node.SortOrder = ++categorySort;
            return node;
        }

        var order = 0;
        foreach (var cause in tree.Causes)
        {
            var parent = CategoryNode(cause.Category);
            var node = Upsert(n => n.Kind == DiagnosticNodeKind.Cause && n.Key == cause.Definition.Key, () => new DiagnosticNode
            {
                SessionId = session.Id,
                Kind = DiagnosticNodeKind.Cause,
                Key = cause.Definition.Key,
                Origin = ActorKind.System,
                Evidence = EvidenceClass.UnconfirmedPossibility,
            });

            node.ParentId = parent.Id;
            node.Category = cause.Category;
            node.SortOrder = ++order;
            if (node.Origin == ActorKind.System)
            {
                node.Title = cause.Definition.Title;
                node.Description = BuildCauseDescription(cause);
                node.PriorProbability = cause.Prior;
                node.OriginDetail = $"Playbook: {cause.PlaybookKey}";
                node.SafetyTags = cause.Definition.Safety.Where(SafetyTags.IsValid).Select(s => s.ToLowerInvariant()).Distinct().ToList();
            }
            else
            {
                // An AI/technician cause that a playbook also proposes: keep its origin, take the stronger prior.
                node.PriorProbability = Math.Max(node.PriorProbability, cause.Prior);
            }
        }

        foreach (var added in session.Nodes.Where(n => n.Kind == DiagnosticNodeKind.Cause && n.Origin != ActorKind.System && !keepNodes.Contains(n)).ToList())
        {
            keepNodes.Add(added);
            added.ParentId = CategoryNode(added.Category).Id;
            added.SortOrder = ++order;
        }

        var unlisted = Upsert(n => n.Key == DiagnosticMath.UnlistedCauseKey, () => new DiagnosticNode
        {
            SessionId = session.Id,
            Kind = DiagnosticNodeKind.Cause,
            Key = DiagnosticMath.UnlistedCauseKey,
            Category = DiagnosticCategory.Other,
            Origin = ActorKind.System,
        });
        unlisted.ParentId = root.Id;
        unlisted.Title = "Cause not in this tree";
        unlisted.Description = "Probability reserved for a fault that none of the listed causes describe. If it rises, the evidence does not fit the current tree — re-check results and research further before replacing parts.";
        unlisted.Evidence = EvidenceClass.AiInference;
        unlisted.SortOrder = 999;

        DiagnosticNode? general = null;
        DiagnosticNode General()
        {
            general ??= Upsert(n => n.Key == GeneralCategoryKey, () => new DiagnosticNode
            {
                SessionId = session.Id,
                Kind = DiagnosticNodeKind.Category,
                Key = GeneralCategoryKey,
                Category = DiagnosticCategory.Other,
                Origin = ActorKind.System,
            });
            general.ParentId = root.Id;
            general.Title = "Cross-system checks";
            general.Description = "Tests that help separate causes in different systems.";
            general.SortOrder = 0;
            return general;
        }

        var causeNodes = session.Nodes
            .Where(n => n.Kind == DiagnosticNodeKind.Cause && keepNodes.Contains(n))
            .GroupBy(n => n.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        Guid PrimaryFor(IEnumerable<string> relatedKeys)
        {
            var related = relatedKeys.Where(causeNodes.ContainsKey).Select(k => causeNodes[k]).ToList();
            if (related.Count > 0 && related.Select(r => r.Category).Distinct().Count() == 1)
            {
                return related.OrderByDescending(r => r.PriorProbability).First().Id;
            }

            return General().Id;
        }

        var keepTests = new HashSet<DiagnosticTest>(ReferenceEqualityComparer.Instance);
        foreach (var built in tree.Tests)
        {
            var fresh = DiagnosticTreeBuilder.ToEntity(built, session.Id);
            var existing = session.Tests.FirstOrDefault(t => t.Key == fresh.Key && t.Origin == ActorKind.System);
            if (existing is null)
            {
                session.Tests.Add(fresh);
                existing = fresh;
            }
            else
            {
                CopyDefinition(fresh, existing);
                if (existing.SelectedOutcomeKey is not null && existing.Outcomes.All(o => o.Key != existing.SelectedOutcomeKey))
                {
                    existing.SelectedOutcomeKey = null;
                    existing.Result = null;
                    if (existing.Status == TestStatus.Completed) existing.Status = TestStatus.Available;
                }
            }

            existing.PrimaryNodeId = PrimaryFor(existing.RelatedCauseKeys);
            keepTests.Add(existing);
        }

        foreach (var test in session.Tests.Where(t => !keepTests.Contains(t)).ToList())
        {
            var keep = test.Origin != ActorKind.System || test.Status is TestStatus.Completed or TestStatus.Skipped;
            if (!keep)
            {
                session.Tests.Remove(test);
                continue;
            }

            keepTests.Add(test);
            if (test.PrimaryNodeId is not { } id || !keepNodes.Any(n => n.Id == id))
            {
                test.PrimaryNodeId = PrimaryFor(test.RelatedCauseKeys);
            }
        }

        foreach (var stale in session.Nodes.Where(n => !keepNodes.Contains(n)).ToList())
        {
            session.Nodes.Remove(stale);
        }
    }

    private static void CopyDefinition(DiagnosticTest from, DiagnosticTest to)
    {
        to.Title = from.Title;
        to.Purpose = from.Purpose;
        to.Procedure = from.Procedure;
        to.Tools = from.Tools;
        to.ExpectedResult = from.ExpectedResult;
        to.SpecificationNote = from.SpecificationNote;
        to.EstimatedMinutes = from.EstimatedMinutes;
        to.Difficulty = from.Difficulty;
        to.Invasiveness = from.Invasiveness;
        to.SafetyTags = from.SafetyTags;
        to.RelatedCauseKeys = from.RelatedCauseKeys;
        to.Outcomes = from.Outcomes;
        to.OriginDetail = from.OriginDetail;
        to.Sources = from.Sources;
    }

    private static string BuildCauseDescription(BuiltCause cause)
    {
        var description = cause.Definition.Description ?? string.Empty;
        if (cause.AppliedModifiers.Count == 0) return description;
        return $"{description}\n\nPriority adjusted: {string.Join("; ", cause.AppliedModifiers)}.".Trim();
    }

    public static string CategoryTitle(DiagnosticCategory category) => category switch
    {
        DiagnosticCategory.AirIntake => "Air / Intake",
        DiagnosticCategory.Hvac => "HVAC",
        DiagnosticCategory.HighVoltage => "High Voltage",
        DiagnosticCategory.Restraints => "Restraints (SRS)",
        _ => category.ToString(),
    };
}

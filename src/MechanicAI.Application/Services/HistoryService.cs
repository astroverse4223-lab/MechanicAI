using MechanicAI.Application.Abstractions;
using MechanicAI.Application.DTOs;
using MechanicAI.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MechanicAI.Application.Services;

public sealed record VehicleHistory(
    Guid VehicleId,
    string VehicleName,
    IReadOnlyList<VehicleHistoryItem> Items,
    IReadOnlyList<(string Code, int Occurrences, DateTime LastSeenUtc)> DtcHistory,
    int SessionCount,
    int RepairCount,
    int OpenRecallCount);

/// <summary>A vehicle's complete service story: sessions, DTCs, repairs, parts, notes, photos, documents, recalls, live data.</summary>
public sealed class HistoryService(IAppDbContextFactory dbFactory)
{
    public async Task<VehicleHistory?> GetVehicleHistoryAsync(Guid vehicleId, string? search = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var vehicle = await db.Vehicles.AsNoTracking().Include(v => v.Recalls).FirstOrDefaultAsync(v => v.Id == vehicleId, ct);
        if (vehicle is null) return null;

        var items = new List<VehicleHistoryItem>();
        var sessions = await db.DiagnosticSessions.AsNoTracking().Include(s => s.Dtcs)
            .Where(s => s.VehicleId == vehicleId).ToListAsync(ct);
        foreach (var s in sessions)
        {
            items.Add(new VehicleHistoryItem(s.StartedUtc, "Diagnostic session", s.Title,
                $"{s.Complaint}{(s.FinalDiagnosis is null ? string.Empty : $"\nDiagnosis: {s.FinalDiagnosis}")}", s.Id, null,
                s.FinalDiagnosis is null ? null : EvidenceClass.Verified));
            foreach (var dtc in s.Dtcs)
            {
                items.Add(new VehicleHistoryItem(dtc.RecordedUtc, "DTC", $"{dtc.Code} ({dtc.Status})", dtc.Description, s.Id, dtc.Code,
                    EvidenceClass.TechnicianObservation));
            }
        }

        var repairs = await db.Repairs.AsNoTracking().Include(r => r.Parts).Where(r => r.VehicleId == vehicleId).ToListAsync(ct);
        foreach (var r in repairs)
        {
            var parts = r.Parts.Count == 0 ? null : "Parts: " + string.Join(", ", r.Parts.Select(p => p.PartNumber is null ? p.Description : $"{p.Description} ({p.PartNumber})"));
            items.Add(new VehicleHistoryItem(r.PerformedUtc, r.Kind == RepairKind.Maintenance ? "Maintenance" : "Repair", r.Title,
                string.Join('\n', new[] { r.Description, parts }.Where(x => !string.IsNullOrWhiteSpace(x))), r.Id, null, EvidenceClass.TechnicianObservation));
        }

        var notes = await db.Notes.AsNoTracking().Where(n => n.VehicleId == vehicleId).ToListAsync(ct);
        items.AddRange(notes.Select(n => new VehicleHistoryItem(n.CreatedUtc, "Note", n.Title, n.Body, n.Id, null, EvidenceClass.TechnicianObservation)));

        var photos = await db.MediaAttachments.AsNoTracking().Where(m => m.VehicleId == vehicleId).ToListAsync(ct);
        items.AddRange(photos.Select(p => new VehicleHistoryItem(p.CreatedUtc, "Photo", p.Caption ?? p.FileName, null, p.Id, null, null)));

        var documents = await db.Documents.AsNoTracking().Where(d => d.VehicleId == vehicleId).ToListAsync(ct);
        items.AddRange(documents.Select(d => new VehicleHistoryItem(d.CreatedUtc, "Document", d.Title, d.Kind.ToString(), d.Id, null, null)));

        items.AddRange(vehicle.Recalls.Select(r => new VehicleHistoryItem(r.ReportReceivedDate ?? r.RetrievedUtc, "Recall",
            $"{r.CampaignNumber} — {r.Component}", r.Summary, r.Id, r.CampaignNumber, EvidenceClass.Verified)));

        var recordings = await db.LiveDataSessions.AsNoTracking().Where(l => l.VehicleId == vehicleId).ToListAsync(ct);
        items.AddRange(recordings.Select(l => new VehicleHistoryItem(l.StartedUtc, "Live data", l.Title,
            l.IsSimulated ? "Simulator recording" : l.AdapterDescription, l.Id, null, null)));

        var inspections = await db.Inspections.AsNoTracking().Where(i => i.VehicleId == vehicleId).ToListAsync(ct);
        items.AddRange(inspections.Select(i => new VehicleHistoryItem(i.CreatedUtc, "Inspection", i.TemplateName, i.Summary, i.Id, null, null)));

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            items = items.Where(i => i.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                                     (i.Detail?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
                                     i.Kind.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var dtcHistory = sessions.SelectMany(s => s.Dtcs)
            .GroupBy(d => d.Code)
            .Select(g => (g.Key, g.Count(), g.Max(d => d.RecordedUtc)))
            .OrderByDescending(x => x.Item3)
            .ToList();

        return new VehicleHistory(vehicleId, vehicle.DisplayName, items.OrderByDescending(i => i.WhenUtc).ToList(), dtcHistory,
            sessions.Count, repairs.Count, vehicle.Recalls.Count(r => r.Status is RecallStatus.Open or RecallStatus.Unknown));
    }

    /// <summary>Confirmed diagnoses across all vehicles that match a query ("shop knowledge").</summary>
    public async Task<IReadOnlyList<(Guid SessionId, string Vehicle, string Diagnosis, string Complaint, IReadOnlyList<string> Codes, DateTime WhenUtc)>>
        SearchConfirmedDiagnosesAsync(string query, int take = 10, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var sessions = await db.DiagnosticSessions.AsNoTracking().Include(s => s.Dtcs).Include(s => s.Vehicle)
            .Where(s => s.FinalDiagnosis != null)
            .OrderByDescending(s => s.CompletedUtc)
            .Take(500)
            .ToListAsync(ct);
        var parsed = Search.QueryParser.Parse(query);
        var terms = Common.Text.Tokenize(query).Where(t => t.Length > 2).ToList();
        return sessions
            .Select(s => new
            {
                Session = s,
                Score = parsed.Dtcs.Count(code => s.Dtcs.Any(d => d.Code == code)) * 3
                        + terms.Count(t => (s.Complaint + " " + s.FinalDiagnosis + " " + s.VehicleDescription + " " + s.Vehicle?.Description)
                            .Contains(t, StringComparison.OrdinalIgnoreCase)),
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(take)
            .Select(x => (x.Session.Id, x.Session.Vehicle?.Description ?? x.Session.VehicleDescription, x.Session.FinalDiagnosis!, x.Session.Complaint,
                (IReadOnlyList<string>)x.Session.Dtcs.Select(d => d.Code).ToList(), x.Session.CompletedUtc ?? x.Session.UpdatedUtc))
            .ToList();
    }
}

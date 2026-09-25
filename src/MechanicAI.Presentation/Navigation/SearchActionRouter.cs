using System.Globalization;
using MechanicAI.Application.Services;

namespace MechanicAI.Presentation.Navigation;

/// <summary>Maps universal-search actions to page navigations.</summary>
public static class SearchActionRouter
{
    public static (PageKey Page, object? Parameter) Route(SearchAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var arg = action.Argument ?? string.Empty;
        return action.Kind switch
        {
            SearchActionKind.StartDiagnosis => (PageKey.Diagnostics, new NavigationParameters.StartDiagnosisFromText(arg)),
            SearchActionKind.OpenDtc => (PageKey.Dtc, arg),
            SearchActionKind.DecodeVin => (PageKey.Vehicles, new NavigationParameters.DecodeVin(arg)),
            SearchActionKind.OpenVehicle => (PageKey.Vehicles, ParseGuid(arg)),
            SearchActionKind.CreateVehicle => (PageKey.Vehicles, new NavigationParameters.AddVehicle(arg)),
            SearchActionKind.OpenSession => (PageKey.Diagnostics, ParseGuid(arg)),
            SearchActionKind.OpenDocument => (PageKey.KnowledgeBase, ParseDocument(arg)),
            SearchActionKind.OpenWiring => (PageKey.Wiring, ParseDocument(arg)),
            SearchActionKind.OpenLesson => (PageKey.Training, ParseGuid(arg)),
            SearchActionKind.SearchWeb => (PageKey.Research, new NavigationParameters.SearchWeb(arg)),
            SearchActionKind.AskAssistant => (PageKey.Assistant, new NavigationParameters.AskAssistant(arg)),
            _ => (PageKey.Home, null),
        };
    }

    public static string Glyph(SearchActionKind kind) => kind switch
    {
        SearchActionKind.StartDiagnosis => "",
        SearchActionKind.OpenDtc => "",
        SearchActionKind.DecodeVin or SearchActionKind.OpenVehicle or SearchActionKind.CreateVehicle => "",
        SearchActionKind.OpenSession => "",
        SearchActionKind.OpenDocument => "",
        SearchActionKind.OpenWiring => "",
        SearchActionKind.OpenLesson => "",
        SearchActionKind.SearchWeb => "",
        SearchActionKind.AskAssistant => "",
        _ => "",
    };

    private static object? ParseGuid(string text) => Guid.TryParse(text, out var id) ? id : null;

    /// <summary>Document arguments look like "documentId|page|chunkId".</summary>
    private static object? ParseDocument(string text)
    {
        var parts = text.Split('|');
        if (parts.Length == 0 || !Guid.TryParse(parts[0], out var id)) return null;
        int? page = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : null;
        return new NavigationParameters.OpenDocument(id, page);
    }
}

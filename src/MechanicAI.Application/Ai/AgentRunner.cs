using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MechanicAI.Application.Abstractions.Ai;
using MechanicAI.Application.Common;
using MechanicAI.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace MechanicAI.Application.Ai;

public abstract record AgentEvent;

public sealed record AgentTextDelta(string Text) : AgentEvent;

public sealed record AgentToolStarted(string CallId, string Tool, string ArgumentsJson) : AgentEvent;

public sealed record AgentToolFinished(string CallId, string Tool, string Summary, bool IsError, IReadOnlyList<SourceCitation> NewSources) : AgentEvent;

public sealed record AgentStatus(string Message) : AgentEvent;

public sealed record ToolInvocation(string CallId, string Tool, string ArgumentsJson, string ResultJson, bool IsError, long DurationMs);

public sealed class AgentRequest
{
    public required IChatModel Model { get; init; }

    public required string SystemPrompt { get; init; }

    public IReadOnlyList<ChatMessage> History { get; init; } = [];

    public required ChatMessage UserMessage { get; init; }

    /// <summary>Tool names the agent may use (null = all registered tools).</summary>
    public IReadOnlySet<string>? AllowedTools { get; init; }

    public required ToolContext Context { get; init; }

    public int MaxRounds { get; init; } = 8;

    public double? Temperature { get; init; }
}

public sealed record AgentResult(
    string FinalText,
    IReadOnlyList<ChatMessage> NewMessages,
    IReadOnlyList<ToolInvocation> Invocations,
    CitationReport Citations,
    ChatFinishReason Finish,
    int? InputTokens,
    int? OutputTokens,
    string Provider,
    string Model,
    long ElapsedMs);

/// <summary>
/// Runs the model ↔ tools loop. Tool calls are validated against each tool's schema before
/// execution; invalid or truncated inputs, unknown tools, and tool failures are returned to
/// the model as error results — the model can never supply tool output itself. The final
/// answer is passed through <see cref="CitationValidator"/>. Models without tool support get
/// a deterministic retrieval pass instead (see <see cref="PreRetrieval"/>).
/// </summary>
public sealed class AgentRunner(IEnumerable<IAiTool> tools, ILogger<AgentRunner> logger)
{
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(90);
    private readonly Dictionary<string, IAiTool> _tools = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);

    public IReadOnlyCollection<IAiTool> Tools => _tools.Values;

    public async Task<AgentResult> RunAsync(AgentRequest request, Func<AgentEvent, Task>? onEvent, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var available = _tools.Values
            .Where(t => request.AllowedTools is null || request.AllowedTools.Contains(t.Name))
            .Where(t => !t.ReturnsPrivateData || request.Context.ModelIsLocal || request.AllowedTools?.Contains(t.Name) == true)
            .ToDictionary(t => t.Name, StringComparer.Ordinal);

        var supportsTools = request.Model.Capabilities.HasFlag(ModelCapabilities.Tools) && available.Count > 0;
        var definitions = supportsTools
            ? available.Values.Select(t => new ToolDefinition(t.Name, t.Description, t.ParametersSchema)).ToList()
            : [];

        var messages = new List<ChatMessage>(request.History);
        var newMessages = new List<ChatMessage>();
        var invocations = new List<ToolInvocation>();
        var userMessage = request.UserMessage;

        if (!supportsTools && available.Count > 0)
        {
            // Model can't call tools: gather evidence deterministically and give it to the model.
            if (onEvent is not null) await onEvent(new AgentStatus("Researching (this model can't call tools directly)…"));
            var gathered = await PreRetrieval.RunAsync(userMessage.Content ?? string.Empty, available, request.Context, invocations, onEvent, logger, ct);
            if (gathered.Length > 0)
            {
                userMessage = ChatMessage.User(
                    $"{userMessage.Content}\n\n---\nResearch results retrieved automatically for this question (cite them by label):\n{gathered}",
                    userMessage.Images);
            }
        }

        messages.Add(userMessage);
        var finalText = new StringBuilder();
        var finish = ChatFinishReason.Stop;
        int? inputTokens = null, outputTokens = null;

        for (var round = 1; round <= Math.Max(1, request.MaxRounds); round++)
        {
            var text = new StringBuilder();
            var calls = new List<ToolCall>();
            CompletionUpdate? completion = null;

            await foreach (var update in request.Model.StreamAsync(new ChatRequest
                           {
                               SystemPrompt = request.SystemPrompt,
                               Messages = messages,
                               Tools = definitions,
                               Temperature = request.Temperature,
                           }, ct))
            {
                switch (update)
                {
                    case TextDeltaUpdate delta:
                        text.Append(delta.Text);
                        if (onEvent is not null) await onEvent(new AgentTextDelta(delta.Text));
                        break;
                    case ToolCallUpdate call:
                        calls.Add(call.Call);
                        break;
                    case CompletionUpdate done:
                        completion = done;
                        break;
                }
            }

            inputTokens = (inputTokens ?? 0) + (completion?.InputTokens ?? 0);
            outputTokens = (outputTokens ?? 0) + (completion?.OutputTokens ?? 0);
            finish = completion?.Reason ?? (calls.Count > 0 ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop);

            if (finish == ChatFinishReason.Refusal)
            {
                var note = completion?.Detail ?? "The AI provider declined this request.";
                finalText.Append(text).Append(text.Length > 0 ? "\n\n" : string.Empty).Append("> ").Append(note);
                newMessages.Add(ChatMessage.Assistant(text.ToString()));
                break;
            }

            if (calls.Count == 0)
            {
                finalText.Append(text);
                var assistant = ChatMessage.Assistant(text.ToString());
                newMessages.Add(assistant);
                if (finish == ChatFinishReason.Length) finalText.Append("\n\n> The answer reached the output length limit and may be incomplete.");
                break;
            }

            var assistantTurn = ChatMessage.Assistant(text.ToString(), calls);
            messages.Add(assistantTurn);
            newMessages.Add(assistantTurn);

            // Execute all calls from this turn concurrently; return every result together.
            var results = await Task.WhenAll(calls.Select(call =>
                ExecuteCallAsync(call, available, request.Context, truncated: finish == ChatFinishReason.Length, onEvent, ct)));
            foreach (var (message, invocation) in results)
            {
                messages.Add(message);
                newMessages.Add(message);
                invocations.Add(invocation);
            }

            if (round == request.MaxRounds)
            {
                finalText.Append(text);
                finalText.Append("\n\n> Stopped after the maximum number of research steps. Ask a follow-up to continue.");
            }
        }

        var report = CitationValidator.Validate(finalText.ToString(), request.Context.Sources);
        if (report.HadProblems)
        {
            logger.LogWarning("Citation validation adjusted the answer: {Warnings}", string.Join(" | ", report.Warnings));
        }

        return new AgentResult(report.CleanedText, newMessages, invocations, report, finish, inputTokens, outputTokens,
            request.Model.Provider, request.Model.Model, stopwatch.ElapsedMilliseconds);
    }

    private async Task<(ChatMessage Message, ToolInvocation Invocation)> ExecuteCallAsync(
        ToolCall call,
        IReadOnlyDictionary<string, IAiTool> available,
        ToolContext context,
        bool truncated,
        Func<AgentEvent, Task>? onEvent,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        if (onEvent is not null) await onEvent(new AgentToolStarted(call.Id, call.Name, call.ArgumentsJson));
        var before = context.Sources.Sources.Count;
        ToolExecutionResult result;

        if (truncated)
        {
            result = ToolExecutionResult.Fail("Your output hit the length limit while writing this tool call, so its input may be incomplete. The tool was not run. Re-issue a shorter call.");
        }
        else if (!available.TryGetValue(call.Name, out var tool))
        {
            result = ToolExecutionResult.Fail($"Unknown tool '{call.Name}'. Available tools: {string.Join(", ", available.Keys)}.");
        }
        else if (ToolInputValidator.Validate(tool.ParametersSchema, call.ArgumentsJson, out var args) is { } validationError)
        {
            result = new ToolExecutionResult(Json.Serialize(new { INVALID_JSON = call.ArgumentsJson, error = validationError }), true, validationError);
        }
        else
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(ToolTimeout);
                result = await tool.ExecuteAsync(args, context, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                result = ToolExecutionResult.Fail($"The {call.Name} tool timed out.");
            }
            catch (ExternalServiceException ex)
            {
                result = ToolExecutionResult.Fail(ex.UserMessage);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Tool {Tool} failed", call.Name);
                result = ToolExecutionResult.Fail($"The {call.Name} tool failed unexpectedly.");
            }
        }

        stopwatch.Stop();
        logger.LogInformation("AI tool call {Tool} ({CallId}) completed in {Ms} ms, error={IsError}", call.Name, call.Id, stopwatch.ElapsedMilliseconds, result.IsError);
        var newSources = context.Sources.Sources.Skip(before).ToList();
        if (onEvent is not null) await onEvent(new AgentToolFinished(call.Id, call.Name, result.Summary, result.IsError, newSources));
        return (ChatMessage.ToolResult(call.Id, call.Name, result.ContentJson, result.IsError),
            new ToolInvocation(call.Id, call.Name, call.ArgumentsJson, result.ContentJson, result.IsError, stopwatch.ElapsedMilliseconds));
    }
}

/// <summary>
/// Deterministic research pass for models without tool calling: parses the question and
/// runs the obviously relevant lookups (DTC, VIN, recalls, documents, shop history, web).
/// </summary>
internal static class PreRetrieval
{
    public static async Task<string> RunAsync(
        string question,
        IReadOnlyDictionary<string, IAiTool> tools,
        ToolContext context,
        List<ToolInvocation> invocations,
        Func<AgentEvent, Task>? onEvent,
        ILogger logger,
        CancellationToken ct)
    {
        var parsed = Search.QueryParser.Parse(question);
        var plan = new List<(string Tool, object Args)>();
        foreach (var code in parsed.Dtcs.Take(4)) plan.Add(("search_dtc", new { code, make = parsed.Make }));
        if (parsed.Vin is not null) plan.Add(("decode_vin", new { vin = parsed.Vin }));
        if (context.VehicleId is { } vehicleId)
        {
            plan.Add(("get_vehicle_history", new { vehicle_id = vehicleId.ToString() }));
            plan.Add(("search_recalls", new { vehicle_id = vehicleId.ToString() }));
        }

        if (context.SessionId is { } sessionId) plan.Add(("get_diagnostic_session", new { session_id = sessionId.ToString() }));
        plan.Add(("search_documents", new { query = question }));
        plan.Add(("search_knowledge_base", new { query = question }));
        if (parsed.PrimaryIntent is Domain.Enums.SearchIntent.Web or Domain.Enums.SearchIntent.Repair or Domain.Enums.SearchIntent.Component
            || parsed.Dtcs.Count > 0 || parsed.Symptoms.Count > 0)
        {
            plan.Add(("search_web", new { query = question, max_results = 5 }));
        }

        var sb = new StringBuilder();
        foreach (var (name, args) in plan)
        {
            if (!tools.TryGetValue(name, out var tool)) continue;
            var argsJson = Json.Serialize(args);
            if (onEvent is not null) await onEvent(new AgentToolStarted(name, name, argsJson));
            var before = context.Sources.Sources.Count;
            var stopwatch = Stopwatch.StartNew();
            ToolExecutionResult result;
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                result = await tool.ExecuteAsync(doc.RootElement.Clone(), context, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Pre-retrieval step {Tool} failed", name);
                result = ToolExecutionResult.Fail(ex is ExternalServiceException e ? e.UserMessage : "failed");
            }

            invocations.Add(new ToolInvocation(name, name, argsJson, result.ContentJson, result.IsError, stopwatch.ElapsedMilliseconds));
            if (onEvent is not null) await onEvent(new AgentToolFinished(name, name, result.Summary, result.IsError, context.Sources.Sources.Skip(before).ToList()));
            if (result.IsError) continue;
            sb.Append("### ").Append(name).Append('\n').Append(Text.Truncate(result.ContentJson, 6000)).Append("\n\n");
        }

        return sb.ToString();
    }
}

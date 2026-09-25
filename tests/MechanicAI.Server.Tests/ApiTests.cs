using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MechanicAI.Application.Abstractions;
using MechanicAI.Application.DTOs;
using MechanicAI.Application.Services;
using MechanicAI.Domain.Enums;
using MechanicAI.Server.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MechanicAI.Server.Tests;

public sealed class ApiTests(SharedServer server) : IClassFixture<SharedServer>
{
    private static readonly JsonSerializerOptions Json = ServerFactory.Json;

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Health_endpoints_are_anonymous_and_healthy(string path)
    {
        using var client = server.CreateClient();
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Missing_resource_maps_to_404_problem()
    {
        using var client = await server.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/vehicles/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(404, problem.GetProperty("status").GetInt32());
        Assert.Equal("NotFound", problem.GetProperty("errorKind").GetString());
        Assert.Equal("Vehicle was not found.", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Updating_a_missing_record_returns_404_not_500()
    {
        using var client = await server.CreateAuthenticatedClientAsync();
        var vehicle = await client.PutAsJsonAsync($"/api/vehicles/{Guid.NewGuid()}", new VehicleInput { Make = "Ford", Model = "F-150" }, Json);
        Assert.Equal(HttpStatusCode.NotFound, vehicle.StatusCode);

        var customer = await client.PutAsJsonAsync($"/api/customers/{Guid.NewGuid()}",
            new CustomerRequest("Pat", "Smith", null, null, null, null, null, null, null, null), Json);
        Assert.Equal(HttpStatusCode.NotFound, customer.StatusCode);
    }

    [Fact]
    public async Task Validation_errors_map_to_400_problem()
    {
        using var client = await server.CreateAuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync("/api/vehicles", new VehicleInput { Make = "", Model = "" }, Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Validation", problem.GetProperty("errorKind").GetString());
    }

    [Fact]
    public async Task Unconfigured_web_search_maps_to_503()
    {
        using var client = await server.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/research/search?q=p0301%20misfire");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("NotConfigured", problem.GetProperty("errorKind").GetString());
    }

    [Fact]
    public async Task Customer_and_vehicle_crud_round_trip()
    {
        using var client = await server.CreateAuthenticatedClientAsync();
        var created = await client.PostAsJsonAsync("/api/customers",
            new CustomerRequest("Dana", "Lopez", null, "555-0100", "dana@example.test", null, null, null, null, null), Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var customerId = (await created.Content.ReadFromJsonAsync<CreatedResponse>(Json))!.Id;

        var vehicle = await client.PostAsJsonAsync("/api/vehicles",
            new VehicleInput { Year = 2017, Make = "chevrolet", Model = "Silverado 1500", Engine = "5.3L V8", Mileage = 98000, CustomerId = customerId }, Json);
        Assert.Equal(HttpStatusCode.Created, vehicle.StatusCode);
        var vehicleId = (await vehicle.Content.ReadFromJsonAsync<CreatedResponse>(Json))!.Id;

        var dto = await client.GetFromJsonAsync<VehicleDto>($"/api/vehicles/{vehicleId}", Json);
        Assert.Equal("Silverado 1500", dto!.Model);
        Assert.Equal(customerId, dto.CustomerId);

        var search = await client.GetFromJsonAsync<List<VehicleSummary>>("/api/vehicles?search=silverado", Json);
        Assert.Contains(search!, v => v.Id == vehicleId);

        var customer = await client.GetFromJsonAsync<CustomerDto>($"/api/customers/{customerId}", Json);
        Assert.Contains(customer!.Vehicles, v => v.Id == vehicleId);

        var history = await client.GetFromJsonAsync<VehicleHistoryDto>($"/api/vehicles/{vehicleId}/history", Json);
        Assert.Equal(vehicleId, history!.VehicleId);
    }

    [Fact]
    public async Task Diagnostic_session_builds_a_tree_and_records_test_outcomes()
    {
        using var client = await server.CreateAuthenticatedClientAsync();
        var start = await client.PostAsJsonAsync("/api/sessions",
            new StartSessionRequest(null, "2015 Honda Accord 2.4L", "Rough idle, check engine light flashing", ["rough idle"], ["P0301"], null, 120000), Json);
        Assert.Equal(HttpStatusCode.Created, start.StatusCode);
        var sessionId = (await start.Content.ReadFromJsonAsync<CreatedResponse>(Json))!.Id;

        var session = (await client.GetFromJsonAsync<DiagnosticSessionDto>($"/api/sessions/{sessionId}", Json))!;
        Assert.Contains(session.Dtcs, d => d.Code == "P0301");
        Assert.Equal("Test Owner", session.TechnicianName);
        Assert.NotEmpty(session.Nodes);
        Assert.NotEmpty(session.Tests);
        Assert.NotNull(session.NextTestId);

        var test = session.Tests.Single(t => t.Id == session.NextTestId);
        var outcome = test.Outcomes.First(o => !o.Inconclusive);
        var recorded = await client.PostAsJsonAsync($"/api/sessions/{sessionId}/tests/{test.Id}/result",
            new RecordTestResultRequest(outcome.Key, "Measured during test"), Json);
        Assert.Equal(HttpStatusCode.OK, recorded.StatusCode);
        var updated = (await recorded.Content.ReadFromJsonAsync<DiagnosticSessionDto>(Json))!;
        var done = updated.Tests.Single(t => t.Id == test.Id);
        Assert.Equal(TestStatus.Completed, done.Status);
        Assert.Equal(outcome.Key, done.SelectedOutcomeKey);
        Assert.Equal(1, updated.TestsCompleted);

        var bad = await client.PostAsJsonAsync($"/api/sessions/{sessionId}/tests/{test.Id}/result", new RecordTestResultRequest("no-such-outcome", null), Json);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var report = await client.GetAsync($"/api/sessions/{sessionId}/report");
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        Assert.Contains("P0301", await report.Content.ReadAsStringAsync());

        var list = await client.GetFromJsonAsync<List<DiagnosticSessionSummary>>("/api/sessions", Json);
        Assert.Contains(list!, s => s.Id == sessionId);
    }

    [Fact]
    public async Task Dtc_lookup_returns_reference_data()
    {
        using var client = await server.CreateAuthenticatedClientAsync();
        var detail = await client.GetFromJsonAsync<DtcDetailDto>("/api/dtcs/U0100", Json);
        Assert.Equal("U0100", detail!.Code);
        Assert.True(detail.IsKnown);
        Assert.NotEmpty(detail.Definitions);

        var search = await client.GetFromJsonAsync<List<DtcSearchResult>>("/api/dtcs?q=U010", Json);
        Assert.NotEmpty(search!);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/dtcs/NOT-A-CODE")).StatusCode);
    }

    [Fact]
    public async Task Knowledge_base_upload_is_indexed_and_searchable()
    {
        using var client = await server.CreateAuthenticatedClientAsync();
        var marker = $"zorbulator{Guid.NewGuid():N}"[..20];
        var text = $"Intake manifold bolts: tighten to 22 N·m in sequence. The {marker} sensor sits behind the throttle body.";
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(file, "file", "torque-notes.txt");
        form.Add(new StringContent("ServiceManual"), "kind");
        form.Add(new StringContent("Torque notes"), "title");

        var upload = await client.PostAsync("/api/knowledge/documents", form);
        Assert.Equal(HttpStatusCode.Accepted, upload.StatusCode);
        var id = (await upload.Content.ReadFromJsonAsync<CreatedResponse>(Json))!.Id;

        DocumentDto? document = null;
        for (var i = 0; i < 100; i++)
        {
            document = await client.GetFromJsonAsync<DocumentDto>($"/api/knowledge/documents/{id}", Json);
            if (document!.Status is DocumentStatus.Ready or DocumentStatus.ReadyKeywordOnly or DocumentStatus.Failed) break;
            await Task.Delay(100);
        }

        Assert.Equal(DocumentStatus.ReadyKeywordOnly, document!.Status);
        Assert.Equal(DocumentKind.ServiceManual, document.Kind);

        var hits = await client.GetFromJsonAsync<List<KnowledgeHit>>($"/api/knowledge/search?q={marker}", Json);
        Assert.Contains(hits!, h => h.DocumentId == id);

        var download = await client.GetAsync($"/api/knowledge/documents/{id}/file");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(text, await download.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Knowledge_base_rejects_unsupported_and_oversized_uploads()
    {
        using var client = await server.CreateAuthenticatedClientAsync();

        using var exe = new MultipartFormDataContent { { new ByteArrayContent([0x4D, 0x5A, 0x90, 0x00]), "file", "tool.exe" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/knowledge/documents", exe)).StatusCode);

        using var fakePdf = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes("not a pdf at all")), "file", "manual.pdf" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/knowledge/documents", fakePdf)).StatusCode);

        var big = new byte[(1024 * 1024) + 10];
        Array.Fill(big, (byte)'a');
        using var tooLarge = new MultipartFormDataContent { { new ByteArrayContent(big), "file", "big.txt" } };
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.PostAsync("/api/knowledge/documents", tooLarge)).StatusCode);
    }

    [Fact]
    public async Task Estimates_are_calculated_by_the_shop_service()
    {
        using var client = await server.CreateAuthenticatedClientAsync();
        var created = await client.PostAsJsonAsync("/api/estimates", new EstimateRequest(null, null, null, 0.10m, "Test",
        [
            new EstimateLineInput(EstimateLineKind.Labor, "Replace coil", null, 1.5m, 100m, false),
            new EstimateLineInput(EstimateLineKind.Part, "Ignition coil", "C-123", 1m, 80m, true),
        ]), Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<CreatedResponse>(Json))!.Id;

        var estimate = await client.GetFromJsonAsync<EstimateDto>($"/api/estimates/{id}", Json);
        Assert.Equal(230m, estimate!.Subtotal);
        Assert.Equal(8m, estimate.Tax);
        Assert.Equal(238m, estimate.Total);
    }

    [Fact]
    public async Task Training_quizzes_hide_the_answer_key()
    {
        using var client = await server.CreateAuthenticatedClientAsync();
        var courses = await client.GetFromJsonAsync<List<CourseSummary>>("/api/training/courses", Json);
        Assert.NotEmpty(courses!);

        var raw = await client.GetStringAsync($"/api/training/courses/{courses![0].Id}");
        Assert.DoesNotContain("answerIndex", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Audit_entries_record_the_request_identity()
    {
        using var client = await server.CreateAuthenticatedClientAsync();
        var created = await client.PostAsJsonAsync("/api/users",
            new CreateUserRequest($"audit-{Guid.NewGuid():N}@shop.test", "Audit", "Audit-Passw0rd!!", UserRole.ServiceAdvisor), Json);
        var user = (await created.Content.ReadFromJsonAsync<UserDto>(Json))!;

        var factory = server.Services.GetRequiredService<IAppDbContextFactory>();
        await using var db = await factory.CreateAsync();
        var entityId = user.Id.ToString();
        var entry = await db.AuditLog.SingleAsync(a => a.Action == "user.create" && a.EntityId == entityId);
        Assert.Equal("Test Owner", entry.UserName);
        Assert.NotNull(entry.UserId);
    }
}

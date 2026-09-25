using MechanicAI.Server.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.AddMechanicAiServer();
var app = builder.Build();

if (args is ["create-admin", ..])
{
    // One-time CLI bootstrap: `MechanicAI.Server create-admin --email owner@shop.example [--name "Pat Owner"]`.
    // The password comes from MECHANICAI_ADMIN_PASSWORD or an interactive prompt, never from the command line.
    return await AdminCli.RunAsync(app.Services, args[1..]);
}

await app.Services.InitializeServerAsync();
app.UseMechanicAiServer();
await app.RunAsync();
return 0;

/// <summary>Entry point marker for WebApplicationFactory-based integration tests.</summary>
public partial class Program;

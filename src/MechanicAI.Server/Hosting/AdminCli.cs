using System.Text;
using MechanicAI.Infrastructure.Persistence;
using MechanicAI.Server.Auth;
using Microsoft.Extensions.Options;

namespace MechanicAI.Server.Hosting;

/// <summary>
/// <c>create-admin</c> command: creates the first Owner account on an empty server. Refuses to
/// run once any Owner or Admin exists, so it cannot be used to take over a configured shop.
/// </summary>
public static class AdminCli
{
    public const string PasswordEnvironmentVariable = "MECHANICAI_ADMIN_PASSWORD";

    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        var email = Option(args, "--email");
        var name = Option(args, "--name") ?? "Shop owner";
        if (string.IsNullOrWhiteSpace(email))
        {
            await Console.Error.WriteLineAsync("Usage: MechanicAI.Server create-admin --email <email> [--name <display name>]");
            await Console.Error.WriteLineAsync($"The password is read from {PasswordEnvironmentVariable} or prompted for.");
            return 2;
        }

        if (services.GetRequiredService<IOptions<DatabaseOptions>>().Value.ApplyMigrationsOnStartup)
        {
            await services.GetRequiredService<DatabaseInitializer>().InitializeAsync();
        }

        var password = Environment.GetEnvironmentVariable(PasswordEnvironmentVariable);
        if (string.IsNullOrEmpty(password))
        {
            password = ReadPassword("Password: ");
            var confirm = ReadPassword("Confirm password: ");
            if (!string.Equals(password, confirm, StringComparison.Ordinal))
            {
                await Console.Error.WriteLineAsync("Passwords do not match.");
                return 1;
            }
        }

        var auth = services.GetRequiredService<AuthService>();
        var result = await auth.BootstrapOwnerAsync(email, password, name, CancellationToken.None);
        if (result.IsFailure)
        {
            await Console.Error.WriteLineAsync(result.Error!.Message);
            return 1;
        }

        Console.WriteLine($"Owner account created for {result.Value!.Email}.");
        return 0;
    }

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string ReadPassword(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected) return Console.ReadLine() ?? string.Empty;

        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0) sb.Length--;
                continue;
            }

            if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
        }

        Console.WriteLine();
        return sb.ToString();
    }
}

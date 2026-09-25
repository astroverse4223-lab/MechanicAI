using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Common;

namespace MechanicAI.Application.Services;

/// <summary>
/// Optional workstation sign-in: a password (stored only as a PBKDF2 hash in the encrypted
/// secret store) required at startup and after the auto-lock timeout. Repeated failures are
/// throttled and every attempt is audited.
/// </summary>
public sealed class WorkstationAuthService(ISecretStore secrets, IPasswordHasher hasher, ISettingsStore settings, IAuditLogger audit)
{
    public const int MinimumPasswordLength = 8;
    private int _failures;
    private DateTime _lockedUntilUtc = DateTime.MinValue;

    public bool IsSignInRequired => settings.Current.Security.RequireSignIn;

    public async Task<bool> HasPasswordAsync(CancellationToken ct = default) =>
        !string.IsNullOrEmpty(await secrets.GetAsync(SecretNames.LocalPasswordHash, ct));

    public async Task<Result> SetPasswordAsync(string? currentPassword, string newPassword, CancellationToken ct = default)
    {
        var existing = await secrets.GetAsync(SecretNames.LocalPasswordHash, ct);
        if (!string.IsNullOrEmpty(existing) && hasher.Verify(existing, currentPassword ?? string.Empty) == PasswordVerification.Failed)
        {
            await audit.LogAsync("workstation.password.change", succeeded: false, ct: ct);
            return Error.Validation("The current password is incorrect.");
        }

        var problem = ValidateStrength(newPassword);
        if (problem is not null) return Error.Validation(problem);
        await secrets.SetAsync(SecretNames.LocalPasswordHash, hasher.Hash(newPassword), ct);
        await settings.UpdateAsync(s => s.Security.RequireSignIn = true, ct);
        await audit.LogAsync("workstation.password.set", ct: ct);
        return Result.Success();
    }

    public async Task<Result> RemovePasswordAsync(string currentPassword, CancellationToken ct = default)
    {
        var existing = await secrets.GetAsync(SecretNames.LocalPasswordHash, ct);
        if (string.IsNullOrEmpty(existing)) return Result.Success();
        if (hasher.Verify(existing, currentPassword) == PasswordVerification.Failed)
        {
            await audit.LogAsync("workstation.password.remove", succeeded: false, ct: ct);
            return Error.Validation("The password is incorrect.");
        }

        await secrets.DeleteAsync(SecretNames.LocalPasswordHash, ct);
        await settings.UpdateAsync(s => s.Security.RequireSignIn = false, ct);
        await audit.LogAsync("workstation.password.remove", ct: ct);
        return Result.Success();
    }

    public async Task<Result> SignInAsync(string password, CancellationToken ct = default)
    {
        if (DateTime.UtcNow < _lockedUntilUtc)
        {
            return Error.Validation($"Too many attempts. Try again in {(int)(_lockedUntilUtc - DateTime.UtcNow).TotalSeconds + 1} seconds.");
        }

        var hash = await secrets.GetAsync(SecretNames.LocalPasswordHash, ct);
        if (string.IsNullOrEmpty(hash)) return Result.Success();

        var verification = hasher.Verify(hash, password);
        if (verification == PasswordVerification.Failed)
        {
            _failures++;
            if (_failures >= 5) _lockedUntilUtc = DateTime.UtcNow.AddSeconds(Math.Min(300, 30 * (_failures - 4)));
            await audit.LogAsync("workstation.signin", succeeded: false, ct: ct);
            return Error.Validation("Incorrect password.");
        }

        _failures = 0;
        if (verification == PasswordVerification.SuccessRehashNeeded) await secrets.SetAsync(SecretNames.LocalPasswordHash, hasher.Hash(password), ct);
        await audit.LogAsync("workstation.signin", ct: ct);
        return Result.Success();
    }

    public static string? ValidateStrength(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinimumPasswordLength) return $"Use at least {MinimumPasswordLength} characters.";
        if (!password.Any(char.IsLetter) || !password.Any(c => !char.IsLetter(c))) return "Use a mix of letters and numbers or symbols.";
        return null;
    }
}

using System.Globalization;
using System.Security.Cryptography;
using MechanicAI.Application.Abstractions;

namespace MechanicAI.Infrastructure.Security;

/// <summary>
/// PBKDF2-HMAC-SHA256 password hashing with a per-password random salt.
/// Format: <c>PBKDF2$SHA256$&lt;iterations&gt;$&lt;salt b64&gt;$&lt;hash b64&gt;</c>.
/// Iteration count follows current OWASP guidance and is upgraded transparently on login.
/// </summary>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    public const int CurrentIterations = 600_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    private readonly int _iterations;

    public Pbkdf2PasswordHasher()
        : this(CurrentIterations)
    {
    }

    /// <summary>Lower iteration counts are only for fast unit tests.</summary>
    public Pbkdf2PasswordHasher(int iterations) => _iterations = iterations;

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, _iterations, HashAlgorithmName.SHA256, HashBytes);
        return string.Create(CultureInfo.InvariantCulture,
            $"PBKDF2$SHA256${_iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");
    }

    public PasswordVerification Verify(string hash, string password)
    {
        if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(password)) return PasswordVerification.Failed;
        var parts = hash.Split('$');
        if (parts.Length != 5 || parts[0] != "PBKDF2" || parts[1] != "SHA256") return PasswordVerification.Failed;
        if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var iterations) || iterations < 1)
        {
            return PasswordVerification.Failed;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[3]);
            expected = Convert.FromBase64String(parts[4]);
        }
        catch (FormatException)
        {
            return PasswordVerification.Failed;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected)) return PasswordVerification.Failed;
        return iterations < _iterations ? PasswordVerification.SuccessRehashNeeded : PasswordVerification.Success;
    }
}

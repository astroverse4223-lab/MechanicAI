using MechanicAI.Application.Abstractions;
using MechanicAI.Infrastructure.Security;

namespace MechanicAI.Infrastructure.Tests.Security;

public class Pbkdf2PasswordHasherTests
{
    private const int FastIterations = 1_000;
    private readonly Pbkdf2PasswordHasher _hasher = new(FastIterations);

    [Fact]
    public void Hash_UsesDocumentedFormat()
    {
        var hash = _hasher.Hash("wrench-2026");

        var parts = hash.Split('$');
        Assert.Equal(5, parts.Length);
        Assert.Equal("PBKDF2", parts[0]);
        Assert.Equal("SHA256", parts[1]);
        Assert.Equal("1000", parts[2]);
        Assert.Equal(16, Convert.FromBase64String(parts[3]).Length);
        Assert.Equal(32, Convert.FromBase64String(parts[4]).Length);
    }

    [Fact]
    public void Hash_IsSaltedPerCall()
    {
        Assert.NotEqual(_hasher.Hash("same"), _hasher.Hash("same"));
    }

    [Fact]
    public void Hash_RejectsEmptyPassword()
    {
        Assert.Throws<ArgumentException>(() => _hasher.Hash(string.Empty));
    }

    [Fact]
    public void Verify_AcceptsCorrectAndRejectsWrongPassword()
    {
        var hash = _hasher.Hash("wrench-2026");

        Assert.Equal(PasswordVerification.Success, _hasher.Verify(hash, "wrench-2026"));
        Assert.Equal(PasswordVerification.Failed, _hasher.Verify(hash, "Wrench-2026"));
        Assert.Equal(PasswordVerification.Failed, _hasher.Verify(hash, string.Empty));
    }

    [Fact]
    public void Verify_RequestsRehashWhenIterationsAreBelowCurrent()
    {
        var oldHash = new Pbkdf2PasswordHasher(500).Hash("wrench-2026");

        Assert.Equal(PasswordVerification.SuccessRehashNeeded, _hasher.Verify(oldHash, "wrench-2026"));
    }

    [Fact]
    public void Verify_HigherIterationHashIsStillPlainSuccess()
    {
        var strongerHash = new Pbkdf2PasswordHasher(2_000).Hash("wrench-2026");

        Assert.Equal(PasswordVerification.Success, _hasher.Verify(strongerHash, "wrench-2026"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("PBKDF2$SHA1$1000$AAAA$AAAA")]
    [InlineData("BCRYPT$SHA256$1000$AAAA$AAAA")]
    [InlineData("PBKDF2$SHA256$0$AAAA$AAAA")]
    [InlineData("PBKDF2$SHA256$abc$AAAA$AAAA")]
    [InlineData("PBKDF2$SHA256$1000$not base64!$AAAA")]
    [InlineData("PBKDF2$SHA256$1000$AAAA")]
    public void Verify_RejectsMalformedHashes(string hash)
    {
        Assert.Equal(PasswordVerification.Failed, _hasher.Verify(hash, "wrench-2026"));
    }

    [Fact]
    public void Verify_RejectsTamperedHash()
    {
        var parts = _hasher.Hash("wrench-2026").Split('$');
        var digest = Convert.FromBase64String(parts[4]);
        digest[0] ^= 0xFF;
        parts[4] = Convert.ToBase64String(digest);

        Assert.Equal(PasswordVerification.Failed, _hasher.Verify(string.Join('$', parts), "wrench-2026"));
    }

    [Fact]
    public void DefaultConstructor_UsesCurrentIterationCount()
    {
        Assert.Equal(600_000, Pbkdf2PasswordHasher.CurrentIterations);
        // A fast hash made with fewer iterations must be upgraded by the production hasher.
        var fast = _hasher.Hash("wrench-2026");
        Assert.Equal(PasswordVerification.SuccessRehashNeeded, new Pbkdf2PasswordHasher().Verify(fast, "wrench-2026"));
    }
}

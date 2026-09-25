using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Common;
using MechanicAI.Application.Services;
using MechanicAI.Application.Settings;
using NSubstitute;

namespace MechanicAI.Application.Tests.Services;

public class WorkstationAuthServiceTests
{
    private readonly ISecretStore _secrets = Substitute.For<ISecretStore>();
    private readonly IPasswordHasher _hasher = Substitute.For<IPasswordHasher>();
    private readonly ISettingsStore _settings = Substitute.For<ISettingsStore>();
    private readonly IAuditLogger _audit = Substitute.For<IAuditLogger>();
    private readonly AppSettings _appSettings = new();

    public WorkstationAuthServiceTests()
    {
        _settings.Current.Returns(_appSettings);
        _settings
            .When(s => s.UpdateAsync(Arg.Any<Action<AppSettings>>(), Arg.Any<CancellationToken>()))
            .Do(call => call.Arg<Action<AppSettings>>()(_appSettings));
        _hasher.Hash(Arg.Any<string>()).Returns(call => "hash:" + call.Arg<string>());
    }

    private WorkstationAuthService Service() => new(_secrets, _hasher, _settings, _audit);

    private void StoredHash(string? hash) =>
        _secrets.GetAsync(SecretNames.LocalPasswordHash, Arg.Any<CancellationToken>()).Returns(hash);

    [Theory]
    [InlineData("", "Use at least 8 characters.")]
    [InlineData("short1", "Use at least 8 characters.")]
    [InlineData("onlyletters", "Use a mix of letters and numbers or symbols.")]
    [InlineData("12345678", "Use a mix of letters and numbers or symbols.")]
    [InlineData("letters&1", null)]
    public void ValidateStrength(string password, string? expected)
    {
        Assert.Equal(expected, WorkstationAuthService.ValidateStrength(password));
    }

    [Fact]
    public async Task SetPassword_FirstTime_StoresHashEnablesSignInAndAudits()
    {
        StoredHash(null);

        var result = await Service().SetPasswordAsync(null, "wrench-2026");

        Assert.True(result.IsSuccess);
        await _secrets.Received(1).SetAsync(SecretNames.LocalPasswordHash, "hash:wrench-2026", Arg.Any<CancellationToken>());
        Assert.True(_appSettings.Security.RequireSignIn);
        await _audit.Received(1).LogAsync("workstation.password.set", ct: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetPassword_RejectsWeakPasswordWithoutStoringAnything()
    {
        StoredHash(null);

        var result = await Service().SetPasswordAsync(null, "weak");

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Validation, result.Error!.Kind);
        await _secrets.DidNotReceiveWithAnyArgs().SetAsync(default!, default!, default);
        Assert.False(_appSettings.Security.RequireSignIn);
    }

    [Fact]
    public async Task SetPassword_RequiresCorrectCurrentPassword()
    {
        StoredHash("existing");
        _hasher.Verify("existing", "wrong").Returns(PasswordVerification.Failed);

        var result = await Service().SetPasswordAsync("wrong", "wrench-2026");

        Assert.Equal("The current password is incorrect.", result.Error?.Message);
        await _secrets.DidNotReceiveWithAnyArgs().SetAsync(default!, default!, default);
        await _audit.Received(1).LogAsync("workstation.password.change", succeeded: false, ct: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SignIn_SucceedsWhenNoPasswordIsSet()
    {
        StoredHash(null);

        var result = await Service().SignInAsync("anything");

        Assert.True(result.IsSuccess);
        _hasher.DidNotReceiveWithAnyArgs().Verify(default!, default!);
    }

    [Fact]
    public async Task SignIn_RehashesWhenHashIsOutdated()
    {
        StoredHash("old-hash");
        _hasher.Verify("old-hash", "wrench-2026").Returns(PasswordVerification.SuccessRehashNeeded);

        var result = await Service().SignInAsync("wrench-2026");

        Assert.True(result.IsSuccess);
        await _secrets.Received(1).SetAsync(SecretNames.LocalPasswordHash, "hash:wrench-2026", Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync("workstation.signin", ct: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SignIn_LocksOutAfterFiveFailures()
    {
        StoredHash("hash");
        _hasher.Verify("hash", Arg.Any<string>()).Returns(PasswordVerification.Failed);
        var service = Service();

        for (var i = 0; i < 5; i++)
        {
            var failed = await service.SignInAsync("wrong");
            Assert.Equal("Incorrect password.", failed.Error?.Message);
        }

        _hasher.ClearReceivedCalls();
        var locked = await service.SignInAsync("wrench-2026");

        Assert.StartsWith("Too many attempts", locked.Error?.Message, StringComparison.Ordinal);
        _hasher.DidNotReceiveWithAnyArgs().Verify(default!, default!);
        await _audit.Received(5).LogAsync("workstation.signin", succeeded: false, ct: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemovePassword_DeletesSecretAndDisablesSignIn()
    {
        StoredHash("hash");
        _hasher.Verify("hash", "wrench-2026").Returns(PasswordVerification.Success);
        _appSettings.Security.RequireSignIn = true;

        var result = await Service().RemovePasswordAsync("wrench-2026");

        Assert.True(result.IsSuccess);
        await _secrets.Received(1).DeleteAsync(SecretNames.LocalPasswordHash, Arg.Any<CancellationToken>());
        Assert.False(_appSettings.Security.RequireSignIn);
    }

    [Fact]
    public async Task RemovePassword_WrongPasswordKeepsSecret()
    {
        StoredHash("hash");
        _hasher.Verify("hash", "nope").Returns(PasswordVerification.Failed);

        var result = await Service().RemovePasswordAsync("nope");

        Assert.True(result.IsFailure);
        await _secrets.DidNotReceiveWithAnyArgs().DeleteAsync(default!, default);
    }

    [Fact]
    public async Task HasPassword_ReflectsSecretStore()
    {
        StoredHash("hash");
        Assert.True(await Service().HasPasswordAsync());

        StoredHash(string.Empty);
        Assert.False(await Service().HasPasswordAsync());
    }
}

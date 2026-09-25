using MechanicAI.Domain.Entities;
using MechanicAI.Domain.Enums;

namespace MechanicAI.Server.Contracts;

public sealed record LoginRequest(string Email, string Password);

public sealed record RefreshRequest(string RefreshToken);

public sealed record LogoutRequest(string? RefreshToken);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record TokenResponse(
    string AccessToken,
    DateTime AccessTokenExpiresUtc,
    string RefreshToken,
    DateTime RefreshTokenExpiresUtc,
    UserDto User)
{
    public string TokenType => "Bearer";
}

public sealed record UserDto(Guid Id, string Email, string DisplayName, UserRole Role, bool IsActive, DateTime? LastLoginUtc, DateTime? LockoutEndUtc)
{
    public static UserDto From(User u) => new(u.Id, u.Email, u.DisplayName, u.Role, u.IsActive, u.LastLoginUtc, u.LockoutEndUtc);
}

public sealed record CreateUserRequest(string Email, string DisplayName, string Password, UserRole Role);

public sealed record UpdateUserRequest(string? DisplayName, UserRole? Role, bool? IsActive);

public sealed record ResetPasswordRequest(string NewPassword);

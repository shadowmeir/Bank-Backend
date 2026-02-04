using System.Security.Claims;

namespace Bank.Application.Abstractions;

public interface IJwtTokenService
{
    string CreateAccessToken(string userId, string? email, string? userName, IEnumerable<Claim>? extraClaims = null);
}
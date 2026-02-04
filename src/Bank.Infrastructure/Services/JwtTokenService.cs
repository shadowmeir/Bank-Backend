using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Bank.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace Bank.Infrastructure.Services;

public sealed class JwtTokenService : IJwtTokenService
{
    private readonly IConfiguration _cfg;

    public JwtTokenService(IConfiguration cfg) => _cfg = cfg;

    public string CreateAccessToken(string userId, string? email, string? userName, IEnumerable<Claim>? extraClaims = null)
    {
        var issuer = _cfg["Jwt:Issuer"] ?? "Bank.Api";
        var audience = _cfg["Jwt:Audience"] ?? "Bank.Client";
        var key = _cfg["Jwt:Key"] ?? throw new InvalidOperationException("Missing Jwt:Key in configuration.");
        var minutes = int.TryParse(_cfg["Jwt:AccessTokenMinutes"], out var m) ? m : 15;

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId),
            new(ClaimTypes.NameIdentifier, userId),
        };

        if (!string.IsNullOrWhiteSpace(email))
            claims.Add(new(JwtRegisteredClaimNames.Email, email));

        if (!string.IsNullOrWhiteSpace(userName))
            claims.Add(new(ClaimTypes.Name, userName));

        if (extraClaims is not null)
            claims.AddRange(extraClaims);

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
        var creds = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(minutes),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
using System.Security.Claims;
using Bank.Application.Abstractions;
using Bank.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Bank.Api.Controllers;

[ApiController]
[Route("recipients")]
[Authorize]
public class RecipientsController : ControllerBase
{
    private readonly UserManager<AppUser> _users;
    private readonly IAccountRepository _accounts;

    public RecipientsController(UserManager<AppUser> users, IAccountRepository accounts)
    {
        _users = users;
        _accounts = accounts;
    }

    private string ClientId => User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("Missing NameIdentifier claim");

    public record RecipientAccountDto(Guid Id, string Currency);
    public record RecipientResolveResponse(string Email, string EmailMasked, List<RecipientAccountDto> Accounts);

    [HttpGet("resolve")]
    public async Task<IActionResult> Resolve([FromQuery] string email, CancellationToken ct)
    {
        email = (email ?? "").Trim();
        if (string.IsNullOrWhiteSpace(email))
            return Problem(statusCode: 400, title: "Validation", detail: "email is required");

        // Only resolve recipients who are registered users of THIS bank
        var user = await _users.FindByEmailAsync(email);
        if (user is null)
            return Problem(statusCode: 404, title: "RecipientNotFound", detail: "No bank user with that email.");

        // We keep this minimal: sender already knows the email they typed.
        // Don't leak user.Id or other sensitive profile fields here.
        var recipientAccounts = await _accounts.ListByClientAsync(user.Id, ct);

        var dto = recipientAccounts
            .Select(a => new RecipientAccountDto(a.Id, a.Currency))
            .ToList();

        if (dto.Count == 0)
            return Problem(statusCode: 409, title: "RecipientHasNoAccounts", detail: "Recipient exists but has no accounts.");

        return Ok(new RecipientResolveResponse(email, MaskEmail(email), dto));
    }

    private static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 1) return "***";
        var name = email[..at];
        var domain = email[(at + 1)..];
        var maskedName = name.Length <= 2 ? $"{name[0]}*" : $"{name[0]}***{name[^1]}";
        return $"{maskedName}@{domain}";
    }
}
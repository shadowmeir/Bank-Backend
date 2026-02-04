using System.Security.Claims;
using Bank.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Bank.Api.Controllers;

[Authorize]
[ApiController]
[Route("users")]
public class UsersController : ControllerBase
{
    private readonly UserManager<AppUser> _users;

    public UsersController(UserManager<AppUser> users)
    {
        _users = users;
    }

    public record UserProfileResponse(
        string Email,
        string? FirstName,
        string? LastName,
        string? PhoneNumber,
        string? Address
    );

    public record UpdateUserProfileRequest(
        string? FirstName,
        string? LastName,
        string? PhoneNumber,
        string? Address
    );

    private string? GetUserId()
    {
        // Works whether your JWT uses NameIdentifier or "sub"
        return User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub");
    }

    [HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var user = await _users.FindByIdAsync(userId);
        if (user is null) return Unauthorized();

        return Ok(new UserProfileResponse(
            user.Email ?? user.UserName ?? "",
            user.FirstName,
            user.LastName,
            user.PhoneNumber,
            user.Address
        ));
    }

    [HttpPut("me")]
    public async Task<IActionResult> UpdateMe([FromBody] UpdateUserProfileRequest req)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var user = await _users.FindByIdAsync(userId);
        if (user is null) return Unauthorized();

        user.FirstName = string.IsNullOrWhiteSpace(req.FirstName) ? null : req.FirstName.Trim();
        user.LastName  = string.IsNullOrWhiteSpace(req.LastName)  ? null : req.LastName.Trim();
        user.PhoneNumber = string.IsNullOrWhiteSpace(req.PhoneNumber) ? null : req.PhoneNumber.Trim();
        user.Address = string.IsNullOrWhiteSpace(req.Address) ? null : req.Address.Trim();

        var result = await _users.UpdateAsync(user);
        if (!result.Succeeded)
        {
            var msg = string.Join("; ", result.Errors.Select(e => $"{e.Code}: {e.Description}"));
            return Problem(statusCode: 400, title: "IdentityError", detail: msg);
        }

        return Ok(new UserProfileResponse(
            user.Email ?? user.UserName ?? "",
            user.FirstName,
            user.LastName,
            user.PhoneNumber,
            user.Address
        ));
    }
}
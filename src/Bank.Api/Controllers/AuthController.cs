using System.Text;
using Bank.Application.Abstractions;
using Bank.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace Bank.Api.Controllers;

[ApiController]
[Route("auth")]
public class AuthController : ControllerBase
{
    private readonly UserManager<AppUser> _users;
    private readonly SignInManager<AppUser> _signIn;
    private readonly IJwtTokenService _jwt;
    private readonly IEmailSender _email;
    private readonly IConfiguration _config;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        UserManager<AppUser> users,
        SignInManager<AppUser> signIn,
        IJwtTokenService jwt,
        IEmailSender email,
        IConfiguration config,
        ILogger<AuthController> logger)
    {
        _users = users;
        _signIn = signIn;
        _jwt = jwt;
        _email = email;
        _config = config;
        _logger = logger;
    }

    public record RegisterRequest(
        string Email,
        string Password,
        string? FirstName,
        string? LastName,
        string? PhoneNumber
    );

    public record LoginRequest(string Email, string Password);

    public record AuthResponse(string AccessToken);

    public record ResendConfirmationRequest(string Email);

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
            return Problem(statusCode: 400, title: "Validation", detail: "Email and Password are required.");

        var email = req.Email.Trim().ToLowerInvariant();

        var existing = await _users.FindByEmailAsync(email);
        if (existing is not null)
            return Problem(statusCode: 409, title: "Conflict", detail: "User already exists.");

        var user = new AppUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = false,
            FirstName = string.IsNullOrWhiteSpace(req.FirstName) ? null : req.FirstName.Trim(),
            LastName = string.IsNullOrWhiteSpace(req.LastName) ? null : req.LastName.Trim(),
            PhoneNumber = string.IsNullOrWhiteSpace(req.PhoneNumber) ? null : req.PhoneNumber.Trim()
        };

        var result = await _users.CreateAsync(user, req.Password);
        if (!result.Succeeded)
        {
            var msg = string.Join("; ", result.Errors.Select(e => $"{e.Code}: {e.Description}"));
            return Problem(statusCode: 400, title: "IdentityError", detail: msg);
        }

        // Send email confirmation (don’t block the user with opaque 500s)
        try
        {
            await SendConfirmationEmailAsync(user);
            _logger.LogInformation("Sent confirmation email to {Email}", user.Email);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed sending confirmation email to {Email}", user.Email);

            // User already exists now; return an actionable error
            var detail = IsDev() ? ex.Message : "Failed to send verification email. Please try again.";
            return Problem(statusCode: 500, title: "EmailSendFailed", detail: detail);
        }

        // You can choose NOT to issue a token until verified, but keeping your current behavior:
        var token = _jwt.CreateAccessToken(user.Id, user.Email, user.UserName);
        return Ok(new AuthResponse(token));
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
            return Problem(statusCode: 400, title: "Validation", detail: "Email and Password are required.");

        var email = req.Email.Trim().ToLowerInvariant();

        var user = await _users.FindByEmailAsync(email);
        if (user is null)
            return Problem(statusCode: 401, title: "Unauthorized", detail: "Invalid credentials.");

        if (!user.EmailConfirmed)
            return Problem(statusCode: 403, title: "EmailNotConfirmed", detail: "Please confirm your email before logging in.");

        var ok = await _signIn.CheckPasswordSignInAsync(user, req.Password, lockoutOnFailure: true);
        if (!ok.Succeeded)
            return Problem(statusCode: 401, title: "Unauthorized", detail: "Invalid credentials.");

        var token = _jwt.CreateAccessToken(user.Id, user.Email, user.UserName);
        return Ok(new AuthResponse(token));
    }

    [HttpGet("confirm")]
    public async Task<IActionResult> ConfirmEmail([FromQuery] string userId, [FromQuery] string token)
    {
        var user = await _users.FindByIdAsync(userId);
        if (user is null)
            return Redirect($"{GetFrontendBaseUrl()}/login?verified=0&reason=nouser");

        try
        {
            var decodedBytes = WebEncoders.Base64UrlDecode(token);
            var rawToken = Encoding.UTF8.GetString(decodedBytes);

            var result = await _users.ConfirmEmailAsync(user, rawToken);
            if (result.Succeeded)
                return Redirect($"{GetFrontendBaseUrl()}/login?verified=1");

            return Redirect($"{GetFrontendBaseUrl()}/login?verified=0&reason=badtoken");
        }
        catch
        {
            return Redirect($"{GetFrontendBaseUrl()}/login?verified=0&reason=badtoken");
        }
    }

    [HttpPost("resend-confirmation")]
    public async Task<IActionResult> ResendConfirmation([FromBody] ResendConfirmationRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Email))
            return Ok(new { ok = true });

        var email = req.Email.Trim().ToLowerInvariant();
        var user = await _users.FindByEmailAsync(email);

        // Don’t reveal whether the user exists
        if (user is null || user.EmailConfirmed)
            return Ok(new { ok = true });

        try
        {
            await SendConfirmationEmailAsync(user);
            _logger.LogInformation("Re-sent confirmation email to {Email}", user.Email);
            return Ok(new { ok = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed resending confirmation email to {Email}", user.Email);

            var detail = IsDev() ? ex.Message : "Failed to send verification email. Please try again.";
            return Problem(statusCode: 500, title: "EmailSendFailed", detail: detail);
        }
    }

    private async Task SendConfirmationEmailAsync(AppUser user)
    {
        // Generate token -> encode URL-safe
        var token = await _users.GenerateEmailConfirmationTokenAsync(user);
        var encodedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));

        var confirmUrl = QueryHelpers.AddQueryString(
            $"{GetApiBaseUrl()}/auth/confirm",
            new Dictionary<string, string?>
            {
                ["userId"] = user.Id,
                ["token"] = encodedToken
            }!);

        var subject = "Confirm your email";

        // Keep it simple; Brevo accepts HTML just fine
        var html = $@"
<div style='font-family:Arial,sans-serif;line-height:1.6'>
  <h2>Confirm your email</h2>
  <p>Click to verify your email address:</p>
  <p><a href='{confirmUrl}'>Verify email</a></p>
  <p style='color:#888'>If you didn't sign up, ignore this message.</p>
</div>";

        await _email.SendAsync(user.Email!, subject, html);
    }

    private string GetApiBaseUrl()
    {
        // Prefer explicit config for production (reverse proxies)
        var cfg = _config["Api:PublicBaseUrl"];
        if (!string.IsNullOrWhiteSpace(cfg)) return cfg.TrimEnd('/');

        // Dev fallback
        return $"{Request.Scheme}://{Request.Host}";
    }

    private string GetFrontendBaseUrl()
    {
        var cfg = _config["Frontend:BaseUrl"];
        if (!string.IsNullOrWhiteSpace(cfg)) return cfg.TrimEnd('/');

        return "http://localhost:5173";
    }

    private static bool IsDev()
        => (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production")
            .Equals("Development", StringComparison.OrdinalIgnoreCase);
}
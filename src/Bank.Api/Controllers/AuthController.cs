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

    // Frontend expects this shape when verification is required
    public record RegisterResponse(bool RequiresEmailConfirmation, string Email);

    public record ResendConfirmationRequest(string Email);

    public record ForgotPasswordRequest(string Email);

    public record ResetPasswordRequest(string UserId, string Token, string NewPassword);

    // -------------------------
    // REGISTER: Pending + send email + NO JWT
    // -------------------------
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
            Status = ClientStatus.Pending,

            FirstName = string.IsNullOrWhiteSpace(req.FirstName) ? null : req.FirstName.Trim(),
            LastName  = string.IsNullOrWhiteSpace(req.LastName)  ? null : req.LastName.Trim(),
            PhoneNumber = string.IsNullOrWhiteSpace(req.PhoneNumber) ? null : req.PhoneNumber.Trim()
        };

        var result = await _users.CreateAsync(user, req.Password);
        if (!result.Succeeded)
        {
            var msg = string.Join("; ", result.Errors.Select(e => $"{e.Code}: {e.Description}"));
            return Problem(statusCode: 400, title: "IdentityError", detail: msg);
        }

        try
        {
            await SendConfirmationEmailAsync(user);
            _logger.LogInformation("Sent confirmation email to {Email}", user.Email);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed sending confirmation email to {Email}", user.Email);
            var detail = IsDev() ? ex.Message : "Failed to send verification email. Please try again.";
            return Problem(statusCode: 500, title: "EmailSendFailed", detail: detail);
        }

        // IMPORTANT: do not issue JWT here
        return Ok(new RegisterResponse(true, user.Email!));
    }

    // -------------------------
    // LOGIN: only Active + EmailConfirmed
    // -------------------------
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
            return Problem(statusCode: 400, title: "Validation", detail: "Email and Password are required.");

        var email = req.Email.Trim().ToLowerInvariant();

        var user = await _users.FindByEmailAsync(email);
        if (user is null)
            return Problem(statusCode: 401, title: "Unauthorized", detail: "Invalid credentials.");

        if (user.Status == ClientStatus.Blocked)
            return Problem(statusCode: 403, title: "AccountBlocked", detail: "Your account is blocked. Please contact support.");

        // Self-heal: confirmed email but still Pending => make Active
        if (user.EmailConfirmed && user.Status == ClientStatus.Pending)
        {
            user.Status = ClientStatus.Active;
            await _users.UpdateAsync(user);
        }

        if (!user.EmailConfirmed || user.Status != ClientStatus.Active)
            return Problem(statusCode: 403, title: "EmailNotConfirmed", detail: "Please confirm your email before logging in.");

        var ok = await _signIn.CheckPasswordSignInAsync(user, req.Password, lockoutOnFailure: true);
        if (!ok.Succeeded)
            return Problem(statusCode: 401, title: "Unauthorized", detail: "Invalid credentials.");

        var token = _jwt.CreateAccessToken(user.Id, user.Email, user.UserName);
        return Ok(new AuthResponse(token));
    }

    // -------------------------
    // CONFIRM EMAIL (SPA): frontend calls this
    // GET /auth/confirm-email?userId=...&token=...
    // -------------------------
    [HttpGet("confirm-email")]
    public async Task<IActionResult> ConfirmEmailSpa([FromQuery] string userId, [FromQuery] string token)
    {
        var user = await _users.FindByIdAsync(userId);
        if (user is null)
            return Problem(statusCode: 400, title: "InvalidLink", detail: "Invalid verification link.");

        try
        {
            // If already confirmed, just ensure status is Active
            if (user.EmailConfirmed)
            {
                if (user.Status != ClientStatus.Active)
                {
                    user.Status = ClientStatus.Active;
                    await _users.UpdateAsync(user);
                }

                return Ok(new { ok = true });
            }

            var decodedBytes = WebEncoders.Base64UrlDecode(token);
            var rawToken = Encoding.UTF8.GetString(decodedBytes);

            var result = await _users.ConfirmEmailAsync(user, rawToken);
            if (!result.Succeeded)
                return Problem(statusCode: 400, title: "BadToken", detail: "Verification token is invalid or expired.");

            user.Status = ClientStatus.Active;
            await _users.UpdateAsync(user);

            return Ok(new { ok = true });
        }
        catch
        {
            return Problem(statusCode: 400, title: "BadToken", detail: "Verification token is invalid or expired.");
        }
    }

    // -------------------------
    // LEGACY CONFIRM (old emails): redirects to frontend login
    // GET /auth/confirm?userId=...&token=...
    // -------------------------
    [HttpGet("confirm")]
    public async Task<IActionResult> ConfirmEmailLegacy([FromQuery] string userId, [FromQuery] string token)
    {
        var user = await _users.FindByIdAsync(userId);
        if (user is null)
            return Redirect($"{GetFrontendBaseUrl()}/login?verified=0&reason=nouser");

        try
        {
            var decodedBytes = WebEncoders.Base64UrlDecode(token);
            var rawToken = Encoding.UTF8.GetString(decodedBytes);

            var result = await _users.ConfirmEmailAsync(user, rawToken);
            if (!result.Succeeded)
                return Redirect($"{GetFrontendBaseUrl()}/login?verified=0&reason=badtoken");

            user.Status = ClientStatus.Active;
            await _users.UpdateAsync(user);

            return Redirect($"{GetFrontendBaseUrl()}/login?verified=1");
        }
        catch
        {
            return Redirect($"{GetFrontendBaseUrl()}/login?verified=0&reason=badtoken");
        }
    }

    // -------------------------
    // RESEND CONFIRMATION
    // -------------------------
    [HttpPost("resend-confirmation")]
    public async Task<IActionResult> ResendConfirmation([FromBody] ResendConfirmationRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Email))
            return Ok(new { ok = true });

        var email = req.Email.Trim().ToLowerInvariant();
        var user = await _users.FindByEmailAsync(email);

        // Don’t reveal whether the user exists
        if (user is null || user.EmailConfirmed || user.Status == ClientStatus.Blocked)
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

    // -------------------------
    // FORGOT PASSWORD
    // -------------------------
    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest req)
    {
        // Always OK to prevent account enumeration
        if (string.IsNullOrWhiteSpace(req.Email))
            return Ok(new { ok = true });

        var email = req.Email.Trim().ToLowerInvariant();
        var user = await _users.FindByEmailAsync(email);

        if (user is null || !user.EmailConfirmed || user.Status == ClientStatus.Blocked)
            return Ok(new { ok = true });

        try
        {
            var token = await _users.GeneratePasswordResetTokenAsync(user);
            var encoded = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));

            var resetUrl = QueryHelpers.AddQueryString(
                $"{GetFrontendBaseUrl()}/reset-password",
                new Dictionary<string, string?>
                {
                    ["userId"] = user.Id,
                    ["token"] = encoded
                }!);

            var subject = "Reset your password";
            var html = $@"
<div style='font-family:Arial,sans-serif;line-height:1.6'>
  <h2>Password reset</h2>
  <p>Click to reset your password:</p>
  <p><a href='{resetUrl}'>Reset password</a></p>
  <p style='color:#888'>If you didn’t request this, ignore this email.</p>
</div>";

            await _email.SendAsync(user.Email!, subject, html);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed sending reset email to {Email}", user.Email);
            // Still return OK
        }

        return Ok(new { ok = true });
    }

    // -------------------------
    // RESET PASSWORD
    // -------------------------
    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.UserId) ||
            string.IsNullOrWhiteSpace(req.Token) ||
            string.IsNullOrWhiteSpace(req.NewPassword))
        {
            return Problem(statusCode: 400, title: "Validation", detail: "Missing reset parameters.");
        }

        var user = await _users.FindByIdAsync(req.UserId);
        if (user is null)
            return Problem(statusCode: 400, title: "InvalidLink", detail: "Invalid reset link.");

        try
        {
            var decodedBytes = WebEncoders.Base64UrlDecode(req.Token);
            var rawToken = Encoding.UTF8.GetString(decodedBytes);

            var result = await _users.ResetPasswordAsync(user, rawToken, req.NewPassword);
            if (!result.Succeeded)
            {
                var msg = string.Join("; ", result.Errors.Select(e => $"{e.Code}: {e.Description}"));
                return Problem(statusCode: 400, title: "ResetFailed", detail: msg);
            }

            return Ok(new { ok = true });
        }
        catch
        {
            return Problem(statusCode: 400, title: "BadToken", detail: "Reset token is invalid or expired.");
        }
    }

    // -------------------------
    // Email helper: send FRONTEND verify link
    // -------------------------
    private async Task SendConfirmationEmailAsync(AppUser user)
    {
        var token = await _users.GenerateEmailConfirmationTokenAsync(user);
        var encodedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));

        var verifyUrl = QueryHelpers.AddQueryString(
            $"{GetFrontendBaseUrl()}/verify-email",
            new Dictionary<string, string?>
            {
                ["userId"] = user.Id,
                ["token"] = encodedToken
            }!);

        var subject = "Confirm your email";
        var html = $@"
<div style='font-family:Arial,sans-serif;line-height:1.6'>
  <h2>Confirm your email</h2>
  <p>Click to verify your email address:</p>
  <p><a href='{verifyUrl}'>Verify email</a></p>
  <p style='color:#888'>If you didn't sign up, ignore this message.</p>
</div>";

        await _email.SendAsync(user.Email!, subject, html);
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
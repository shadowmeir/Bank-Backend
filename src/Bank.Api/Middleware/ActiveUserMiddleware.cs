using System.Security.Claims;
using Bank.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Bank.Api.Middleware;

public sealed class ActiveUserMiddleware : IMiddleware
{
    private readonly UserManager<AppUser> _users;

    public ActiveUserMiddleware(UserManager<AppUser> users)
    {
        _users = users;
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        // Only enforce when a valid authenticated principal exists
        var userId =
            context.User.FindFirstValue(ClaimTypes.NameIdentifier) ??
            context.User.FindFirstValue("sub");

        if (string.IsNullOrWhiteSpace(userId))
        {
            await next(context);
            return;
        }

        var user = await _users.FindByIdAsync(userId);
        if (user is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = StatusCodes.Status401Unauthorized,
                Title = "Unauthorized",
                Detail = "User no longer exists."
            });
            return;
        }

        // Self-heal: confirmed email but still Pending => make Active
        if (user.EmailConfirmed && user.Status == ClientStatus.Pending)
        {
            user.Status = ClientStatus.Active;
            await _users.UpdateAsync(user);
        }

        if (user.Status != ClientStatus.Active)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;

            var title = user.Status == ClientStatus.Blocked ? "AccountBlocked" : "AccountPending";
            var detail = user.Status == ClientStatus.Blocked
                ? "Your account is blocked. Please contact support."
                : "Your account is not active yet. Please verify your email.";

            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = title,
                Detail = detail
            });
            return;
        }

        await next(context);
    }
}
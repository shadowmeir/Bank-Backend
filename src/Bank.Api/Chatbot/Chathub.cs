using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Bank.Api.Chatbot;

// [Authorize] forces a valid JWT for any hub connection/calls.
[Authorize]
public sealed class ChatHub : Hub
{
    private readonly IChatbotRouter _router;

    public ChatHub(IChatbotRouter router) => _router = router;

    // Frontend calls this: connection.invoke("SendToBot", message, optionalAccountId)
    public async Task SendToBot(string message, string? accountId = null)
    {
        // We identify the caller via the JWT claim.
        // You already put ClaimTypes.NameIdentifier into your JWT (see JwtTokenService).
        var clientId =
            Context.User?.FindFirstValue(ClaimTypes.NameIdentifier) ??
            Context.UserIdentifier ?? // fallback (SignalR user id provider)
            "(anonymous)";

        // SignalR can cancel pending tasks when client disconnects.
        var ct = Context.ConnectionAborted;

        // Basic input hygiene (prevents someone from sending huge payloads)
        message ??= "";
        if (message.Length > 2000)
        {
            await Clients.Caller.SendAsync(
                "ReceiveBotMessage",
                new ChatBotMessage(BotMessageKind.Error, BotIntent.Unknown, "Message too long (max 2000 chars).", DateTimeOffset.UtcNow),
                ct);
            return;
        }

        // Route the message => compute reply (may query DB).
        var reply = await _router.RouteAsync(clientId, message, accountId, ct);

        // Return reply only to the caller.
        await Clients.Caller.SendAsync("ReceiveBotMessage", reply, ct);
    }
}
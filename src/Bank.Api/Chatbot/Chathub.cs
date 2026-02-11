using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Bank.Api.Chatbot;

// [Authorize] forces a valid JWT for any hub connection/calls.
[Authorize]
public sealed class ChatHub : Hub
{
    private readonly IChatbotRouter _router;
    private readonly ILogger<ChatHub> _logger;

    public ChatHub(IChatbotRouter router, ILogger<ChatHub> logger)
    {
        _router = router;
        _logger = logger;
    }

    // Frontend calls this: connection.invoke("SendToBot", message, optionalAccountId)
    public async Task SendToBot(string message, string? accountId = null)
    {
        // Identify the caller via JWT claim (or SignalR fallback)
        var clientId =
            Context.User?.FindFirstValue(ClaimTypes.NameIdentifier) ??
            Context.UserIdentifier ??
            "(anonymous)";

        var ct = Context.ConnectionAborted;

        // Basic input hygiene
        message ??= "";
        if (message.Length > 2000)
        {
            await SafeSendAsync(
                new ChatBotMessage(
                    BotMessageKind.Error,
                    BotIntent.Unknown,
                    "Message too long (max 2000 chars).",
                    DateTimeOffset.UtcNow),
                ct);
            return;
        }

        _logger.LogInformation(
            "ChatHub.SendToBot clientId={ClientId} connId={ConnectionId} accountId={AccountId} msgLen={Len} msg='{Msg}'",
            clientId,
            Context.ConnectionId,
            accountId ?? "(null)",
            message.Length,
            message.Length <= 200 ? message : message[..200] + "…");

        try
        {
            // Route the message => compute reply (may query DB)
            var reply = await _router.RouteAsync(clientId, message, accountId, ct);

            // Return reply only to the caller
            await SafeSendAsync(reply, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client disconnected or cancelled request — not an error.
            _logger.LogInformation(
                "ChatHub.SendToBot cancelled clientId={ClientId} connId={ConnectionId}",
                clientId,
                Context.ConnectionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "ChatHub.SendToBot failed clientId={ClientId} connId={ConnectionId} accountId={AccountId}",
                clientId,
                Context.ConnectionId,
                accountId ?? "(null)");

            // Never let exceptions kill the hub connection: return an error message to the caller.
            await SafeSendAsync(
                new ChatBotMessage(
                    BotMessageKind.Error,
                    BotIntent.Unknown,
                    "Server error while handling your request. Please try again. (Check backend logs for details.)",
                    DateTimeOffset.UtcNow),
                ct);
        }
    }

    private Task SafeSendAsync(ChatBotMessage msg, CancellationToken ct)
    {
        // In case the client disconnected, SendAsync may throw; we prefer not to crash the hub.
        try
        {
            return Clients.Caller.SendAsync("ReceiveBotMessage", msg, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ChatHub.SafeSendAsync failed connId={ConnectionId}", Context.ConnectionId);
            return Task.CompletedTask;
        }
    }
}
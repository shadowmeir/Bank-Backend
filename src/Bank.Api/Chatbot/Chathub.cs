using System.Security.Claims;
using System.Text.Json;
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

    // Frontend can call either:
    // 1) connection.invoke("SendToBot", "balance", optionalAccountId)
    // 2) connection.invoke("SendToBot", { text: "balance", accountId: "..." })
    public async Task SendToBot(object? message, string? accountId = null)
    {
        var normalized = NormalizeIncoming(message, accountId);
        message = normalized.Message;
        accountId = normalized.AccountId;

        // Identify the caller via JWT claim (or SignalR fallback)
        var clientId =
            Context.User?.FindFirstValue(ClaimTypes.NameIdentifier) ??
            Context.UserIdentifier ??
            "(anonymous)";

        var ct = Context.ConnectionAborted;

        // Basic input hygiene
        var text = message?.ToString() ?? "";
        if (text.Length > 2000)
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
            text.Length,
            text.Length <= 200 ? text : text[..200] + "…");

        try
        {
            // Route the message => compute reply (may query DB)
            var reply = await _router.RouteAsync(clientId, text, accountId, ct);

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
            // Keep current frontend contract: ReceiveBotMessage(string).
            // If/when UI needs metadata, it can subscribe to ReceiveBotEnvelope.
            return Task.WhenAll(
                Clients.Caller.SendAsync("ReceiveBotMessage", msg.Text, ct),
                Clients.Caller.SendAsync("ReceiveBotEnvelope", msg, ct));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ChatHub.SafeSendAsync failed connId={ConnectionId}", Context.ConnectionId);
            return Task.CompletedTask;
        }
    }

    private static (string Message, string? AccountId) NormalizeIncoming(object? incoming, string? fallbackAccountId)
    {
        if (incoming is null)
            return ("", fallbackAccountId);

        if (incoming is string s)
            return (s, fallbackAccountId);

        if (incoming is ChatClientMessage m)
            return (m.Text ?? "", string.IsNullOrWhiteSpace(m.AccountId) ? fallbackAccountId : m.AccountId);

        if (incoming is JsonElement json)
        {
            if (json.ValueKind == JsonValueKind.String)
                return (json.GetString() ?? "", fallbackAccountId);

            if (json.ValueKind == JsonValueKind.Object)
            {
                var text = TryGetString(json, "text") ?? TryGetString(json, "Text") ?? "";
                var accountId = TryGetString(json, "accountId") ?? TryGetString(json, "AccountId");
                return (text, string.IsNullOrWhiteSpace(accountId) ? fallbackAccountId : accountId);
            }
        }

        return (incoming.ToString() ?? "", fallbackAccountId);
    }

    private static string? TryGetString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;
}

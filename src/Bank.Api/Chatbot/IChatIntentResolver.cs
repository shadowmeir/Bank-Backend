namespace Bank.Api.Chatbot;

public interface IChatIntentResolver
{
    Task<string?> ResolveSlashCommandAsync(string rawText, string? fallbackAccountId, CancellationToken ct);
}

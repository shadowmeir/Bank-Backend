namespace Bank.Api.Chatbot;

public sealed class NoopChatIntentResolver : IChatIntentResolver
{
    public Task<string?> ResolveSlashCommandAsync(string rawText, string? fallbackAccountId, CancellationToken ct)
        => Task.FromResult<string?>(null);
}

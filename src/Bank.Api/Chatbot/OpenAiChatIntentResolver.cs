using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bank.Api.Chatbot;

public sealed class OpenAiChatIntentResolver : IChatIntentResolver
{
    private const string SystemPrompt = """
        You map banking chatbot free text to one canonical slash command.
        Allowed commands:
        - /help
        - /accounts
        - /balance
        - /balance <accountId-guid>
        - /recent
        - /recent <limit-int>
        - /recent <accountId-guid>
        - /recent <accountId-guid> <limit-int>
        If the message does not match these commands, return "unknown".
        Return only JSON: {"command":"<allowed command or unknown>"}.
        Do not add any extra fields.
        """;

    private readonly HttpClient _httpClient;
    private readonly ChatbotLlmOptions _options;
    private readonly ILogger<OpenAiChatIntentResolver> _logger;

    public OpenAiChatIntentResolver(
        HttpClient httpClient,
        IOptions<ChatbotLlmOptions> options,
        ILogger<OpenAiChatIntentResolver> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string?> ResolveSlashCommandAsync(string rawText, string? fallbackAccountId, CancellationToken ct)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(rawText))
            return null;

        if (string.IsNullOrWhiteSpace(_options.ApiKey)
            || string.IsNullOrWhiteSpace(_options.Endpoint)
            || string.IsNullOrWhiteSpace(_options.Model))
            return null;

        var timeoutSeconds = _options.TimeoutSeconds <= 0 ? 10 : _options.TimeoutSeconds;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        using var req = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", NormalizeApiKey(_options.ApiKey));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Content = JsonContent.Create(new
        {
            model = _options.Model,
            temperature = 0,
            response_format = new { type = "json_object" },
            messages = new[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = BuildUserPrompt(rawText, fallbackAccountId) }
            }
        });

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("Chatbot LLM intent resolution timed out after {TimeoutSeconds}s", timeoutSeconds);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chatbot LLM intent resolution request failed.");
            return null;
        }

        using (response)
        {
            var payload = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Chatbot LLM returned non-success status {StatusCode}. Body={Body}",
                    (int)response.StatusCode,
                    Truncate(payload, 500));
                return null;
            }

            string? assistantContent;
            try
            {
                using var doc = JsonDocument.Parse(payload);
                assistantContent = ExtractAssistantContent(doc.RootElement);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Chatbot LLM response was not valid JSON.");
                return null;
            }

            if (string.IsNullOrWhiteSpace(assistantContent))
                return null;

            return TryParseModelCommand(assistantContent, out var command)
                ? command
                : null;
        }
    }

    private static string BuildUserPrompt(string rawText, string? fallbackAccountId)
    {
        var fallback = string.IsNullOrWhiteSpace(fallbackAccountId) ? "null" : fallbackAccountId;
        return $"""
            user_text: {rawText}
            fallback_account_id: {fallback}
            Use fallback_account_id only if user refers to "this account" and fallback_account_id is a valid GUID.
            """;
    }

    private static string NormalizeApiKey(string apiKey)
    {
        var trimmed = apiKey.Trim();
        return trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? trimmed["Bearer ".Length..].Trim()
            : trimmed;
    }

    private static string? ExtractAssistantContent(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
            return null;

        var choice = choices[0];
        if (!choice.TryGetProperty("message", out var message))
            return null;

        if (!message.TryGetProperty("content", out var content))
            return null;

        return content.ValueKind switch
        {
            JsonValueKind.String => content.GetString(),
            JsonValueKind.Array => string.Join(
                "",
                content.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.Object
                        && x.TryGetProperty("text", out var text)
                        && text.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetProperty("text").GetString())
                    .Where(x => !string.IsNullOrWhiteSpace(x))),
            _ => null
        };
    }

    private static bool TryParseModelCommand(string assistantContent, out string? command)
    {
        command = null;
        string? candidate = null;

        try
        {
            using var doc = JsonDocument.Parse(assistantContent);
            candidate = ExtractCandidateCommand(doc.RootElement);
        }
        catch (JsonException)
        {
            candidate = assistantContent;
        }

        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        candidate = candidate.Trim().Trim('"', '\'', '`');
        if (candidate.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            return false;

        return TryNormalizeCommand(candidate, out command);
    }

    private static string? ExtractCandidateCommand(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.String)
            return root.GetString();

        if (root.ValueKind != JsonValueKind.Object)
            return null;

        if (root.TryGetProperty("command", out var cmd) && cmd.ValueKind == JsonValueKind.String)
            return cmd.GetString();

        if (!root.TryGetProperty("intent", out var intentNode) || intentNode.ValueKind != JsonValueKind.String)
            return null;

        var intent = intentNode.GetString()?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(intent))
            return null;

        var accountId = TryGetString(root, "accountId");
        var limit = TryGetInt(root, "limit");

        return intent switch
        {
            "help" => BotCommands.Help,
            "accounts" => BotCommands.Accounts,
            "balance" when Guid.TryParse(accountId, out var accId) => $"{BotCommands.Balance} {accId}",
            "balance" => BotCommands.Balance,
            "recent" or "recenttransactions" or "transactions"
                when Guid.TryParse(accountId, out var recentAccId) && limit.HasValue
                    => $"{BotCommands.Recent} {recentAccId} {limit.Value}",
            "recent" or "recenttransactions" or "transactions"
                when Guid.TryParse(accountId, out var recentOnlyAccId)
                    => $"{BotCommands.Recent} {recentOnlyAccId}",
            "recent" or "recenttransactions" or "transactions"
                when limit.HasValue
                    => $"{BotCommands.Recent} {limit.Value}",
            "recent" or "recenttransactions" or "transactions"
                    => BotCommands.Recent,
            "unknown" => null,
            _ => null
        };
    }

    private static string? TryGetString(JsonElement obj, string name)
    {
        return obj.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString()
            : null;
    }

    private static int? TryGetInt(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var node))
            return null;

        return node.ValueKind switch
        {
            JsonValueKind.Number when node.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(node.GetString(), out var n) => n,
            _ => null
        };
    }

    private static bool TryNormalizeCommand(string rawCommand, out string? normalized)
    {
        normalized = null;

        var compact = string.Join(
            " ",
            rawCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToLowerInvariant();

        if (!compact.StartsWith('/'))
            return false;

        var parts = compact.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return false;

        var cmd = parts[0];
        if (cmd == BotCommands.Help || cmd == BotCommands.Accounts)
        {
            if (parts.Length != 1)
                return false;

            normalized = cmd;
            return true;
        }

        if (cmd == BotCommands.Balance)
        {
            if (parts.Length == 1)
            {
                normalized = BotCommands.Balance;
                return true;
            }

            if (parts.Length == 2 && Guid.TryParse(parts[1], out var accountId))
            {
                normalized = $"{BotCommands.Balance} {accountId}";
                return true;
            }

            return false;
        }

        if (cmd == BotCommands.Recent)
        {
            if (parts.Length == 1)
            {
                normalized = BotCommands.Recent;
                return true;
            }

            if (parts.Length == 2)
            {
                if (Guid.TryParse(parts[1], out var accountId))
                {
                    normalized = $"{BotCommands.Recent} {accountId}";
                    return true;
                }

                if (int.TryParse(parts[1], out var limit))
                {
                    normalized = $"{BotCommands.Recent} {limit}";
                    return true;
                }

                return false;
            }

            if (parts.Length == 3
                && Guid.TryParse(parts[1], out var accountIdWithLimit)
                && int.TryParse(parts[2], out var limitWithAccount))
            {
                normalized = $"{BotCommands.Recent} {accountIdWithLimit} {limitWithAccount}";
                return true;
            }
        }

        return false;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "...";
}

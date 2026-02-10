using System.Globalization;
using Bank.Application.Abstractions;
using Bank.Application.UseCases;
using Bank.Domain.Entities;
using Bank.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Bank.Api.Chatbot;

// Interface so the Hub depends on an abstraction (easy to replace later with LLM router).
public interface IChatbotRouter
{
    Task<ChatBotMessage> RouteAsync(string clientId, string rawText, string? accountId, CancellationToken ct);
}

// Implementation: deterministic routing (stage 1).
public sealed class ChatbotRouter : IChatbotRouter
{
    private readonly BankDbContext _db;
    private readonly IAccountRepository _accounts;
    private readonly ILedgerRepository _ledger;

    public ChatbotRouter(BankDbContext db, IAccountRepository accounts, ILedgerRepository ledger)
    {
        _db = db;
        _accounts = accounts;
        _ledger = ledger;
    }

    public async Task<ChatBotMessage> RouteAsync(string clientId, string rawText, string? accountId, CancellationToken ct)
    {
        // Normalize once so all comparisons are stable.
        var text = Normalize(rawText);

        // Empty input => guide user.
        if (string.IsNullOrWhiteSpace(text))
            return Bot(BotIntent.Unknown, "Type /help to see what I can do.");

        // 1) Prefer slash-commands (canonical).
        if (text.StartsWith('/'))
            return await RouteSlashAsync(clientId, text, accountId, ct);

        // 2) Minimal synonym mapping (still deterministic, not AI).
        if (text is "help" or "?" or "commands" || text.Contains("what can you do"))
            return Help();

        if (text is "accounts" or "list accounts" || text.Contains("list my accounts"))
            return await AccountsAsync(clientId, ct);

        if (text is "balance" || text.Contains("my balance") || text.Contains("current balance"))
            return await BalanceAsync(clientId, accountId, ct);

        if (text.Contains("recent") || text.Contains("transactions"))
            return await RecentAsync(clientId, accountId, limit: 10, ct);

        // Default fallback.
        return Bot(BotIntent.Unknown, "Sorry — I didn’t understand. Type /help.");
    }

    private async Task<ChatBotMessage> RouteSlashAsync(string clientId, string text, string? fallbackAccountId, CancellationToken ct)
    {
        // Parse: "/cmd arg1 arg2 ..."
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var cmd = parts[0];

        // Handle each command.
        return cmd switch
        {
            BotCommands.Help => Help(),

            BotCommands.Accounts => await AccountsAsync(clientId, ct),

            // /balance [accountId]
            BotCommands.Balance => await BalanceAsync(
                clientId,
                accountId: parts.Length >= 2 ? parts[1] : fallbackAccountId,
                ct),

            // /recent [accountId] [limit] OR /recent [limit]
            BotCommands.Recent => await RecentAsync(
                clientId,
                accountId: ExtractAccountId(parts, fallbackAccountId),
                limit: ExtractLimit(parts, fallback: 10),
                ct),

            _ => Bot(BotIntent.Unknown, $"Unknown command '{cmd}'. Type /help.")
        };
    }

    // -------------------------
    // Commands
    // -------------------------

    private ChatBotMessage Help()
    {
        var lines = new List<string>
        {
            "Here are my commands:",
            ""
        };

        foreach (var c in BotCommands.All)
        {
            lines.Add($"- {c.Command} — {c.Description}");
            lines.Add($"  Examples: {string.Join(" | ", c.Examples)}");
        }

        lines.Add("");
        lines.Add("Tip: Use /accounts to get account IDs.");

        return Bot(BotIntent.Help, string.Join('\n', lines));
    }

    private async Task<ChatBotMessage> AccountsAsync(string clientId, CancellationToken ct)
    {
        // AsNoTracking = faster for reads (we don't edit entities here).
        // Select(...) = only pull columns we actually need.
        var accounts = await _db.Set<Account>()
            .AsNoTracking()
            .Where(a => a.ClientId == clientId)
            .OrderBy(a => a.CreatedAtUtc)
            .Select(a => new
            {
                a.Id,
                a.Currency,
                a.BalanceCached,
                a.Status,
                a.CreatedAtUtc
            })
            .ToListAsync(ct);

        if (accounts.Count == 0)
            return Bot(BotIntent.Accounts, "You have no accounts yet.");

        var lines = new List<string> { "Your accounts:" };

        foreach (var a in accounts)
        {
            lines.Add($"- {a.Id} | {a.Currency} | BalanceCached={a.BalanceCached:0.00} | {a.Status} | Created {a.CreatedAtUtc:u}");
        }

        lines.Add("");
        lines.Add("Tip: /balance <accountId> or /recent <accountId> 10");

        return Bot(BotIntent.Accounts, string.Join('\n', lines));
    }

    private async Task<ChatBotMessage> BalanceAsync(string clientId, string? accountId, CancellationToken ct)
    {
        // Fetch all balances once. This makes "/balance" cheap.
        var accounts = await _db.Set<Account>()
            .AsNoTracking()
            .Where(a => a.ClientId == clientId)
            .Select(a => new { a.Id, a.Currency, a.BalanceCached })
            .ToListAsync(ct);

        if (accounts.Count == 0)
            return Bot(BotIntent.Balance, "You have no accounts yet.");

        // If user asked for one account, show only that.
        if (!string.IsNullOrWhiteSpace(accountId) && Guid.TryParse(accountId, out var accId))
        {
            var a = accounts.FirstOrDefault(x => x.Id == accId);
            if (a is null)
                return Bot(BotIntent.Balance, "That accountId doesn’t belong to you (or doesn’t exist).");

            return Bot(BotIntent.Balance, $"Balance for {a.Id}: {a.BalanceCached.ToString("0.00", CultureInfo.InvariantCulture)} {a.Currency}");
        }

        // Otherwise show a total + per-account.
        // NOTE: currencies could differ; we group by currency and total each.
        var grouped = accounts
            .GroupBy(x => x.Currency)
            .OrderBy(g => g.Key)
            .ToList();

        var lines = new List<string> { "Balances:" };

        foreach (var g in grouped)
        {
            var total = g.Sum(x => x.BalanceCached);
            lines.Add($"- Total ({g.Key}): {total:0.00}");
            foreach (var a in g.OrderBy(x => x.Id))
                lines.Add($"  • {a.Id}: {a.BalanceCached:0.00} {a.Currency}");
        }

        lines.Add("");
        lines.Add("Tip: /balance <accountId>");

        return Bot(BotIntent.Balance, string.Join('\n', lines));
    }

    private async Task<ChatBotMessage> RecentAsync(string clientId, string? accountId, int limit, CancellationToken ct)
    {
        limit = Clamp(limit, 1, 50);

        // If an accountId was provided => single-account query.
        if (!string.IsNullOrWhiteSpace(accountId) && Guid.TryParse(accountId, out var accId))
        {
            // Use application use-case to enforce "not your account" checks consistently.
            var resp = await GetTransactions.Handle(
                new GetTransactions.Request(clientId, accId, limit),
                _accounts,
                _ledger,
                ct);

            if (resp.Items.Count == 0)
                return Bot(BotIntent.RecentTransactions, $"No recent transactions for {accId}.");

            return Bot(BotIntent.RecentTransactions, FormatTxListSingle(accId, resp.Items, limit));
        }

        // Otherwise: ALL accounts.
        // MVP approach:
        // 1) Get account ids once.
        // 2) For each account, fetch recent up to 'limit'.
        // 3) Merge + sort in-memory, then take top 'limit'.
        //
        // This minimizes "chatbot-side logic" while keeping correctness.
        var accountIds = await _db.Set<Account>()
            .AsNoTracking()
            .Where(a => a.ClientId == clientId)
            .OrderBy(a => a.CreatedAtUtc)
            .Select(a => a.Id)
            .ToListAsync(ct);

        if (accountIds.Count == 0)
            return Bot(BotIntent.RecentTransactions, "You have no accounts yet.");

        var merged = new List<(Guid AccountId, GetTransactions.Tx Tx)>();

        foreach (var id in accountIds)
        {
            var resp = await GetTransactions.Handle(
                new GetTransactions.Request(clientId, id, limit),
                _accounts,
                _ledger,
                ct);

            foreach (var tx in resp.Items)
                merged.Add((id, tx));
        }

        if (merged.Count == 0)
            return Bot(BotIntent.RecentTransactions, "No recent transactions across your accounts.");

        var top = merged
            .OrderByDescending(x => x.Tx.CreatedAtUtc)
            .Take(limit)
            .ToList();

        return Bot(BotIntent.RecentTransactions, FormatTxListAll(top, limit));
    }

    // -------------------------
    // Formatting
    // -------------------------

    private static string FormatTxListSingle(Guid accountId, List<GetTransactions.Tx> items, int limit)
    {
        var lines = new List<string> { $"Recent transactions for {accountId} (limit {limit}):" };

        foreach (var t in items.OrderByDescending(x => x.CreatedAtUtc))
        {
            lines.Add($"- {t.CreatedAtUtc:u} | {t.Type} | {t.Amount:0.00}" +
                      (string.IsNullOrWhiteSpace(t.Description) ? "" : $" | {t.Description}"));
        }

        lines.Add("");
        lines.Add("Tip: /recent <accountId> 10");

        return string.Join('\n', lines);
    }

    private static string FormatTxListAll(List<(Guid AccountId, GetTransactions.Tx Tx)> items, int limit)
    {
        var lines = new List<string> { $"Recent transactions across ALL accounts (limit {limit}):" };

        foreach (var x in items)
        {
            var t = x.Tx;
            lines.Add($"- {t.CreatedAtUtc:u} | acct {x.AccountId} | {t.Type} | {t.Amount:0.00}" +
                      (string.IsNullOrWhiteSpace(t.Description) ? "" : $" | {t.Description}"));
        }

        lines.Add("");
        lines.Add("Tip: /accounts then /recent <accountId> 10");

        return string.Join('\n', lines);
    }

    // -------------------------
    // Helpers
    // -------------------------

    private static ChatBotMessage Bot(BotIntent intent, string text)
        => new(BotMessageKind.Bot, intent, text, DateTimeOffset.UtcNow);

    private static string Normalize(string s)
        => (s ?? "").Trim().ToLowerInvariant();

    private static int Clamp(int x, int min, int max)
        => x < min ? min : (x > max ? max : x);

    private static string? ExtractAccountId(string[] parts, string? fallback)
    {
        // /recent <accountId> [limit]
        // /recent [limit]
        // If parts[1] parses as Guid => it's an accountId
        if (parts.Length >= 2 && Guid.TryParse(parts[1], out _))
            return parts[1];

        return fallback;
    }

    private static int ExtractLimit(string[] parts, int fallback)
    {
        // We scan from the end so:
        // "/recent 10" -> 10
        // "/recent <guid> 10" -> 10
        for (var i = parts.Length - 1; i >= 1; i--)
        {
            if (int.TryParse(parts[i], out var n))
                return n;
        }
        return fallback;
    }
}
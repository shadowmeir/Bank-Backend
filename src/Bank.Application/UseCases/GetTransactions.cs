using Bank.Application.Abstractions;
using Bank.Application.Errors;
using Bank.Domain.Entities;

namespace Bank.Application.UseCases;

public static class GetTransactions
{
    public record Request(string ClientId, Guid AccountId, int Limit);

    // ADDED CounterpartyAccountId
    public record Tx(Guid Id, decimal Amount, LedgerEntryType Type, Guid CorrelationId,
        Guid? CounterpartyAccountId, string IdempotencyKey, string? Description, DateTime CreatedAtUtc);

    public record Response(Guid AccountId, List<Tx> Items);

    public static async Task<Response> Handle(Request req, IAccountRepository accounts, ILedgerRepository ledger, CancellationToken ct)
    {
        var account = await accounts.GetByIdAsync(req.AccountId, ct);
        if (account is null) throw new BankException(BankErrorCode.NotFound, "Account not found.");
        if (account.ClientId != req.ClientId) throw new BankException(BankErrorCode.Forbidden, "Not your account.");

        var limit = req.Limit <= 0 ? 20 : Math.Min(req.Limit, 200);

        var items = await ledger.ListRecentAsync(account.Id, limit, ct);

        return new Response(account.Id, items.Select(x => new Tx(x.Id, x.Amount, x.Type,
                x.CorrelationId, x.CounterpartyAccountId,   // NEW
                x.IdempotencyKey, x.Description, x.CreatedAtUtc)).ToList());
    }
}
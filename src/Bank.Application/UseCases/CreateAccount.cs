using Bank.Application.Abstractions;
using Bank.Application.Errors;
using Bank.Domain.Entities;

namespace Bank.Application.UseCases;

public static class CreateAccount
{
    public record Request(string ClientId, string Currency);
    public record Response(Guid AccountId, string Currency, decimal Balance);

    public static async Task<Response> Handle(Request req, IAccountRepository accounts, IBankUnitOfWork uow, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Currency))
            throw new BankException(BankErrorCode.Validation, "Currency is required.");

        var currency = req.Currency.Trim().ToUpperInvariant();

        // Enforce "one account per currency per client" (MVP)
        var existing = await accounts.FindByClientAndCurrencyAsync(req.ClientId, currency, ct);
        if (existing is not null)
            throw new BankException(BankErrorCode.Conflict, $"Account already exists for currency '{currency}'.");

        var account = new Account
        {
            ClientId = req.ClientId,
            Currency = currency,
            BalanceCached = 0m,
            Status = AccountStatus.Active
        };

        accounts.Add(account);
        await uow.SaveChangesAsync(ct);

        return new Response(account.Id, account.Currency, account.BalanceCached);
    }
}
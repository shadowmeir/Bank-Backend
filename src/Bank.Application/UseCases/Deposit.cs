using Bank.Application.Abstractions;
using Bank.Application.Errors;
using Bank.Domain.Entities;

namespace Bank.Application.UseCases;

public static class Deposit
{
    public record Request(string ClientId, Guid AccountId, decimal Amount, string IdempotencyKey, string? Description);
    public record Response(Guid LedgerEntryId, Guid AccountId, decimal NewBalance, Guid CorrelationId);

    public static async Task<Response> Handle(Request req, IAccountRepository accounts, ILedgerRepository ledger, IBankUnitOfWork uow, CancellationToken ct)
    {
        if (req.Amount <= 0m)
            throw new BankException(BankErrorCode.Validation, "Amount must be > 0.");
        if (string.IsNullOrWhiteSpace(req.IdempotencyKey))
            throw new BankException(BankErrorCode.Validation, "Idempotency-Key is required.");

        await using var tx = await uow.BeginTransactionAsync(ct);
        try
        {
            var account = await accounts.GetByIdAsync(req.AccountId, ct);
            if (account is null) throw new BankException(BankErrorCode.NotFound, "Account not found.");
            if (account.ClientId != req.ClientId) throw new BankException(BankErrorCode.Forbidden, "Not your account.");
            if (account.Status != AccountStatus.Active) throw new BankException(BankErrorCode.Validation, "Account is not active.");

            var dup = await ledger.FindByAccountAndIdempotencyKeyAsync(req.AccountId, req.IdempotencyKey, ct);
            if (dup is not null)
                throw new BankException(BankErrorCode.DuplicateRequest, "Duplicate request (already processed).");

            var correlationId = Guid.NewGuid();

            var entry = new LedgerEntry
            {
                AccountId = account.Id,
                Amount = req.Amount,
                Type = LedgerEntryType.Deposit,
                CorrelationId = correlationId,
                IdempotencyKey = req.IdempotencyKey,
                Description = req.Description
            };

            ledger.Add(entry);

            account.BalanceCached += req.Amount;

            await uow.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return new Response(entry.Id, account.Id, account.BalanceCached, correlationId);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }
}
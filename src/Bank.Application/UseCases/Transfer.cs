using Bank.Application.Abstractions;
using Bank.Application.Errors;
using Bank.Domain.Entities;

namespace Bank.Application.UseCases;

public static class Transfer
{
    public sealed record Command(
        string ClientId,
        Guid FromAccountId,
        Guid ToAccountId,
        decimal Amount,
        string IdempotencyKey,
        string? Description);

    public sealed record Result(
        Guid CorrelationId,
        Guid FromAccountId,
        Guid ToAccountId,
        decimal Amount,
        decimal FromBalanceAfter,
        decimal ToBalanceAfter,
        DateTime CreatedAtUtc);

    public static async Task<Result> Handle(
        Command cmd,
        IAccountRepository accounts,
        ILedgerRepository ledger,
        IBankUnitOfWork uow,
        CancellationToken ct)
    {
        if (cmd.Amount <= 0m)
            throw new BankException(BankErrorCode.Validation, "Amount must be > 0.");
        if (cmd.FromAccountId == cmd.ToAccountId)
            throw new BankException(BankErrorCode.Validation, "FromAccountId and ToAccountId must be different.");
        if (string.IsNullOrWhiteSpace(cmd.IdempotencyKey))
            throw new BankException(BankErrorCode.Validation, "Idempotency-Key is required.");

        // Idempotency check (per sender account)
        var dup = await ledger.FindByAccountAndIdempotencyKeyAsync(cmd.FromAccountId, cmd.IdempotencyKey, ct);
        if (dup is not null)
            throw new BankException(BankErrorCode.DuplicateRequest, "Duplicate request (already processed).");

        await using var tx = await uow.BeginTransactionAsync(ct);
        try
        {
            // Load both accounts tracked, deterministic order to reduce deadlocks
            var firstId = cmd.FromAccountId.CompareTo(cmd.ToAccountId) < 0 ? cmd.FromAccountId : cmd.ToAccountId;
            var secondId = firstId == cmd.FromAccountId ? cmd.ToAccountId : cmd.FromAccountId;

            var a1 = await accounts.GetByIdAsync(firstId, ct);
            if (a1 is null) throw new BankException(BankErrorCode.NotFound, "Account not found.");

            var a2 = await accounts.GetByIdAsync(secondId, ct);
            if (a2 is null) throw new BankException(BankErrorCode.NotFound, "Account not found.");

            var from = (cmd.FromAccountId == a1.Id) ? a1 : a2;
            var to = (cmd.ToAccountId == a1.Id) ? a1 : a2;

            // Sender must own the from-account
            if (from.ClientId != cmd.ClientId)
                throw new BankException(BankErrorCode.Forbidden, "Cannot transfer from an account you do not own.");

            if (from.Status != AccountStatus.Active)
                throw new BankException(BankErrorCode.Validation, "From account is not active.");
            if (to.Status != AccountStatus.Active)
                throw new BankException(BankErrorCode.Validation, "To account is not active.");

            // Currency must match (unless you implement FX later)
            if (!string.Equals(from.Currency, to.Currency, StringComparison.OrdinalIgnoreCase))
                throw new BankException(BankErrorCode.Validation, "Currency mismatch between accounts.");

            // Funds check
            if (from.BalanceCached < cmd.Amount)
                throw new BankException(BankErrorCode.InsufficientFunds, "Insufficient funds.");

            var correlationId = Guid.NewGuid();
            var now = DateTime.UtcNow;

            // Update balances (still in memory; will be persisted on SaveChanges)
            from.BalanceCached -= cmd.Amount;
            to.BalanceCached += cmd.Amount;

            // Double-ledger: two legs of the same transfer
            var outEntry = new LedgerEntry
            {
                AccountId = from.Id,
                Amount = -cmd.Amount,
                Type = LedgerEntryType.TransferOut,
                CorrelationId = correlationId,
                CounterpartyAccountId = to.Id,
                IdempotencyKey = cmd.IdempotencyKey,
                Description = cmd.Description,
                CreatedAtUtc = now
            };

            var inEntry = new LedgerEntry
            {
                AccountId = to.Id,
                Amount = +cmd.Amount,
                Type = LedgerEntryType.TransferIn,
                CorrelationId = correlationId,
                CounterpartyAccountId = from.Id,
                IdempotencyKey = cmd.IdempotencyKey,
                Description = cmd.Description,
                CreatedAtUtc = now
            };

            ledger.AddRange(new[] { outEntry, inEntry });

            await uow.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return new Result(
                correlationId,
                from.Id,
                to.Id,
                cmd.Amount,
                from.BalanceCached,
                to.BalanceCached,
                now);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }
}
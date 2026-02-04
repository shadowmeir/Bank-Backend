using Bank.Domain.Entities;

namespace Bank.Application.Abstractions;

public interface ILedgerRepository
{
    Task<LedgerEntry?> FindByAccountAndIdempotencyKeyAsync(Guid accountId, string idempotencyKey, CancellationToken ct);

    void Add(LedgerEntry entry);

    void AddRange(IEnumerable<LedgerEntry> entries);

    Task<List<LedgerEntry>> ListRecentAsync(Guid accountId, int limit, CancellationToken ct);
}
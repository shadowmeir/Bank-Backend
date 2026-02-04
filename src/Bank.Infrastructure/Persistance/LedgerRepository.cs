using Bank.Application.Abstractions;
using Bank.Domain.Entities;
using Bank.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Bank.Infrastructure.Persistence;

public sealed class LedgerRepository : ILedgerRepository
{
    private readonly BankDbContext _db;

    public LedgerRepository(BankDbContext db) => _db = db;

    public Task<LedgerEntry?> FindByAccountAndIdempotencyKeyAsync(Guid accountId, string idempotencyKey, CancellationToken ct)
        => _db.LedgerEntries.AsNoTracking()
            .FirstOrDefaultAsync(x => x.AccountId == accountId && x.IdempotencyKey == idempotencyKey, ct);

    public void Add(LedgerEntry entry) => _db.LedgerEntries.Add(entry);

    public void AddRange(IEnumerable<LedgerEntry> entries) => _db.LedgerEntries.AddRange(entries);

    public Task<List<LedgerEntry>> ListRecentAsync(Guid accountId, int limit, CancellationToken ct)
        => _db.LedgerEntries.AsNoTracking()
            .Where(x => x.AccountId == accountId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(limit)
            .ToListAsync(ct);
}
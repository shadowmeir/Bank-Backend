using Bank.Application.Abstractions;
using Bank.Domain.Entities;
using Bank.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Bank.Infrastructure.Persistence;

public sealed class AccountRepository : IAccountRepository
{
    private readonly BankDbContext _db;

    public AccountRepository(BankDbContext db) => _db = db;

    public Task<Account?> GetByIdAsync(Guid accountId, CancellationToken ct)
        => _db.Accounts.AsTracking().FirstOrDefaultAsync(a => a.Id == accountId, ct);

    public Task<List<Account>> ListByClientAsync(string clientId, CancellationToken ct)
        => _db.Accounts.AsNoTracking().Where(a => a.ClientId == clientId).OrderBy(a => a.CreatedAtUtc).ToListAsync(ct);

    public Task<Account?> FindByClientAndCurrencyAsync(string clientId, string currency, CancellationToken ct)
        => _db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.ClientId == clientId && a.Currency == currency, ct);

    public void Add(Account account) => _db.Accounts.Add(account);
}
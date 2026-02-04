using Bank.Application.Abstractions;
using Bank.Application.Errors;
using Bank.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Bank.Infrastructure.Persistence;

public sealed class EfUnitOfWork : IBankUnitOfWork
{
    private readonly BankDbContext _db;

    public EfUnitOfWork(BankDbContext db) => _db = db;

    public async Task<IBankTransaction> BeginTransactionAsync(CancellationToken ct)
    {
        var tx = await _db.Database.BeginTransactionAsync(ct);
        return new EfBankTransaction(tx);
    }

    public async Task<int> SaveChangesAsync(CancellationToken ct)
    {
        try
        {
            return await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new BankException(BankErrorCode.Conflict, "Concurrent update detected. Please retry.");
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg && pg.SqlState == "40P01")
        {
            // 40P01 = deadlock_detected
            throw new BankException(BankErrorCode.Conflict, "Database deadlock detected. Please retry.");
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg && pg.SqlState == "23505")
        {
            // 23505 = unique_violation (e.g., idempotency unique index race)
            throw new BankException(BankErrorCode.Conflict, "Conflict (unique constraint).");
        }
    }

    private sealed class EfBankTransaction : IBankTransaction
    {
        private readonly IDbContextTransaction _tx;

        public EfBankTransaction(IDbContextTransaction tx) => _tx = tx;

        public Task CommitAsync(CancellationToken ct) => _tx.CommitAsync(ct);
        public Task RollbackAsync(CancellationToken ct) => _tx.RollbackAsync(ct);
        public ValueTask DisposeAsync() => _tx.DisposeAsync();
    }
}
namespace Bank.Application.Abstractions;

public interface IBankTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken ct);
    Task RollbackAsync(CancellationToken ct);
}

public interface IBankUnitOfWork
{
    Task<IBankTransaction> BeginTransactionAsync(CancellationToken ct);
    Task<int> SaveChangesAsync(CancellationToken ct);
}
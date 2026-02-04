using Bank.Domain.Entities;

namespace Bank.Application.Abstractions;

public interface IAccountRepository
{
    Task<Account?> GetByIdAsync(Guid accountId, CancellationToken ct);

    Task<List<Account>> ListByClientAsync(string clientId, CancellationToken ct);

    Task<Account?> FindByClientAndCurrencyAsync(string clientId, string currency, CancellationToken ct);

    void Add(Account account);
}
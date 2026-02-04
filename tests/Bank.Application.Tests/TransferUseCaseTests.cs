using Bank.Application.Abstractions;
using Bank.Application.Errors;
using Bank.Application.UseCases;
using Bank.Domain.Entities;
using Xunit;

namespace Bank.Application.Tests;

public class TransferUseCaseTests
{
    [Fact]
    public async Task Transfer_CreatesTwoLedgerEntries_UpdatesBalances()
    {
        var clientA = "client-a";
        var clientB = "client-b";

        var accA = new Account { ClientId = clientA, Currency = "ILS", BalanceCached = 1000m, Status = AccountStatus.Active };
        var accB = new Account { ClientId = clientB, Currency = "ILS", BalanceCached = 10m, Status = AccountStatus.Active };

        var accounts = new FakeAccountRepository(accA, accB);
        var ledger = new FakeLedgerRepository();
        var uow = new FakeUnitOfWork();

        var cmd = new Transfer.Command(clientA, accA.Id, accB.Id, 250m, "k1", "rent");

        var res = await Transfer.Handle(cmd, accounts, ledger, uow, CancellationToken.None);

        Assert.Equal(accA.Id, res.FromAccountId);
        Assert.Equal(accB.Id, res.ToAccountId);
        Assert.Equal(250m, res.Amount);
        Assert.Equal(750m, res.FromBalanceAfter);
        Assert.Equal(260m, res.ToBalanceAfter);

        Assert.Equal(2, ledger.Items.Count);

        var outLeg = ledger.Items.Single(x => x.AccountId == accA.Id);
        var inLeg  = ledger.Items.Single(x => x.AccountId == accB.Id);

        Assert.Equal(LedgerEntryType.TransferOut, outLeg.Type);
        Assert.Equal(-250m, outLeg.Amount);
        Assert.Equal(accB.Id, outLeg.CounterpartyAccountId);
        Assert.Equal(res.CorrelationId, outLeg.CorrelationId);

        Assert.Equal(LedgerEntryType.TransferIn, inLeg.Type);
        Assert.Equal(250m, inLeg.Amount);
        Assert.Equal(accA.Id, inLeg.CounterpartyAccountId);
        Assert.Equal(res.CorrelationId, inLeg.CorrelationId);
    }

    [Fact]
    public async Task Transfer_Forbidden_WhenFromAccountNotOwned()
    {
        var clientA = "client-a";
        var clientB = "client-b";

        var accA = new Account { ClientId = clientB, Currency = "ILS", BalanceCached = 1000m, Status = AccountStatus.Active };
        var accB = new Account { ClientId = clientA, Currency = "ILS", BalanceCached = 0m, Status = AccountStatus.Active };

        var accounts = new FakeAccountRepository(accA, accB);
        var ledger = new FakeLedgerRepository();
        var uow = new FakeUnitOfWork();

        var cmd = new Transfer.Command(clientA, accA.Id, accB.Id, 1m, "k1", null);

        var ex = await Assert.ThrowsAsync<BankException>(() => Transfer.Handle(cmd, accounts, ledger, uow, CancellationToken.None));
        Assert.Equal(BankErrorCode.Forbidden, ex.Code);
    }

    [Fact]
    public async Task Transfer_DuplicateRequest_WhenIdempotencyUsed()
    {
        var clientA = "client-a";
        var clientB = "client-b";

        var accA = new Account { ClientId = clientA, Currency = "ILS", BalanceCached = 1000m, Status = AccountStatus.Active };
        var accB = new Account { ClientId = clientB, Currency = "ILS", BalanceCached = 0m, Status = AccountStatus.Active };

        var accounts = new FakeAccountRepository(accA, accB);
        var ledger = new FakeLedgerRepository();
        var uow = new FakeUnitOfWork();

        // seed existing ledger entry on from-account with same idempotency key
        ledger.Add(new LedgerEntry
        {
            AccountId = accA.Id,
            Amount = -1m,
            Type = LedgerEntryType.TransferOut,
            IdempotencyKey = "k1",
            CorrelationId = Guid.NewGuid()
        });

        var cmd = new Transfer.Command(clientA, accA.Id, accB.Id, 1m, "k1", null);

        var ex = await Assert.ThrowsAsync<BankException>(() => Transfer.Handle(cmd, accounts, ledger, uow, CancellationToken.None));
        Assert.Equal(BankErrorCode.DuplicateRequest, ex.Code);
    }

    // ===== fakes =====

    private sealed class FakeAccountRepository : IAccountRepository
    {
        private readonly Dictionary<Guid, Account> _map = new();

        public FakeAccountRepository(params Account[] accounts)
        {
            foreach (var a in accounts) _map[a.Id] = a;
        }

        public Task<Account?> GetByIdAsync(Guid accountId, CancellationToken ct)
            => Task.FromResult(_map.TryGetValue(accountId, out var a) ? a : null);

        public Task<List<Account>> ListByClientAsync(string clientId, CancellationToken ct)
            => Task.FromResult(_map.Values.Where(a => a.ClientId == clientId).ToList());

        public Task<Account?> FindByClientAndCurrencyAsync(string clientId, string currency, CancellationToken ct)
            => Task.FromResult(_map.Values.FirstOrDefault(a => a.ClientId == clientId && a.Currency == currency));

        public void Add(Account account) => _map[account.Id] = account;
    }

    private sealed class FakeLedgerRepository : ILedgerRepository
    {
        public List<LedgerEntry> Items { get; } = new();

        public Task<LedgerEntry?> FindByAccountAndIdempotencyKeyAsync(Guid accountId, string idempotencyKey, CancellationToken ct)
            => Task.FromResult(Items.FirstOrDefault(x => x.AccountId == accountId && x.IdempotencyKey == idempotencyKey));

        public void Add(LedgerEntry entry) => Items.Add(entry);

        public void AddRange(IEnumerable<LedgerEntry> entries) => Items.AddRange(entries);

        public Task<List<LedgerEntry>> ListRecentAsync(Guid accountId, int limit, CancellationToken ct)
            => Task.FromResult(
                Items.Where(x => x.AccountId == accountId)
                     .OrderByDescending(x => x.CreatedAtUtc)
                     .Take(limit)
                     .ToList());
    }

    private sealed class FakeUnitOfWork : IBankUnitOfWork
    {
        public Task<IBankTransaction> BeginTransactionAsync(CancellationToken ct)
            => Task.FromResult<IBankTransaction>(new NoopTx());

        public Task<int> SaveChangesAsync(CancellationToken ct) => Task.FromResult(1);

        private sealed class NoopTx : IBankTransaction
        {
            public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;
            public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
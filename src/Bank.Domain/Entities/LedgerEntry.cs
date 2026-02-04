namespace Bank.Domain.Entities;

public sealed class LedgerEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid AccountId { get; set; }

    // Signed amount: +deposit/+incoming, -withdraw/-outgoing
    public decimal Amount { get; set; }

    public LedgerEntryType Type { get; set; }

    // Ties both legs of a transfer
    public Guid CorrelationId { get; set; } = Guid.NewGuid();

    // NEW: For transfers, store the other side's accountId so UI/history can show "to/from".
    // Null for non-transfer operations.
    public Guid? CounterpartyAccountId { get; set; }

    // Client-provided idempotency key (required for money ops)
    public string IdempotencyKey { get; set; } = default!;

    public string? Description { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
namespace Bank.Domain.Entities;

public sealed class Account
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // Identity user id (Client)
    public string ClientId { get; set; } = default!;

    public string Currency { get; set; } = "ILS";

    public decimal BalanceCached { get; set; } = 0m;

    public AccountStatus Status { get; set; } = AccountStatus.Active;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
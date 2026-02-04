using Bank.Domain.Entities;
using Bank.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Bank.Infrastructure.Data;

public class BankDbContext : IdentityDbContext<AppUser>
{
    public BankDbContext(DbContextOptions<BankDbContext> options) : base(options) { }

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Refresh tokens
        builder.Entity<RefreshToken>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.UserId, x.TokenHash }).IsUnique();
            b.Property(x => x.TokenHash).IsRequired();

            b.HasOne(x => x.User)
             .WithMany()
             .HasForeignKey(x => x.UserId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // Accounts
        builder.Entity<Account>(b =>
        {
            b.HasKey(x => x.Id);

            b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            b.Property(x => x.BalanceCached).HasPrecision(18, 2);

            b.HasIndex(x => x.ClientId);
            b.HasIndex(x => new { x.ClientId, x.Currency }).IsUnique();

            // Concurrency token (xmin) in PostgreSQL via rowversion mapping
            b.Property<uint>("Version").IsRowVersion();
        });

        // Ledger entries
        builder.Entity<LedgerEntry>(b =>
        {
            b.HasKey(x => x.Id);

            b.Property(x => x.Amount).HasPrecision(18, 2);

            b.Property(x => x.Type)
             .HasConversion<int>() // store enum as int
             .IsRequired();

            b.Property(x => x.CorrelationId).IsRequired();

            // NEW
            b.Property(x => x.CounterpartyAccountId);

            b.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
            b.Property(x => x.Description).HasMaxLength(512);

            b.HasIndex(x => new { x.AccountId, x.CreatedAtUtc });
            b.HasIndex(x => new { x.AccountId, x.IdempotencyKey }).IsUnique();

            // Optional but useful for debugging / joining both legs
            b.HasIndex(x => x.CorrelationId);
            b.HasIndex(x => x.CounterpartyAccountId);
        });
    }
}
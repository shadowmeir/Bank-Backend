namespace Bank.Domain.Entities;

public enum LedgerEntryType
{
    Deposit = 0,
    Withdraw = 1,
    TransferOut = 2,
    TransferIn = 3
}
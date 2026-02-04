namespace Bank.Application.Errors;

public enum BankErrorCode
{
    NotFound,
    Forbidden,
    Validation,
    Conflict,
    InsufficientFunds,
    DuplicateRequest
}
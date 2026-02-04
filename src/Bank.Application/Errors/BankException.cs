namespace Bank.Application.Errors;

public sealed class BankException : Exception
{
    public BankErrorCode Code { get; }

    public BankException(BankErrorCode code, string message) : base(message)
    {
        Code = code;
    }
}
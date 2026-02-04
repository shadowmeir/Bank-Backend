using System.Security.Claims;
using Bank.Application.Abstractions;
using Bank.Application.Errors;
using Bank.Application.UseCases;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Bank.Api.Controllers;

[ApiController]
[Route("transactions")]
[Authorize]
public class TransactionsController : ControllerBase
{
    private readonly IAccountRepository _accounts;
    private readonly ILedgerRepository _ledger;
    private readonly IBankUnitOfWork _uow;

    public TransactionsController(IAccountRepository accounts, ILedgerRepository ledger, IBankUnitOfWork uow)
    {
        _accounts = accounts;
        _ledger = ledger;
        _uow = uow;
    }

    private string ClientId => User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("Missing NameIdentifier claim");

    public record TransferRequest(Guid FromAccountId, Guid ToAccountId, decimal Amount, string? Description);

    [HttpPost("transfer")]
    public async Task<IActionResult> TransferMoney([FromBody] TransferRequest req, CancellationToken ct)
    {
        var idempotencyKey = Request.Headers["Idempotency-Key"].ToString();

        try
        {
            var res = await Transfer.Handle(
                new(ClientId, req.FromAccountId, req.ToAccountId, req.Amount, idempotencyKey, req.Description),
                _accounts, _ledger, _uow, ct);

            return Ok(res);
        }
        catch (BankException ex)
        {
            var status = ex.Code switch
            {
                BankErrorCode.NotFound => 404,
                BankErrorCode.Forbidden => 403,
                BankErrorCode.Validation => 400,
                BankErrorCode.Conflict => 409,
                BankErrorCode.InsufficientFunds => 409,
                BankErrorCode.DuplicateRequest => 409,
                _ => 400
            };
            return Problem(statusCode: status, title: ex.Code.ToString(), detail: ex.Message);
        }
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] Guid accountId, [FromQuery] int limit = 20, CancellationToken ct = default)
    {
        try
        {
            var res = await GetTransactions.Handle(new(ClientId, accountId, limit), _accounts, _ledger, ct);
            return Ok(res);
        }
        catch (BankException ex)
        {
            var status = ex.Code switch
            {
                BankErrorCode.NotFound => 404,
                BankErrorCode.Forbidden => 403,
                _ => 400
            };
            return Problem(statusCode: status, title: ex.Code.ToString(), detail: ex.Message);
        }
    }
}
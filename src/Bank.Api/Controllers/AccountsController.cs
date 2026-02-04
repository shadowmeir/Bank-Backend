using System.Security.Claims;
using Bank.Application.Abstractions;
using Bank.Application.Errors;
using Bank.Application.UseCases;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Bank.Api.Controllers;

[ApiController]
[Route("accounts")]
[Authorize]
public class AccountsController : ControllerBase
{
    private readonly IAccountRepository _accounts;
    private readonly ILedgerRepository _ledger;
    private readonly IBankUnitOfWork _uow;

    public AccountsController(IAccountRepository accounts, ILedgerRepository ledger, IBankUnitOfWork uow)
    {
        _accounts = accounts;
        _ledger = ledger;
        _uow = uow;
    }

    private string ClientId => User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("Missing NameIdentifier claim");

    public record CreateAccountRequest(string Currency);

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateAccountRequest req, CancellationToken ct)
    {
        try
        {
            var res = await CreateAccount.Handle(new(ClientId, req.Currency), _accounts, _uow, ct);
            return CreatedAtAction(nameof(GetMine), new { }, res);
        }
        catch (BankException ex) { return ProblemFromBankException(ex); }
    }

    [HttpGet("mine")]
    public async Task<IActionResult> GetMine(CancellationToken ct)
    {
        var items = await _accounts.ListByClientAsync(ClientId, ct);
        return Ok(items.Select(a => new
        {
            a.Id,
            a.Currency,
            a.BalanceCached,
            a.Status,
            a.CreatedAtUtc
        }));
    }

    public record MoneyRequest(decimal Amount, string? Description);

    [HttpPost("{accountId:guid}/deposit")]
    public async Task<IActionResult> DepositTo([FromRoute] Guid accountId, [FromBody] MoneyRequest req, CancellationToken ct)
    {
        var idempotencyKey = Request.Headers["Idempotency-Key"].ToString();
        try
        {
            var res = await Deposit.Handle(new(ClientId, accountId, req.Amount, idempotencyKey, req.Description), _accounts, _ledger, _uow, ct);
            return Ok(res);
        }
        catch (BankException ex) { return ProblemFromBankException(ex); }
    }

    [HttpPost("{accountId:guid}/withdraw")]
    public async Task<IActionResult> WithdrawFrom([FromRoute] Guid accountId, [FromBody] MoneyRequest req, CancellationToken ct)
    {
        var idempotencyKey = Request.Headers["Idempotency-Key"].ToString();
        try
        {
            var res = await Withdraw.Handle(new(ClientId, accountId, req.Amount, idempotencyKey, req.Description), _accounts, _ledger, _uow, ct);
            return Ok(res);
        }
        catch (BankException ex) { return ProblemFromBankException(ex); }
    }

    private ObjectResult ProblemFromBankException(BankException ex)
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
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFinance.Application.DTOs.Account;
using SmartFinance.Application.Interfaces;

namespace SmartFinance.API.Controllers;

[ApiController]
[Route("api/account")]
[Authorize]
public class AccountController : ControllerBase
{
    private readonly IAccountService _service;
    public AccountController(IAccountService service) => _service = service;

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] bool includeArchived = false) =>
        Ok(await _service.GetAllAsync(includeArchived));

    [HttpPost]
    public async Task<IActionResult> Create(CreateAccountDto dto)
    {
        var account = await _service.CreateAsync(dto);
        return Created($"/api/account/{account.Id}", account);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, CreateAccountDto dto) => Ok(await _service.UpdateAsync(id, dto));

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        if (await _service.DeleteAsync(id)) return Ok(new { archived = true });
        return NoContent();
    }

    [HttpPost("{id:int}/unarchive")]
    public async Task<IActionResult> Unarchive(int id) => Ok(await _service.UnarchiveAsync(id));

    [HttpPost("transfer")]
    public async Task<IActionResult> Transfer(CreateTransferDto dto)
    {
        var transfer = await _service.CreateTransferAsync(dto);
        return Created($"/api/account/transfer/{transfer.Id}", transfer);
    }

    [HttpGet("{id:int}/transfers")]
    public async Task<IActionResult> Transfers(int id, [FromQuery] int page = 1, [FromQuery] int pageSize = 50) =>
        Ok(await _service.GetTransfersAsync(id, page, pageSize));

    [HttpDelete("transfer/{id:int}")]
    public async Task<IActionResult> DeleteTransfer(int id)
    {
        await _service.DeleteTransferAsync(id);
        return NoContent();
    }

    [HttpGet("{id:int}/statements")]
    public async Task<IActionResult> Statements(int id, [FromQuery] int count = 3) =>
        Ok(await _service.GetStatementsAsync(id, count));
}

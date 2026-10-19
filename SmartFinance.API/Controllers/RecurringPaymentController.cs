using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFinance.Application.DTOs.RecurringPayment;
using SmartFinance.Application.Interfaces;

namespace SmartFinance.API.Controllers;

[ApiController]
[Route("api/recurring")]
[Authorize]
public class RecurringPaymentController : ControllerBase
{
    private readonly IRecurringPaymentService _service;
    public RecurringPaymentController(IRecurringPaymentService service) => _service = service;

    [HttpGet]
    public async Task<IActionResult> List() => Ok(await _service.ListAsync());

    [HttpGet("upcoming")]
    public async Task<IActionResult> Upcoming([FromQuery] int days = 30) => Ok(await _service.UpcomingAsync(days));

    [HttpPost]
    public async Task<IActionResult> Create(CreateRecurringPaymentDto dto)
    {
        var p = await _service.CreateAsync(dto);
        return Created($"/api/recurring/{p.Id}", p);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, CreateRecurringPaymentDto dto) => Ok(await _service.UpdateAsync(id, dto));

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        await _service.DeleteAsync(id);
        return NoContent();
    }

    [HttpPost("{id:int}/pay")]
    public async Task<IActionResult> Pay(int id, PayRecurringPaymentDto dto) => Ok(await _service.PayAsync(id, dto));

    [HttpPost("{id:int}/skip")]
    public async Task<IActionResult> Skip(int id, ProcessRecurringPaymentDto dto) => Ok(await _service.SkipAsync(id, dto));
}

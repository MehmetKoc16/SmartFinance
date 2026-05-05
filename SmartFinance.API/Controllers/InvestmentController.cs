using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFinance.Application.DTOs.Investment;
using SmartFinance.Application.Interfaces;
 
namespace SmartFinance.API.Controllers;
 
[ApiController]
[Route("api/[controller]")]
[Authorize]

public class InvestmentController : ControllerBase
{
    private readonly IInvestmentService _investmentService;

    public InvestmentController(IInvestmentService investmentService)
    {
        _investmentService=investmentService;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var investments = await _investmentService.GetAllInvestmentsAsync();
        return Ok(investments);
    }

    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary()
    {
        var summary = await _investmentService.GetPortfolioSummaryAsync();
        return Ok(summary);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int id)
    {
        var investment = await _investmentService.GetInvestmentByIdAsync(id);
        return Ok(investment);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateInvestmentDto dto)
    {
        var created = await _investmentService.CreateInvestmentAsync(dto);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, [FromBody] CreateInvestmentDto dto)
    {
        await _investmentService.UpdateInvestmentAsync(id, dto);
        return NoContent();
    }
    
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        await _investmentService.DeleteInvestmentAsync(id);
        return NoContent();
    }
 
}
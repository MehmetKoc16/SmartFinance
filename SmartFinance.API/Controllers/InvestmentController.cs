using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
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

        // Ayni semboldan tekrar alimda yeni kayit acilmiyor, mevcut pozisyon
        // guncelleniyor — bu durumda 201 Created yaniltici olurdu.
        if (created.Merged)
            return Ok(created);

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

    // Portfoydeki her yatirim icin dis servise gider — tek istek N dis cagri demek.
    [HttpPost("refresh-prices")]
    [EnableRateLimiting("market")]
    public async Task<IActionResult> RefreshPrices()
    {
        var result = await _investmentService.RefreshPricesAsync();
        return Ok(result);
    }

    [HttpGet("{id}/technical-analysis")]
    [EnableRateLimiting("market")]
    public async Task<IActionResult> GetTechnicalAnalysis(int id, [FromQuery] string range = "6m", [FromQuery] string? indicators = null)
    {
        var keys = string.IsNullOrWhiteSpace(indicators)
            ? Enumerable.Empty<string>()
            : indicators.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var analysis = await _investmentService.GetTechnicalAnalysisAsync(id, range, keys);
        return Ok(analysis);
    }

}
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using SmartFinance.Application.DTOs.Budget;
using SmartFinance.Application.Interfaces;

namespace SmartFinance.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class BudgetController : ControllerBase
    {
        private readonly IBudgetService _budgetService;

        public BudgetController(IBudgetService budgetService)
        {
            _budgetService = budgetService;
        }

        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var budgets = await _budgetService.GetAllAsync();
            return Ok(budgets);
        }

        [HttpPost]
        public async Task<IActionResult> Upsert(CreateBudgetDto dto)
        {
            var budget = await _budgetService.UpsertAsync(dto);
            return Ok(budget);
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(int id)
        {
            await _budgetService.DeleteAsync(id);
            return NoContent();
        }

        [HttpGet("status/{year}/{month}")]
        public async Task<IActionResult> GetStatus(int year, int month)
        {
            var status = await _budgetService.GetStatusAsync(year, month);
            return Ok(status);
        }
    }
}

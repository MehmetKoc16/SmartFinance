using Microsoft.AspNetCore.Mvc;
using SmartFinance.Application.DTOs.Transaction;
using SmartFinance.Application.Interfaces;

namespace SmartFinance.API.Controllers{
    [ApiController]
    [Route("api/[controller]")]
    public class TransactionController : ControllerBase
    {
        private readonly ITransactionService _transactionService;

        public TransactionController(ITransactionService transactionService)
        {
            _transactionService = transactionService;
        }
        [HttpGet]
        public async Task<IActionResult> GetAllTransactionsAsync()
        {
            var transactions = await _transactionService.GetAllTransactionsAsync();
            return Ok(transactions);
        }
        [HttpGet("{id}")]
        public async Task<IActionResult> GetTransactionByIdAsync(int id)
        {
            var transaction = await _transactionService.GetTransactionByIdAsync(id);
            if (transaction == null) return NotFound();
            return Ok(transaction);
        }
        [HttpPost]
        public async Task<IActionResult> CreateTransactionAsync(CreateTransactionDto dto)
        {
            var transaction = await _transactionService.CreateTransactionAsync(dto);
            return CreatedAtAction(nameof (GetTransactionByIdAsync), new {id=transaction.Id},transaction);
        }
        [HttpPut("{id}")]
        public async Task<IActionResult> UpdateTransactionAsync(int id,CreateTransactionDto dto)
        {
            await _transactionService.UpdateTransactionAsync(id,dto);
            return NoContent();
        }
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteTransactionAsync(int id)
        {
            await _transactionService.DeleteTransactionAsync(id);
            return NoContent();
        }
    }
}

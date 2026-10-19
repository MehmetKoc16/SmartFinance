using SmartFinance.Application.DTOs.Account;

namespace SmartFinance.Application.Interfaces;

public interface IAccountService
{
    Task<IReadOnlyList<AccountDto>> GetAllAsync(bool includeArchived = false);
    Task<AccountDto> GetAsync(int id);
    Task<AccountDto> CreateAsync(CreateAccountDto dto);
    Task<AccountDto> UpdateAsync(int id, CreateAccountDto dto);
    Task<bool> DeleteAsync(int id);
    Task<AccountDto> UnarchiveAsync(int id);
    Task<TransferDto> CreateTransferAsync(CreateTransferDto dto);
    Task<TransfersDto> GetTransfersAsync(int id, int page = 1, int pageSize = 50);
    Task DeleteTransferAsync(int id);
    Task<StatementsDto> GetStatementsAsync(int id, int count = 3);
}

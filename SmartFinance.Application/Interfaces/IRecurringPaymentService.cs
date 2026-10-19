using SmartFinance.Application.DTOs.RecurringPayment;

namespace SmartFinance.Application.Interfaces;

public interface IRecurringPaymentService
{
    Task<IReadOnlyList<RecurringPaymentDto>> ListAsync();
    Task<RecurringPaymentDto> CreateAsync(CreateRecurringPaymentDto dto);
    Task<RecurringPaymentDto> UpdateAsync(int id, CreateRecurringPaymentDto dto);
    Task DeleteAsync(int id);
    Task<PayRecurringPaymentResultDto> PayAsync(int id, PayRecurringPaymentDto dto);
    Task<RecurringPaymentDto> SkipAsync(int id, ProcessRecurringPaymentDto dto);
    Task<UpcomingRecurringPaymentsDto> UpcomingAsync(int days = 30);
}

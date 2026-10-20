namespace SmartFinance.Application.DTOs.Transaction;

// PUT /api/transaction/{id} gövdesi: CreateTransactionDto alanları + ClearAccount.
// Hesap bağı kuralı (v14/v15 istemcileri accountId göndermez):
// - AccountId dolu → doğrulanıp atanır.
// - AccountId null ve ClearAccount == true → bağ kaldırılır.
// - İkisi de yok → mevcut bağ korunur.
public class UpdateTransactionDto : CreateTransactionDto
{
    public bool? ClearAccount { get; set; }
}

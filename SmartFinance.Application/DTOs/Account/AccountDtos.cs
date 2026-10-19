using System.ComponentModel.DataAnnotations;
using SmartFinance.Domain.Enums;

namespace SmartFinance.Application.DTOs.Account;

public class CreateAccountDto
{
    [Required, MaxLength(60)]
    public string Name { get; set; } = string.Empty;
    public AccountType Type { get; set; }
    public decimal? OpeningBalance { get; set; }
    public decimal? CreditLimit { get; set; }
    public int? StatementDay { get; set; }
    public int? DueDay { get; set; }
}

public class AccountDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public AccountType Type { get; set; }
    public decimal OpeningBalance { get; set; }
    public decimal? CreditLimit { get; set; }
    public int? StatementDay { get; set; }
    public int? DueDay { get; set; }
    public bool IsArchived { get; set; }
    public int SortOrder { get; set; }
    public decimal? Balance { get; set; }
    public decimal? Debt { get; set; }
    public decimal? AvailableLimit { get; set; }
}

public class CreateTransferDto
{
    public int FromAccountId { get; set; }
    public int ToAccountId { get; set; }
    public decimal Amount { get; set; }
    public DateTime TransferDate { get; set; }
    [MaxLength(200)]
    public string? Description { get; set; }
}

public class TransferDto : CreateTransferDto
{
    public int Id { get; set; }
}

public class TransfersDto
{
    public IReadOnlyList<TransferDto> Items { get; set; } = Array.Empty<TransferDto>();
    public int TotalCount { get; set; }
}

public class StatementDto
{
    public string PeriodStart { get; set; } = string.Empty;
    public string PeriodEnd { get; set; } = string.Empty;
    public string DueDate { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public decimal Paid { get; set; }
    public decimal Remaining { get; set; }
    public string Status { get; set; } = string.Empty;
}

public class StatementsDto
{
    public StatementDto Current { get; set; } = new();
    public IReadOnlyList<StatementDto> Past { get; set; } = Array.Empty<StatementDto>();
}

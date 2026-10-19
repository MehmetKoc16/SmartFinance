using SmartFinance.Domain.Common;
using SmartFinance.Domain.Enums;

namespace SmartFinance.Domain.Entities;

public class Account : BaseEntity
{
    public int UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public AccountType Type { get; set; }
    public decimal OpeningBalance { get; set; }
    public decimal? CreditLimit { get; set; }
    public int? StatementDay { get; set; }
    public int? DueDay { get; set; }
    public bool IsArchived { get; set; }
    public int SortOrder { get; set; }
}

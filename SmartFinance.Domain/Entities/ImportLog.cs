using SmartFinance.Domain.Common;

namespace SmartFinance.Domain.Entities;

/// <summary>
/// Başarılı bir ekstre içe aktarma kaydı.
///
/// Ücretsiz katmanda aylık içe aktarma sınırını sayabilmek için tutuluyor;
/// başka türlü "bu ay kaç kez içe aktardı" bilgisine ulaşmanın yolu yok
/// (işlemlerin CreatedDate'i tek tek sayılabilirdi ama elle eklenen işlemlerle
/// karışırdı).
///
/// Yalnızca EN AZ BİR işlem kaydedilen içe aktarmalar buraya yazılıyor:
/// tamamı mükerrer olduğu için hiçbir şey eklenmeyen bir yükleme kullanıcının
/// hakkını yakmamalı.
/// </summary>
public class ImportLog : BaseEntity
{
    public int UserId { get; set; }
    public User User { get; set; } = null!;

    /// İçe aktarmada kaydedilen işlem sayısı.
    public int TransactionCount { get; set; }
}

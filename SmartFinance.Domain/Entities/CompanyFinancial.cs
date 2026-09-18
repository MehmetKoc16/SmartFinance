using SmartFinance.Domain.Common;

namespace SmartFinance.Domain.Entities;

/// <summary>
/// Bir sirketin KAP'ta yayimlanan finansal tablosundan tek bir donemin iki kalemi.
/// Gelir tablosu kalemleri KUMULATIF: Period=2 ilk 6 ayin toplami, Period=4 yillik.
/// Kaynak: KAP "Finansal Tablo Kalem Sorgulama" — ilgili donemin kendi raporundaki
/// "cari donem" kolonu (sonradan yeniden ifade edilmis deger degil).
/// </summary>
public class CompanyFinancial : BaseEntity
{
    public string Symbol { get; set; } = string.Empty;
    public int Year { get; set; }

    /// 1 = 3 aylik, 2 = 6 aylik, 3 = 9 aylik, 4 = yillik.
    public int Period { get; set; }

    /// Donem karinin ana ortakliga dusen payi, TL (sunum birimiyle carpilmis).
    public decimal? NetProfitParent { get; set; }

    /// Ana ortakliga ait ozkaynaklar, TL.
    public decimal? EquityParent { get; set; }

    public bool Consolidated { get; set; }
    public string DisclosureId { get; set; } = string.Empty;
    public DateTime PublishedAt { get; set; }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFinance.Infrastructure.Migrations
{
    /// <summary>
    /// Mevcut RefreshTokens.Token satirlarini duz degerden SHA-256 ozetine cevirir.
    ///
    /// Sema degismiyor (kolon nvarchar(200), unique IX_RefreshTokens_Token aynen
    /// kaliyor); yalnizca veri donusuyor. Uretilen bicim C# tarafindaki
    /// AuthService.HashToken ile BIREBIR ayni olmali:
    ///   Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)))
    ///   = dolgulu standart base64, 44 karakter, 0x oneki yok.
    ///
    /// Kolon nvarchar oldugu icin HASHBYTES dogrudan UTF-16 baytlarini ozetlerdi.
    /// Token'lar base64 (A-Z a-z 0-9 + / =) yani saf ASCII; VARCHAR'a cevirmek
    /// kayipsizdir ve ASCII baytlari UTF-8 baytlariyla aynidir.
    /// SQL Server'da yerlesik base64 fonksiyonu olmadigi (2025 oncesi) icin
    /// XML xs:base64Binary donusumu kullaniliyor.
    /// </summary>
    public partial class HashRefreshTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
UPDATE rt
SET rt.[Token] = CAST(N'' AS XML).value('xs:base64Binary(sql:column(""h.Ozet""))', 'VARCHAR(64)')
FROM [RefreshTokens] AS rt
CROSS APPLY (SELECT HASHBYTES('SHA2_256', CAST(rt.[Token] AS VARCHAR(200))) AS Ozet) AS h;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Ozetten duz degere geri donus IMKANSIZ (SHA-256 tek yonlu). Eski kod
            // ozetleri duz token sanip hicbiriyle eslesemeyecegi icin satirlarin
            // kalmasinin anlami yok; tum oturumlar siliniyor, kullanicilar
            // yeniden giris yapar.
            migrationBuilder.Sql("DELETE FROM [RefreshTokens];");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFinance.Infrastructure.Migrations
{
    /// <summary>
    /// Yeni kullanicilar icin varsayilan gelir kategorileri AuthService'te
    /// cogaltildi; bu migration ayni kategorileri MEVCUT kullanicilara da
    /// ekler. Aksi halde kapali testteki kullanicilarda gelir tarafinda tek
    /// secenek ("Maaş") kalmaya devam ederdi.
    ///
    /// Sema degisikligi yok, yalnizca veri. Ayni isimde kategorisi olan
    /// kullaniciya tekrar eklenmez, bu yuzden tekrar calistirilabilir.
    /// </summary>
    public partial class VarsayilanGelirKategorileri : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
INSERT INTO Categories (Name, Type, Icon, Color, UserId, CreatedDate, IsDeleted)
SELECT v.Name, 1, v.Icon, v.Color, u.Id, SYSUTCDATETIME(), 0
FROM Users u
CROSS JOIN (VALUES
    (N'Ek Gelir',       N'briefcase',   N'#14B8A6'),
    (N'Kira Geliri',    N'home',        N'#F97316'),
    (N'Yatırım Geliri', N'trending-up', N'#159A5B'),
    (N'Hediye',         N'gift',        N'#EC4899')
) AS v(Name, Icon, Color)
WHERE u.IsDeleted = 0
  AND NOT EXISTS (
      SELECT 1 FROM Categories c
      WHERE c.UserId = u.Id AND c.Name = v.Name AND c.IsDeleted = 0
  );");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Yalnizca hic kullanilmamis olanlar siliniyor: kullanici bu
            // kategorilere islem baglamissa geri alma sirasinda veri kaybolmasin.
            migrationBuilder.Sql(@"
DELETE FROM Categories
WHERE Type = 1
  AND Name IN (N'Ek Gelir', N'Kira Geliri', N'Yatırım Geliri', N'Hediye')
  AND NOT EXISTS (SELECT 1 FROM Transactions t WHERE t.CategoryId = Categories.Id);");
        }
    }
}

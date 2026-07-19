using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFinance.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveInsecureSeedTestUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 20260221114157_SeedTestUser migration'i Id=1 kullanicisini PasswordHash
            // alaninda BCrypt hash'i degil duz metin ("test123") ile olusturuyordu.
            // Bu kullanici her migrate edilen veritabaninda (uretim dahil) otomatik
            // olusuyordu ve onunla giris denemesi BCrypt.Verify'in gecersiz hash
            // formatinda patlamasiyla 500 hatasi veriyordu. E-posta kontrolu, Id=1'in
            // gercekten bu seed kaydi oldugundan emin olmak icin ekstra guvenlik.
            migrationBuilder.Sql(@"
                DELETE FROM Transactions WHERE UserId = 1 AND EXISTS (SELECT 1 FROM Users WHERE Id = 1 AND Email = 'test@smartfinance.com');
                DELETE FROM CategoryMappings WHERE UserId = 1 AND EXISTS (SELECT 1 FROM Users WHERE Id = 1 AND Email = 'test@smartfinance.com');
                DELETE FROM Investments WHERE UserId = 1 AND EXISTS (SELECT 1 FROM Users WHERE Id = 1 AND Email = 'test@smartfinance.com');
                DELETE FROM Categories WHERE UserId = 1 AND EXISTS (SELECT 1 FROM Users WHERE Id = 1 AND Email = 'test@smartfinance.com');
                DELETE FROM Users WHERE Id = 1 AND Email = 'test@smartfinance.com';
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Kasitli olarak geri alinamiyor — guvensiz test kullanicisini
            // yeniden olusturmak istenmeyen bir durum.
        }
    }
}

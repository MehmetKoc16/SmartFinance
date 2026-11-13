using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFinance.Infrastructure.Migrations
{
    public partial class RefreshTokenConcurrency : Migration
    {
        // Yalnız EF eşzamanlılık metadatası değişir; mevcut sütun yeterlidir.
        protected override void Up(MigrationBuilder migrationBuilder) { }
        protected override void Down(MigrationBuilder migrationBuilder) { }
    }
}
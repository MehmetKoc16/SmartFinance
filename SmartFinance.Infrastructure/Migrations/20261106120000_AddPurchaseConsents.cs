using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFinance.Infrastructure.Migrations
{
    public partial class AddPurchaseConsents : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PurchaseConsents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: true),
                    PurchaseToken = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    OrderId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ProductId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TextVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ConsentedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseConsents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PurchaseConsents_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseConsents_PurchaseToken",
                table: "PurchaseConsents",
                column: "PurchaseToken");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseConsents_UserId",
                table: "PurchaseConsents",
                column: "UserId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "PurchaseConsents");
        }
    }
}

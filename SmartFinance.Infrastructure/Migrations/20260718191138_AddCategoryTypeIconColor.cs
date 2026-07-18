using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFinance.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCategoryTypeIconColor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Color",
                table: "Categories",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Icon",
                table: "Categories",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Type",
                table: "Categories",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // Mevcut kategoriler icin geriye donuk doldurma: yeni sutunlar eklenmeden
            // once olusturulmus satirlar Type=0 (gecersiz) ile kalirdi.
            migrationBuilder.Sql("UPDATE Categories SET Type = 1, Icon = 'banknote', Color = '#159A5B' WHERE Name = N'Maaş'");
            migrationBuilder.Sql("UPDATE Categories SET Type = 2, Icon = 'utensils', Color = '#F43F5E' WHERE Name = N'Yeme-İçme'");
            migrationBuilder.Sql("UPDATE Categories SET Type = 2, Icon = 'car', Color = '#14B8A6' WHERE Name = N'Ulaşım'");
            migrationBuilder.Sql("UPDATE Categories SET Type = 2, Icon = 'receipt', Color = '#06B6D4' WHERE Name = N'Fatura'");
            migrationBuilder.Sql("UPDATE Categories SET Type = 2, Icon = 'landmark', Color = '#64748B' WHERE Name = N'ATM'");
            migrationBuilder.Sql("UPDATE Categories SET Type = 2, Icon = 'arrow-left-right', Color = '#3B82F6' WHERE Name = N'Transfer'");
            migrationBuilder.Sql("UPDATE Categories SET Type = 2, Icon = 'shopping-bag', Color = '#8B5CF6' WHERE Name = N'Alışveriş'");
            migrationBuilder.Sql("UPDATE Categories SET Type = 2, Icon = 'more-horizontal', Color = '#64748B' WHERE Name = N'Diğer'");
            // Bilinmeyen/ozel kategori adlari (yukaridakiler disinda kalan, hala Type=0 olanlar)
            // guvenli varsayilan olarak Gider'e ayarlanir.
            migrationBuilder.Sql("UPDATE Categories SET Type = 2 WHERE Type = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Color",
                table: "Categories");

            migrationBuilder.DropColumn(
                name: "Icon",
                table: "Categories");

            migrationBuilder.DropColumn(
                name: "Type",
                table: "Categories");
        }
    }
}

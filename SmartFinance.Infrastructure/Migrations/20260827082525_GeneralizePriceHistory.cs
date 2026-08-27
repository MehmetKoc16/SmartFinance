using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFinance.Infrastructure.Migrations
{
    /// <summary>
    /// Fona özel FundPriceHistories tablosunu, hisse geçmişini de tutabilen
    /// ortak PriceHistories tablosuna dönüştürür.
    ///
    /// EF'in ürettiği taslak DROP + CREATE yapıyordu; bu, tablodaki mevcut
    /// günlük fon fiyatlarının silinmesi demekti. TEFAS dakikada ~6 istek kabul
    /// ettiği için o veriyi yeniden toplamak saatler sürerdi. Bu yüzden tablo
    /// yeniden oluşturulmuyor, YENİDEN ADLANDIRILIYOR ve mevcut satırlar
    /// yeni şemaya taşınıyor.
    /// </summary>
    public partial class GeneralizePriceHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Eski tekillik anahtarı (FundCode + Date) yerini
            // (Symbol + InvestmentType + Date) alacak.
            migrationBuilder.DropIndex(
                name: "IX_FundPriceHistories_FundCode_Date",
                table: "FundPriceHistories");

            migrationBuilder.RenameTable(
                name: "FundPriceHistories",
                newName: "PriceHistories");

            // sp_rename tabloyu yeniden adlandırırken birincil anahtar kısıtının
            // adını değiştirmiyor; şema ile isimlendirme tutarlı kalsın diye
            // elle yeniden adlandırılıyor.
            migrationBuilder.Sql("EXEC sp_rename N'PK_FundPriceHistories', N'PK_PriceHistories', N'OBJECT';");

            migrationBuilder.RenameColumn(
                name: "FundCode",
                table: "PriceHistories",
                newName: "Symbol");

            migrationBuilder.RenameColumn(
                name: "Price",
                table: "PriceHistories",
                newName: "Close");

            // Hisse sembolleri 3 harflik fon kodlarından uzun olabiliyor
            // (borsa soneki dahil).
            migrationBuilder.AlterColumn<string>(
                name: "Symbol",
                table: "PriceHistories",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(10)",
                oldMaxLength: 10);

            // Yeni kolonlar önce nullable ekleniyor: NOT NULL + varsayılan değerle
            // eklemek tabloda kalıcı bir DEFAULT kısıtı bırakırdı ve bu kısıt
            // modelde karşılığı olmadığı için sonraki migration'larda gürültü
            // yaratırdı. Satırlar doldurulduktan sonra NOT NULL'a çevriliyor.
            migrationBuilder.AddColumn<string>(
                name: "InvestmentType", table: "PriceHistories",
                type: "nvarchar(20)", maxLength: 20, nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Open", table: "PriceHistories", type: "decimal(18,6)", nullable: true);
            migrationBuilder.AddColumn<decimal>(
                name: "High", table: "PriceHistories", type: "decimal(18,6)", nullable: true);
            migrationBuilder.AddColumn<decimal>(
                name: "Low", table: "PriceHistories", type: "decimal(18,6)", nullable: true);
            migrationBuilder.AddColumn<decimal>(
                name: "Volume", table: "PriceHistories", type: "decimal(20,2)", nullable: true);

            // Tablodaki mevcut satırların tamamı TEFAS fon NAV'ı: TEFAS tek bir
            // birim pay fiyatı yayınladığı için OHLC alanlarının hepsi aynı
            // değeri taşır, hacim yayınlanmaz.
            migrationBuilder.Sql(@"
                UPDATE [PriceHistories]
                SET [InvestmentType] = N'fund',
                    [Open] = [Close],
                    [High] = [Close],
                    [Low]  = [Close],
                    [Volume] = 0;");

            migrationBuilder.AlterColumn<string>(
                name: "InvestmentType", table: "PriceHistories",
                type: "nvarchar(20)", maxLength: 20, nullable: false,
                oldClrType: typeof(string), oldType: "nvarchar(20)", oldMaxLength: 20, oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "Open", table: "PriceHistories", type: "decimal(18,6)", nullable: false,
                oldClrType: typeof(decimal), oldType: "decimal(18,6)", oldNullable: true);
            migrationBuilder.AlterColumn<decimal>(
                name: "High", table: "PriceHistories", type: "decimal(18,6)", nullable: false,
                oldClrType: typeof(decimal), oldType: "decimal(18,6)", oldNullable: true);
            migrationBuilder.AlterColumn<decimal>(
                name: "Low", table: "PriceHistories", type: "decimal(18,6)", nullable: false,
                oldClrType: typeof(decimal), oldType: "decimal(18,6)", oldNullable: true);
            migrationBuilder.AlterColumn<decimal>(
                name: "Volume", table: "PriceHistories", type: "decimal(20,2)", nullable: false,
                oldClrType: typeof(decimal), oldType: "decimal(20,2)", oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PriceHistories_Symbol_InvestmentType_Date",
                table: "PriceHistories",
                columns: new[] { "Symbol", "InvestmentType", "Date" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PriceHistories_Symbol_InvestmentType_Date",
                table: "PriceHistories");

            // Eski şemada tip kolonu yok; fon dışındaki satırların gidecek yeri
            // olmadığı için siliniyor. Kayıp değil: bunlar dış kaynaktan
            // yeniden çekilebilen piyasa verisi, kullanıcı verisi değil.
            migrationBuilder.Sql("DELETE FROM [PriceHistories] WHERE [InvestmentType] <> N'fund';");

            migrationBuilder.DropColumn(name: "InvestmentType", table: "PriceHistories");
            migrationBuilder.DropColumn(name: "Open", table: "PriceHistories");
            migrationBuilder.DropColumn(name: "High", table: "PriceHistories");
            migrationBuilder.DropColumn(name: "Low", table: "PriceHistories");
            migrationBuilder.DropColumn(name: "Volume", table: "PriceHistories");

            migrationBuilder.AlterColumn<string>(
                name: "Symbol",
                table: "PriceHistories",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(20)",
                oldMaxLength: 20);

            migrationBuilder.RenameColumn(
                name: "Close", table: "PriceHistories", newName: "Price");
            migrationBuilder.RenameColumn(
                name: "Symbol", table: "PriceHistories", newName: "FundCode");

            migrationBuilder.Sql("EXEC sp_rename N'PK_PriceHistories', N'PK_FundPriceHistories', N'OBJECT';");

            migrationBuilder.RenameTable(
                name: "PriceHistories",
                newName: "FundPriceHistories");

            migrationBuilder.CreateIndex(
                name: "IX_FundPriceHistories_FundCode_Date",
                table: "FundPriceHistories",
                columns: new[] { "FundCode", "Date" },
                unique: true);
        }
    }
}

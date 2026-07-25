using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPriceNow.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPriceQuoteExchangePairIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Idempotent: on production this index was already created CONCURRENTLY (to avoid
            // locking the 7M-row table on a live deploy), so a plain CreateIndex here would
            // fail with "already exists". IF NOT EXISTS makes it a no-op there and creates it
            // normally on a fresh database.
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_PriceQuotes_ExchangeId_Pair_TimestampUtc\" " +
                "ON \"PriceQuotes\" (\"ExchangeId\", \"Pair\", \"TimestampUtc\");");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PriceQuotes_ExchangeId_Pair_TimestampUtc",
                table: "PriceQuotes");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPriceNow.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPriceQuoteCoveringIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Idempotent: on production this covering index was created CONCURRENTLY (to avoid
            // locking the 7M-row table on a live deploy) and renamed to the EF name below, so a
            // plain CreateIndex would fail with "already exists". IF NOT EXISTS makes it a no-op
            // there and creates it normally on a fresh database.
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_PriceQuotes_ExchangeId_Pair_RateType_TimestampUtc\" " +
                "ON \"PriceQuotes\" (\"ExchangeId\", \"Pair\", \"RateType\", \"TimestampUtc\") " +
                "INCLUDE (\"Buy\", \"Sell\");");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PriceQuotes_ExchangeId_Pair_RateType_TimestampUtc",
                table: "PriceQuotes");
        }
    }
}

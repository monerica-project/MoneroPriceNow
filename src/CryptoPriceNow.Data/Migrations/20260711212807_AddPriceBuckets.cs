using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPriceNow.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPriceBuckets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PriceBuckets",
                columns: table => new
                {
                    Pair = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RateType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Bucket = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SumBuy = table.Column<decimal>(type: "numeric(38,10)", precision: 38, scale: 10, nullable: false),
                    BuyCount = table.Column<int>(type: "integer", nullable: false),
                    SumSell = table.Column<decimal>(type: "numeric(38,10)", precision: 38, scale: 10, nullable: false),
                    SellCount = table.Column<int>(type: "integer", nullable: false),
                    Samples = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceBuckets", x => new { x.Pair, x.RateType, x.Bucket });
                });

            // One-time backfill: fold every existing raw quote into its 1-minute bucket.
            // Sums + separate buy/sell counts so coarser chart buckets re-aggregate exactly.
            // Runs once (migrations are tracked); ON CONFLICT keeps it re-safe.
            migrationBuilder.Sql("""
                INSERT INTO "PriceBuckets" ("Pair", "RateType", "Bucket", "SumBuy", "BuyCount", "SumSell", "SellCount", "Samples")
                SELECT "Pair",
                       "RateType",
                       date_bin(INTERVAL '1 minute', "TimestampUtc", TIMESTAMPTZ '2000-01-03') AS bucket,
                       COALESCE(SUM("Buy"), 0),  COUNT("Buy"),
                       COALESCE(SUM("Sell"), 0), COUNT("Sell"),
                       COUNT(*)
                FROM "PriceQuotes"
                GROUP BY "Pair", "RateType", bucket
                ON CONFLICT ("Pair", "RateType", "Bucket") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PriceBuckets");
        }
    }
}

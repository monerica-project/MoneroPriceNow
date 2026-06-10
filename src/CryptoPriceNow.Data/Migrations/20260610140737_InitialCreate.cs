using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CryptoPriceNow.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Exchanges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ExchangeKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SiteName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SiteUrl = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    PrivacyLevel = table.Column<string>(type: "character varying(1)", maxLength: 1, nullable: true),
                    RateType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    FirstSeenUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Exchanges", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PriceQuotes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ExchangeId = table.Column<int>(type: "integer", nullable: false),
                    Pair = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Buy = table.Column<decimal>(type: "numeric(28,10)", precision: 28, scale: 10, nullable: true),
                    Sell = table.Column<decimal>(type: "numeric(28,10)", precision: 28, scale: 10, nullable: true),
                    RateType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    TimestampUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceQuotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PriceQuotes_Exchanges_ExchangeId",
                        column: x => x.ExchangeId,
                        principalTable: "Exchanges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Exchanges_ExchangeKey",
                table: "Exchanges",
                column: "ExchangeKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PriceQuotes_ExchangeId_TimestampUtc",
                table: "PriceQuotes",
                columns: new[] { "ExchangeId", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PriceQuotes_Pair_TimestampUtc",
                table: "PriceQuotes",
                columns: new[] { "Pair", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PriceQuotes_TimestampUtc",
                table: "PriceQuotes",
                column: "TimestampUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PriceQuotes");

            migrationBuilder.DropTable(
                name: "Exchanges");
        }
    }
}

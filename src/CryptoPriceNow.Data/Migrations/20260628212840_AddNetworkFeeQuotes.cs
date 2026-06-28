using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CryptoPriceNow.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNetworkFeeQuotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NetworkFeeQuotes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Network = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Native = table.Column<decimal>(type: "numeric(28,6)", precision: 28, scale: 6, nullable: false),
                    NativeUnit = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    UsdPerTx = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    TimestampUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NetworkFeeQuotes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NetworkFeeQuotes_Network_TimestampUtc",
                table: "NetworkFeeQuotes",
                columns: new[] { "Network", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_NetworkFeeQuotes_TimestampUtc",
                table: "NetworkFeeQuotes",
                column: "TimestampUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NetworkFeeQuotes");
        }
    }
}

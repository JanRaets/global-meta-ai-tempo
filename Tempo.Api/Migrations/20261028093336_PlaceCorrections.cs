using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tempo.Api.Migrations
{
    /// <inheritdoc />
    public partial class PlaceCorrections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlaceOverrides",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RawText = table.Column<string>(type: "text", nullable: false),
                    PlaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaceOverrides", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlaceResolutionLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RawText = table.Column<string>(type: "text", nullable: false),
                    CandidatesJson = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    ChosenPlaceId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaceResolutionLogs", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlaceOverrides");

            migrationBuilder.DropTable(
                name: "PlaceResolutionLogs");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tempo.Api.Migrations
{
    /// <inheritdoc />
    public partial class ReplacementNudgeCause : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ShiftReason",
                table: "DeparturePlans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ShiftedFromLeaveAt",
                table: "DeparturePlans",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ShiftReason",
                table: "DeparturePlans");

            migrationBuilder.DropColumn(
                name: "ShiftedFromLeaveAt",
                table: "DeparturePlans");
        }
    }
}

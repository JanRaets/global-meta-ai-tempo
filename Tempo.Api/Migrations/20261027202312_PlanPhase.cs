using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tempo.Api.Migrations
{
    /// <inheritdoc />
    public partial class PlanPhase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "LeaveFixLat",
                table: "DeparturePlans",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "LeaveFixLng",
                table: "DeparturePlans",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Phase",
                table: "DeparturePlans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PhaseChangedAt",
                table: "DeparturePlans",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LeaveFixLat",
                table: "DeparturePlans");

            migrationBuilder.DropColumn(
                name: "LeaveFixLng",
                table: "DeparturePlans");

            migrationBuilder.DropColumn(
                name: "Phase",
                table: "DeparturePlans");

            migrationBuilder.DropColumn(
                name: "PhaseChangedAt",
                table: "DeparturePlans");
        }
    }
}

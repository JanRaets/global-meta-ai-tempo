using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tempo.Api.Migrations
{
    /// <inheritdoc />
    public partial class PlanOutcomes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ActualArriveAt",
                table: "DeparturePlans",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LeftAt",
                table: "DeparturePlans",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OutcomeKind",
                table: "DeparturePlans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "OutcomeLateMin",
                table: "DeparturePlans",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActualArriveAt",
                table: "DeparturePlans");

            migrationBuilder.DropColumn(
                name: "LeftAt",
                table: "DeparturePlans");

            migrationBuilder.DropColumn(
                name: "OutcomeKind",
                table: "DeparturePlans");

            migrationBuilder.DropColumn(
                name: "OutcomeLateMin",
                table: "DeparturePlans");
        }
    }
}

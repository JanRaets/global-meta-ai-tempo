using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tempo.Api.Migrations
{
    /// <inheritdoc />
    public partial class ChainedOriginHabits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Context_GapBucket",
                table: "Habits",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "Habits",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Context_GapBucket",
                table: "Habits");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Habits");
        }
    }
}

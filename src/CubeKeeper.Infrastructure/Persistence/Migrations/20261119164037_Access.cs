using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CubeKeeper.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Access : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Blocked",
                table: "Players",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "BlockedAt",
                table: "Players",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AccessRequests",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Xuid = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    RequestedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    DecidedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessRequests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccessRequests_Xuid_Status",
                table: "AccessRequests",
                columns: new[] { "Xuid", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccessRequests");

            migrationBuilder.DropColumn(
                name: "Blocked",
                table: "Players");

            migrationBuilder.DropColumn(
                name: "BlockedAt",
                table: "Players");
        }
    }
}

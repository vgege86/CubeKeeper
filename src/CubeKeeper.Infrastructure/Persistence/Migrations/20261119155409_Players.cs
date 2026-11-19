using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CubeKeeper.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Players : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Players",
                columns: table => new
                {
                    Xuid = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    FirstSeen = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSeen = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Players", x => x.Xuid);
                });

            migrationBuilder.CreateTable(
                name: "PlayerSessions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Xuid = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ConnectedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    DisconnectedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    EndReason = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlayerSessions_Players_Xuid",
                        column: x => x.Xuid,
                        principalTable: "Players",
                        principalColumn: "Xuid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Players_Name",
                table: "Players",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_PlayerSessions_ConnectedAt",
                table: "PlayerSessions",
                column: "ConnectedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PlayerSessions_DisconnectedAt",
                table: "PlayerSessions",
                column: "DisconnectedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PlayerSessions_Xuid",
                table: "PlayerSessions",
                column: "Xuid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlayerSessions");

            migrationBuilder.DropTable(
                name: "Players");
        }
    }
}

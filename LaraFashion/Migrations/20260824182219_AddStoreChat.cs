using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LaraFashion.Migrations
{
    /// <inheritdoc />
    public partial class AddStoreChat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StoreChatSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastActivityAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PhoneNumber = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoreChatSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StoreChatMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Role = table.Column<string>(type: "TEXT", nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DetectedIntent = table.Column<string>(type: "TEXT", nullable: true),
                    ExtractedSize = table.Column<string>(type: "TEXT", nullable: true),
                    AiUsed = table.Column<bool>(type: "INTEGER", nullable: false),
                    AiProvider = table.Column<string>(type: "TEXT", nullable: true),
                    AiModel = table.Column<string>(type: "TEXT", nullable: true),
                    IsFallback = table.Column<bool>(type: "INTEGER", nullable: false),
                    FallbackReason = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoreChatMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StoreChatMessages_StoreChatSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "StoreChatSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StoreChatMessages_DetectedIntent",
                table: "StoreChatMessages",
                column: "DetectedIntent");

            migrationBuilder.CreateIndex(
                name: "IX_StoreChatMessages_IsFallback",
                table: "StoreChatMessages",
                column: "IsFallback");

            migrationBuilder.CreateIndex(
                name: "IX_StoreChatMessages_SessionId_CreatedAtUtc",
                table: "StoreChatMessages",
                columns: new[] { "SessionId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_StoreChatSessions_LastActivityAtUtc",
                table: "StoreChatSessions",
                column: "LastActivityAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StoreChatMessages");

            migrationBuilder.DropTable(
                name: "StoreChatSessions");
        }
    }
}

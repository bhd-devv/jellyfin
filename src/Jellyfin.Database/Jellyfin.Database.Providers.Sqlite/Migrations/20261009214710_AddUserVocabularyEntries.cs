using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Database.Providers.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddUserVocabularyEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserVocabularyEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MediaSourceId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    SubtitleStreamIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    CueIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    WordOffset = table.Column<int>(type: "INTEGER", nullable: false),
                    StartPositionTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    Word = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Lemma = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    PartOfSpeech = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Translation = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Explanation = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    Sentence = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    SentenceTranslation = table.Column<string>(type: "TEXT", nullable: true),
                    DateCreated = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserVocabularyEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserVocabularyEntries_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserVocabularyEntries_UserId_DateCreated",
                table: "UserVocabularyEntries",
                columns: new[] { "UserId", "DateCreated" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserVocabularyEntries");
        }
    }
}

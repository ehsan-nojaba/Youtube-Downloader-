using System;
using Microsoft.EntityFrameworkCore.Migrations;


namespace YtDownloader.Infrastructure.Migrations;
/// <inheritdoc />
public partial class InitialCreate : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "DownloadJobs",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Url = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                VideoTitle = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                Format = table.Column<int>(type: "INTEGER", nullable: false),
                Quality = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                Status = table.Column<int>(type: "INTEGER", nullable: false),
                ProgressPercent = table.Column<int>(type: "INTEGER", nullable: false),
                FilePath = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                ExpiresAt = table.Column<long>(type: "INTEGER", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DownloadJobs", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_DownloadJobs_CreatedAt",
            table: "DownloadJobs",
            column: "CreatedAt");

        migrationBuilder.CreateIndex(
            name: "IX_DownloadJobs_Status_ExpiresAt",
            table: "DownloadJobs",
            columns: new[] { "Status", "ExpiresAt" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "DownloadJobs");
    }
}


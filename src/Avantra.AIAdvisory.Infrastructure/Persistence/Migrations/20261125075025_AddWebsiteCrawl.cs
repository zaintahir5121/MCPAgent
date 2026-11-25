using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Avantra.AIAdvisory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWebsiteCrawl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CrawlConfigJson",
                table: "KnowledgeBases",
                type: "nvarchar(max)",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastCrawledAt",
                table: "KnowledgeBases",
                type: "datetimeoffset",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CrawlConfigJson",
                table: "KnowledgeBases");

            migrationBuilder.DropColumn(
                name: "LastCrawledAt",
                table: "KnowledgeBases");
        }
    }
}

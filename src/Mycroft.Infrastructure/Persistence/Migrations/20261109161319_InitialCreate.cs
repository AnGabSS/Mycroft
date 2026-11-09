using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mycroft.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Categories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Subcategories = table.Column<List<string>>(type: "text[]", nullable: false),
                    IsSystem = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Categories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MerchantPatterns",
                columns: table => new
                {
                    NormalizedSignature = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    MerchantName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Subcategory = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    DefaultPaymentMethod = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    IsRecurring = table.Column<bool>(type: "boolean", nullable: false),
                    ConfidenceScore = table.Column<decimal>(type: "numeric(4,3)", precision: 4, scale: 3, nullable: false),
                    HitCount = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MerchantPatterns", x => x.NormalizedSignature);
                });

            migrationBuilder.CreateTable(
                name: "ReconciliationJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalReferenceId = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    PayloadHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    SourceFormat = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    OriginalFileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    RawFilePayload = table.Column<byte[]>(type: "bytea", nullable: true),
                    CustomCategoriesJson = table.Column<string>(type: "jsonb", nullable: true),
                    CallbackUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    WebhookSecret = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    TotalItems = table.Column<int>(type: "integer", nullable: false),
                    ProcessedItems = table.Column<int>(type: "integer", nullable: false),
                    AliasHits = table.Column<int>(type: "integer", nullable: false),
                    CacheHits = table.Column<int>(type: "integer", nullable: false),
                    FallbackItems = table.Column<int>(type: "integer", nullable: false),
                    WebhookStatus = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    WebhookAttempts = table.Column<int>(type: "integer", nullable: false),
                    NextWebhookRetryAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReconciliationJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TransactionItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    LineIndex = table.Column<int>(type: "integer", nullable: false),
                    ExternalLineId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RawDescription = table.Column<string>(type: "text", nullable: false),
                    NormalizedSignature = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    RawAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    RawDate = table.Column<DateOnly>(type: "date", nullable: true),
                    IsFreeText = table.Column<bool>(type: "boolean", nullable: false),
                    MerchantName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    Currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false, defaultValue: "BRL"),
                    TransactionDate = table.Column<DateOnly>(type: "date", nullable: true),
                    TransactionType = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: true),
                    Category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Subcategory = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    PaymentMethod = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    InstallmentCurrent = table.Column<int>(type: "integer", nullable: true),
                    InstallmentTotal = table.Column<int>(type: "integer", nullable: true),
                    IsRecurring = table.Column<bool>(type: "boolean", nullable: false),
                    ConfidenceScore = table.Column<decimal>(type: "numeric(4,3)", precision: 4, scale: 3, nullable: false),
                    NeedsReview = table.Column<bool>(type: "boolean", nullable: false),
                    ResolutionSource = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransactionItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TransactionItems_ReconciliationJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "ReconciliationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Categories_Name",
                table: "Categories",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationJobs_CreatedAt",
                table: "ReconciliationJobs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationJobs_ExternalReferenceId",
                table: "ReconciliationJobs",
                column: "ExternalReferenceId",
                unique: true,
                filter: "\"ExternalReferenceId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationJobs_PayloadHash_CreatedAt",
                table: "ReconciliationJobs",
                columns: new[] { "PayloadHash", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationJobs_Status",
                table: "ReconciliationJobs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationJobs_WebhookStatus_NextWebhookRetryAt",
                table: "ReconciliationJobs",
                columns: new[] { "WebhookStatus", "NextWebhookRetryAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TransactionItems_JobId_LineIndex",
                table: "TransactionItems",
                columns: new[] { "JobId", "LineIndex" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Categories");

            migrationBuilder.DropTable(
                name: "MerchantPatterns");

            migrationBuilder.DropTable(
                name: "TransactionItems");

            migrationBuilder.DropTable(
                name: "ReconciliationJobs");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace DocuMind.Data.Migrations
{
    /// <summary>Adds document ingestion and conversation tables without altering existing Identity tables.</summary>
    public partial class AddDocumentProcessingAndConversations : Migration
    {
        /// <summary>Creates only new tables, ownership constraints, retry indexes, and the fixed-dimension vector index.</summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Documents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayFileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    UploadedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false, defaultValue: "Pending"),
                    FailureCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Documents", x => x.Id);
                    table.UniqueConstraint("AK_Documents_Id_OwnerId", x => new { x.Id, x.OwnerId });
                    table.CheckConstraint("CK_Documents_FailureCode", "\"FailureCode\" IS NULL OR (\"FailureCode\" IN ('UnsupportedFile', 'ExtractionFailed', 'EmbeddingFailed', 'StorageUnavailable', 'WorkerInterrupted', 'ProcessingFailed'))");
                    table.CheckConstraint("CK_Documents_FailureState", "(\"Status\" = 'Failed') = (\"FailureCode\" IS NOT NULL)");
                    table.CheckConstraint("CK_Documents_FileName", "btrim(\"DisplayFileName\") <> ''");
                    table.CheckConstraint("CK_Documents_Status", "\"Status\" IN ('Pending', 'Processing', 'Ready', 'Failed')");
                    table.CheckConstraint("CK_Documents_StorageKey", "\"StorageKey\" ~ '^[A-Za-z0-9][A-Za-z0-9_-]{15,127}$'");
                    table.ForeignKey(
                        name: "FK_Documents_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Conversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Conversations", x => x.Id);
                    table.CheckConstraint("CK_Conversations_Title", "btrim(\"Title\") <> ''");
                    table.ForeignKey(
                        name: "FK_Conversations_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Conversations_Documents_DocumentId_OwnerId",
                        columns: x => new { x.DocumentId, x.OwnerId },
                        principalTable: "Documents",
                        principalColumns: new[] { "Id", "OwnerId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DocumentChunks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    PageStart = table.Column<int>(type: "integer", nullable: true),
                    PageEnd = table.Column<int>(type: "integer", nullable: true),
                    Embedding = table.Column<Vector>(type: "vector(1536)", nullable: true),
                    EmbeddingModel = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentChunks", x => x.Id);
                    table.CheckConstraint("CK_DocumentChunks_Model", "\"EmbeddingModel\" = 'openai/text-embedding-3-small'");
                    table.CheckConstraint("CK_DocumentChunks_Pages", "(\"PageStart\" IS NULL AND \"PageEnd\" IS NULL) OR (\"PageStart\" IS NOT NULL AND \"PageEnd\" IS NOT NULL AND \"PageStart\" >= 1 AND \"PageEnd\" >= \"PageStart\")");
                    table.CheckConstraint("CK_DocumentChunks_Position", "\"Position\" >= 0");
                    table.CheckConstraint("CK_DocumentChunks_Text", "btrim(\"Text\") <> '' AND char_length(\"Text\") <= 32000");
                    table.CheckConstraint("CK_DocumentChunks_Vector", "\"Embedding\" IS NULL OR vector_norm(\"Embedding\") > 0");
                    table.ForeignKey(
                        name: "FK_DocumentChunks_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProcessingJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false, defaultValue: "Queued"),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 5),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    AvailableAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastFailureCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessingJobs", x => x.Id);
                    table.CheckConstraint("CK_ProcessingJobs_Attempts", "\"MaxAttempts\" BETWEEN 1 AND 10 AND \"AttemptCount\" BETWEEN 0 AND \"MaxAttempts\"");
                    table.CheckConstraint("CK_ProcessingJobs_Completion", "(\"Status\" IN ('Succeeded', 'Failed')) = (\"CompletedAt\" IS NOT NULL)");
                    table.CheckConstraint("CK_ProcessingJobs_Failed", "\"Status\" <> 'Failed' OR \"LastFailureCode\" IS NOT NULL");
                    table.CheckConstraint("CK_ProcessingJobs_FailureCode", "\"LastFailureCode\" IS NULL OR (\"LastFailureCode\" IN ('UnsupportedFile', 'ExtractionFailed', 'EmbeddingFailed', 'StorageUnavailable', 'WorkerInterrupted', 'ProcessingFailed'))");
                    table.CheckConstraint("CK_ProcessingJobs_Lease", "(\"Status\" = 'Running' AND \"LeaseId\" IS NOT NULL AND \"LeaseId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"LeaseExpiresAt\" IS NOT NULL AND \"StartedAt\" IS NOT NULL AND \"LeaseExpiresAt\" > \"StartedAt\" AND \"AttemptCount\" > 0) OR (\"Status\" <> 'Running' AND \"LeaseId\" IS NULL AND \"LeaseExpiresAt\" IS NULL)");
                    table.CheckConstraint("CK_ProcessingJobs_Status", "\"Status\" IN ('Queued', 'Running', 'Succeeded', 'Failed')");
                    table.ForeignKey(
                        name: "FK_ProcessingJobs_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Messages", x => x.Id);
                    table.CheckConstraint("CK_Messages_Content", "btrim(\"Content\") <> '' AND char_length(\"Content\") <= 64000");
                    table.CheckConstraint("CK_Messages_Position", "\"Position\" >= 0");
                    table.CheckConstraint("CK_Messages_Role", "\"Role\" IN ('User', 'Assistant', 'System')");
                    table.ForeignKey(
                        name: "FK_Messages_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_DocumentId_OwnerId",
                table: "Conversations",
                columns: new[] { "DocumentId", "OwnerId" });

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_OwnerId_CreatedAt_Id",
                table: "Conversations",
                columns: new[] { "OwnerId", "CreatedAt", "Id" },
                descending: new[] { false, true, false });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentChunks_DocumentId_Position",
                table: "DocumentChunks",
                columns: new[] { "DocumentId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentChunks_Embedding",
                table: "DocumentChunks",
                column: "Embedding",
                filter: "\"Embedding\" IS NOT NULL")
                .Annotation("Npgsql:IndexMethod", "hnsw")
                .Annotation("Npgsql:IndexOperators", new[] { "vector_cosine_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_Documents_OwnerId_UploadedAt_Id",
                table: "Documents",
                columns: new[] { "OwnerId", "UploadedAt", "Id" },
                descending: new[] { false, true, false });

            migrationBuilder.CreateIndex(
                name: "IX_Documents_StorageKey",
                table: "Documents",
                column: "StorageKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ConversationId_Position",
                table: "Messages",
                columns: new[] { "ConversationId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProcessingJobs_AvailableAt_CreatedAt",
                table: "ProcessingJobs",
                columns: new[] { "AvailableAt", "CreatedAt" },
                filter: "\"Status\" = 'Queued'");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessingJobs_DocumentId",
                table: "ProcessingJobs",
                column: "DocumentId",
                unique: true,
                filter: "\"Status\" IN ('Queued', 'Running')");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessingJobs_LeaseExpiresAt",
                table: "ProcessingJobs",
                column: "LeaseExpiresAt",
                filter: "\"Status\" = 'Running'");
        }

        /// <summary>Removes this feature's tables only; rolling back discards their data and is not used for local verification.</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DocumentChunks");

            migrationBuilder.DropTable(
                name: "Messages");

            migrationBuilder.DropTable(
                name: "ProcessingJobs");

            migrationBuilder.DropTable(
                name: "Conversations");

            migrationBuilder.DropTable(
                name: "Documents");
        }
    }
}

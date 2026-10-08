using DocuMind.Data;
using DocuMind.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Pgvector;
using Pgvector.EntityFrameworkCore;

// The default mode creates a disposable database; the explicit local mode only applies reviewed additive migrations.
return await DataFlowChecks.RunAsync(args);

/// <summary>Verifies relational/vector behavior on PostgreSQL and can safely migrate the identified local development database.</summary>
internal static class DataFlowChecks
{
    private const string InitialMigration = "20261007065231_InitialIdentity";
    private const string DataMigration = "20261008011434_AddDocumentProcessingAndConversations";
    private static int assertions;
    private static readonly string[] IdentityTables =
        ["AspNetUsers", "AspNetRoles", "AspNetUserClaims", "AspNetRoleClaims", "AspNetUserLogins", "AspNetUserRoles", "AspNetUserTokens"];

    /// <summary>Requires a privately supplied connection and deletes only the uniquely named test database created here.</summary>
    public static async Task<int> RunAsync(string[] arguments)
    {
        var connectionString = Environment.GetEnvironmentVariable("DOCUMIND_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine("Set DOCUMIND_TEST_CONNECTION privately. Default checks require CREATE DATABASE permission.");
            return 1;
        }
        if (arguments.Length > 1 || (arguments.Length == 1 && arguments[0] != "--apply-local-development"))
        {
            Console.Error.WriteLine("Supported option: --apply-local-development. Without an option, only a disposable database is modified.");
            return 1;
        }

        var testName = "documind_data_verify_" + Guid.NewGuid().ToString("N");
        var created = false;
        await using var admin = new NpgsqlConnection(connectionString);
        try
        {
            await admin.OpenAsync();
            if (arguments.Length == 1)
            {
                await ApplyLocalDevelopmentAsync(connectionString, admin);
            }
            else
            {
                // The identifier contains only our fixed prefix and a generated GUID, never caller input.
                await using (var command = new NpgsqlCommand($"CREATE DATABASE \"{testName}\"", admin))
                    await command.ExecuteNonQueryAsync();
                created = true;
                var testConnection = new NpgsqlConnectionStringBuilder(connectionString) { Database = testName }.ConnectionString;
                await VerifyAsync(testConnection);
            }
            Console.WriteLine($"PASS: {assertions} data assertions.");
            return 0;
        }
        catch (Exception exception)
        {
            // PostgreSQL errors may contain user content or connection details; report types/codes rather than raw messages.
            var postgres = exception as PostgresException ?? exception.InnerException as PostgresException;
            Console.Error.WriteLine($"FAIL: {exception.GetType().Name}; SQLSTATE={postgres?.SqlState ?? "n/a"}.");
            if (exception is CheckFailure failure) Console.Error.WriteLine(failure.Message);
            return 1;
        }
        finally
        {
            if (created)
            {
                await using var drop = new NpgsqlCommand($"DROP DATABASE \"{testName}\" WITH (FORCE)", admin);
                await drop.ExecuteNonQueryAsync();
                Console.WriteLine("Removed this run's disposable database.");
            }
        }
    }

    /// <summary>Guards the exact local target and compares all Identity rows before and after the additive migration.</summary>
    private static async Task ApplyLocalDevelopmentAsync(string connectionString, NpgsqlConnection connection)
    {
        var target = new NpgsqlConnectionStringBuilder(connectionString);
        Check(target.Host == "127.0.0.1" && target.Port == 5433 && target.Database == "documind"
            && target.Username == "documind", "Local apply is restricted to documind/documind at 127.0.0.1:5433.");
        await using (var identity = new NpgsqlCommand("SELECT current_database() = 'documind' AND current_user = 'documind'", connection))
            Check((bool)(await identity.ExecuteScalarAsync())!, "The server identifies the expected development database and role.");
        await using var database = OpenContext(connectionString);
        var applied = (await database.Database.GetAppliedMigrationsAsync()).ToArray();
        Check(applied.SequenceEqual([InitialMigration]) || applied.SequenceEqual([InitialMigration, DataMigration]),
            "The target has only the expected migration history.");
        var pending = (await database.Database.GetPendingMigrationsAsync()).ToArray();
        Check(pending.Length == 0 || pending.SequenceEqual([DataMigration]), "Only the reviewed data migration may be applied.");
        var before = await IdentityFingerprintAsync(connection);
        await database.Database.MigrateAsync();
        Check(before == await IdentityFingerprintAsync(connection), "All seven Identity tables retain identical rows.");
        Check(!database.Database.HasPendingModelChanges(), "The migrated model matches its snapshot.");
        Check(!(await database.Database.GetPendingMigrationsAsync()).Any(), "The local development database has no pending migrations.");
        Console.WriteLine("Local development data migration verified; Identity rows preserved.");
    }

    /// <summary>Exercises schema preservation, ownership, vector dimensions/search, retry recovery, and optimistic concurrency.</summary>
    private static async Task VerifyAsync(string connectionString)
    {
        await using var database = OpenContext(connectionString);
        // Seed Identity before applying the new migration, so preservation is tested rather than inferred from empty tables.
        await database.GetService<IMigrator>().MigrateAsync(InitialMigration);
        var owner = new ApplicationUser { UserName = "owner-fixture", NormalizedUserName = "OWNER-FIXTURE" };
        var otherOwner = new ApplicationUser { UserName = "other-fixture", NormalizedUserName = "OTHER-FIXTURE" };
        database.Users.AddRange(owner, otherOwner);
        await database.SaveChangesAsync();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var before = await IdentityFingerprintAsync(connection);
        await database.Database.MigrateAsync();
        Check(before == await IdentityFingerprintAsync(connection), "Applying the new migration preserves existing Identity rows.");
        Check(!database.Database.HasPendingModelChanges(), "The EF model matches the generated migration snapshot.");

        var document = NewDocument(owner.Id);
        var otherDocument = NewDocument(otherOwner.Id);
        var conversation = new Conversation { OwnerId = owner.Id, DocumentId = document.Id, Title = "Fixture discussion" };
        var chunk = new DocumentChunk
        {
            DocumentId = document.Id, Position = 0, Text = "Owner's document text", PageStart = 1, PageEnd = 2,
            Embedding = MakeVector(0)
        };
        var otherChunk = new DocumentChunk { DocumentId = otherDocument.Id, Position = 0, Text = "Other owner's text", Embedding = MakeVector(0) };
        var message = new Message { ConversationId = conversation.Id, Position = 0, Role = MessageRole.User, Content = "Fixture question" };
        var clock = DateTimeOffset.UtcNow;
        var job = new ProcessingJob { DocumentId = document.Id, AvailableAt = clock, MaxAttempts = 3 };
        database.Documents.AddRange(document, otherDocument);
        database.Conversations.Add(conversation);
        database.DocumentChunks.AddRange(chunk, otherChunk);
        database.Messages.Add(message);
        database.ProcessingJobs.Add(job);
        await database.SaveChangesAsync();
        database.ChangeTracker.Clear();
        var roundTrip = await database.DocumentChunks.SingleAsync(item => item.Id == chunk.Id);
        Check(roundTrip.Embedding!.ToArray().Length == EmbeddingContract.Dimensions
            && roundTrip.EmbeddingModel == EmbeddingContract.Model && roundTrip.PageEnd == 2,
            "Vector dimensions, model identity, and optional page metadata round-trip.");
        var queryVector = MakeVector(0);
        // Ownership is included in SQL before ranking; a global similarity search could leak another user's text.
        var matches = await database.DocumentChunks.AsNoTracking()
            .Where(item => item.Document.OwnerId == owner.Id
                && item.Document.Status == DocumentProcessingStatus.Pending
                && item.EmbeddingModel == EmbeddingContract.Model && item.Embedding != null)
            .OrderBy(item => item.Embedding!.CosineDistance(queryVector)).Select(item => item.Id).ToListAsync();
        Check(matches.SequenceEqual([chunk.Id]), "An owner-scoped cosine query excludes the other owner's equally similar chunk.");
        Check(await database.Conversations.Where(item => item.OwnerId == owner.Id)
            .SelectMany(item => item.Messages).CountAsync() == 1, "Messages can be queried through their owner's conversation.");

        await RejectAsync(connectionString, context => context.Conversations.Add(
            new Conversation { OwnerId = otherOwner.Id, DocumentId = document.Id }), "23503", "Cross-owner document-scoped conversations are rejected.");
        await RejectAsync(connectionString, context => context.Documents.Add(NewDocument(Guid.NewGuid())),
            "23503", "Documents require an existing Identity owner.");
        await RejectAsync(connectionString, context => context.DocumentChunks.Add(
            new DocumentChunk { DocumentId = Guid.NewGuid(), Position = 0, Text = "Orphan" }), "23503", "Chunks require an existing document.");
        await RejectAsync(connectionString, context => context.Messages.Add(
            new Message { ConversationId = Guid.NewGuid(), Content = "Orphan", Role = MessageRole.User }), "23503", "Messages require a conversation.");
        await RejectAsync(connectionString, context => context.Documents.Add(
            new Document { OwnerId = owner.Id, StorageKey = document.StorageKey, DisplayFileName = "duplicate.pdf" }),
            "23505", "Internal storage keys are unique.");
        await RejectAsync(connectionString, context => context.Documents.Add(
            new Document { OwnerId = owner.Id, StorageKey = "../unsafe/path", DisplayFileName = "fixture.pdf" }),
            "23514", "Storage keys cannot contain filesystem paths.");
        await RejectAsync(connectionString, context => context.Documents.Add(
            new Document { OwnerId = owner.Id, StorageKey = Guid.NewGuid().ToString("N"), DisplayFileName = "failed.pdf", Status = DocumentProcessingStatus.Failed }),
            "23514", "Failed documents must supply a safe failure code.");
        await RejectChunkAsync(connectionString, document.Id, item => item.Position = 0, "23505", "Chunk positions are unique within a document.");
        await RejectChunkAsync(connectionString, document.Id, item => item.PageStart = 2, "23514", "Incomplete page ranges are rejected.");
        await RejectChunkAsync(connectionString, document.Id, item => item.Position = -1, "23514", "Negative chunk positions are rejected.");
        await RejectChunkAsync(connectionString, document.Id, item => item.Text = "  ", "23514", "Empty chunk text is rejected.");
        await RejectChunkAsync(connectionString, document.Id, item => item.EmbeddingModel = "different/model", "23514", "Incompatible embedding model identities are rejected.");
        await RejectChunkAsync(connectionString, document.Id, item => item.Embedding = new Vector(new float[384]),
            "22000", "Embedding vectors with the wrong dimensions are rejected.");
        await RejectChunkAsync(connectionString, document.Id, item => item.Embedding = new Vector(new float[EmbeddingContract.Dimensions]),
            "23514", "Zero vectors cannot enter the cosine search space.");
        await RejectAsync(connectionString, context => context.Messages.Add(
            new Message { ConversationId = conversation.Id, Position = 0, Role = MessageRole.Assistant, Content = "Duplicate order" }),
            "23505", "Message positions are unique within a conversation.");
        await RejectAsync(connectionString, context => context.Messages.Add(
            new Message { ConversationId = conversation.Id, Position = 1, Role = (MessageRole)999, Content = "Invalid role" }),
            "23514", "Unknown message roles are rejected.");
        await RejectAsync(connectionString, context => context.ProcessingJobs.Add(new ProcessingJob { DocumentId = document.Id }),
            "23505", "Only one queued or running job is allowed per document.");
        await RejectAsync(connectionString, context => context.Documents.Remove(new Document { Id = document.Id, OwnerId = owner.Id }),
            "23503", "Deleting a document referenced by a conversation requires an explicit retention decision.");

        await VerifyLeasesAsync(connectionString, job.Id, clock);

        // Document cascades remove derived chunks/jobs; conversation cascades remove messages.
        await using (var cleanup = OpenContext(connectionString))
        {
            cleanup.Conversations.Remove(await cleanup.Conversations.SingleAsync(item => item.Id == conversation.Id));
            await cleanup.SaveChangesAsync();
            Check(!await cleanup.Messages.AnyAsync(item => item.ConversationId == conversation.Id), "Conversation deletion cascades to its messages.");
            cleanup.Documents.Remove(await cleanup.Documents.SingleAsync(item => item.Id == document.Id));
            await cleanup.SaveChangesAsync();
            Check(!await cleanup.DocumentChunks.AnyAsync(item => item.DocumentId == document.Id)
                && !await cleanup.ProcessingJobs.AnyAsync(item => item.DocumentId == document.Id),
                "Document deletion cascades to chunks and processing jobs.");
            Check(await cleanup.Documents.AnyAsync(item => item.Id == otherDocument.Id), "Deleting one document preserves another owner's document.");
        }
        await using var index = new NpgsqlCommand("""
            SELECT count(*) FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'DocumentChunks'
                AND indexdef LIKE '%USING hnsw%' AND indexdef LIKE '%vector_cosine_ops%'
            """, connection);
        Check((long)(await index.ExecuteScalarAsync())! == 1, "The migration creates the HNSW cosine vector index.");
    }

    /// <summary>Uses two real contexts to verify xmin claims and prevent a stalled worker from saving after recovery.</summary>
    private static async Task VerifyLeasesAsync(string connectionString, Guid jobId, DateTimeOffset clock)
    {
        await using var first = OpenContext(connectionString);
        await using var second = OpenContext(connectionString);
        var firstJob = await first.ProcessingJobs.SingleAsync(item => item.Id == jobId);
        var secondJob = await second.ProcessingJobs.SingleAsync(item => item.Id == jobId);
        var staleLease = Guid.NewGuid();
        Check(firstJob.TryClaim(staleLease, clock, TimeSpan.FromMinutes(1))
            && secondJob.TryClaim(Guid.NewGuid(), clock, TimeSpan.FromMinutes(1)), "Both workers can tentatively claim their loaded queued snapshot.");
        await first.SaveChangesAsync();
        try
        {
            await second.SaveChangesAsync();
            throw new CheckFailure("A competing worker unexpectedly saved its claim.");
        }
        catch (DbUpdateConcurrencyException)
        {
            Check(true, "PostgreSQL xmin allows only one worker to persist its claim.");
        }
        await using var recovery = OpenContext(connectionString);
        var recovered = await recovery.ProcessingJobs.SingleAsync(item => item.Id == jobId);
        Check(recovered.RecoverExpiredLease(clock.AddMinutes(1)), "An interrupted worker's expired persisted lease is recoverable.");
        await recovery.SaveChangesAsync();
        Check(recovered.TryClaim(Guid.NewGuid(), clock.AddMinutes(1), TimeSpan.FromMinutes(1)), "Recovered jobs can be reclaimed within the retry budget.");
        await recovery.SaveChangesAsync();

        // Even a stale in-memory clock/snapshot cannot overwrite the newer claim in PostgreSQL.
        Check(firstJob.TryComplete(staleLease, clock.AddSeconds(30)), "The stale worker still has its original in-memory snapshot.");
        try
        {
            await first.SaveChangesAsync();
            throw new CheckFailure("A stale worker unexpectedly overwrote a recovered job.");
        }
        catch (DbUpdateConcurrencyException)
        {
            Check(true, "A stale completion conflicts with the replacement lease.");
        }
        Check(!recovered.TryComplete(staleLease, clock.AddMinutes(1)), "Lease identity fences the old worker in a refreshed snapshot.");
        Check(recovered.TryFail(recovered.LeaseId!.Value, clock.AddMinutes(1), ProcessingFailureCode.EmbeddingFailed, TimeSpan.FromSeconds(5)),
            "An active worker can schedule a delayed retry.");
        await recovery.SaveChangesAsync();
        Check(!recovered.TryClaim(Guid.NewGuid(), clock.AddMinutes(1), TimeSpan.FromMinutes(1)), "Persisted retry delay prevents immediate reclaim.");
        Check(recovered.TryClaim(Guid.NewGuid(), clock.AddMinutes(1).AddSeconds(5), TimeSpan.FromMinutes(1)), "The third attempt can start after backoff.");
        await recovery.SaveChangesAsync();
        Check(recovered.RecoverExpiredLease(clock.AddMinutes(2).AddSeconds(5))
            && recovered.Status == ProcessingJobStatus.Failed && recovered.LastFailureCode == ProcessingFailureCode.WorkerInterrupted,
            "A crash on the final attempt becomes terminal safe failure.");
        await recovery.SaveChangesAsync();
        await using var terminal = OpenContext(connectionString);
        var persisted = await terminal.ProcessingJobs.SingleAsync(item => item.Id == jobId);
        Check(persisted.Status == ProcessingJobStatus.Failed && persisted.LeaseId is null && persisted.CompletedAt is not null,
            "Terminal retry state satisfies PostgreSQL constraints and survives reload.");
        terminal.ProcessingJobs.Add(new ProcessingJob { DocumentId = persisted.DocumentId });
        await terminal.SaveChangesAsync();
        Check(true, "Terminal job history permits a new queued ingestion request.");
    }

    /// <summary>Confirms invalid chunk mutations fail through database constraints, not just application validation.</summary>
    private static Task RejectChunkAsync(string connectionString, Guid documentId, Action<DocumentChunk> change, string state, string description)
        => RejectAsync(connectionString, context =>
        {
            var chunk = new DocumentChunk { DocumentId = documentId, Position = 10, Text = "Constraint fixture" };
            change(chunk);
            context.DocumentChunks.Add(chunk);
        }, state, description);

    /// <summary>A fresh context isolates each rejected write; unexpected SQL errors never count as a passing check.</summary>
    private static async Task RejectAsync(string connectionString, Action<DocuMindDbContext> change, string state, string description)
    {
        await using var context = OpenContext(connectionString);
        change(context);
        try
        {
            await context.SaveChangesAsync();
            throw new CheckFailure(description + " The invalid write unexpectedly succeeded.");
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException postgres && postgres.SqlState == state)
        {
            Check(true, description);
        }
    }

    /// <summary>Hashes rows inside PostgreSQL without exposing hashes, account fields, or credentials in output.</summary>
    private static async Task<string> IdentityFingerprintAsync(NpgsqlConnection connection)
    {
        var fingerprints = new List<string>();
        foreach (var table in IdentityTables)
        {
            // Table names come exclusively from the fixed allowlist above; row ordering makes the fingerprint deterministic.
            await using var command = new NpgsqlCommand($"""
                SELECT count(*)::text || ':' || coalesce(md5(string_agg(to_jsonb(t)::text, '' ORDER BY to_jsonb(t)::text)), '')
                FROM public."{table}" t
                """, connection);
            fingerprints.Add((string)(await command.ExecuteScalarAsync())!);
        }
        return string.Join("|", fingerprints);
    }

    /// <summary>Uses exactly the same provider/type mapping as the API and migration factory.</summary>
    private static DocuMindDbContext OpenContext(string connectionString)
        => new(new DbContextOptionsBuilder<DocuMindDbContext>().UseDocuMindPostgreSql(connectionString).Options);

    /// <summary>Creates valid fixture metadata with an opaque key rather than a real filesystem path.</summary>
    private static Document NewDocument(Guid ownerId)
        => new() { OwnerId = ownerId, DisplayFileName = "fixture.pdf", StorageKey = Guid.NewGuid().ToString("N") };

    /// <summary>A nonzero one-hot vector makes cosine matching deterministic without contacting an embedding provider.</summary>
    private static Vector MakeVector(int position)
    {
        var values = new float[EmbeddingContract.Dimensions];
        values[position] = 1;
        return new Vector(values);
    }

    /// <summary>Prints descriptions controlled by this checker; data and exception details are never interpolated.</summary>
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new CheckFailure(description);
        assertions++;
        Console.WriteLine("PASS: " + description);
    }

    /// <summary>Distinguishes safe assertion text from infrastructure exceptions that might contain private data.</summary>
    private sealed class CheckFailure(string message) : Exception(message);
}

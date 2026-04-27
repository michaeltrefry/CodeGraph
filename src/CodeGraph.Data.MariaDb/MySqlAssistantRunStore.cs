using System.Data;
using System.Security.Cryptography;
using System.Text;
using CodeGraph.Data;
using CodeGraph.Models.Requests;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace CodeGraph.Data.MariaDb;

public sealed class MySqlAssistantRunStore(IOptions<MariaDbStorageOptions> optionsAccessor) : IAssistantRunStore
{
    private const int AssistantChatLockTimeoutSeconds = 10;
    private readonly MariaDbStorageOptions options = optionsAccessor.Value;

    static MySqlAssistantRunStore()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public Task<AssistantRunCreateResult> CreateAssistantRunAsync(
        AssistantRunCreateRequest request,
        CancellationToken ct = default)
    {
        return WithDeadlockRetryAsync(async () =>
        {
            await using var conn = await GetOpenConnectionAsync();
            var lockName = BuildAssistantChatLockName(request.Username, request.ChatId);
            var lockAcquired = await conn.ExecuteScalarAsync<long?>(
                "SELECT GET_LOCK(@lockName, @timeoutSeconds)",
                new { lockName, timeoutSeconds = AssistantChatLockTimeoutSeconds });

            if (lockAcquired != 1)
            {
                throw new InvalidOperationException("Failed to acquire the assistant chat reconciliation lock.");
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
                {
                    var existingRun = await conn.QuerySingleOrDefaultAsync<AssistantRunEntity>(
                        """
                        SELECT *
                        FROM assistant_runs
                        WHERE username = @Username
                          AND idempotency_key = @IdempotencyKey
                        LIMIT 1
                        """,
                        new { request.Username, request.IdempotencyKey });

                    if (existingRun is not null)
                    {
                        if (string.Equals(existingRun.RequestHash, request.RequestHash, StringComparison.Ordinal))
                        {
                            return new AssistantRunCreateResult(existingRun, ReusedExisting: true);
                        }

                        return new AssistantRunCreateResult(
                            Run: null,
                            Conflict: new AssistantRunConflict(
                                "idempotency_key_mismatch",
                                "This idempotency key was already used for a different assistant request.",
                                ExistingRunId: existingRun.Id));
                    }
                }

                await using var tx = await conn.BeginTransactionAsync(ct);

                var storedMessages = (await conn.QueryAsync<AssistantChatMessageEntity>(
                    """
                    SELECT *
                    FROM assistant_chat_messages
                    WHERE username = @Username
                      AND chat_id = @ChatId
                    ORDER BY message_index
                    FOR UPDATE
                    """,
                    new { request.Username, request.ChatId },
                    tx)).ToList();

                var historyConflict = ValidateSubmittedHistory(request.History, request.Question, storedMessages);
                if (historyConflict is not null)
                {
                    await tx.RollbackAsync(ct);
                    return new AssistantRunCreateResult(Run: null, Conflict: historyConflict);
                }

                var activeRunId = await conn.QuerySingleOrDefaultAsync<long?>(
                    """
                    SELECT id
                    FROM assistant_runs
                    WHERE username = @Username
                      AND chat_id = @ChatId
                      AND status IN ('queued', 'running')
                    ORDER BY id DESC
                    LIMIT 1
                    FOR UPDATE
                    """,
                    new { request.Username, request.ChatId },
                    tx);

                if (activeRunId is not null)
                {
                    await tx.RollbackAsync(ct);
                    return new AssistantRunCreateResult(
                        Run: null,
                        Conflict: new AssistantRunConflict(
                            "active_run_exists",
                            "Another assistant run is already active for this chat.",
                            ExistingRunId: activeRunId));
                }

                var messageIndexEnd = storedMessages.Count;
                var run = new AssistantRunEntity
                {
                    ChatId = request.ChatId,
                    Username = request.Username,
                    Status = "queued",
                    Question = request.Question,
                    Context = request.Context,
                    ProviderRequested = request.ProviderRequested,
                    ModelRequested = request.ModelRequested,
                    MessageIndexStart = 0,
                    MessageIndexEnd = messageIndexEnd,
                    IdempotencyKey = request.IdempotencyKey,
                    RequestHash = request.RequestHash,
                    CreatedAt = request.CreatedAt,
                    LastSequence = 0
                };

                run.Id = await conn.QuerySingleAsync<long>(
                    """
                    INSERT INTO assistant_runs
                        (chat_id, username, status, question, context, history_json,
                         provider_requested, model_requested, provider_used, model_used,
                         final_answer, warnings_json, error, message_index_start, message_index_end,
                         idempotency_key, request_hash, execution_state_json, created_at, started_at, completed_at, last_sequence)
                    VALUES
                        (@ChatId, @Username, @Status, @Question, @Context, NULL,
                         @ProviderRequested, @ModelRequested, @ProviderUsed, @ModelUsed,
                         @FinalAnswer, @WarningsJson, @Error, @MessageIndexStart, @MessageIndexEnd,
                         @IdempotencyKey, @RequestHash, @ExecutionStateJson, @CreatedAt, @StartedAt, @CompletedAt, @LastSequence);
                    SELECT LAST_INSERT_ID();
                    """,
                    run,
                    tx);

                await InsertAssistantChatMessageAsync(
                    conn,
                    tx,
                    new AssistantChatMessageEntity
                    {
                        Username = request.Username,
                        ChatId = request.ChatId,
                        MessageIndex = messageIndexEnd,
                        Role = "user",
                        Content = request.Question,
                        SourceRunId = run.Id,
                        CreatedAt = request.CreatedAt
                    });

                await tx.CommitAsync(ct);
                return new AssistantRunCreateResult(run);
            }
            finally
            {
                await conn.ExecuteAsync("DO RELEASE_LOCK(@lockName)", new { lockName });
            }
        });
    }

    public async Task UpdateAssistantRunStatusAsync(
        long runId,
        string status,
        string? finalAnswer = null,
        string? warningsJson = null,
        DateTime? completedAt = null,
        string? error = null,
        string? providerUsed = null,
        string? modelUsed = null)
    {
        await using var conn = await GetOpenConnectionAsync();

        await conn.ExecuteAsync(
            """
            UPDATE assistant_runs SET
                status = @status,
                final_answer = COALESCE(@finalAnswer, final_answer),
                warnings_json = COALESCE(@warningsJson, warnings_json),
                started_at = CASE
                    WHEN started_at IS NULL AND @status IN ('running', 'completed', 'failed', 'interrupted')
                    THEN UTC_TIMESTAMP(3) ELSE started_at END,
                completed_at = COALESCE(@completedAt, completed_at),
                error = COALESCE(@error, error),
                provider_used = COALESCE(@providerUsed, provider_used),
                model_used = COALESCE(@modelUsed, model_used),
                execution_state_json = CASE
                    WHEN @status IN ('completed', 'failed', 'cancelled', 'interrupted')
                    THEN NULL ELSE execution_state_json END,
                execution_owner = CASE
                    WHEN @status IN ('completed', 'failed', 'cancelled', 'interrupted')
                    THEN NULL ELSE execution_owner END,
                lease_expires_at = CASE
                    WHEN @status IN ('completed', 'failed', 'cancelled', 'interrupted')
                    THEN NULL ELSE lease_expires_at END,
                cancel_requested_at = CASE
                    WHEN @status IN ('completed', 'failed', 'cancelled', 'interrupted')
                    THEN NULL ELSE cancel_requested_at END
            WHERE id = @runId
              AND status IN ('queued', 'running')
            """,
            new { runId, status, finalAnswer, warningsJson, completedAt, error, providerUsed, modelUsed });
    }

    public async Task MarkAssistantRunCompletedAsync(
        long runId,
        string? finalAnswer = null,
        string? warningsJson = null,
        DateTime? completedAt = null,
        string? providerUsed = null,
        string? modelUsed = null)
    {
        await using var conn = await GetOpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        var run = await conn.QuerySingleOrDefaultAsync<AssistantRunEntity>(
            "SELECT * FROM assistant_runs WHERE id = @runId FOR UPDATE",
            new { runId },
            tx);

        if (run is null)
        {
            await tx.RollbackAsync();
            throw new InvalidOperationException($"Assistant run '{runId}' was not found.");
        }

        if (run.Status is "completed" or "failed" or "cancelled" or "interrupted")
        {
            await tx.RollbackAsync();
            return;
        }

        var terminalAt = completedAt ?? DateTime.UtcNow;
        await conn.ExecuteAsync(
            """
            UPDATE assistant_runs SET
                status = 'completed',
                final_answer = COALESCE(@finalAnswer, final_answer),
                warnings_json = COALESCE(@warningsJson, warnings_json),
                started_at = CASE WHEN started_at IS NULL THEN UTC_TIMESTAMP(3) ELSE started_at END,
                completed_at = @terminalAt,
                provider_used = COALESCE(@providerUsed, provider_used),
                model_used = COALESCE(@modelUsed, model_used),
                execution_state_json = NULL,
                execution_owner = NULL,
                lease_expires_at = NULL,
                cancel_requested_at = NULL
            WHERE id = @runId
              AND status IN ('queued', 'running')
            """,
            new { runId, finalAnswer, warningsJson, terminalAt, providerUsed, modelUsed },
            tx);

        if (!string.IsNullOrWhiteSpace(finalAnswer))
        {
            await InsertAssistantChatMessageAsync(
                conn,
                tx,
                new AssistantChatMessageEntity
                {
                    Username = run.Username,
                    ChatId = run.ChatId,
                    MessageIndex = run.MessageIndexEnd + 1,
                    Role = "assistant",
                    Content = finalAnswer,
                    SourceRunId = runId,
                    CreatedAt = terminalAt
                });
        }

        await tx.CommitAsync();
    }

    public async Task<AssistantRunEntity?> GetAssistantRunAsync(long runId)
    {
        await using var conn = await GetOpenConnectionAsync();
        return await conn.QuerySingleOrDefaultAsync<AssistantRunEntity>(
            "SELECT * FROM assistant_runs WHERE id = @runId",
            new { runId });
    }

    public async Task<IReadOnlyList<AssistantRunEntity>> GetAssistantRunsByStatusAsync(IReadOnlyList<string> statuses)
    {
        if (statuses.Count == 0)
        {
            return [];
        }

        await using var conn = await GetOpenConnectionAsync();
        var results = await conn.QueryAsync<AssistantRunEntity>(
            "SELECT * FROM assistant_runs WHERE status IN @statuses",
            new { statuses });
        return results.ToList();
    }

    public async Task<AssistantRunEntity?> TryClaimAssistantRunAsync(
        long runId,
        string ownerId,
        DateTime leaseExpiresAt,
        CancellationToken ct = default)
    {
        await using var conn = await GetOpenConnectionAsync();

        var claimed = await conn.ExecuteAsync(
            """
            UPDATE assistant_runs
            SET execution_owner = @ownerId,
                lease_expires_at = @leaseExpiresAt
            WHERE id = @runId
              AND status IN ('queued', 'running')
              AND completed_at IS NULL
              AND (
                    execution_owner IS NULL
                 OR execution_owner = @ownerId
                 OR lease_expires_at IS NULL
                 OR lease_expires_at < UTC_TIMESTAMP(3)
              )
            """,
            new { runId, ownerId, leaseExpiresAt });

        return claimed == 0 ? null : await GetAssistantRunAsync(runId);
    }

    public async Task<AssistantRunLeaseRenewalResult> RenewAssistantRunLeaseAsync(
        long runId,
        string ownerId,
        DateTime leaseExpiresAt,
        CancellationToken ct = default)
    {
        await using var conn = await GetOpenConnectionAsync();

        var renewed = await conn.ExecuteAsync(
            """
            UPDATE assistant_runs
            SET lease_expires_at = @leaseExpiresAt
            WHERE id = @runId
              AND status IN ('queued', 'running')
              AND execution_owner = @ownerId
            """,
            new { runId, ownerId, leaseExpiresAt });

        if (renewed == 0)
        {
            return new AssistantRunLeaseRenewalResult(false, false);
        }

        var cancelRequested = await conn.ExecuteScalarAsync<DateTime?>(
            "SELECT cancel_requested_at FROM assistant_runs WHERE id = @runId AND execution_owner = @ownerId LIMIT 1",
            new { runId, ownerId });

        return new AssistantRunLeaseRenewalResult(true, cancelRequested is not null);
    }

    public async Task RequestAssistantRunCancellationAsync(
        long runId,
        string username,
        DateTime requestedAt,
        CancellationToken ct = default)
    {
        await using var conn = await GetOpenConnectionAsync();
        await conn.ExecuteAsync(
            """
            UPDATE assistant_runs
            SET cancel_requested_at = COALESCE(cancel_requested_at, @requestedAt)
            WHERE id = @runId
              AND username = @username
              AND status IN ('queued', 'running')
            """,
            new { runId, username, requestedAt });
    }

    public async Task SaveAssistantRunProgressAsync(
        long runId,
        AssistantRunProgressUpdate progress,
        CancellationToken ct = default)
    {
        await using var conn = await GetOpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync(ct);

        foreach (var evt in progress.Events.OrderBy(evt => evt.Sequence))
        {
            var inserted = await conn.ExecuteAsync(
                """
                INSERT INTO assistant_run_events
                    (run_id, sequence, type, content_json, created_at)
                SELECT @RunId, @Sequence, @Type, @ContentJson, @CreatedAt
                FROM assistant_runs
                WHERE id = @RunId
                  AND status IN ('queued', 'running')
                """,
                evt,
                tx);

            if (inserted == 0)
            {
                await tx.RollbackAsync(ct);
                throw new InvalidOperationException($"Assistant run '{evt.RunId}' is no longer active.");
            }
        }

        var lastSequence = progress.Events.Count == 0 ? (long?)null : progress.Events.Max(evt => evt.Sequence);
        await conn.ExecuteAsync(
            """
            UPDATE assistant_runs
            SET last_sequence = CASE
                    WHEN @lastSequence IS NULL THEN last_sequence
                    WHEN last_sequence < @lastSequence THEN @lastSequence
                    ELSE last_sequence
                END,
                execution_state_json = @executionStateJson
            WHERE id = @runId
              AND status IN ('queued', 'running')
            """,
            new { runId, lastSequence, executionStateJson = progress.ExecutionStateJson },
            tx);

        await tx.CommitAsync(ct);
    }

    public async Task TransitionAssistantRunToTerminalAsync(
        long runId,
        AssistantRunTerminalUpdate update,
        CancellationToken ct = default)
    {
        await using var conn = await GetOpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync(ct);

        var terminalAt = update.CompletedAt ?? DateTime.UtcNow;
        var events = update.Events
            .OrderBy(evt => evt.Sequence)
            .Select(evt => new AssistantRunEventEntity
            {
                RunId = runId,
                Sequence = evt.Sequence,
                Type = evt.Type,
                ContentJson = evt.ContentJson,
                CreatedAt = terminalAt
            })
            .ToList();

        if (events.Count == 0)
        {
            await tx.RollbackAsync(ct);
            throw new InvalidOperationException("Terminal assistant updates must include at least one event.");
        }

        var active = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM assistant_runs WHERE id = @runId AND status IN ('queued', 'running')",
            new { runId },
            tx);

        if (active == 0)
        {
            await tx.RollbackAsync(ct);
            return;
        }

        foreach (var evt in events)
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO assistant_run_events
                    (run_id, sequence, type, content_json, created_at)
                VALUES
                    (@RunId, @Sequence, @Type, @ContentJson, @CreatedAt)
                """,
                evt,
                tx);
        }

        var terminalSequence = events.Max(evt => evt.Sequence);

        await conn.ExecuteAsync(
            """
            UPDATE assistant_runs
            SET status = @status,
                final_answer = COALESCE(@finalAnswer, final_answer),
                warnings_json = COALESCE(@warningsJson, warnings_json),
                started_at = CASE
                    WHEN started_at IS NULL AND @status IN ('running', 'completed', 'failed', 'interrupted')
                    THEN UTC_TIMESTAMP(3) ELSE started_at END,
                completed_at = @completedAt,
                error = COALESCE(@error, error),
                provider_used = COALESCE(@providerUsed, provider_used),
                model_used = COALESCE(@modelUsed, model_used),
                last_sequence = CASE
                    WHEN last_sequence < @sequence THEN @sequence
                    ELSE last_sequence
                END,
                execution_state_json = NULL,
                execution_owner = NULL,
                lease_expires_at = NULL,
                cancel_requested_at = NULL
            WHERE id = @runId
              AND status IN ('queued', 'running')
            """,
            new
            {
                runId,
                status = update.Status,
                finalAnswer = update.FinalAnswer,
                warningsJson = update.WarningsJson,
                completedAt = terminalAt,
                error = update.Error,
                providerUsed = update.ProviderUsed,
                modelUsed = update.ModelUsed,
                sequence = terminalSequence
            },
            tx);

        if (string.Equals(update.Status, "completed", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(update.FinalAnswer))
        {
            var run = await conn.QuerySingleOrDefaultAsync<AssistantRunEntity>(
                "SELECT * FROM assistant_runs WHERE id = @runId LIMIT 1",
                new { runId },
                tx);

            if (run is not null)
            {
                await InsertAssistantChatMessageAsync(
                    conn,
                    tx,
                    new AssistantChatMessageEntity
                    {
                        Username = run.Username,
                        ChatId = run.ChatId,
                        MessageIndex = run.MessageIndexEnd + 1,
                        Role = "assistant",
                        Content = update.FinalAnswer,
                        SourceRunId = runId,
                        CreatedAt = terminalAt
                    });
            }
        }

        await tx.CommitAsync(ct);
    }

    public async Task<AssistantRunEntity?> GetLatestAssistantRunAsync(
        string username,
        string chatId,
        CancellationToken ct = default)
    {
        await using var conn = await GetOpenConnectionAsync();
        return await conn.QuerySingleOrDefaultAsync<AssistantRunEntity>(
            """
            SELECT *
            FROM assistant_runs
            WHERE username = @username
              AND chat_id = @chatId
            ORDER BY id DESC
            LIMIT 1
            """,
            new { username, chatId });
    }

    public async Task<IReadOnlyList<AssistantChatSummary>> GetAssistantChatSummariesAsync(
        string username,
        int take = 20,
        CancellationToken ct = default)
    {
        await using var conn = await GetOpenConnectionAsync();
        var results = await conn.QueryAsync<AssistantChatSummary>(
            """
            SELECT
                latest.chat_id AS ChatId,
                latest.question AS Title,
                latest.status AS Status,
                CASE WHEN latest.status IN ('queued', 'running') THEN latest.id ELSE NULL END AS ActiveRunId,
                COALESCE(latest.completed_at, latest.started_at, latest.created_at) AS LastActivityAt
            FROM assistant_runs latest
            INNER JOIN (
                SELECT chat_id, MAX(id) AS latest_run_id
                FROM assistant_runs
                WHERE username = @username
                GROUP BY chat_id
            ) lookup ON lookup.latest_run_id = latest.id
            WHERE latest.username = @username
            ORDER BY LastActivityAt DESC
            LIMIT @take
            """,
            new { username, take });

        return results.ToList();
    }

    public async Task<IReadOnlyList<AssistantChatMessageEntity>> GetAssistantChatMessagesAsync(
        string username,
        string chatId,
        long startMessageIndex = 0,
        long? endMessageIndex = null)
    {
        await using var conn = await GetOpenConnectionAsync();
        var results = await conn.QueryAsync<AssistantChatMessageEntity>(
            """
            SELECT *
            FROM assistant_chat_messages
            WHERE username = @username
              AND chat_id = @chatId
              AND message_index >= @startMessageIndex
              AND (@endMessageIndex IS NULL OR message_index <= @endMessageIndex)
            ORDER BY message_index
            """,
            new { username, chatId, startMessageIndex, endMessageIndex });

        return results.ToList();
    }

    public async Task AppendAssistantRunEventAsync(AssistantRunEventEntity evt)
    {
        await using var conn = await GetOpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        var inserted = await conn.ExecuteAsync(
            """
            INSERT INTO assistant_run_events
                (run_id, sequence, type, content_json, created_at)
            SELECT @RunId, @Sequence, @Type, @ContentJson, @CreatedAt
            FROM assistant_runs
            WHERE id = @RunId
              AND status IN ('queued', 'running')
            """,
            evt,
            tx);

        if (inserted == 0)
        {
            await tx.RollbackAsync();
            throw new InvalidOperationException($"Assistant run '{evt.RunId}' is no longer active.");
        }

        await conn.ExecuteAsync(
            """
            UPDATE assistant_runs
            SET last_sequence = CASE WHEN last_sequence < @Sequence THEN @Sequence ELSE last_sequence END
            WHERE id = @RunId
            """,
            new { evt.RunId, evt.Sequence },
            tx);

        await tx.CommitAsync();
    }

    public async Task<IReadOnlyList<AssistantRunEventEntity>> GetAssistantRunEventsAsync(
        long runId,
        long afterSequence = 0,
        int? take = null)
    {
        await using var conn = await GetOpenConnectionAsync();

        var sql = """
            SELECT *
            FROM assistant_run_events
            WHERE run_id = @runId AND sequence > @afterSequence
            ORDER BY sequence
            """;

        if (take is > 0)
        {
            sql += "\nLIMIT @take";
        }

        var results = await conn.QueryAsync<AssistantRunEventEntity>(sql, new { runId, afterSequence, take });
        return results.ToList();
    }

    public async Task AppendAssistantDebugExchangeAsync(AssistantDebugExchangeEntity exchange, CancellationToken ct = default)
    {
        await using var conn = await GetOpenConnectionAsync();
        await conn.ExecuteAsync(
            """
            INSERT INTO assistant_debug_exchanges
                (run_id, chat_id, username, exchange_index, turn_index, provider, model,
                 request_id, response_id, tool_uses_json, request_metadata_json, response_metadata_json,
                 request_body_json, response_body_json, request_text, response_text,
                 input_tokens, output_tokens, total_tokens, created_at)
            VALUES
                (@RunId, @ChatId, @Username, @ExchangeIndex, @TurnIndex, @Provider, @Model,
                 @RequestId, @ResponseId, @ToolUsesJson, @RequestMetadataJson, @ResponseMetadataJson,
                 @RequestBodyJson, @ResponseBodyJson, @RequestText, @ResponseText,
                 @InputTokens, @OutputTokens, @TotalTokens, @CreatedAt)
            """,
            exchange);
    }

    public async Task<IReadOnlyList<AssistantDebugExchangeEntity>> GetAssistantDebugExchangesAsync(
        long runId,
        CancellationToken ct = default)
    {
        await using var conn = await GetOpenConnectionAsync();
        var results = await conn.QueryAsync<AssistantDebugExchangeEntity>(
            "SELECT * FROM assistant_debug_exchanges WHERE run_id = @runId ORDER BY exchange_index",
            new { runId });
        return results.ToList();
    }

    public async Task AppendAssistantDebugTraceAuditAsync(
        AssistantDebugTraceAuditEntity audit,
        CancellationToken ct = default)
    {
        await using var conn = await GetOpenConnectionAsync();
        await conn.ExecuteAsync(
            """
            INSERT INTO assistant_debug_trace_audit
                (run_id, chat_id, run_username, viewed_by_username, remote_ip, user_agent, viewed_at)
            VALUES
                (@RunId, @ChatId, @RunUsername, @ViewedByUsername, @RemoteIp, @UserAgent, @ViewedAt)
            """,
            audit);
    }

    public async Task<AssistantRetentionCleanupResult> CleanupAssistantRetentionAsync(
        AssistantRetentionCleanupRequest request,
        CancellationToken ct = default)
    {
        await using var conn = await GetOpenConnectionAsync();
        var batchSize = Math.Clamp(request.BatchSize, 1, 10_000);

        var staleRuns = request.StaleActiveRunCutoffUtc is null
            ? 0
            : await ExecuteAsync(conn,
                """
                UPDATE assistant_runs
                SET status = 'failed',
                    error = COALESCE(error, 'Assistant run marked failed by retention cleanup after becoming stale.'),
                    completed_at = COALESCE(completed_at, @NowUtc),
                    execution_owner = NULL,
                    lease_expires_at = NULL
                WHERE status IN ('queued', 'running')
                  AND COALESCE(started_at, created_at) < @CutoffUtc
                LIMIT @BatchSize
                """,
                new { request.NowUtc, CutoffUtc = request.StaleActiveRunCutoffUtc, BatchSize = batchSize },
                ct);

        var audits = request.DebugTraceAuditCutoffUtc is null
            ? 0
            : await ExecuteAsync(conn,
                """
                DELETE FROM assistant_debug_trace_audit
                WHERE viewed_at < @CutoffUtc
                LIMIT @BatchSize
                """,
                new { CutoffUtc = request.DebugTraceAuditCutoffUtc, BatchSize = batchSize },
                ct);

        var debugExchanges = request.DebugExchangeCutoffUtc is null
            ? 0
            : await ExecuteAsync(conn,
                """
                DELETE FROM assistant_debug_exchanges
                WHERE created_at < @CutoffUtc
                LIMIT @BatchSize
                """,
                new { CutoffUtc = request.DebugExchangeCutoffUtc, BatchSize = batchSize },
                ct);

        var events = request.EventCutoffUtc is null
            ? 0
            : await ExecuteAsync(conn,
                """
                DELETE FROM assistant_run_events
                WHERE id IN (
                    SELECT id FROM (
                        SELECT e.id
                        FROM assistant_run_events e
                        INNER JOIN assistant_runs r ON r.id = e.run_id
                        WHERE e.created_at < @CutoffUtc
                          AND r.status IN ('completed', 'failed', 'cancelled')
                        ORDER BY e.created_at
                        LIMIT @BatchSize
                    ) old_events
                )
                """,
                new { CutoffUtc = request.EventCutoffUtc, BatchSize = batchSize },
                ct);

        var chatMessages = request.ChatMessageCutoffUtc is null
            ? 0
            : await ExecuteAsync(conn,
                """
                DELETE FROM assistant_chat_messages
                WHERE created_at < @CutoffUtc
                LIMIT @BatchSize
                """,
                new { CutoffUtc = request.ChatMessageCutoffUtc, BatchSize = batchSize },
                ct);

        var runs = request.TerminalRunCutoffUtc is null
            ? 0
            : await ExecuteAsync(conn,
                """
                DELETE FROM assistant_runs
                WHERE status IN ('completed', 'failed', 'cancelled')
                  AND COALESCE(completed_at, created_at) < @CutoffUtc
                LIMIT @BatchSize
                """,
                new { CutoffUtc = request.TerminalRunCutoffUtc, BatchSize = batchSize },
                ct);

        return new AssistantRetentionCleanupResult(
            staleRuns,
            runs,
            events,
            chatMessages,
            debugExchanges,
            audits);
    }

    private async Task<MySqlConnection> GetOpenConnectionAsync()
    {
        var connection = new MySqlConnection(options.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static Task<int> ExecuteAsync(
        MySqlConnection conn,
        string sql,
        object parameters,
        CancellationToken ct)
    {
        return conn.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: ct));
    }

    private static async Task<TResult> WithDeadlockRetryAsync<TResult>(
        Func<Task<TResult>> action,
        int maxRetries = 3)
    {
        for (var attempt = 0;; attempt++)
        {
            try
            {
                return await action();
            }
            catch (MySqlException ex) when (ex.Number == 1213 && attempt < maxRetries)
            {
                await Task.Delay((attempt + 1) * 200 + Random.Shared.Next(100));
            }
        }
    }

    private static string BuildAssistantChatLockName(string username, string chatId) =>
        $"assistant-chat:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{username}:{chatId}")))[..48].ToLowerInvariant()}";

    private static AssistantRunConflict? ValidateSubmittedHistory(
        IReadOnlyList<ChatMessage> submittedHistory,
        string question,
        IReadOnlyList<AssistantChatMessageEntity> storedMessages)
    {
        for (var i = 0; i < storedMessages.Count; i++)
        {
            if (i >= submittedHistory.Count)
            {
                return new AssistantRunConflict(
                    "chat_history_mismatch",
                    "Submitted chat history does not match the stored conversation.",
                    i);
            }

            var stored = storedMessages[i];
            var submitted = submittedHistory[i];
            if (!string.Equals(stored.Role, submitted.Role, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(stored.Content, submitted.Content, StringComparison.Ordinal))
            {
                return new AssistantRunConflict(
                    "chat_history_mismatch",
                    "Submitted chat history does not match the stored conversation.",
                    i);
            }
        }

        if (submittedHistory.Count > storedMessages.Count + 1)
        {
            return new AssistantRunConflict(
                "chat_history_mismatch",
                "Submitted chat history does not match the stored conversation.",
                storedMessages.Count);
        }

        if (submittedHistory.Count == storedMessages.Count + 1)
        {
            var appendedMessage = submittedHistory[storedMessages.Count];
            if (!string.Equals(appendedMessage.Role, "user", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(appendedMessage.Content, question, StringComparison.Ordinal))
            {
                return new AssistantRunConflict(
                    "chat_history_mismatch",
                    "Submitted chat history does not match the stored conversation.",
                    storedMessages.Count);
            }
        }

        return null;
    }

    private static async Task InsertAssistantChatMessageAsync(
        IDbConnection conn,
        IDbTransaction tx,
        AssistantChatMessageEntity message)
    {
        await conn.ExecuteAsync(
            """
            INSERT INTO assistant_chat_messages
                (username, chat_id, message_index, role, content, source_run_id, created_at)
            VALUES
                (@Username, @ChatId, @MessageIndex, @Role, @Content, @SourceRunId, @CreatedAt)
            ON DUPLICATE KEY UPDATE id = id
            """,
            message,
            tx);

        var persisted = await conn.QuerySingleAsync<AssistantChatMessageEntity>(
            """
            SELECT *
            FROM assistant_chat_messages
            WHERE username = @Username
              AND chat_id = @ChatId
              AND message_index = @MessageIndex
            """,
            new { message.Username, message.ChatId, message.MessageIndex },
            tx);

        if (!string.Equals(persisted.Role, message.Role, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(persisted.Content, message.Content, StringComparison.Ordinal) ||
            persisted.SourceRunId != message.SourceRunId)
        {
            throw new InvalidOperationException(
                $"Assistant chat message index {message.MessageIndex} already exists with different content.");
        }
    }
}

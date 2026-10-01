using ADFi.Infrastructure.Postgres;

namespace ADFi.Api;

public sealed record DiscoverFeedbackReceipt(Guid FeedbackId, string Status, string Message);

public sealed class DiscoverFeedbackRepository(AdfiDatabase database, TimeProvider timeProvider)
{
    public async Task<DiscoverFeedbackReceipt?> CreatePendingAsync(
        Guid projectId,
        Guid authorUserId,
        long timestampMs,
        string body,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(timestampMs);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);
        var id = Guid.NewGuid();
        await using var command = database.DataSource.CreateCommand(
            """
            INSERT INTO feedback_entries
                (id, project_id, author_user_id, feedback_type, body, source_time_ms,
                 ad_segment_id, qualification_status, reason_code, created_at_utc, decided_at_utc)
            SELECT $1, publication.project_id, $2, 'ACCESSIBILITY_CORRECTION', $3, $4,
                   NULL, 'PENDING', NULL, $5, NULL
            FROM project_publications publication
            WHERE publication.project_id = $6
              AND publication.playable = true
              AND publication.discoverable = true
              AND publication.state IN ('COMMUNITY_ACTIVE', 'CLAIMED_ACTIVE', 'RESTORED')
            LIMIT 1
            RETURNING id
            """);
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(authorUserId);
        command.Parameters.AddWithValue(body.Trim());
        command.Parameters.AddWithValue(timestampMs);
        command.Parameters.AddWithValue(timeProvider.GetUtcNow());
        command.Parameters.AddWithValue(projectId);
        var inserted = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return inserted is Guid feedbackId
            ? new(
                feedbackId,
                "PENDING",
                "Feedback submitted for review with Pending status. Asking or submitting does not create earnings automatically.")
            : null;
    }
}

using ADFi.Infrastructure.Postgres;
using ADFi.Web;
using ADFi.Core;
using System.Text.Json;

namespace ADFi.Api;

public sealed class DiscoverRepository(AdfiDatabase database, Uri publicBaseUri)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<DiscoverSurface>> ListAsync(
        int maximumCount,
        CancellationToken cancellationToken)
    {
        if (maximumCount is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        await using var command = database.DataSource.CreateCommand(
            """
            SELECT p.id, p.title, y.youtube_video_id,
                   publication.accessibility_summary, publication.attribution
                   , publication.id
            FROM project_publications publication
            JOIN projects p ON p.id = publication.project_id
            JOIN LATERAL (
                SELECT asset.youtube_video_id
                FROM youtube_assets asset
                WHERE asset.publication_id = publication.id
                ORDER BY asset.last_refreshed_at_utc DESC
                LIMIT 1
            ) y ON true
            WHERE publication.playable = true
              AND publication.discoverable = true
              AND publication.state IN ('COMMUNITY_ACTIVE', 'CLAIMED_ACTIVE', 'RESTORED')
            ORDER BY publication.updated_at_utc DESC, p.id
            LIMIT $1
            """);
        command.Parameters.AddWithValue(maximumCount);
        var results = new List<DiscoverSurface>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var projectId = reader.GetGuid(0);
            results.Add(new(
                projectId.ToString("D"),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                new Uri(publicBaseUri, $"discover/{projectId:D}"),
                reader.GetGuid(5).ToString("D")));
        }

        return results;
    }

    public async Task<VideoContextResolution?> ResolveMomentContextAsync(
        Guid projectId,
        long timestampMs,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(timestampMs);
        await using var command = database.DataSource.CreateCommand(
            """
            SELECT evidence.scene_id, evidence.start_time_ms, evidence.end_time_ms,
                   evidence.objective_visual_summary,
                   evidence.established_entities_json::text,
                   evidence.ocr_context_json::text,
                   evidence.dialogue_context_json::text,
                   evidence.ad_text, evidence.evidence_confidence
            FROM project_scene_evidence evidence
            JOIN project_publications publication ON publication.id = evidence.publication_id
            WHERE evidence.project_id = $1
              AND evidence.start_time_ms <= $2
              AND evidence.end_time_ms > $2
              AND publication.playable = true
              AND publication.discoverable = true
              AND publication.state IN ('COMMUNITY_ACTIVE', 'CLAIMED_ACTIVE', 'RESTORED')
            ORDER BY evidence.start_time_ms DESC
            LIMIT 1
            """);
        command.Parameters.AddWithValue(projectId);
        command.Parameters.AddWithValue(timestampMs);
        return await ReadMomentContextAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VideoContextResolution?> ResolveMomentContextByYouTubeIdAsync(
        string youtubeVideoId,
        long timestampMs,
        CancellationToken cancellationToken)
    {
        if (!YoutubeVideoUrl.TryParseVideoId(youtubeVideoId, out var normalized))
        {
            throw new ArgumentException("A valid YouTube video ID is required.", nameof(youtubeVideoId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(timestampMs);
        await using var command = database.DataSource.CreateCommand(
            """
            SELECT evidence.scene_id, evidence.start_time_ms, evidence.end_time_ms,
                   evidence.objective_visual_summary,
                   evidence.established_entities_json::text,
                   evidence.ocr_context_json::text,
                   evidence.dialogue_context_json::text,
                   evidence.ad_text, evidence.evidence_confidence
            FROM project_scene_evidence evidence
            JOIN project_publications publication ON publication.id = evidence.publication_id
            JOIN youtube_assets asset ON asset.publication_id = publication.id
            WHERE asset.youtube_video_id = $1
              AND evidence.start_time_ms <= $2
              AND evidence.end_time_ms > $2
              AND publication.playable = true
              AND publication.discoverable = true
              AND publication.state IN ('COMMUNITY_ACTIVE', 'CLAIMED_ACTIVE', 'RESTORED')
            ORDER BY evidence.start_time_ms DESC
            LIMIT 1
            """);
        command.Parameters.AddWithValue(normalized);
        command.Parameters.AddWithValue(timestampMs);
        return await ReadMomentContextAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<VideoContextResolution?> ReadMomentContextAsync(
        Npgsql.NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var sceneId = reader.GetString(0);
        var start = reader.GetInt64(1);
        var end = reader.GetInt64(2);
        var summary = reader.IsDBNull(3) ? null : reader.GetString(3);
        var entities = Deserialize<EstablishedEntityContext[]>(reader.GetString(4));
        var ocr = Deserialize<string[]>(reader.GetString(5));
        var dialogue = Deserialize<string[]>(reader.GetString(6));
        var adText = reader.IsDBNull(7) ? null : reader.GetString(7);
        var confidence = reader.GetDecimal(8);
        var evidence = new List<MomentVqaEvidence>();
        if (!string.IsNullOrWhiteSpace(summary))
        {
            evidence.Add(new(
                $"scene:{sceneId}", MomentEvidenceKind.PersistedSceneAnalysis,
                MomentEvidenceBasis.ExistingAdfiAnalysis, start, end, summary, null, null, null, confidence, false));
        }

        if (ocr.Length > 0)
        {
            evidence.Add(new(
                $"ocr:{sceneId}", MomentEvidenceKind.PersistedOcr,
                MomentEvidenceBasis.ExistingAdfiAnalysis, start, end, string.Join(" | ", ocr), null, null, null, confidence, false));
        }

        if (!string.IsNullOrWhiteSpace(adText))
        {
            evidence.Add(new(
                $"ad:{sceneId}", MomentEvidenceKind.PersistedAudioDescription,
                MomentEvidenceBasis.ExistingAdfiAnalysis, start, end, adText, null, null, null, confidence, false));
        }

        return new(
            new(sceneId, start, end, summary, [], confidence),
            evidence,
            entities,
            ocr,
            dialogue,
            evidence.Count > 0);
    }

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidDataException("Stored ADFi scene evidence is invalid.");
}

using ADFi.Infrastructure.Postgres;
using ADFi.Web;

namespace ADFi.Api;

public sealed class DiscoverRepository(AdfiDatabase database, Uri publicBaseUri)
{
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
                new Uri(publicBaseUri, $"discover/{projectId:D}")));
        }

        return results;
    }
}

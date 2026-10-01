using ADFi.Core;

namespace ADFi.Core.Tests;

public sealed class SourceAuthorizationTests
{
    [Fact]
    public void RejectedAttestationCannotEnterPipeline()
    {
        Assert.Throws<InvalidOperationException>(() => RightsAttestation.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            new string('A', 64),
            false));
    }

    [Fact]
    public void AttestationPersistsVersionTextAndUppercaseHash()
    {
        var result = RightsAttestation.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero),
            new string('a', 64),
            true);

        Assert.Equal(RightsAttestationPolicy.Version, result.AttestationVersion);
        Assert.Equal(RightsAttestationPolicy.Text, result.AttestationText);
        Assert.Equal(new string('A', 64), result.SourceSha256);
        Assert.True(result.Accepted);
    }
}


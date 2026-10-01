using ADFi.Core;

namespace ADFi.Core.Tests;

public sealed class ProjectPolicyTests
{
    [Theory]
    [InlineData(ProjectPublicationState.CommunityActive, true, true, true, true)]
    [InlineData(ProjectPublicationState.ClaimedActive, true, true, true, true)]
    [InlineData(ProjectPublicationState.Unlisted, true, false, false, false)]
    [InlineData(ProjectPublicationState.Removed, false, false, false, false)]
    [InlineData(ProjectPublicationState.Blocked, false, false, false, false)]
    public void AvailabilityIsIndependentAndExplicit(
        ProjectPublicationState state,
        bool playable,
        bool discoverable,
        bool shareable,
        bool revenueEligible)
    {
        var result = ProjectAvailabilityPolicy.For(state);
        Assert.Equal(playable, result.Playable);
        Assert.Equal(discoverable, result.Discoverable);
        Assert.Equal(shareable, result.Shareable);
        Assert.Equal(revenueEligible, result.RevenueEligible);
    }

    [Fact]
    public void NamesAreOnlyUsedAfterEvidenceEstablishesThem()
    {
        var entity = new ProjectEntity(
            "Person_01",
            EntityKind.Person,
            "the first person",
            [new(IdentityEvidenceKind.TranscriptDialogue, "segment-14", TimeSpan.FromMinutes(2), "John", 0.99m)]);

        Assert.Equal("the first person", entity.DisplayNameAt(TimeSpan.FromMinutes(1)));
        Assert.Equal("John", entity.DisplayNameAt(TimeSpan.FromMinutes(2)));
    }
}


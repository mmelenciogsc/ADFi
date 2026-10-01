using ADFi.Economics;

namespace ADFi.Core.Tests;

public sealed class PeriodAllocatorTests
{
    [Fact]
    public void QualifiedEventsAllocateExactCentsAndUnqualifiedEventsEarnNothing()
    {
        var memberOne = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var memberTwo = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var events = new[]
        {
            Event("00000000-0000-0000-0000-000000000011", memberOne, EconomicEventKind.QualifiedReferral, QualificationStatus.Qualified, 1),
            Event("00000000-0000-0000-0000-000000000012", memberTwo, EconomicEventKind.QualifiedReferral, QualificationStatus.Qualified, 1),
            Event("00000000-0000-0000-0000-000000000013", memberTwo, EconomicEventKind.QualifiedFeedback, QualificationStatus.Rejected, 100),
            Event("00000000-0000-0000-0000-000000000014", memberTwo, EconomicEventKind.CreatorPublishedContent, QualificationStatus.Qualified, 1),
        };

        var result = PeriodAllocator.Allocate(70m, new(2500, 3500, 4000), events, new Dictionary<Guid, EffectiveSplit>());

        Assert.Equal(7000, result.CommunityPoolUsdCents);
        Assert.Equal(4550, result.AllocatedUsdCents);
        Assert.Equal(2450, result.UnallocatedReserveUsdCents);
        Assert.DoesNotContain(result.MemberAllocations, item => item.EventId == Guid.Parse("00000000-0000-0000-0000-000000000013"));
        Assert.Equal(result.CommunityPoolUsdCents, result.AllocatedUsdCents + result.UnallocatedReserveUsdCents);
    }

    [Fact]
    public void EventUsesItsRecordedSplitVersionAndConservesCents()
    {
        var owner = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var collaborator = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var project = Guid.Parse("00000000-0000-0000-0000-000000000003");
        var splitId = Guid.Parse("00000000-0000-0000-0000-000000000004");
        var economicEvent = Event(
            "00000000-0000-0000-0000-000000000011",
            owner,
            EconomicEventKind.CreatorPublishedContent,
            QualificationStatus.Qualified,
            1,
            project,
            splitId);
        var splits = new Dictionary<Guid, EffectiveSplit>
        {
            [splitId] = new(splitId, project, [new(owner, 6000), new(collaborator, 4000)]),
        };

        var result = PeriodAllocator.Allocate(1m, new(0, 0, 10_000), [economicEvent], splits);

        Assert.Equal(100, result.AllocatedUsdCents);
        Assert.Equal(60, result.MemberAllocations.Single(item => item.MemberId == owner).AmountUsdCents);
        Assert.Equal(40, result.MemberAllocations.Single(item => item.MemberId == collaborator).AmountUsdCents);
        Assert.All(result.MemberAllocations, item => Assert.Equal(splitId, item.AppliedSplitVersionId));
    }

    private static AllocationEvent Event(
        string eventId,
        Guid memberId,
        EconomicEventKind kind,
        QualificationStatus status,
        decimal weight,
        Guid? projectId = null,
        Guid? splitVersionId = null) =>
        new(Guid.Parse(eventId), memberId, projectId, kind, status, weight, RevenuePolicy.CurrentVersion, splitVersionId);
}

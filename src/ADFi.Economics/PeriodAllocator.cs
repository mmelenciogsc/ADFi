namespace ADFi.Economics;

public enum EconomicSubpool
{
    Referral,
    Contribution,
    CreatorContent,
}

public sealed record SubpoolWeights(
    int ReferralBasisPoints,
    int ContributionBasisPoints,
    int CreatorContentBasisPoints)
{
    public void Validate()
    {
        if (ReferralBasisPoints < 0
            || ContributionBasisPoints < 0
            || CreatorContentBasisPoints < 0
            || ReferralBasisPoints + ContributionBasisPoints + CreatorContentBasisPoints != 10_000)
        {
            throw new InvalidOperationException("Economic sub-pool weights must be nonnegative and total exactly 10,000 basis points.");
        }
    }
}

public sealed record AllocationEvent(
    Guid EventId,
    Guid MemberId,
    Guid? ProjectId,
    EconomicEventKind Kind,
    QualificationStatus Status,
    decimal Weight,
    string PolicyVersion,
    Guid? SplitVersionId);

public sealed record SplitParticipant(Guid UserId, int BasisPoints);

public sealed record EffectiveSplit(
    Guid SplitVersionId,
    Guid ProjectId,
    IReadOnlyList<SplitParticipant> Participants);

public sealed record MemberAllocation(
    Guid EventId,
    Guid MemberId,
    EconomicSubpool Subpool,
    long AmountUsdCents,
    Guid? AppliedSplitVersionId,
    string Explanation);

public sealed record PeriodAllocationResult(
    long CommunityPoolUsdCents,
    long AllocatedUsdCents,
    long UnallocatedReserveUsdCents,
    IReadOnlyList<MemberAllocation> MemberAllocations);

public static class PeriodAllocator
{
    public static PeriodAllocationResult Allocate(
        decimal communityPoolUsd,
        SubpoolWeights subpoolWeights,
        IReadOnlyList<AllocationEvent> events,
        IReadOnlyDictionary<Guid, EffectiveSplit> splits)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(communityPoolUsd);

        subpoolWeights.Validate();
        var communityCents = checked((long)decimal.Round(communityPoolUsd * 100m, 0, MidpointRounding.ToEven));
        var poolCents = AllocateByBasisPoints(
            communityCents,
            [
                (EconomicSubpool.Referral, subpoolWeights.ReferralBasisPoints),
                (EconomicSubpool.Contribution, subpoolWeights.ContributionBasisPoints),
                (EconomicSubpool.CreatorContent, subpoolWeights.CreatorContentBasisPoints),
            ]);
        var allocations = new List<MemberAllocation>();
        long reserve = 0;

        foreach (var pool in poolCents)
        {
            var eligible = events
                .Where(item => item.Status == QualificationStatus.Qualified)
                .Where(item => SubpoolFor(item.Kind) == pool.Key)
                .Where(item => item.Weight > 0)
                .OrderBy(item => item.EventId)
                .ToArray();
            if (eligible.Length == 0)
            {
                reserve += pool.Value;
                continue;
            }

            var eventAmounts = AllocateByWeight(pool.Value, eligible);
            foreach (var pair in eventAmounts)
            {
                var economicEvent = pair.Event;
                if (economicEvent.SplitVersionId is { } splitVersionId)
                {
                    if (!splits.TryGetValue(splitVersionId, out var split)
                        || split.ProjectId != economicEvent.ProjectId)
                    {
                        throw new InvalidOperationException($"Missing or mismatched split version {splitVersionId}.");
                    }

                    ValidateSplit(split);
                    foreach (var memberAmount in AllocateByBasisPoints(
                                 pair.Amount,
                                 split.Participants
                                     .OrderBy(participant => participant.UserId)
                                     .Select(participant => (participant.UserId, participant.BasisPoints))
                                     .ToArray()))
                    {
                        allocations.Add(new(
                            economicEvent.EventId,
                            memberAmount.Key,
                            pool.Key,
                            memberAmount.Value,
                            splitVersionId,
                            $"Qualified {economicEvent.Kind} under {economicEvent.PolicyVersion}; split {splitVersionId}."));
                    }
                }
                else
                {
                    allocations.Add(new(
                        economicEvent.EventId,
                        economicEvent.MemberId,
                        pool.Key,
                        pair.Amount,
                        null,
                        $"Qualified {economicEvent.Kind} under {economicEvent.PolicyVersion}."));
                }
            }
        }

        return new(
            communityCents,
            allocations.Sum(item => item.AmountUsdCents),
            reserve,
            allocations);
    }

    private static EconomicSubpool SubpoolFor(EconomicEventKind kind) => kind switch
    {
        EconomicEventKind.QualifiedReferral => EconomicSubpool.Referral,
        EconomicEventKind.ReferralDownstreamAction => EconomicSubpool.Referral,
        EconomicEventKind.QualifiedFeedback => EconomicSubpool.Contribution,
        EconomicEventKind.CollaborationContribution => EconomicSubpool.Contribution,
        EconomicEventKind.CreatorPublishedContent => EconomicSubpool.CreatorContent,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static (AllocationEvent Event, long Amount)[] AllocateByWeight(
        long amount,
        IReadOnlyList<AllocationEvent> events)
    {
        var totalWeight = events.Sum(item => item.Weight);
        var preliminary = events
            .Select(item =>
            {
                var exact = amount * item.Weight / totalWeight;
                var floor = (long)decimal.Floor(exact);
                return new { Event = item, Floor = floor, Remainder = exact - floor };
            })
            .ToArray();
        var unassigned = amount - preliminary.Sum(item => item.Floor);
        var bonusIds = preliminary
            .OrderByDescending(item => item.Remainder)
            .ThenBy(item => item.Event.EventId)
            .Take(checked((int)unassigned))
            .Select(item => item.Event.EventId)
            .ToHashSet();
        return preliminary
            .Select(item => (item.Event, item.Floor + (bonusIds.Contains(item.Event.EventId) ? 1 : 0)))
            .ToArray();
    }

    private static Dictionary<TKey, long> AllocateByBasisPoints<TKey>(
        long amount,
        (TKey Key, int BasisPoints)[] shares)
        where TKey : notnull
    {
        var result = new Dictionary<TKey, long>();
        long assigned = 0;
        for (var index = 0; index < shares.Length; index++)
        {
            var share = shares[index];
            var value = index == shares.Length - 1
                ? amount - assigned
                : amount * share.BasisPoints / 10_000;
            assigned += value;
            result.Add(share.Key, value);
        }

        return result;
    }

    private static void ValidateSplit(EffectiveSplit split)
    {
        if (split.Participants.Count == 0
            || split.Participants.Any(participant => participant.BasisPoints < 0)
            || split.Participants.Sum(participant => participant.BasisPoints) != 10_000
            || split.Participants.Select(participant => participant.UserId).Distinct().Count() != split.Participants.Count)
        {
            throw new InvalidOperationException($"Split version {split.SplitVersionId} is invalid.");
        }
    }
}

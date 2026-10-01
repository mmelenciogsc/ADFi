namespace ADFi.Economics;

public enum QualificationStatus
{
    Pending,
    Qualified,
    Rejected,
    Reversed,
}

public enum EconomicEventKind
{
    QualifiedReferral,
    ReferralDownstreamAction,
    QualifiedFeedback,
    CollaborationContribution,
    CreatorPublishedContent,
}

public sealed record EconomicEvent(
    Guid EventId,
    Guid MemberId,
    EconomicEventKind Kind,
    QualificationStatus Status,
    DateTimeOffset OccurredAtUtc,
    string PolicyVersion,
    Guid? ProjectId,
    Guid? ReferralId,
    string ReasonCode,
    decimal Weight);

public enum WalletEntryKind
{
    Earning,
    HoldRelease,
    PayoutReservation,
    PayoutFailureReturn,
    Reversal,
    Adjustment,
}

public sealed record WalletEntry(
    Guid EntryId,
    Guid MemberId,
    WalletEntryKind Kind,
    decimal AmountUsd,
    DateTimeOffset CreatedAtUtc,
    Guid? EconomicEventId,
    Guid? PayoutRequestId,
    string Explanation);


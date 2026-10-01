namespace ADFi.Media;

public sealed record NarrationFitResult(
    bool Fits,
    TimeSpan Available,
    TimeSpan Rendered,
    TimeSpan Remaining,
    int AttemptNumber,
    bool RequiresUserReview);

public static class NarrationTimingPolicy
{
    public const int MaximumAutoFitAttempts = 3;

    public static NarrationFitResult Evaluate(
        TimeSpan renderedDuration,
        TimeSpan windowDuration,
        TimeSpan breathingRoom,
        int attemptNumber)
    {
        if (renderedDuration < TimeSpan.Zero || windowDuration <= TimeSpan.Zero || breathingRoom < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(renderedDuration));
        }

        if (attemptNumber is < 1 or > MaximumAutoFitAttempts)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptNumber));
        }

        var available = windowDuration - breathingRoom;
        var remaining = available - renderedDuration;
        var fits = remaining >= TimeSpan.Zero;
        return new(
            fits,
            available,
            renderedDuration,
            remaining,
            attemptNumber,
            !fits && attemptNumber == MaximumAutoFitAttempts);
    }
}

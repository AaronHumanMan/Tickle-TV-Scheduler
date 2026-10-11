namespace TickleTV.Scheduler.Core.Engine;

/// <summary>How a slot's end snaps to the wall clock.</summary>
public enum PaddingMode
{
    /// <summary>No padding: the next item starts when the content ends.</summary>
    None,
    /// <summary>Snap to the next :00 or :30.</summary>
    HalfHour,
    /// <summary>Snap to the next :00.</summary>
    Hour,
    /// <summary>Snap to the next multiple of 5 minutes.</summary>
    Next5,
    /// <summary>Snap to the next multiple of 10 minutes.</summary>
    Next10,
}

public sealed record PaddingPolicy
{
    public PaddingMode Mode { get; init; } = PaddingMode.HalfHour;
    /// <summary>If the natural boundary leaves less than this, jump one more boundary.</summary>
    public long MinPadMs { get; init; } = 0;
    /// <summary>Guardrail: a pad above this is reported as a warning by the caller.</summary>
    public long? MaxPadMs { get; init; }

    public long BoundaryMs => Mode switch
    {
        PaddingMode.HalfHour => 30 * 60_000L,
        PaddingMode.Hour => 60 * 60_000L,
        PaddingMode.Next5 => 5 * 60_000L,
        PaddingMode.Next10 => 10 * 60_000L,
        _ => 0,
    };
}

public static class Padding
{
    /// <summary>
    /// Returns the slot end for content of <paramref name="contentMs"/> starting at <paramref name="start"/>.
    /// Boundaries are measured on the wall clock in the start time's offset, so an 8:03 start with
    /// <see cref="PaddingMode.Next10"/> snaps to 8:10, 8:20, and so on.
    /// </summary>
    public static DateTimeOffset SlotEnd(DateTimeOffset start, long contentMs, PaddingPolicy policy)
    {
        DateTimeOffset contentEnd = start.AddMilliseconds(contentMs);
        long b = policy.BoundaryMs;
        if (b == 0) return contentEnd;

        DateTimeOffset midnight = new(start.Date, start.Offset);
        long sinceMidnightMs = (long)(start - midnight).TotalMilliseconds;
        long target = sinceMidnightMs + contentMs + policy.MinPadMs;
        long snapped = (target + b - 1) / b * b; // ceiling to the boundary
        return midnight.AddMilliseconds(snapped);
    }

    public static long PadMs(DateTimeOffset start, long contentMs, PaddingPolicy policy) =>
        (long)(SlotEnd(start, contentMs, policy) - start).TotalMilliseconds - contentMs;
}

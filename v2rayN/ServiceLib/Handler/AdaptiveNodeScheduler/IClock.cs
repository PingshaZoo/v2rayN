using System.Diagnostics;

namespace ServiceLib.Handler.AdaptiveNodeScheduler;

/// <summary>
/// Time abstraction for deterministic unit testing of time-sensitive modules
/// (CooldownFsm, FreezeController, RecoveryConfirmationFsm, ReloadPolicyApplier).
/// Production implementation = <see cref="SystemClock"/>.
/// Test implementation = <see cref="FakeClock"/>.
///
/// <h2>Why not .NET TimeProvider?</h2>
/// TimeProvider is feature-rich but over-abstracted for our needs. IClock exposes
/// only the three time operations the system actually uses, keeping the test surface
/// minimal.
/// </summary>
public interface IClock
{
    DateTime UtcNow { get; }
    long GetTimestamp();
    long TimestampFrequency { get; }
    Task Delay(TimeSpan duration, CancellationToken ct = default);
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
    public long GetTimestamp() => Stopwatch.GetTimestamp();
    public long TimestampFrequency => Stopwatch.Frequency;
    public Task Delay(TimeSpan duration, CancellationToken ct = default)
        => Task.Delay(duration, ct);
}

public sealed class FakeClock : IClock
{
    public DateTime UtcNow { get; set; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    public long GetTimestamp() => _timestamp;
    public long TimestampFrequency => 10_000_000;
    public Task Delay(TimeSpan duration, CancellationToken ct = default)
    {
        UtcNow = UtcNow.Add(duration);
        return Task.CompletedTask;
    }

    private long _timestamp;
    public void AdvanceTime(TimeSpan duration)
    {
        UtcNow = UtcNow.Add(duration);
        _timestamp += (long)(duration.TotalSeconds * TimestampFrequency);
    }
}

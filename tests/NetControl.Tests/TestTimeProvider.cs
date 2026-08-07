namespace NetControl.Tests;

/// <summary>
/// A clock the tests drive by hand. Small enough not to justify a package dependency, and this
/// project keeps its dependency list short deliberately.
/// </summary>
internal sealed class TestTimeProvider(DateTimeOffset start) : TimeProvider()
{
    private DateTimeOffset _now = start;

    public TestTimeProvider() : this(new DateTimeOffset(2026, 8, 6, 21, 0, 0, TimeSpan.Zero)) { }

    /// <summary>One tick per 100 ns keeps GetElapsedTime arithmetic exact.</summary>
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow() => _now;

    public override long GetTimestamp() => _now.UtcTicks;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}

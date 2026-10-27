namespace GetDocument.Core.EasterEgg;

/// <summary>
/// Hidden local gesture only; does not make network requests or provide gameplay.
/// Ten consecutive taps on the application mark unlock an intranet-game placeholder.
/// </summary>
public sealed class HiddenGameGesture
{
    private const int RequiredTaps = 10;
    private static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(4);
    private DateTimeOffset? _lastTap;
    private int _count;

    public bool Tap(DateTimeOffset now)
    {
        if (_lastTap is { } last && (now < last || now - last > MaxGap))
            _count = 0;
        _lastTap = now;
        _count++;
        if (_count < RequiredTaps) return false;
        Reset();
        return true;
    }

    public void Reset()
    {
        _count = 0;
        _lastTap = null;
    }
}

/// <summary>
/// Networking is deliberately opt-in. Any future gateway must target a
/// preapproved internal endpoint and must not bypass application/server policy.
/// </summary>
public interface IIntranetDurakGateway
{
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
}

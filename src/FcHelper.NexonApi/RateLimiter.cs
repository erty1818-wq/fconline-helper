namespace FcHelper.NexonApi;

/// <summary>
/// Token bucket shared by every API call. The development-key limits are not confirmed yet
/// (docs/PLANNING.md 3.4), so the rate is a setting rather than a constant.
/// </summary>
public sealed class RateLimiter
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _time;
    private readonly double _capacity;
    private double _tokens;
    private long _lastRefill;

    public RateLimiter(double requestsPerSecond, TimeProvider? time = null)
    {
        if (requestsPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(requestsPerSecond));
        _time = time ?? TimeProvider.System;
        RequestsPerSecond = requestsPerSecond;
        _capacity = Math.Max(1, requestsPerSecond);
        _tokens = _capacity;
        _lastRefill = _time.GetTimestamp();
    }

    public double RequestsPerSecond { get; }

    /// <summary>Requests issued since the process started (shown in the tray tooltip).</summary>
    public int IssuedCount { get; private set; }

    public async Task WaitAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (true)
            {
                Refill();
                if (_tokens >= 1)
                {
                    _tokens -= 1;
                    IssuedCount++;
                    return;
                }
                var wait = TimeSpan.FromSeconds((1 - _tokens) / RequestsPerSecond);
                await Task.Delay(wait, _time, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Refill()
    {
        var now = _time.GetTimestamp();
        var elapsed = _time.GetElapsedTime(_lastRefill, now).TotalSeconds;
        _lastRefill = now;
        _tokens = Math.Min(_capacity, _tokens + elapsed * RequestsPerSecond);
    }
}

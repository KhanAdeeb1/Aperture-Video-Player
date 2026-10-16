using System.Collections.Concurrent;
using System.Net;

namespace Aperture.Server.Security;

/// <summary>
/// Slows down passphrase guessing. After <see cref="FreeAttempts"/> wrong passphrases from one
/// address, that address waits before trying again, twice as long each time (30 s up to 15 min).
/// Across all addresses at most <see cref="GlobalPerMinute"/> attempts a minute are checked.
/// </summary>
public sealed class LoginThrottle(TimeProvider clock)
{
    public const int FreeAttempts = 5;
    public const int GlobalPerMinute = 30;
    private static readonly TimeSpan FirstLock = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LongestLock = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<IPAddress, Record> _byAddress = new();
    private readonly Queue<DateTimeOffset> _recent = new();
    private readonly Lock _gate = new();

    /// <summary>How long <paramref name="address"/> must wait, or zero when it may try now. Counts the attempt.</summary>
    public TimeSpan Check(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            while (_recent.Count > 0 && now - _recent.Peek() > TimeSpan.FromMinutes(1))
            {
                _recent.Dequeue();
            }

            if (_recent.Count >= GlobalPerMinute)
            {
                return TimeSpan.FromMinutes(1) - (now - _recent.Peek());
            }

            if (_byAddress.TryGetValue(address, out var record) && record.LockedUntil > now)
            {
                return record.LockedUntil - now;
            }

            _recent.Enqueue(now);
            return TimeSpan.Zero;
        }
    }

    /// <summary>Records a wrong passphrase.</summary>
    public void Failed(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            var record = _byAddress.GetValueOrDefault(address) ?? new Record();
            record.Failures++;
            if (record.Failures >= FreeAttempts)
            {
                var steps = Math.Min(record.Failures - FreeAttempts, 10);
                var wait = TimeSpan.FromTicks(Math.Min(FirstLock.Ticks << steps, LongestLock.Ticks));
                record.LockedUntil = now + wait;
            }

            _byAddress[address] = record;
        }
    }

    /// <summary>Forgets an address's failures after a correct passphrase.</summary>
    public void Succeeded(IPAddress address) => _byAddress.TryRemove(address, out _);

    private sealed class Record
    {
        public int Failures { get; set; }

        public DateTimeOffset LockedUntil { get; set; }
    }
}

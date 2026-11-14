using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace PCL.Network;

/// <summary>
///     下载通道。按文件大小自动选择，业务代码不需要传类型。<br/>
///     分支说明：不同大小的文件对并发连接的需求差别很大 —— 游戏本体是几个
///     几十 MB 的大文件，开太多连接只是在抢带宽；而资源、库文件动辄上千个
///     小文件，需要更多并发才能把管道填满。分成两条独立配额通道后，
///     两者不会互相挤占。
/// </summary>
internal enum DownloadChannel
{
    /// <summary>大文件（游戏 jar、整合包等）。</summary>
    Large,

    /// <summary>小文件（资源、库、日志等）。</summary>
    Small
}

/// <summary>协调所有下载任务共享的连接数、缓冲区和限速额度。</summary>
internal static class DownloadResourceManager
{
    /// <summary>小于此大小的文件走 Small 通道。</summary>
    private const long SmallFileThresholdBytes = 2L * 1024 * 1024;

    private static readonly AsyncQuota LargeConnectionQuota = new();
    private static readonly AsyncQuota SmallConnectionQuota = new();
    private static readonly AsyncQuota BufferQuota = new();
    private static readonly ConcurrentDictionary<string, HostQuotaEntry> HostConnectionQuotas = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock SpeedLock = new();
    private static int _activeConnectionCount;
    private static long _speedBytes;
    private static long _speedSnapshotTick = Stopwatch.GetTimestamp();
    private static long _speed;

    public static int ActiveConnectionCount => Volatile.Read(ref _activeConnectionCount);

    public static long DownloadSpeed
    {
        get
        {
            lock (SpeedLock)
            {
                var now = Stopwatch.GetTimestamp();
                var elapsedTicks = now - _speedSnapshotTick;
                if (elapsedTicks >= Stopwatch.Frequency / 4)
                {
                    var bytes = Interlocked.Exchange(ref _speedBytes, 0);
                    _speed = elapsedTicks > 0
                        ? Math.Max(0L, (long)(bytes * (double)Stopwatch.Frequency / elapsedTicks))
                        : 0L;
                    _speedSnapshotTick = now;
                }

                return _speed;
            }
        }
    }

    internal static void RecordDownloadedBytes(long bytes)
    {
        if (bytes > 0)
            Interlocked.Add(ref _speedBytes, bytes);
    }

    /// <summary>按文件大小推断通道。大小未知时按小文件处理（更保守）。</summary>
    internal static DownloadChannel ResolveChannel(long? fileSizeBytes)
    {
        if (fileSizeBytes is null or <= 0) return DownloadChannel.Small;
        return fileSizeBytes.Value < SmallFileThresholdBytes ? DownloadChannel.Small : DownloadChannel.Large;
    }

    private static AsyncQuota GetQuota(DownloadChannel channel) =>
        channel == DownloadChannel.Large ? LargeConnectionQuota : SmallConnectionQuota;

    /// <summary>
    ///     申请一个下载连接。
    /// </summary>
    /// <param name="url">目标地址，用于按主机限流。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="fileSizeBytes">文件总大小（未知时传 null），决定使用哪个通道的配额。</param>
    public static async ValueTask<DownloadConnectionLease> AcquireConnectionAsync(string url,
        CancellationToken cancellationToken, long? fileSizeBytes = null)
    {
        var channel = ResolveChannel(fileSizeBytes);
        var quota = GetQuota(channel);

        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
        var hostEntry = AcquireHostQuotaEntry(host);
        DownloadQuotaLease? hostLease = null;
        try
        {
            hostLease = await hostEntry.Quota.AcquireAsync(1, () => ModNet.NetTaskConnectionsPerHostLimit,
                cancellationToken)
            .ConfigureAwait(false);
            var channelLease = await quota.AcquireAsync(1,
                () => Math.Clamp(ModNet.GetChannelConnectionLimit(channel == DownloadChannel.Large),
                    1, ModNet.NetTaskConnectionLimitMax), cancellationToken)
                .ConfigureAwait(false);
            Interlocked.Increment(ref _activeConnectionCount);
            return new DownloadConnectionLease(channelLease, hostLease, hostEntry);
        }
        catch
        {
            hostLease?.Dispose();
            ReleaseHostQuotaEntry(hostEntry);
            throw;
        }
    }

    public static async ValueTask<DownloadBufferLease> RentBufferAsync(int minBytes, CancellationToken cancellationToken)
    {
        var lease = await BufferQuota.AcquireAsync(minBytes, () => ModNet.NetTaskBufferBudgetBytes, cancellationToken).ConfigureAwait(false);
        return new DownloadBufferLease(lease, ArrayPool<byte>.Shared.Rent(minBytes));
    }

    internal static void ReleaseConnection()
    {
        Interlocked.Decrement(ref _activeConnectionCount);
    }

    /// <summary>连接上限改变后通知两条通道重新计算可放行的等待者。</summary>
    public static void OnConnectionLimitChanged()
    {
        LargeConnectionQuota.NotifyCapacityChanged();
        SmallConnectionQuota.NotifyCapacityChanged();
    }

    #region Throttle

    // use token bucket

    private const long ThrottleBurstBytes = 128L * 1024;
    private const int MaxThrottleDelayMilliseconds = 2_000;

    private static readonly Lock ThrottleScheduleLock = new();

    private static double _throttleTokens;

    // the last Stopwatch timestamp of refill tokens
    // 0 means not be initialized
    private static long _throttleLastRefillTikck;

    public static async Task ThrottleAsync(int bytes, CancellationToken ct)
    {
        var limitHigh = ModNet.NetTaskSpeedLimitHigh;
        if (limitHigh <= 0 || bytes <= 0)
        {
            return;
        }

        long waitTicks;
        lock (ThrottleScheduleLock)
        {
            var now = Stopwatch.GetTimestamp();

            // will be initialize at first calling
            if (_throttleLastRefillTikck == 0)
            {
                _throttleLastRefillTikck = now;
                _throttleTokens = ThrottleBurstBytes;
            }
            else
            {
                var elaspedTicks = now - _throttleLastRefillTikck;
                if (elaspedTicks > 0)
                {
                    var refill = elaspedTicks * (double)limitHigh / Stopwatch.Frequency;
                    _throttleTokens = Math.Min(ThrottleBurstBytes, _throttleTokens + refill);
                    _throttleLastRefillTikck = now;
                }
            }

            // use tokens
            // alow be negative
            _throttleTokens -= bytes;

            if (_throttleTokens >= 0)
            {
                return;
            }

            waitTicks = (long)Math.Ceiling(-_throttleTokens * Stopwatch.Frequency / limitHigh);
        }

        if (waitTicks <= 0)
        {
            return;
        }

        var deadlineTick = Stopwatch.GetTimestamp() + waitTicks;
        var maxChunkTicks = MaxThrottleDelayMilliseconds > 0
            ? (long)Math.Ceiling(MaxThrottleDelayMilliseconds * (double)Stopwatch.Frequency / 1000.0)
            : long.MaxValue;

        try
        {
            while (true)
            {
                var currentLimit = ModNet.NetTaskSpeedLimitHigh;
                if (currentLimit <= 0)
                {
                    break;
                }

                if (currentLimit != limitHigh)
                {
                    lock (ThrottleScheduleLock)
                    {
                        limitHigh = ModNet.NetTaskSpeedLimitHigh;
                        if (limitHigh <= 0)
                            break;

                        if (_throttleTokens < 0)
                        {
                            var now = Stopwatch.GetTimestamp();
                            var newWaitTicks = (long)Math.Ceiling(-_throttleTokens * Stopwatch.Frequency / limitHigh);
                            deadlineTick = now + newWaitTicks;
                        }
                        else
                        {
                            break;
                        }
                    }
                }

                var remainingTicks = deadlineTick - Stopwatch.GetTimestamp();
                if (remainingTicks <= 0)
                {
                    break;
                }

                var chunkTicks = Math.Min(remainingTicks, maxChunkTicks);
                var chunkMs = (long)Math.Ceiling(chunkTicks * 1000d / Stopwatch.Frequency);

                if (chunkMs <= 0)
                {
                    chunkMs = 1;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(chunkMs), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            lock (ThrottleScheduleLock)
            {
                _throttleTokens = Math.Min(ThrottleBurstBytes, _throttleTokens + bytes);
            }

            throw;
        }
    }

    #endregion

    private static HostQuotaEntry AcquireHostQuotaEntry(string host)
    {
        while (true)
        {
            var entry = HostConnectionQuotas.GetOrAdd(host, static key => new HostQuotaEntry(key));
            var referenceAdded = entry.TryAddReference();
            if (referenceAdded)
            {
                if (HostConnectionQuotas.TryGetValue(host, out var current) && ReferenceEquals(entry, current))
                    return entry;

                ReleaseHostQuotaEntry(entry);
                continue;
            }

            if (HostConnectionQuotas.TryGetValue(host, out var retiredEntry) && ReferenceEquals(entry, retiredEntry))
                ((ICollection<KeyValuePair<string, HostQuotaEntry>>)HostConnectionQuotas)
                    .Remove(new KeyValuePair<string, HostQuotaEntry>(host, entry));
        }
    }

    internal static void ReleaseHostQuotaEntry(HostQuotaEntry entry)
    {
        if (!entry.ReleaseReference())
            return;

        ((ICollection<KeyValuePair<string, HostQuotaEntry>>)HostConnectionQuotas)
            .Remove(new KeyValuePair<string, HostQuotaEntry>(entry.Host, entry));
    }
}

internal sealed class DownloadConnectionLease(DownloadQuotaLease globalLease, DownloadQuotaLease hostLease,
    HostQuotaEntry hostEntry) : IDisposable
{
    private DownloadQuotaLease? _globalLease = globalLease;
    private DownloadQuotaLease? _hostLease = hostLease;
    private HostQuotaEntry? _hostEntry = hostEntry;

    public void Dispose()
    {
        var globalLease = Interlocked.Exchange(ref _globalLease, null);
        if (globalLease is null)
            return;

        DownloadResourceManager.ReleaseConnection();
        globalLease.Dispose();
        Interlocked.Exchange(ref _hostLease, null)?.Dispose();
        var hostEntry = Interlocked.Exchange(ref _hostEntry, null);
        if (hostEntry is not null)
            DownloadResourceManager.ReleaseHostQuotaEntry(hostEntry);
    }
}

internal sealed class DownloadBufferLease(DownloadQuotaLease lase, byte[] buffer) : IDisposable
{
    public byte[] Buffer { get; } = buffer;
    public int Length => Buffer.Length;


    /// <inheritdoc />
    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(Buffer);
        lase.Dispose();
    }
}

internal sealed class DownloadQuotaLease : IDisposable
{
    private AsyncQuota? _quota;
    private readonly long _amount;

    internal DownloadQuotaLease(AsyncQuota quota, long amount)
    {
        _quota = quota;
        _amount = amount;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _quota, null)?.Release(_amount);
    }
}

internal sealed class AsyncQuota
{
    private readonly Lock _lock = new();
    private readonly LinkedList<QuotaWaiter> _waiters = new();
    private long _used;

    public async ValueTask<DownloadQuotaLease> AcquireAsync(long amount, Func<long> getCapacity,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);

        QuotaWaiter waiter;
        lock (_lock)
        {
            var capacity = Math.Max(amount, getCapacity());
            if (_waiters.Count == 0 && _used + amount <= capacity)
            {
                _used += amount;
                return new DownloadQuotaLease(this, amount);
            }

            waiter = new QuotaWaiter(amount, getCapacity);
            waiter.Node = _waiters.AddLast(waiter);
        }

        try
        {
            await waiter.Tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new DownloadQuotaLease(this, amount);
        }
        catch
        {
            lock (_lock)
            {
                if (waiter.Node?.List is not null)
                {
                    _waiters.Remove(waiter.Node);
                }
                else if (waiter.Assigned)
                {
                    _used = Math.Max(0, _used - amount);
                    PromoteWaitersLocked();
                }
            }
            throw;
        }
    }

    public void Release(long amount)
    {
        List<QuotaWaiter> waitersToWake = [];
        lock (_lock)
        {
            _used = Math.Max(0, _used - amount);
            PromoteWaitersLocked(waitersToWake);
        }

        foreach (var waiter in waitersToWake)
            waiter.Tcs.TrySetResult();
    }

    public void NotifyCapacityChanged()
    {
        List<QuotaWaiter> waitersToWake = [];
        lock (_lock)
        {
            PromoteWaitersLocked(waitersToWake);
        }

        foreach (var waiter in waitersToWake)
            waiter.Tcs.TrySetResult();
    }

    private void PromoteWaitersLocked(List<QuotaWaiter>? waitersToWake = null)
    {
        var current = _waiters.First;
        while (current is not null)
        {
            var waiter = current.Value;
            var capacity = Math.Max(waiter.Amount, waiter.GetCapacity());
            if (_used + waiter.Amount <= capacity)
            {
                _used += waiter.Amount;
                waiter.Assigned = true;
                var next = current.Next;
                _waiters.Remove(current);
                if (waitersToWake is not null)
                    waitersToWake.Add(waiter);
                else
                    waiter.Tcs.TrySetResult();
                current = next;
            }
            else
            {
                break;
            }
        }
    }

    private sealed class QuotaWaiter(long amount, Func<long> getCapacity)
    {
        public long Amount { get; } = amount;
        public Func<long> GetCapacity { get; } = getCapacity;
        public TaskCompletionSource Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LinkedListNode<QuotaWaiter>? Node { get; set; }
        public bool Assigned { get; set; }
    }
}

internal sealed class HostQuotaEntry(string host)
{
    private readonly Lock _lock = new();
    private int _referenceCount;
    private bool _retired;

    public string Host { get; } = host;
    public AsyncQuota Quota { get; } = new();

    public bool TryAddReference()
    {
        lock (_lock)
        {
            if (_retired)
                return false;

            _referenceCount++;
            return true;
        }
    }

    public bool ReleaseReference()
    {
        lock (_lock)
        {
            if (--_referenceCount != 0)
                return false;

            _retired = true;
            return true;
        }
    }
}
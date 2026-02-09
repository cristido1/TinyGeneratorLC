using System;
using System.Threading;

namespace TinyGenerator.Services;

/// <summary>
/// Monotonic id generator for (1) logical log thread ids and (2) story ids.
/// Values are initialized at application startup from the database and then
/// incremented atomically for each new request.
/// </summary>
public sealed class NumeratorService
{
    private const int MaxAllowedThreadId = int.MaxValue - 1;
    private const string ThreadIdKey = "threadid";
    private const string StoryIdKey = "story_id";

    private readonly DatabaseService _db;

    private long _storyIdCounter;
    private int _threadIdCounter;
    private int _initialized;

    public NumeratorService(DatabaseService db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>
    /// Initializes counters from persisted data. Safe to call multiple times.
    /// Must be called once at startup after DB migrations.
    /// </summary>
    public void InitializeFromDatabase()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 1)
            return;

        var persistedStoryId = _db.GetNumeratorState(StoryIdKey) ?? 0;
        var persistedThreadId = (int)Math.Min(_db.GetNumeratorState(ThreadIdKey) ?? 0, int.MaxValue);

        var maxStoryIdFromDb = _db.GetMaxStoryId();
        var maxThreadIdFromDb = _db.GetMaxLogThreadId();

        var maxStoryId = Math.Max(persistedStoryId, maxStoryIdFromDb);
        var maxThreadId = Math.Max(persistedThreadId, maxThreadIdFromDb);

        if (maxThreadId >= MaxAllowedThreadId)
        {
            throw new InvalidOperationException($"Max ThreadId in DB is {maxThreadId}, too high to allocate new ids without hitting int.MaxValue. Clear logs or adjust strategy.");
        }

        // Next allocations must be > current max.
        _storyIdCounter = maxStoryId;
        _threadIdCounter = Math.Max(0, maxThreadId);

        // Persist the effective starting points so deletes don't reduce counters.
        _db.SetNumeratorState(StoryIdKey, _storyIdCounter);
        _db.SetNumeratorState(ThreadIdKey, _threadIdCounter);
    }

    public long NextStoryId()
    {
        EnsureInitialized();
        var next = Interlocked.Increment(ref _storyIdCounter);
        _db.SetNumeratorState(StoryIdKey, next);
        return next;
    }

    public int NextThreadId()
    {
        EnsureInitialized();

        var next = Interlocked.Increment(ref _threadIdCounter);
        if (next <= 0)
        {
            // Extremely unlikely, but handle overflow/invalid.
            next = Interlocked.Exchange(ref _threadIdCounter, 1);
        }

        if (next >= int.MaxValue)
        {
            // Hard stop: requirement says never use int.MaxValue.
            throw new InvalidOperationException("ThreadId counter exhausted (reached int.MaxValue). Please reset logs or adjust strategy.");
        }

        _db.SetNumeratorState(ThreadIdKey, next);
        return next;
    }

    public void ResetThreadIds()
    {
        EnsureInitialized();
        Interlocked.Exchange(ref _threadIdCounter, 0);
        _db.SetNumeratorState(ThreadIdKey, 0);
    }

    public long EnsureStoryIdForStoryDbId(long storyDbId)
    {
        EnsureInitialized();

        var existing = _db.GetStoryCorrelationId(storyDbId);
        if (existing.HasValue && existing.Value > 0) return existing.Value;

        // Allocate and persist; DatabaseService enforces write-once (only when NULL).
        var allocated = NextStoryId();
        try
        {
            return _db.EnsureStoryCorrelationId(storyDbId, allocated);
        }
        catch
        {
            // If a race happened (or story missing), re-read.
            var reread = _db.GetStoryCorrelationId(storyDbId);
            if (reread.HasValue && reread.Value > 0) return reread.Value;
            throw;
        }
    }

    private void EnsureInitialized()
    {
        if (Volatile.Read(ref _initialized) == 1) return;
        throw new InvalidOperationException("NumeratorService is not initialized. Call InitializeFromDatabase() at startup.");
    }
}

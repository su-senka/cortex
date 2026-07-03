namespace RagAssistant.Web.Services;

public enum IngestionState { Pending, Running, Completed, Failed }

/// <summary>
/// Tracks the state of the most recent ingestion run so the readiness probe and
/// /api/ingest/status can report it. Thread-safe: the background service writes,
/// request handlers read.
/// </summary>
public sealed class IngestionStatusService
{
    private readonly Lock _lock = new();

    private IngestionState _state = IngestionState.Pending;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _completedAt;
    private string? _error;

    public void MarkRunning()
    {
        lock (_lock)
        {
            _state       = IngestionState.Running;
            _startedAt   = DateTimeOffset.UtcNow;
            _completedAt = null;
            _error       = null;
        }
    }

    public void MarkCompleted()
    {
        lock (_lock)
        {
            _state       = IngestionState.Completed;
            _completedAt = DateTimeOffset.UtcNow;
        }
    }

    public void MarkFailed(string error)
    {
        lock (_lock)
        {
            _state       = IngestionState.Failed;
            _completedAt = DateTimeOffset.UtcNow;
            _error       = error;
        }
    }

    public IngestionStatusSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new IngestionStatusSnapshot(
                _state.ToString(), _startedAt, _completedAt, _error);
        }
    }
}

public sealed record IngestionStatusSnapshot(
    string State,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? Error);

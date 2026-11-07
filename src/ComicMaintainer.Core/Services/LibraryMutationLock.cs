using ComicMaintainer.Core.Interfaces;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Default <see cref="ILibraryMutationLock"/>: a single-holder semaphore, so
/// synchronous callers (the Suwayomi importer) and asynchronous ones (folder
/// combine) exclude each other.
/// </summary>
public sealed class LibraryMutationLock : ILibraryMutationLock, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Each acquisition gets a fresh token, stored both here and in the
    // holder's async flow. A flow whose token is the current one already holds
    // the lock, so taking it again fails loudly instead of waiting on itself.
    // Work started inside a locked section that only asks for the lock after
    // the section ended carries a stale token and is not mistaken for the
    // holder.
    private readonly AsyncLocal<object?> _flowToken = new();
    private object? _holderToken;

    public T Execute<T>(Func<T> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ThrowIfHeldByThisFlow();

        _gate.Wait(cancellationToken);
        Enter();
        try
        {
            return action();
        }
        finally
        {
            Exit();
        }
    }

    public void Execute(Action action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        Execute<bool>(() =>
        {
            action();
            return true;
        }, cancellationToken);
    }

    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ThrowIfHeldByThisFlow();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        // AsyncLocal changes made inside an async method do not flow back to
        // its caller, so the caller never sees this acquisition's token.
        Enter();
        try
        {
            return await action(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Exit();
        }
    }

    public void Dispose() => _gate.Dispose();

    private void Enter()
    {
        var token = new object();
        Volatile.Write(ref _holderToken, token);
        _flowToken.Value = token;
    }

    private void Exit()
    {
        _flowToken.Value = null;
        Volatile.Write(ref _holderToken, null);
        _gate.Release();
    }

    private void ThrowIfHeldByThisFlow()
    {
        var token = _flowToken.Value;
        if (token is not null && ReferenceEquals(token, Volatile.Read(ref _holderToken)))
        {
            throw new InvalidOperationException(
                "The library mutation lock is already held by this operation and is not reentrant.");
        }
    }
}

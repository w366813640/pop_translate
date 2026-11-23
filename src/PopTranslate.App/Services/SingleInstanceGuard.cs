namespace PopTranslate.App.Services;

/// <summary>
/// Guarantees a single running PopTranslate instance per interactive user session
/// and signals the running instance when a secondary launch occurs.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Local\PopTranslate.V1.SingleInstance";
    private const string ActivateEventName = @"Local\PopTranslate.V1.ActivateSignal";

    private Mutex? _mutex;
    private EventWaitHandle? _activateEvent;
    private CancellationTokenSource? _listenCts;
    private bool _ownsMutex;
    private bool _disposed;

    public event EventHandler? SecondInstanceSignaled;

    public bool TryAcquire()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_ownsMutex)
        {
            return true;
        }

        var mutex = new Mutex(initiallyOwned: true, name: MutexName, out var createdNew);
        if (!createdNew)
        {
            // Check if the previous holder crashed and abandoned the mutex.
            try
            {
                if (!mutex.WaitOne(0, exitContext: false))
                {
                    mutex.Dispose();
                    return false;
                }
            }
            catch (AbandonedMutexException)
            {
                // Acquired abandoned mutex cleanly.
            }
        }

        _mutex = mutex;
        _ownsMutex = true;
        StartSignalListener();
        return true;
    }

    public void SignalExistingInstance()
    {
        try
        {
            using var existingEvent = EventWaitHandle.OpenExisting(ActivateEventName);
            existingEvent.Set();
        }
        catch
        {
            // Ignore if the primary instance is shutting down or hasn't opened the handle yet.
        }
    }

    /// <summary>
    /// Releases the single-instance lock prior to spawning an elevated replacement process.
    /// </summary>
    public void ReleaseForRestart()
    {
        StopSignalListener();

        if (_mutex is not null)
        {
            if (_ownsMutex)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch
                {
                    // Ignore release mismatch on restart.
                }
            }

            _mutex.Dispose();
            _mutex = null;
            _ownsMutex = false;
        }
    }

    private void StartSignalListener()
    {
        StopSignalListener();

        _activateEvent = new EventWaitHandle(
            initialState: false,
            mode: EventResetMode.AutoReset,
            name: ActivateEventName);
        _listenCts = new CancellationTokenSource();
        var token = _listenCts.Token;
        var waitHandle = _activateEvent;

        _ = Task.Run(() =>
        {
            var handles = new WaitHandle[] { waitHandle, token.WaitHandle };
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var index = WaitHandle.WaitAny(handles);
                    if (index == 0)
                    {
                        SecondInstanceSignaled?.Invoke(this, EventArgs.Empty);
                    }
                    else
                    {
                        break;
                    }
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
            }
        }, CancellationToken.None);
    }

    private void StopSignalListener()
    {
        if (_listenCts is not null)
        {
            try
            {
                _listenCts.Cancel();
            }
            catch
            {
                // Ignore cancel errors during shutdown.
            }

            _listenCts.Dispose();
            _listenCts = null;
        }

        if (_activateEvent is not null)
        {
            _activateEvent.Dispose();
            _activateEvent = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseForRestart();
    }
}

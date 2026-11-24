using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace PopTranslate.App.Services;

/// <summary>
/// Guarantees a single running PopTranslate instance per interactive user session
/// and forwards command-line arguments from secondary launches over a current-user Named Pipe
/// (with cross-elevation Admin &lt;-&gt; Standard ACL support and EventWaitHandle fallback).
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Local\PopTranslate.V1.SingleInstance";
    private const string ActivateEventName = @"Local\PopTranslate.V1.ActivateSignal";
    private const int MaxIpcPayloadBytes = 16 * 1024;

    private readonly string _pipeName;
    private Mutex? _mutex;
    private EventWaitHandle? _activateEvent;
    private CancellationTokenSource? _listenCts;
    private bool _ownsMutex;
    private bool _disposed;

    public SingleInstanceGuard()
    {
        _pipeName = $"PopTranslate.V1.Ipc.{GetCurrentUserSidTag()}";
    }

    public event EventHandler? SecondInstanceSignaled;

    public event EventHandler<string[]>? SecondInstanceArgumentsReceived;

    public bool TryAcquire()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_ownsMutex)
        {
            return true;
        }

        Mutex mutex;
        bool createdNew;
        try
        {
            var security = CreateCurrentUserMutexSecurity();
            mutex = security is not null
                ? MutexAcl.Create(initiallyOwned: true, name: MutexName, out createdNew, security)
                : new Mutex(initiallyOwned: true, name: MutexName, out createdNew);
        }
        catch (UnauthorizedAccessException)
        {
            // Another instance of PopTranslate (e.g. elevated Administrator instance) already owns the mutex.
            return false;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }

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
            catch (UnauthorizedAccessException)
            {
                mutex.Dispose();
                return false;
            }
        }

        _mutex = mutex;
        _ownsMutex = true;
        StartIpcListeners();
        return true;
    }

    public void SignalExistingInstance(string[]? args = null)
    {
        var payload = args ?? [];

        if (TrySendArgumentsOverPipe(payload))
        {
            return;
        }

        // Graceful fallback if named pipe server is busy or unreachable.
        try
        {
            if (EventWaitHandleAcl.TryOpenExisting(
                ActivateEventName,
                EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize,
                out var existingEvent) && existingEvent is not null)
            {
                using (existingEvent)
                {
                    existingEvent.Set();
                }
            }
        }
        catch
        {
            // Never throw across Admin <-> Unelevated boundary or during shutdown races.
        }
    }

    /// <summary>
    /// Releases the single-instance lock prior to spawning an elevated replacement process.
    /// </summary>
    public void ReleaseForRestart()
    {
        StopIpcListeners();

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

    private bool TrySendArgumentsOverPipe(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                serverName: ".",
                pipeName: _pipeName,
                direction: PipeDirection.Out,
                options: PipeOptions.Asynchronous);

            client.Connect(timeout: 600);

            var bytes = JsonSerializer.SerializeToUtf8Bytes(args);
            if (bytes.Length > MaxIpcPayloadBytes)
            {
                bytes = JsonSerializer.SerializeToUtf8Bytes(Array.Empty<string>());
            }

            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            return true;
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Warn("SingleInstance", "Named pipe signal fell back to event handle.", ex);
            return false;
        }
    }

    private void StartIpcListeners()
    {
        StopIpcListeners();

        _listenCts = new CancellationTokenSource();
        var token = _listenCts.Token;

        _ = Task.Run(() => RunNamedPipeListenerLoopAsync(token), CancellationToken.None);
        StartFallbackEventListener(token);
    }

    private async Task RunNamedPipeListenerLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = CreatePipeServerInstance();
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                var receivedArgs = await ReadPipeArgumentsAsync(server, token).ConfigureAwait(false);
                RaiseSecondInstanceActivated(receivedArgs);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }
            }
            catch (Exception ex)
            {
                AppDiagnosticsLogger.Warn("SingleInstance", "Named pipe listener iteration failed; retrying.", ex);
                try
                {
                    await Task.Delay(250, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                if (server is not null)
                {
                    try
                    {
                        server.Dispose();
                    }
                    catch
                    {
                        // Ignore pipe disposal errors.
                    }
                }
            }
        }
    }

    private NamedPipeServerStream CreatePipeServerInstance()
    {
        var pipeSecurity = CreateCurrentUserPipeSecurity();
        if (pipeSecurity is not null)
        {
            try
            {
                return NamedPipeServerStreamAcl.Create(
                    pipeName: _pipeName,
                    direction: PipeDirection.In,
                    maxNumberOfServerInstances: 4,
                    transmissionMode: PipeTransmissionMode.Byte,
                    options: PipeOptions.Asynchronous,
                    inBufferSize: 4096,
                    outBufferSize: 0,
                    pipeSecurity: pipeSecurity,
                    inheritability: HandleInheritability.None,
                    additionalAccessRights: default);
            }
            catch
            {
                // Fall back to default pipe creation if custom DACL fails in restricted environments.
            }
        }

        return new NamedPipeServerStream(
            pipeName: _pipeName,
            direction: PipeDirection.In,
            maxNumberOfServerInstances: 4,
            transmissionMode: PipeTransmissionMode.Byte,
            options: PipeOptions.Asynchronous);
    }

    private static async Task<string[]> ReadPipeArgumentsAsync(NamedPipeServerStream server, CancellationToken outerToken)
    {
        using var readTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
        readTimeoutCts.CancelAfter(TimeSpan.FromMilliseconds(1500));

        using var memory = new MemoryStream(capacity: 256);
        var buffer = new byte[1024];

        while (memory.Length < MaxIpcPayloadBytes)
        {
            var read = await server.ReadAsync(buffer.AsMemory(0, buffer.Length), readTimeoutCts.Token).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            memory.Write(buffer, 0, read);
        }

        if (memory.Length == 0)
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(memory.GetBuffer().AsSpan(0, (int)memory.Length)) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void StartFallbackEventListener(CancellationToken token)
    {
        try
        {
            var security = CreateCurrentUserEventSecurity();
            _activateEvent = security is not null
                ? EventWaitHandleAcl.Create(
                    initialState: false,
                    mode: EventResetMode.AutoReset,
                    name: ActivateEventName,
                    out _,
                    security)
                : new EventWaitHandle(
                    initialState: false,
                    mode: EventResetMode.AutoReset,
                    name: ActivateEventName);

            var waitHandle = _activateEvent;
            _ = Task.Run(() =>
            {
                try
                {
                    var handles = new WaitHandle[] { waitHandle, token.WaitHandle };
                    while (!token.IsCancellationRequested)
                    {
                        var index = WaitHandle.WaitAny(handles);
                        if (index == 0)
                        {
                            RaiseSecondInstanceActivated([]);
                        }
                        else
                        {
                            break;
                        }
                    }
                }
                catch (ObjectDisposedException)
                {
                    // Clean shutdown when _listenCts or _activateEvent is disposed.
                }
                catch
                {
                    // Ignore wait handle shutdown race.
                }
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Warn("SingleInstance", "Fallback EventWaitHandle listener unavailable.", ex);
        }
    }

    private void RaiseSecondInstanceActivated(string[] args)
    {
        try
        {
            SecondInstanceArgumentsReceived?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Error("SingleInstance", "Error handling second-instance arguments.", ex);
        }

        try
        {
            SecondInstanceSignaled?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Error("SingleInstance", "Error handling second-instance signal.", ex);
        }
    }

    private void StopIpcListeners()
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

    private static MutexSecurity? CreateCurrentUserMutexSecurity()
    {
        try
        {
            var security = new MutexSecurity();
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.User is not null)
            {
                security.AddAccessRule(new MutexAccessRule(
                    identity.User,
                    MutexRights.FullControl,
                    AccessControlType.Allow));
            }

            security.AddAccessRule(new MutexAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                MutexRights.FullControl,
                AccessControlType.Allow));

            return security;
        }
        catch
        {
            return null;
        }
    }

    private static PipeSecurity? CreateCurrentUserPipeSecurity()
    {
        try
        {
            var security = new PipeSecurity();
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.User is not null)
            {
                security.AddAccessRule(new PipeAccessRule(
                    identity.User,
                    PipeAccessRights.FullControl,
                    AccessControlType.Allow));
            }

            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                PipeAccessRights.FullControl,
                AccessControlType.Allow));

            return security;
        }
        catch
        {
            return null;
        }
    }

    private static EventWaitHandleSecurity? CreateCurrentUserEventSecurity()
    {
        try
        {
            var security = new EventWaitHandleSecurity();
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.User is not null)
            {
                security.AddAccessRule(new EventWaitHandleAccessRule(
                    identity.User,
                    EventWaitHandleRights.FullControl,
                    AccessControlType.Allow));
            }

            security.AddAccessRule(new EventWaitHandleAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                EventWaitHandleRights.FullControl,
                AccessControlType.Allow));

            return security;
        }
        catch
        {
            return null;
        }
    }

    private static string GetCurrentUserSidTag()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var raw = identity.User?.Value ?? Environment.UserName;
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(hash.AsSpan(0, 8));
        }
        catch
        {
            return "DefaultUser";
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

namespace XiaobianPet.Models;

/// <summary>
/// Owns cancellation for background frame decoding that belongs to one pet
/// window. Closing the window cancels every in-flight preparation before the
/// token source is disposed.
/// </summary>
public sealed class PetAnimationPreparationLifetime : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public CancellationToken Token
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _cts.Token;
            }
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _cts.Cancel();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}

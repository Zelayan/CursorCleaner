using CursorCleaner.Models;

namespace CursorCleaner.Services;

public interface IScanResultStore
{
    ScanResult? Latest { get; }
    event EventHandler<ScanResult?>? Changed;
    void Set(ScanResult result);
    void Clear();
}

public sealed class ScanResultStore : IScanResultStore
{
    private readonly object _gate = new();
    private ScanResult? _latest;

    public ScanResult? Latest
    {
        get
        {
            lock (_gate)
            {
                return _latest;
            }
        }
    }

    public event EventHandler<ScanResult?>? Changed;

    public void Set(ScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_gate)
        {
            _latest = result;
        }

        Changed?.Invoke(this, result);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _latest = null;
        }

        Changed?.Invoke(this, null);
    }
}

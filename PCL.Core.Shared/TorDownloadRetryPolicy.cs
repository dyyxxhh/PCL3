namespace PCL.Core.Shared;

/// <summary>Tracks direct download outcomes for one task, including its first five candidate files.</summary>
public sealed class TorDownloadRetryPolicy(int initialFileCount = 5, int initialAttempts = 5, int isolatedAttempts = 2, int torAttempts = 3)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _failures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _initialFiles = new(StringComparer.Ordinal);
    private bool _initialised;
    private bool _initialSuccess;
    private bool _batchTor;

    public bool BatchTorEnabled { get { lock (_gate) return _batchTor; } }
    public int TorAttempts => torAttempts;

    public void ObserveInitialFiles(IEnumerable<string> files)
    {
        lock (_gate)
        {
            if (_initialised) return;
            foreach (var file in files.Distinct(StringComparer.Ordinal).Take(initialFileCount)) _initialFiles.Add(file);
            _initialised = true;
        }
    }

    public void RecordSuccess(string file)
    {
        lock (_gate)
            if (_initialFiles.Contains(file)) _initialSuccess = true;
    }

    public TorRetryDecision RecordFailure(string file)
    {
        lock (_gate)
        {
            var count = _failures.GetValueOrDefault(file) + 1;
            _failures[file] = count;
            if (!_initialSuccess && _initialFiles.Count > 0 && _initialFiles.All(item => _failures.GetValueOrDefault(item) >= initialAttempts))
                _batchTor = true;
            var useTor = _batchTor || count >= (_initialFiles.Contains(file) && !_initialSuccess ? initialAttempts : isolatedAttempts);
            return new TorRetryDecision(useTor, _batchTor, !useTor);
        }
    }

    public readonly record struct TorRetryDecision(bool UseTor, bool SwitchRemainingFiles, bool RetryDirect);
}

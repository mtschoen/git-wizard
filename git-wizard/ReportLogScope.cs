namespace GitWizard.CLI;

sealed class ReportLogScope : IDisposable
{
    readonly Action<string?, GitWizardLog.LogType>? _previous;
    readonly UpdateHandler _handler;
    readonly RunConfiguration _configuration;
    readonly TextWriter _stdout;
    readonly TextWriter _stderr;
    readonly object _outputLock = new();

    internal ReportLogScope(UpdateHandler handler, RunConfiguration configuration,
        TextWriter stdout, TextWriter stderr)
    {
        _handler = handler;
        _configuration = configuration;
        _stdout = stdout;
        _stderr = stderr;
        _previous = GitWizardLog.TypedLogMethod;
        GitWizardLog.TypedLogMethod = Write;
    }

    void Write(string? message, GitWizardLog.LogType type)
    {
        if (string.IsNullOrEmpty(message)) return;
        if (type is GitWizardLog.LogType.Warning or GitWizardLog.LogType.Error)
        {
            _handler.RecordWarning(message);
            return;
        }
        if (_configuration.Silent || GitWizardLog.SilentMode) return;
        lock (_outputLock)
        {
            if (type is GitWizardLog.LogType.Verbose or GitWizardLog.LogType.Debug)
                _stderr.WriteLine(message);
            else if (!_configuration.Json)
                _stdout.WriteLine(message);
            else if (GitWizardLog.VerboseMode)
                _stderr.WriteLine(message);
        }
    }

    public void Dispose() => GitWizardLog.TypedLogMethod = _previous;
}

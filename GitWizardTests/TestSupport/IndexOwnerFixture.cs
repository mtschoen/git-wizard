using GitWizard;
using MFTLibTestExtensions;

namespace GitWizardTests.TestSupport;

sealed class IndexOwnerFixture : IAsyncDisposable
{
    readonly PackedIndexFixture _files = new();
    readonly CountingConnection _connection = new();
    MftIndexSession? _owner;
    public MftIndexSession Owner => _owner ??
        throw new InvalidOperationException("The index owner fixture has not started.");
    public int ConnectionDisposals => _connection.Disposals;

    public async Task StartAsync(ScriptedWatchSource? watchSource = null)
    {
        _files.AddMarker("repo");
        var index = watchSource is null
            ? await _files.OpenIndexAsync()
            : await _files.OpenMftIndexAsync(watchSource, 'X');
        _owner = new MftIndexSession(index, _connection, ["X"], [], new Dictionary<string, string>());
    }

    public async ValueTask DisposeAsync()
    {
        if (_owner is not null)
        {
            await _owner.DisposeAsync();
        }

        _files.Dispose();
    }

    sealed class CountingConnection : IAsyncDisposable
    {
        public int Disposals { get; private set; }
        public ValueTask DisposeAsync()
        {
            Disposals++;
            return ValueTask.CompletedTask;
        }
    }
}

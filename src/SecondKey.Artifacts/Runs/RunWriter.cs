using System.Text;
using System.Text.Json;
using SecondKey.Artifacts.Json;

namespace SecondKey.Artifacts.Runs;

/// <summary>Writes a <c>*.skrun</c> file line by line. The replay harness is single-writer.</summary>
public sealed class RunWriter : IAsyncDisposable
{
    private readonly StreamWriter _writer;
    private int _results;
    private bool _ended;

    private RunWriter(StreamWriter writer) => _writer = writer;

    public static async Task<RunWriter> CreateAsync(string path, RunHeader header, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var writer = new RunWriter(new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { NewLine = "\n" });
        await writer.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        return writer;
    }

    public int Results => _results;

    public async Task WriteAsync(RunEvent runEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runEvent);
        if (_ended)
        {
            throw new InvalidOperationException("The run has ended; nothing may follow run.end.");
        }

        if (runEvent is ExchangeResult)
        {
            _results++;
        }

        _ended = runEvent is RunEnd;
        await _writer.WriteLineAsync(JsonSerializer.Serialize(runEvent, ArtifactJson.Lines).AsMemory(), cancellationToken).ConfigureAwait(false);
        await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await _writer.DisposeAsync().ConfigureAwait(false);
}

using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

namespace CodeRag.Analyzers.TypeScript;

public partial class TsCompilerAnalyzer
{
    /// <summary>
    /// Owns one Node child process and serializes request/response over its
    /// stdin/stdout NDJSON channel. A background reader streams every stdout
    /// line into an unbounded channel; each request drains that channel until
    /// it sees an envelope whose <c>type</c> equals the completion sentinel
    /// (<c>"opened"</c> for <c>open</c>, <c>"done"</c> for analyze/reanalyze).
    /// One request at a time per session; the gate enforces it.
    /// </summary>
    private sealed class SidecarSession : IAsyncDisposable
    {
        private readonly Process _proc;
        private readonly string _projectPath;
        private readonly Channel<string> _stdoutChannel;
        private readonly Task _readerTask;
        private readonly Task _errReaderTask;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private volatile bool _disposed;

        public SidecarSession(Process proc, string projectPath)
        {
            _proc = proc;
            _projectPath = projectPath;
            _stdoutChannel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
            });

            _readerTask = Task.Run(async () =>
            {
                try
                {
                    string? line;
                    while ((line = await _proc.StandardOutput.ReadLineAsync()) is not null)
                        await _stdoutChannel.Writer.WriteAsync(line);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"ts-sidecar reader crashed: {ex.Message}");
                }
                finally
                {
                    _stdoutChannel.Writer.TryComplete();
                }
            });

            _errReaderTask = Task.Run(async () =>
            {
                try
                {
                    string? line;
                    while ((line = await _proc.StandardError.ReadLineAsync()) is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                            Console.Error.WriteLine($"ts-sidecar stderr: {line}");
                    }
                }
                catch { /* nothing we can do */ }
            });
        }

        /// <summary>
        /// Send one NDJSON request and drain stdout until the completion sentinel
        /// is observed. Returns every non-sentinel, non-log line for the caller
        /// to parse. <c>log</c> envelopes are surfaced to the console and
        /// dropped from the returned list.
        /// </summary>
        public async Task<IReadOnlyList<string>> SendAsync(string requestJson, string completionType)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SidecarSession));
            await _gate.WaitAsync();
            try
            {
                if (_proc.HasExited)
                    throw new InvalidOperationException(
                        $"ts-sidecar for '{_projectPath}' has exited (code {_proc.ExitCode}).");

                await _proc.StandardInput.WriteLineAsync(requestJson);
                await _proc.StandardInput.FlushAsync();

                var collected = new List<string>();
                var reader = _stdoutChannel.Reader;
                while (await reader.WaitToReadAsync())
                {
                    while (reader.TryRead(out var line))
                    {
                        if (string.IsNullOrEmpty(line)) continue;
                        var type = PeekType(line);
                        if (type == completionType) return collected;
                        if (type == "log")
                        {
                            var msg = PeekMessage(line);
                            if (!string.IsNullOrEmpty(msg))
                                Console.WriteLine($"ts-sidecar: {msg}");
                            continue;
                        }
                        collected.Add(line);
                    }
                }

                throw new InvalidOperationException(
                    $"ts-sidecar exited before producing a '{completionType}' response.");
            }
            finally
            {
                _gate.Release();
            }
        }

        private static string? PeekType(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
                    return t.GetString();
            }
            catch { /* malformed line — main parser will log it */ }
            return null;
        }

        private static string? PeekMessage(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                    return m.GetString();
            }
            catch { }
            return null;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (!_proc.HasExited)
                {
                    try { await _proc.StandardInput.WriteLineAsync("{\"op\":\"shutdown\"}"); } catch { }
                    try { await _proc.StandardInput.FlushAsync(); } catch { }
                    if (!_proc.WaitForExit(2000))
                        _proc.Kill(entireProcessTree: true);
                }
            }
            catch { /* best-effort */ }
            finally
            {
                try { await Task.WhenAny(_readerTask, Task.Delay(1000)); } catch { }
                try { await Task.WhenAny(_errReaderTask, Task.Delay(1000)); } catch { }
                _proc.Dispose();
                _gate.Dispose();
            }
        }
    }
}

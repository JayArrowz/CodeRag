using System.Collections.Concurrent;
using System.Text;

namespace CodeRag.Dashboard.Services;

/// <summary>
/// A TextWriter that wraps the original Console.Out and, when the current async
/// flow has a registered job id, also appends written lines to that job's log.
/// Buffers partial writes until a newline is seen, so progress prints stay tidy.
/// </summary>
internal sealed class JobConsoleWriter : TextWriter
{
    public static readonly AsyncLocal<Guid?> CurrentJobId = new();
    private static readonly ConcurrentDictionary<Guid, IndexingJob> _jobs = new();
    private static readonly AsyncLocal<StringBuilder?> _buffer = new();

    private readonly TextWriter _inner;

    private JobConsoleWriter(TextWriter inner) => _inner = inner;
    public override Encoding Encoding => _inner.Encoding;

    public static void Install()
    {
        if (Console.Out is JobConsoleWriter) return;
        Console.SetOut(new JobConsoleWriter(Console.Out));
        Console.SetError(new JobConsoleWriter(Console.Error));
    }

    public static void Register(IndexingJob job) => _jobs[job.Id] = job;
    public static void Unregister(Guid id) => _jobs.TryRemove(id, out _);

    public override void Write(char value)
    {
        _inner.Write(value);
        AppendToJob(value.ToString());
    }

    public override void Write(string? value)
    {
        _inner.Write(value);
        if (!string.IsNullOrEmpty(value)) AppendToJob(value);
    }

    public override void WriteLine(string? value)
    {
        _inner.WriteLine(value);
        AppendToJob((value ?? "") + Environment.NewLine);
    }

    public override void WriteLine()
    {
        _inner.WriteLine();
        AppendToJob(Environment.NewLine);
    }

    private static void AppendToJob(string text)
    {
        var id = CurrentJobId.Value;
        if (id is null) return;
        if (!_jobs.TryGetValue(id.Value, out var job)) return;

        var buf = _buffer.Value ??= new StringBuilder();
        buf.Append(text);

        int newlineIdx;
        while ((newlineIdx = IndexOfNewline(buf)) >= 0)
        {
            var line = buf.ToString(0, newlineIdx);
            // remove the line plus its newline marker (1 or 2 chars)
            var skip = (newlineIdx + 1 < buf.Length && buf[newlineIdx] == '\r' && buf[newlineIdx + 1] == '\n') ? 2 : 1;
            buf.Remove(0, newlineIdx + skip);

            lock (job.Log)
            {
                job.Log.Add(line);
                if (job.Log.Count > 5000) job.Log.RemoveRange(0, job.Log.Count - 5000);
            }
            job.Touch();
        }
    }

    private static int IndexOfNewline(StringBuilder sb)
    {
        for (int i = 0; i < sb.Length; i++)
            if (sb[i] == '\n' || sb[i] == '\r') return i;
        return -1;
    }
}

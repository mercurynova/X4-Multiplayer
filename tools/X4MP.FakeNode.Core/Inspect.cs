using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.FakeNode;

/// <summary>
/// <c>fakenode inspect</c>: a client that connects (and walks the join path unless <c>--no-join</c>) and prints every frame it receives as one line:
/// time, lane, type, size and the decoded message. <c>--filter</c> limits it to some types, <c>--max-frames</c> ends the run after that many printed
/// lines. A debugging aid: the server, the mod and FakeNode itself can all be checked against what actually travels.
/// </summary>
public sealed class FrameInspector
{
    private const int MaxDepth = 3;
    private const int MaxItems = 4;
    private const int MaxLine = 600;

    private readonly Action<string> _write;
    private readonly Action _stop;
    private readonly HashSet<MsgType>? _filter;
    private readonly int _max;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly ConcurrentDictionary<MsgType, long> _counts = new();
    private long _printed;
    private long _total;
    private long _bytes;

    public FrameInspector(CliOptions options, Action<string> write, Action stop)
    {
        _write = write;
        _stop = stop;
        _max = options.MaxFrames;
        if (options.Filter.Count > 0)
        {
            _filter = [];
            foreach (string name in options.Filter)
            {
                if (Enum.TryParse<MsgType>(name, ignoreCase: true, out var type))
                    _filter.Add(type);
                else
                    _write($"inspect: unknown message type '{name}' in --filter (ignored)");
            }
        }
    }

    public long Printed => Interlocked.Read(ref _printed);

    public long Total => Interlocked.Read(ref _total);

    /// <summary>Called for every frame the node receives (TCP and UDP).</summary>
    public void Tap(Frame frame)
    {
        Interlocked.Increment(ref _total);
        Interlocked.Add(ref _bytes, frame.Payload.Length + FrameCodec.HeaderSize);
        _counts.AddOrUpdate(frame.Type, 1, (_, v) => v + 1);
        if (_filter is not null && !_filter.Contains(frame.Type))
            return;
        long n = Interlocked.Increment(ref _printed);
        if (_max > 0 && n > _max)
            return;
        _write(string.Create(CultureInfo.InvariantCulture, $"[{_clock.Elapsed.TotalSeconds,7:F2}s] {frame.Lane,-8} {frame.Type} ({frame.Payload.Length} B) {Describe(frame)}"));
        if (_max > 0 && n >= _max)
            _stop();
    }

    public string Summary() => string.Create(CultureInfo.InvariantCulture,
        $"inspect: frames-received={Total} bytes={Interlocked.Read(ref _bytes)} printed={Math.Min(Printed, _max > 0 ? _max : long.MaxValue)} " +
        $"by-type=[{string.Join(",", _counts.OrderByDescending(p => p.Value).ThenBy(p => p.Key).Take(12).Select(p => $"{p.Key}={p.Value}"))}]");

    /// <summary>The decoded message as <c>Name{field=value, ...}</c>, or why it could not be decoded.</summary>
    public static string Describe(Frame frame)
    {
        try
        {
            var descriptor = MessageRegistry.Default.GetDescriptor(frame.Type);
            var decoded = MessageRegistry.Default.Decode(frame);
            var unpack = descriptor.ClrType.GetMethod("UnPack", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
            if (unpack is null)
                return "(no object form)";
            var text = new StringBuilder();
            Format(text, unpack.Invoke(decoded, null), 0);
            return text.Length > MaxLine ? text.ToString(0, MaxLine) + "..." : text.ToString();
        }
        catch (Exception ex)
        {
            return $"(undecodable: {ex.GetType().Name}: {(ex is TargetInvocationException { InnerException: { } inner } ? inner.Message : ex.Message)})";
        }
    }

    private static void Format(StringBuilder text, object? value, int depth)
    {
        switch (value)
        {
            case null:
                text.Append("null");
                return;
            case string s:
                text.Append('"').Append(s.Length > 60 ? s[..60] + "..." : s).Append('"');
                return;
            case byte[] bytes:
                text.Append(bytes.Length).Append(" B");
                return;
            case bool flag:
                text.Append(flag ? "true" : "false");
                return;
            case IFormattable f and not Enum:
                text.Append(f.ToString(null, CultureInfo.InvariantCulture));
                return;
            case Enum e:
                text.Append(e);
                return;
            case IEnumerable list:
                {
                    var items = list.Cast<object?>().ToList();
                    text.Append('[');
                    if (items.Count > 0 && depth >= MaxDepth)
                    {
                        text.Append(items.Count).Append(" items");
                    }
                    else
                    {
                        for (int i = 0; i < Math.Min(items.Count, MaxItems); i++)
                        {
                            if (i > 0)
                                text.Append(", ");
                            Format(text, items[i], depth + 1);
                        }

                        if (items.Count > MaxItems)
                            text.Append(", ... +").Append(items.Count - MaxItems);
                    }

                    text.Append(']');
                    return;
                }
        }

        var type = value.GetType();
        if (depth >= MaxDepth)
        {
            text.Append(type.Name).Append("{...}");
            return;
        }

        text.Append(type.Name.EndsWith('T') ? type.Name[..^1] : type.Name).Append('{');
        bool first = true;
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0)
                continue;
            object? v;
            try
            {
                v = property.GetValue(value);
            }
            catch (Exception)
            {
                continue;
            }

            if (!first)
                text.Append(", ");
            first = false;
            text.Append(property.Name).Append('=');
            Format(text, v, depth + 1);
        }

        text.Append('}');
    }
}

public static partial class LiveRunner
{
    /// <summary>inspect --no-join: stay connected, answering pings, until the run ends.</summary>
    private static async Task InspectOnlyAsync(NodeLink link, CliOptions o, LiveRunOptions run, SynchronizedWriter lines, CancellationToken ct)
    {
        await lines.WriteAsync($"inspect: connected as '{o.Name}', listening (not joining)").ConfigureAwait(false);
        while (!ct.IsCancellationRequested)
        {
            if (link.Closed)
                throw new IOException(link.DisconnectedBy is { } code ? $"server closed the connection ({code})" : "connection closed");
            await link.Client.SendPingAsync(ct).ConfigureAwait(false);
            await Task.Delay(run.PingInterval, ct).ConfigureAwait(false);
        }
    }
}

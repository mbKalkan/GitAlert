using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;

namespace GitAlert.Tests;

/// <summary>What a request looked like, captured before the client disposes it.</summary>
internal sealed record RecordedRequest(
    string Method,
    string Path,
    string Query,
    string? Authorization,
    string? IfNoneMatch,
    string? Accept,
    string? UserAgent,
    string? ApiVersion)
{
    public string PathAndQuery => Query.Length == 0 ? Path : Path + Query;
}

/// <summary>
/// Stands in for GitHub. Answers from a script keyed on the request, records what was asked, and
/// hands back bodies over streams that say whether they were disposed - which is how the tests
/// can tell a response was released rather than left to the finaliser.
/// </summary>
internal sealed class StubHandler(Func<RecordedRequest, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<RecordedRequest> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;

        var recorded = new RecordedRequest(
            request.Method.Method,
            uri.AbsolutePath,
            uri.Query,
            request.Headers.Authorization?.ToString(),
            Header(request, "If-None-Match"),
            request.Headers.Accept.ToString() is { Length: > 0 } accept ? accept : null,
            request.Headers.UserAgent.ToString() is { Length: > 0 } agent ? agent : null,
            Header(request, "X-GitHub-Api-Version"));

        Requests.Add(recorded);

        return Task.FromResult(respond(recorded));
    }

    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;
}

/// <summary>Response builders, so the tests read as the situation rather than as plumbing.</summary>
internal static class Responses
{
    /// <summary>The stream behind the last response built here, for asking whether it was closed.</summary>
    public static TrackingStream? LastBody { get; private set; }

    public static HttpResponseMessage Json(
        HttpStatusCode status,
        string body,
        params (string Name, string Value)[] headers)
    {
        var stream = new TrackingStream(Encoding.UTF8.GetBytes(body));
        LastBody = stream;

        var response = new HttpResponseMessage(status) { Content = new StreamContent(stream) };
        response.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");

        foreach (var (name, value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }

    public static HttpResponseMessage Ok(string body, params (string, string)[] headers) =>
        Json(HttpStatusCode.OK, body, headers);

    public static HttpResponseMessage Status(HttpStatusCode status, params (string, string)[] headers) =>
        Json(status, """{"message":"Something GitHub said"}""", headers);

    /// <summary>Headers, and then nothing: a body that never arrives, the way a dead connection reads.</summary>
    public static HttpResponseMessage Stalled()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StallingStream()),
        };

        response.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        return response;
    }

    /// <summary>A body that never ends, for driving the client into its own size ceiling.</summary>
    public static HttpResponseMessage Endless(string prefix)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new EndlessStream(prefix)),
        };

        response.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        return response;
    }
}

/// <summary>A body that reports whether whoever read it also closed it.</summary>
internal sealed class TrackingStream(byte[] bytes) : MemoryStream(bytes)
{
    public bool Disposed { get; private set; }

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}

/// <summary>
/// Every read waits until it is cancelled. That is what a connection the other side has silently
/// dropped looks like from here, and the only way out of it is a clock of the reader's own.
/// </summary>
internal sealed class StallingStream : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => 0;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("The client is expected to read bodies asynchronously.");

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return 0;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// Opens with a fragment of plausible JSON and then goes on forever, so a reader with no ceiling
/// of its own reads until it runs out of memory. Nothing is allocated to produce it.
/// </summary>
internal sealed class EndlessStream(string prefix) : Stream
{
    private readonly byte[] _prefix = Encoding.UTF8.GetBytes(prefix);
    private int _written;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _written;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        for (var i = 0; i < buffer.Length; i++)
        {
            var at = _written + i;
            buffer[i] = at < _prefix.Length ? _prefix[at] : (byte)'a';
        }

        _written += buffer.Length;
        return buffer.Length;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        new(Read(buffer.Span));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        Task.FromResult(Read(buffer.AsSpan(offset, count)));

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;

namespace TheDiscordList.DSharpPlus.Tests;

internal sealed record Sent(string Method, string Url, string? Authorization, string? UserAgent, string? ContentType, string Body);

/// <summary>Records every request and answers from <see cref="Respond"/>. Never touches the network.</summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<Sent> _sent = new();

    public Func<Sent, HttpResponseMessage> Respond { get; set; } = _ => Json(200, "{}");

    public IReadOnlyList<Sent> Sent => [.. _sent];

    public IReadOnlyList<Sent> To(string endpoint) => [.. _sent.Where(sent => sent.Url.EndsWith("/" + endpoint, StringComparison.Ordinal))];

    public static HttpResponseMessage Json(int status, string body, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        foreach (var (name, value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var sent = new Sent(
            request.Method.Method,
            request.RequestUri!.ToString(),
            Raw(request, "Authorization"),
            Raw(request, "User-Agent"),
            request.Content?.Headers.ContentType?.ToString(),
            request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
        _sent.Enqueue(sent);
        return Respond(sent);
    }

    private static string? Raw(HttpRequestMessage request, string name) =>
        request.Headers.NonValidated.TryGetValues(name, out var values) ? values.ToString() : null;
}

internal sealed class FakeHost : ITdlHost
{
    public TdlTopology Topology { get; set; } = new(0, 1, 1, true);

    public IReadOnlyList<TdlShard> Shards { get; set; } = [new(0, TdlShard.Ready, 42, 10)];

    public int? LocalServerCount { get; set; } = 10;

    public List<TdlCommand> Commands { get; set; } = [new(1, "ping", "Replies with pong", false, null, null)];

    public ConcurrentQueue<string> Logs { get; } = new();

    public Task<IReadOnlyList<TdlCommand>> GetCommandsAsync() => Task.FromResult<IReadOnlyList<TdlCommand>>(Commands);

    public void Log(TdlLogLevel level, string message) => Logs.Enqueue($"{level}: {message}");
}

/// <summary>A poster wired to a fake host, a fake transport and a fake clock.</summary>
internal sealed class Harness : IAsyncDisposable
{
    public static readonly DateTimeOffset Epoch = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    public Harness(Action<TdlOptions>? configure = null, string? token = "tdl_token")
    {
        Options = new TdlOptions { BotId = 123, HttpClient = new HttpClient(Http), TimeProvider = Time };
        configure?.Invoke(Options);
        Poster = new TdlAutoposter(Host, token, Options);
        Poster.Posted += posted => Posted.Enqueue(posted);
        Poster.Error += error => Errors.Enqueue(error);
        Poster.Disabled += disabled => Disabled.Enqueue(disabled);
    }

    public FakeHost Host { get; } = new();

    public FakeHandler Http { get; } = new();

    public FakeTimeProvider Time { get; } = new(Epoch);

    public TdlOptions Options { get; }

    public TdlAutoposter Poster { get; }

    public ConcurrentQueue<TdlPosted> Posted { get; } = new();

    public ConcurrentQueue<TdlError> Errors { get; } = new();

    public ConcurrentQueue<TdlDisabled> Disabled { get; } = new();

    public static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }

        Assert.True(condition(), "Condition not met in time.");
    }

    public void Ready() => Poster.Start(123);

    /// <summary>Moves the fake clock in steps, letting continuations run between them.</summary>
    public async Task Advance(TimeSpan by, TimeSpan? step = null)
    {
        var size = step ?? TimeSpan.FromSeconds(1);
        while (by > TimeSpan.Zero)
        {
            var next = by < size ? by : size;
            Time.Advance(next);
            by -= next;
            await Task.Delay(1);
        }
    }

    /// <summary>Advances the clock until a task that waits on it (a retry backoff) completes.</summary>
    public async Task<T> Drive<T>(Task<T> task)
    {
        for (var i = 0; i < 400 && !task.IsCompleted; i++)
        {
            Time.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Delay(1);
        }

        return await task;
    }

    public ValueTask DisposeAsync() => Poster.DisposeAsync();
}

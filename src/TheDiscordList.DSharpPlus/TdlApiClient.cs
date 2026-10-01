using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TheDiscordList.DSharpPlus;

internal enum OutcomeKind
{
    Ok,
    Payload,
    Auth,
    Plan,
    Config,
    Throttled,
    Transient,
    Dropped,
}

internal readonly record struct Outcome(OutcomeKind Kind, int? Status, string? Body = null, string? Error = null);

/// <summary>
/// The one HTTP client every publisher shares. The server keeps a single rate-limit counter
/// per client IP, so a 429 suspends stats and commands together. Heartbeats bypass the
/// suspension: a dropped stats post is a stale number, a dropped heartbeat is a false "down".
/// </summary>
internal sealed class TdlApiClient(HttpClient http, string baseUrl, string token, string userAgent, TimeProvider time, CancellationToken stopping)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _baseUrl = baseUrl.TrimEnd('/');
    private long _suspendedUntilTicks;

    public async Task<Outcome> PostAsync(string path, object body, bool heartbeat)
    {
        if (!heartbeat && time.GetUtcNow().UtcTicks < Interlocked.Read(ref _suspendedUntilTicks))
        {
            return new(OutcomeKind.Dropped, null);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + path);
        request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), Json));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        timeout.CancelAfter(Timeout);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (!stopping.IsCancellationRequested)
        {
            var message = exception is OperationCanceledException ? "Request timed out after 10 seconds." : exception.Message;
            return new(OutcomeKind.Transient, null, Error: message);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            string text;
            try
            {
                text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (!stopping.IsCancellationRequested)
            {
                return new(OutcomeKind.Transient, status, Error: exception.Message);
            }

            var outcome = Classify(status, text);
            if (outcome.Kind == OutcomeKind.Throttled)
            {
                Suspend(response);
            }

            return outcome;
        }
    }

    /// <summary>Server precedence is 404, 429, 401, 422, 403/409, 200.</summary>
    private static Outcome Classify(int status, string body) => status switch
    {
        >= 200 and < 300 => new(OutcomeKind.Ok, status, body),
        401 => new(OutcomeKind.Auth, 401),
        403 => new(OutcomeKind.Plan, 403),
        // 404 is "unknown id, or the id belongs to a server listing". It never means a bad token.
        404 or 409 => new(OutcomeKind.Config, status),
        400 or 422 => new(OutcomeKind.Payload, status, body),
        429 => new(OutcomeKind.Throttled, 429),
        _ => new(OutcomeKind.Transient, status, Error: $"TheDiscordList responded {status}."),
    };

    private void Suspend(HttpResponseMessage response)
    {
        var now = time.GetUtcNow();
        var retryAfter = response.Headers.RetryAfter?.Delta ?? response.Headers.RetryAfter?.Date - now;
        var until = now + (retryAfter > TimeSpan.Zero ? retryAfter.Value : TimeSpan.FromSeconds(60));

        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var values)
            && long.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var reset)
            && reset is > 0 and < 253_402_300_800)
        {
            var resetAt = DateTimeOffset.FromUnixTimeSeconds(reset);
            if (resetAt > until)
            {
                until = resetAt;
            }
        }

        until += TimeSpan.FromMilliseconds(Random.Shared.Next(0, 2001));
        Interlocked.Exchange(ref _suspendedUntilTicks, until.UtcTicks);
    }

    /// <summary>The <c>message</c> of a JSON error body, for logs.</summary>
    public static string? Detail(string? body)
    {
        try
        {
            using var document = JsonDocument.Parse(body ?? string.Empty);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String
                ? message.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

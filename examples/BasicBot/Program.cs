// The whole integration for most bots, plus an ASP.NET Core webhook endpoint.
//
//   dotnet run --project examples/BasicBot
//
// Environment: DISCORD_TOKEN, TDL_TOKEN, and TDL_WEBHOOK_SECRET for the webhook route.
using DSharpPlus;
using TheDiscordList.DSharpPlus;

var client = new DiscordClient(new DiscordConfiguration
{
    Token = Environment.GetEnvironmentVariable("DISCORD_TOKEN") ?? string.Empty,
    TokenType = TokenType.Bot,
    Intents = DiscordIntents.Guilds,
});

// Before ConnectAsync: the first Ready starts the publishers. Logs go to client.Logger.
await using var poster = client.AddTheDiscordList(Environment.GetEnvironmentVariable("TDL_TOKEN"));

// Entirely optional. Without these the package still never throws at your bot.
poster.Posted += posted => Console.WriteLine($"[tdl] {posted.Publisher} -> {posted.Status}");
poster.Disabled += disabled => Console.WriteLine($"[tdl] {disabled.Publisher} disabled: {disabled.Reason}");

await client.ConnectAsync();

var secret = Environment.GetEnvironmentVariable("TDL_WEBHOOK_SECRET") ?? string.Empty;
var app = WebApplication.Create(args);

app.MapPost("/webhooks/tdl", async (HttpRequest request) =>
{
    using var body = new MemoryStream();
    await request.Body.CopyToAsync(body);
    try
    {
        var webhook = TdlWebhook.Verify(body.ToArray(), request.Headers[TdlWebhook.SignatureHeader], secret);
        if (webhook.Event == TdlWebhookEvents.ListingUpvoted)
        {
            Console.WriteLine($"{webhook.DataAs<TdlListingUpvoted>()?.User?.Username ?? "Someone"} upvoted the bot.");
        }

        return Results.NoContent();
    }
    catch (TdlWebhookException exception)
    {
        Console.WriteLine($"[tdl] rejected a webhook: {exception.Code}");
        return Results.BadRequest();
    }
});

await app.RunAsync();

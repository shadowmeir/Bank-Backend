using System.Net;
using System.Text;
using Bank.Api.Chatbot;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bank.Api.Tests.Chatbot;

public sealed class OpenAiChatIntentResolverTests
{
    [Fact]
    public async Task ResolveSlashCommandAsync_ReturnsNormalizedCommand_WhenResponseIsValid()
    {
        var handler = new StaticResponseHandler(_ =>
            JsonOk("""{"choices":[{"message":{"content":"{\"command\":\"/recent 5\"}"}}]}"""));
        var resolver = CreateResolver(handler, enabled: true);

        var cmd = await resolver.ResolveSlashCommandAsync("show me five recent transactions", null, CancellationToken.None);

        Assert.Equal("/recent 5", cmd);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task ResolveSlashCommandAsync_ReturnsNull_WhenModelReturnsUnsupportedCommand()
    {
        var handler = new StaticResponseHandler(_ =>
            JsonOk("""{"choices":[{"message":{"content":"{\"command\":\"/transfer\"}"}}]}"""));
        var resolver = CreateResolver(handler, enabled: true);

        var cmd = await resolver.ResolveSlashCommandAsync("send money to john", null, CancellationToken.None);

        Assert.Null(cmd);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task ResolveSlashCommandAsync_SupportsIntentStylePayload()
    {
        var accountId = Guid.Parse("6C7CF8E7-0950-4F18-A8E3-59E6F2D1BF72");
        var handler = new StaticResponseHandler(_ =>
            JsonOk("{\"choices\":[{\"message\":{\"content\":\"{\\\"intent\\\":\\\"recent\\\",\\\"accountId\\\":\\\"" + accountId + "\\\",\\\"limit\\\":7}\"}}]}"));
        var resolver = CreateResolver(handler, enabled: true);

        var cmd = await resolver.ResolveSlashCommandAsync("latest seven operations on this account", accountId.ToString(), CancellationToken.None);

        Assert.Equal($"/recent {accountId} 7", cmd);
    }

    [Fact]
    public async Task ResolveSlashCommandAsync_DoesNotCallApi_WhenDisabled()
    {
        var handler = new StaticResponseHandler(_ =>
            JsonOk("""{"choices":[{"message":{"content":"{\"command\":\"/help\"}"}}]}"""));
        var resolver = CreateResolver(handler, enabled: false);

        var cmd = await resolver.ResolveSlashCommandAsync("anything", null, CancellationToken.None);

        Assert.Null(cmd);
        Assert.Equal(0, handler.CallCount);
    }

    private static OpenAiChatIntentResolver CreateResolver(StaticResponseHandler handler, bool enabled)
    {
        var client = new HttpClient(handler);
        var options = Options.Create(new ChatbotLlmOptions
        {
            Enabled = enabled,
            Endpoint = "https://api.openai.com/v1/chat/completions",
            ApiKey = "test-key",
            Model = "gpt-4o-mini",
            TimeoutSeconds = 2
        });

        return new OpenAiChatIntentResolver(client, options, NullLogger<OpenAiChatIntentResolver>.Instance);
    }

    private static HttpResponseMessage JsonOk(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StaticResponseHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _factory;

        public StaticResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> factory)
        {
            _factory = factory;
        }

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_factory(request));
        }
    }
}

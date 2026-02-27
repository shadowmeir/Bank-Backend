using Bank.Api.Chatbot;
using Bank.Application.Abstractions;
using Bank.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Bank.Api.Tests.Chatbot;

public sealed class ChatbotRouterIntentResolutionTests
{
    [Fact]
    public async Task RouteAsync_UsesLlmCommand_WhenDeterministicMappingMisses()
    {
        using var db = CreateDb();
        var resolver = new Mock<IChatIntentResolver>();
        resolver
            .Setup(x => x.ResolveSlashCommandAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BotCommands.Help);

        var router = new ChatbotRouter(
            db,
            Mock.Of<IAccountRepository>(),
            Mock.Of<ILedgerRepository>(),
            resolver.Object);

        var reply = await router.RouteAsync("client-1", "show me what i can ask", null, CancellationToken.None);

        Assert.Equal(BotIntent.Help, reply.Intent);
        Assert.Contains("Here are my commands:", reply.Text);
    }

    [Fact]
    public async Task RouteAsync_ReturnsUnknown_WhenLlmReturnsUnsupportedCommand()
    {
        using var db = CreateDb();
        var resolver = new Mock<IChatIntentResolver>();
        resolver
            .Setup(x => x.ResolveSlashCommandAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("/transfer");

        var router = new ChatbotRouter(
            db,
            Mock.Of<IAccountRepository>(),
            Mock.Of<ILedgerRepository>(),
            resolver.Object);

        var reply = await router.RouteAsync("client-1", "move money to my other account", null, CancellationToken.None);

        Assert.Equal(BotIntent.Unknown, reply.Intent);
        Assert.Contains("Unknown command '/transfer'", reply.Text);
    }

    [Fact]
    public async Task RouteAsync_DoesNotCallLlm_WhenDeterministicMappingMatches()
    {
        using var db = CreateDb();
        var resolver = new Mock<IChatIntentResolver>(MockBehavior.Strict);

        var router = new ChatbotRouter(
            db,
            Mock.Of<IAccountRepository>(),
            Mock.Of<ILedgerRepository>(),
            resolver.Object);

        var reply = await router.RouteAsync("client-1", "help", null, CancellationToken.None);

        Assert.Equal(BotIntent.Help, reply.Intent);
        resolver.Verify(
            x => x.ResolveSlashCommandAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static BankDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<BankDbContext>()
            .UseInMemoryDatabase($"chatbot-router-tests-{Guid.NewGuid()}")
            .Options;
        return new BankDbContext(options);
    }
}

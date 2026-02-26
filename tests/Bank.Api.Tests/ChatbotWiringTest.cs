// tests/Bank.Api.Tests/ChatbotWiringTest.cs
using Bank.Api.Chatbot;
using Bank.Application.Abstractions;
using Bank.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Bank.Api.Tests.Chatbot;

public sealed class ChatbotProgramWiringTests
{
    [Fact]
    public void ProgramChatbotRegistrations_DI_CanResolveRouter_AndConstructHub()
    {
        var services = new ServiceCollection();

        // SignalR (optional here, but fine)
        services.AddSignalR();
        services.AddLogging();

        // ✅ Register a minimal BankDbContext so DI can construct ChatbotRouter.
        // No provider configured -> OK for "can it be constructed" tests.
        services.AddScoped<BankDbContext>(_ =>
        {
            var opts = new DbContextOptionsBuilder<BankDbContext>().Options;
            return new BankDbContext(opts);
        });

        // If your router also depends on these, mock them too (harmless even if unused)
        services.AddScoped<IAccountRepository>(_ => Mock.Of<IAccountRepository>());
        services.AddScoped<ILedgerRepository>(_ => Mock.Of<ILedgerRepository>());
        services.AddScoped<IBankUnitOfWork>(_ => Mock.Of<IBankUnitOfWork>());

        // Router + Hub (match Program.cs)
        services.AddScoped<IChatbotRouter, ChatbotRouter>();
        services.AddTransient<ChatHub>();

        var sp = services.BuildServiceProvider();

        using var scope = sp.CreateScope();

        // Act
        var router = scope.ServiceProvider.GetRequiredService<IChatbotRouter>();
        var hub = ActivatorUtilities.CreateInstance<ChatHub>(scope.ServiceProvider);

        // Assert
        Assert.NotNull(router);
        Assert.NotNull(hub);
    }
}

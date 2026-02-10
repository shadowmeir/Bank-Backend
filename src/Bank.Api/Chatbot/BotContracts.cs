using System;

namespace Bank.Api.Chatbot;

// 1) What "topic" the bot understood.
public enum BotIntent
{
    Unknown = 0,
    Help,
    Accounts,
    Balance,
    RecentTransactions,
}

// 2) What kind of message is being sent back (helps UI styling).
public enum BotMessageKind
{
    Bot = 0,
    System,
    Error
}

// 3) Canonical commands ("switch/case keys").
//    Stage-1 MVP: user can type exactly these, OR the router maps a few synonyms.
public static class BotCommands
{
    public const string Help = "/help";
    public const string Accounts = "/accounts";
    public const string Balance = "/balance";
    public const string Recent = "/recent";

    // Metadata showable in UI as a help menu.
    public record BotCommand(string Command, BotIntent Intent, string Description, string[] Examples);

    public static readonly BotCommand[] All =
    [
        new BotCommand(
            Help,
            BotIntent.Help,
            "Show what I can do.",
            ["/help", "help", "what can you do?"]),

        new BotCommand(
            Accounts,
            BotIntent.Accounts,
            "List your accounts (IDs you can use in other commands).",
            ["/accounts", "accounts", "list my accounts"]),

        new BotCommand(
            Balance,
            BotIntent.Balance,
            "Show balances. Without accountId shows total + per-account.",
            ["/balance", "/balance <accountId>", "balance", "what's my balance?"]),

        new BotCommand(
            Recent,
            BotIntent.RecentTransactions,
            "Show recent transactions. You can pass accountId and/or limit.",
            ["/recent", "/recent 10", "/recent <accountId>", "/recent <accountId> 10", "recent transactions"])
    ];
}

// Client -> server payload (optional, useful later; current Hub method uses simple params).
public record ChatClientMessage(string Text, string? AccountId = null);

// Server -> client payload.
public record ChatBotMessage(
    BotMessageKind Kind,
    BotIntent Intent,
    string Text,
    DateTimeOffset TimestampUtc
);
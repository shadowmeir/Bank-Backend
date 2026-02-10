
using Bank.Application.UseCases;
using Bank.Application.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.VisualBasic;
using Bank.Api.Controllers;

namespace SignalRChat.Hubs
{
    [Authorize]
    public class ChatHub : Hub
    {
        private readonly IAccountRepository _accounts;
        private readonly ILedgerRepository _ledgers;

        public ChatHub(IAccountRepository accounts, ILedgerRepository ledgers)
        {
            _accounts = accounts;
            _ledgers = ledgers;
        }
        public async Task SendToBot(string message)
        {
            var userId = Context.UserIdentifier ?? "(anonymous)";
            var reply = RouteBot(userId, message); // switch/case router

            await Clients.Caller.SendAsync("ReceiveBotMessage", reply);
        }

        private static string RouteBot(string userId, string message)
        {
            return message switch
            {
                "Balance" => "Your balance is ...",   // call app service in real code
                "Recent Transactions" => $"Recent transactions in your account were ...",
                
                "Help"    => "Try: Balance, RecentTx, Transfer ...",
                _         => "Sorry, I didn't understand. Type Help."
            };
        }
    }
}

using ServerManagerTool.DiscordBot.Delegates;
using ServerManagerTool.DiscordBot.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ServerManagerTool.DiscordBot.Interfaces
{
    public interface IServerManagerBot
    {
        CancellationToken Token { get; }

        // Started only means "the background run-loop is alive", not "the gateway
        // connection is actually up" - check IsConnected too for real connectivity
        // (e.g. before deciding a UI status indicator should say "Running").
        bool Started { get; }
        bool IsConnected { get; }

        Task RunAsync(DiscordBotConfig discordBotConfig, HandleCommandDelegate handleCommandCallback, HandleTranslationDelegate handleTranslationCallback, CancellationToken token);

        // Posts/edits a per-server control panel message with Start/Stop/Restart/Update
        // buttons. profileIdOrAlias is embedded in each button's custom ID so clicks
        // route back through the same command pipeline as chat commands.
        Task<ulong?> PostOrUpdateControlPanelAsync(ulong channelId, ulong? existingMessageId, string profileIdOrAlias, string title, string description);

        // Posts a new embed message to the given channel, or edits an existing one
        // in place if existingMessageId is provided and still valid. Returns the
        // message ID to persist for future edits. Throws (rather than returning
        // null) if the bot isn't running/connected yet or the channel Id doesn't
        // resolve to a postable message channel, so callers can log the real
        // reason instead of a post silently never happening. fields, if provided,
        // renders as proper Discord embed fields (grouped layout) rather than one
        // long description block.
        Task<ulong?> PostOrUpdateEmbedAsync(ulong channelId, ulong? existingMessageId, string title, string description, CancellationToken token, IList<(string Name, string Value, bool Inline)> fields = null, string footer = null);

        // Deletes a previously-posted message, e.g. a stale overflow embed that's
        // no longer needed because the list it continued has shrunk. Safe to call
        // with an ID that's already gone - failures are swallowed.
        Task DeleteMessageAsync(ulong channelId, ulong messageId);
    }
}

using Discord.Commands;
using Discord;
using Discord.Interactions;
using Discord.Net.Providers.WS4Net;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ServerManagerTool.DiscordBot.Delegates;
using ServerManagerTool.DiscordBot.Enums;
using ServerManagerTool.DiscordBot.Interfaces;
using ServerManagerTool.DiscordBot.Models;
using ServerManagerTool.DiscordBot.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ServerManagerTool.DiscordBot
{
    public sealed class ServerManagerBot : IServerManagerBot
    {
        internal ServerManagerBot()
        {
            Started = false;
        }

        public CancellationToken Token { get; private set; }
        public bool Started { get; private set; }

        // Started only reflects "the bot's run loop is alive", not "the
        // gateway connection is actually up" - Discord.Net usually
        // reconnects transient drops on its own, but a connection can get
        // stuck (e.g. after a longer network outage) without Started ever
        // going false. Track the real connection state separately so a
        // watchdog can notice and force a reconnect.
        private DateTime? _disconnectedSinceUTC;
        public bool IsConnected => _client?.ConnectionState == Discord.ConnectionState.Connected;

        private DiscordSocketClient _client;
        private HandleCommandDelegate _handleCommandCallback;
        private DiscordBotConfig _botConfig;

        // Custom IDs for control panel buttons are encoded as "asm-wipe:{action}:{profileIdOrAlias}"
        // so a single shared handler can route any button click back to the same
        // command pipeline used by chat commands (DiscordBotHelper.HandleDiscordCommand),
        // including its existing Config.Default.AllowDiscord* permission checks.
        private const string ControlButtonPrefix = "asm-ctl";

        public async Task<ulong?> PostOrUpdateControlPanelAsync(ulong channelId, ulong? existingMessageId, string profileIdOrAlias, string title, string description)
        {
            // Used to return null here silently - MainWindow.UpdateDiscordControlPanels
            // then just saw "no message id" and moved on, with no log entry or any
            // other trace of why the panel never posted. Throw instead so the real
            // reason (bot loop not running / gateway not actually connected yet /
            // no channel configured) reaches that method's own catch block, which
            // does log it - same reasoning as the "deliberately not swallowed"
            // exception below.
            if (channelId == 0)
                throw new Exception("PostOrUpdateControlPanelAsync failed: no Discord channel Id was provided.");
            if (!Started)
                throw new Exception("PostOrUpdateControlPanelAsync failed: the Discord bot is not running.");
            if (!IsConnected || _client == null)
                throw new Exception("PostOrUpdateControlPanelAsync failed: the Discord bot is running but not currently connected to the gateway.");

            try
            {
                var channel = await _client.GetChannelAsync(channelId) as Discord.IMessageChannel;
                if (channel == null)
                {
                    // Used to return null here silently too - a bad/mistyped Channel 1
                    // Id, or an Id that resolves to something that isn't a postable
                    // message channel (a category, a forum channel, a voice channel
                    // with no text chat, etc), looked identical to every other silent
                    // failure. Resolve what it actually is (if anything) so the
                    // caller's log line says why, instead of a generic "no message id".
                    var rawChannel = await _client.GetChannelAsync(channelId);
                    throw new Exception($"PostOrUpdateControlPanelAsync failed: channel {channelId} could not be used as a message channel (resolved to: {rawChannel?.GetType().Name ?? "not found - check the channel Id and that this bot has access to it"}).");
                }

                // Discord-side identity for "is this the same panel I already posted for
                // this profile" used to be resolved purely by the embed's TITLE (the
                // server's map display name, e.g. "The Center"). With a real cluster
                // running both a PVE and a PVP instance of the same map (or any other
                // two profiles that happen to render the same title), that let a SECOND
                // profile's post silently find and overwrite the FIRST profile's already-
                // posted message via FindExistingBotMessageByFooterAsync below - one
                // profile's buttons would then clobber the other's on every refresh cycle,
                // which is exactly what "buttons not posting for each profile" looks like
                // with several same-map profiles in the mix. Embed a stable, invisible
                // per-profile marker (the same profileIdOrAlias used in every button's own
                // custom_id) in the footer instead, and match on THAT - never on the
                // human-readable title, which multiple profiles can legitimately share.
                var footerMarker = $"{ControlButtonPrefix}-id:{profileIdOrAlias}";

                var embed = new Discord.EmbedBuilder()
                    .WithTitle(title)
                    .WithDescription(description)
                    .WithColor(Discord.Color.Blue)
                    .WithCurrentTimestamp()
                    .WithFooter(footerMarker)
                    .Build();

                var components = new Discord.ComponentBuilder()
                    .WithButton("!status", $"{ControlButtonPrefix}:{CommandType.Status}:{profileIdOrAlias}", Discord.ButtonStyle.Secondary)
                    .WithButton("!start", $"{ControlButtonPrefix}:{CommandType.Start}:{profileIdOrAlias}", Discord.ButtonStyle.Success)
                    .WithButton("!stop", $"{ControlButtonPrefix}:{CommandType.Stop}:{profileIdOrAlias}", Discord.ButtonStyle.Danger)
                    .WithButton("!restart", $"{ControlButtonPrefix}:{CommandType.Restart}:{profileIdOrAlias}", Discord.ButtonStyle.Primary)
                    .WithButton("!update", $"{ControlButtonPrefix}:{CommandType.Update}:{profileIdOrAlias}", Discord.ButtonStyle.Secondary, row: 1)
                    .WithButton("!backup", $"{ControlButtonPrefix}:{CommandType.Backup}:{profileIdOrAlias}", Discord.ButtonStyle.Secondary, row: 1)
                    .Build();

                if (existingMessageId.HasValue)
                {
                    try
                    {
                        var existingMessage = await channel.GetMessageAsync(existingMessageId.Value) as Discord.IUserMessage;
                        if (existingMessage != null)
                        {
                            await existingMessage.ModifyAsync(m =>
                            {
                                m.Embed = embed;
                                m.Components = components;
                            });
                            return existingMessageId;
                        }
                    }
                    catch (Exception ex)
                    {
                        // Original message may have been deleted, or this bot no longer
                        // has permission to read/edit it - either way, fall through and
                        // look for/post a fresh one, but leave a trace of why the fast
                        // path didn't work rather than swallowing it outright.
                        Debug.WriteLine($"[ServerManagerBot] Existing control panel message {existingMessageId} could not be read/modified, will search/repost. {ex.Message}");
                    }
                }

                // The stored message ID might be missing entirely (e.g. lost to a
                // config reset, or this is being run for the first time after
                // some earlier posting attempts) - before assuming there's
                // nothing there and creating a duplicate, actually check the
                // channel itself for a message this bot already posted for THIS
                // profile (matched by the footer marker, not the title - see above),
                // and update that instead.
                var foundExisting = await FindExistingBotMessageByFooterAsync(channel, footerMarker);
                if (foundExisting != null)
                {
                    await foundExisting.ModifyAsync(m =>
                    {
                        m.Embed = embed;
                        m.Components = components;
                    });
                    return foundExisting.Id;
                }

                var sent = await channel.SendMessageAsync(embed: embed, components: components);
                return sent?.Id;
            }
            catch (Exception ex)
            {
                // Deliberately not swallowed - MainWindow.UpdateDiscordControlPanels
                // already wraps this call in a try/catch that logs ex.Message, so
                // let it propagate there instead of hiding the real reason a post
                // failed (permissions, invalid channel, etc.).
                throw new Exception($"PostOrUpdateControlPanelAsync failed for channel {channelId}: {ex.Message}", ex);
            }
        }

        // Searches this bot's own recent messages in the channel for one carrying this
        // exact profile's footer marker - used as a fallback before ever posting a new
        // message, so a lost/missing stored message ID (e.g. after a config reset, or
        // repeated testing) doesn't result in duplicate messages piling up. Matched on
        // the invisible per-profile footer marker rather than the visible title, since
        // two different profiles (e.g. a PVE and a PVP instance of the same map) can
        // legitimately render the identical human-readable title - see the comment in
        // PostOrUpdateControlPanelAsync above. Only looks at messages this bot itself
        // sent, and only the most recent 100 - a channel that also carries chat-command
        // traffic can push an older, not-yet-linked panel further back, so this scans
        // deeper than the single default page. Old, unrelated messages beyond that are
        // left alone either way.
        private async Task<Discord.IUserMessage> FindExistingBotMessageByFooterAsync(Discord.IMessageChannel channel, string footerMarker)
        {
            if (string.IsNullOrWhiteSpace(footerMarker) || _client?.CurrentUser == null)
                return null;

            try
            {
                var recentMessages = await channel.GetMessagesAsync(100).FlattenAsync();

                foreach (var message in recentMessages)
                {
                    if (!(message is Discord.IUserMessage userMessage))
                        continue;

                    if (userMessage.Author?.Id != _client.CurrentUser.Id)
                        continue;

                    if (userMessage.Embeds.Any(e => string.Equals(e.Footer?.Text, footerMarker, StringComparison.Ordinal)))
                        return userMessage;
                }
            }
            catch (Exception ex)
            {
                // If the search itself fails for any reason, just fall through
                // to posting a new message rather than blocking the update -
                // but leave a trace of why the search didn't work.
                Debug.WriteLine($"[ServerManagerBot] Search for an existing control panel message failed, will post a new one. {ex.Message}");
            }

            return null;
        }

        // Same idea as FindExistingBotMessageByFooterAsync above, but matched on the
        // embed's TITLE instead - used by PostOrUpdateEmbedAsync (e.g. the global wipe
        // notice), which posts one distinct, genuinely-unique-titled message rather than
        // one-per-profile, so the title-collision problem that made the control panel
        // switch to a footer marker doesn't apply here. Only looks at messages this bot
        // itself sent, and only the most recent 100.
        private async Task<Discord.IUserMessage> FindExistingBotMessageByTitleAsync(Discord.IMessageChannel channel, string title)
        {
            if (string.IsNullOrWhiteSpace(title) || _client?.CurrentUser == null)
                return null;

            try
            {
                var recentMessages = await channel.GetMessagesAsync(100).FlattenAsync();

                foreach (var message in recentMessages)
                {
                    if (!(message is Discord.IUserMessage userMessage))
                        continue;

                    if (userMessage.Author?.Id != _client.CurrentUser.Id)
                        continue;

                    if (userMessage.Embeds.Any(e => string.Equals(e.Title, title, StringComparison.OrdinalIgnoreCase)))
                        return userMessage;
                }
            }
            catch (Exception ex)
            {
                // If the search itself fails for any reason, just fall through
                // to posting a new message rather than blocking the update -
                // but leave a trace of why the search didn't work.
                Debug.WriteLine($"[ServerManagerBot] Search for an existing message by title failed, will post a new one. {ex.Message}");
            }

            return null;
        }

        private bool IsAllowedAdmin(ulong? userId)
        {
            if (!userId.HasValue)
                return false;

            var allowList = _botConfig?.AllowedAdminIds;
            if (string.IsNullOrWhiteSpace(allowList))
                return true; // Empty allow-list means anyone who can see the channel can use it.

            var ids = allowList.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return ids.Any(id => id.Trim() == userId.Value.ToString());
        }

        private Task OnClientDisconnected(Exception ex)
        {
            if (!_disconnectedSinceUTC.HasValue)
            {
                _disconnectedSinceUTC = DateTime.UtcNow;
                Debug.WriteLine($"Discord bot disconnected. {ex?.Message}");
            }

            return Task.CompletedTask;
        }

        private Task OnClientConnected()
        {
            if (_disconnectedSinceUTC.HasValue)
                Debug.WriteLine("Discord bot reconnected.");

            _disconnectedSinceUTC = null;
            return Task.CompletedTask;
        }

        private async Task OnButtonExecutedAsync(Discord.WebSocket.SocketMessageComponent component)
        {
            try
            {
                var customId = component.Data?.CustomId;
                if (string.IsNullOrWhiteSpace(customId) || !customId.StartsWith(ControlButtonPrefix + ":"))
                    return;

                var parts = customId.Split(new[] { ':' }, 3);
                if (parts.Length != 3 || !Enum.TryParse<CommandType>(parts[1], out var commandType))
                {
                    await component.RespondAsync("This button could not be understood.", ephemeral: true);
                    return;
                }

                var profileIdOrAlias = parts[2];
                var serverId = component.GuildId?.ToString() ?? string.Empty;
                var channelId = component.ChannelId?.ToString() ?? string.Empty;

                if (!IsAllowedAdmin(component.User?.Id))
                {
                    await component.RespondAsync("You're not authorized to use these controls.", ephemeral: true);
                    return;
                }

                await component.DeferAsync(ephemeral: true);

                var results = _handleCommandCallback?.Invoke(commandType, serverId, channelId, profileIdOrAlias, Token) ?? new System.Collections.Generic.List<string>();
                var reply = results.Count > 0 ? string.Join("\n", results) : $"{commandType} requested for {profileIdOrAlias}.";

                await component.FollowupAsync(reply, ephemeral: true);
            }
            catch (Exception ex)
            {
                try
                {
                    await component.FollowupAsync($"Command failed: {ex.Message}", ephemeral: true);
                }
                catch
                {
                    // Interaction may have already expired - nothing more we can do.
                }
            }
        }

        public async Task<ulong?> PostOrUpdateEmbedAsync(ulong channelId, ulong? existingMessageId, string title, string description, CancellationToken token, IList<(string Name, string Value, bool Inline)> fields = null, string footer = null)
        {
            // See the identical guards in PostOrUpdateControlPanelAsync - throwing here
            // (instead of silently returning null) is what lets the caller's own
            // try/catch actually log why a post never happened.
            if (channelId == 0)
                throw new Exception("PostOrUpdateEmbedAsync failed: no Discord channel Id was provided.");
            if (!Started)
                throw new Exception("PostOrUpdateEmbedAsync failed: the Discord bot is not running.");
            if (!IsConnected || _client == null)
                throw new Exception("PostOrUpdateEmbedAsync failed: the Discord bot is running but not currently connected to the gateway.");

            try
            {
                var channel = await _client.GetChannelAsync(channelId) as Discord.IMessageChannel;
                if (channel == null)
                {
                    var rawChannel = await _client.GetChannelAsync(channelId);
                    throw new Exception($"PostOrUpdateEmbedAsync failed: channel {channelId} could not be used as a message channel (resolved to: {rawChannel?.GetType().Name ?? "not found - check the channel Id and that this bot has access to it"}).");
                }

                var embedBuilder = new Discord.EmbedBuilder()
                    .WithTitle(title)
                    .WithColor(Discord.Color.Gold)
                    .WithCurrentTimestamp();

                if (!string.IsNullOrWhiteSpace(description))
                    embedBuilder.WithDescription(description);

                if (!string.IsNullOrWhiteSpace(footer))
                    embedBuilder.WithFooter(footer);

                if (fields != null)
                {
                    foreach (var field in fields)
                    {
                        embedBuilder.AddField(field.Name, field.Value, field.Inline);
                    }
                }

                var embed = embedBuilder.Build();

                if (existingMessageId.HasValue)
                {
                    try
                    {
                        var existingMessage = await channel.GetMessageAsync(existingMessageId.Value) as Discord.IUserMessage;
                        if (existingMessage != null)
                        {
                            await existingMessage.ModifyAsync(m => m.Embed = embed);
                            return existingMessageId;
                        }
                    }
                    catch (Exception ex)
                    {
                        // Original message may have been deleted, or this bot no longer
                        // has permission to read/edit it - either way, fall through and
                        // look for/post a fresh one, but leave a trace of why.
                        Debug.WriteLine($"[ServerManagerBot] Existing message {existingMessageId} could not be read/modified, will search/repost. {ex.Message}");
                    }
                }

                // Same reasoning as PostOrUpdateControlPanelAsync - check the
                // channel itself for an existing message with this exact title
                // before assuming there's nothing there and posting a duplicate.
                var foundExisting = await FindExistingBotMessageByTitleAsync(channel, title);
                if (foundExisting != null)
                {
                    await foundExisting.ModifyAsync(m => m.Embed = embed);
                    return foundExisting.Id;
                }

                var sent = await channel.SendMessageAsync(embed: embed);
                return sent?.Id;
            }
            catch (Exception ex)
            {
                // Deliberately not swallowed - MainWindow.UpdateGlobalDiscordWipeEmbed
                // already wraps this call in a try/catch that logs ex.Message, so
                // let it propagate there instead of hiding the real reason a post
                // failed (permissions, invalid channel, etc.).
                throw new Exception($"PostOrUpdateEmbedAsync failed for channel {channelId}: {ex.Message}", ex);
            }
        }

        public async Task<bool> SendDirectMessageAsync(ulong userId, string message)
        {
            if (!IsConnected || _client == null || userId == 0 || string.IsNullOrWhiteSpace(message))
                return false;

            try
            {
                var user = await _client.GetUserAsync(userId);
                if (user == null)
                    return false;

                await user.SendMessageAsync(message);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public async Task DeleteMessageAsync(ulong channelId, ulong messageId)
        {
            if (!Started || _client == null || channelId == 0 || messageId == 0)
                return;

            try
            {
                var channel = await _client.GetChannelAsync(channelId) as Discord.IMessageChannel;
                if (channel == null)
                    return;

                await channel.DeleteMessageAsync(messageId);
            }
            catch
            {
                // Message may already be gone - nothing more to do.
            }
        }

        public async Task RunAsync(DiscordBotConfig discordBotConfig, HandleCommandDelegate handleCommandCallback, HandleTranslationDelegate handleTranslationCallback, CancellationToken token)
        {
            if (discordBotConfig is null || handleTranslationCallback is null || handleCommandCallback is null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(discordBotConfig.DiscordToken))
            {
                throw new Exception("#DiscordBot_MissingTokenError");
            }

            if (Started)
            {
                return;
            }

            Started = true;
            Token = token;
            _handleCommandCallback = handleCommandCallback;
            _botConfig = discordBotConfig;

            // Begin building the configuration file
            var config = new ConfigurationBuilder()
                .Build();

            var socketConfig = new DiscordSocketConfig
            {
                LogLevel = LogLevelHelper.GetLogSeverity(discordBotConfig.LogLevel),
                MessageCacheSize = 1000,
            };
            if (Environment.OSVersion.Version < new Version(6, 2))
            {
                // windows 7 or early
                socketConfig.WebSocketProvider = WS4NetProvider.Instance;
            }

            var commandConfig = new CommandServiceConfig
            {
                // Force all commands to run async
                DefaultRunMode = Discord.Commands.RunMode.Async,
                LogLevel = LogLevelHelper.GetLogSeverity(discordBotConfig.LogLevel),
                CaseSensitiveCommands = false,
            };

            var interactionConfig = new InteractionServiceConfig
            {
                // Force all interactions to run async
                DefaultRunMode = Discord.Interactions.RunMode.Async,
                LogLevel = LogLevelHelper.GetLogSeverity(discordBotConfig.LogLevel),
            };

            // Build the service provider
            var discordClient = new DiscordSocketClient(socketConfig);
            _client = discordClient;
            discordClient.ButtonExecuted += OnButtonExecutedAsync;
            discordClient.Disconnected += OnClientDisconnected;
            discordClient.Connected += OnClientConnected;

            var services = new ServiceCollection()
                .AddSingleton(config)
                .AddSingleton(discordBotConfig)
                // Add the discord client to the service provider
                .AddSingleton(discordClient)
                // Add the command service to the service provider
                .AddSingleton(new CommandService(commandConfig))
                .AddSingleton(x => new InteractionService(x.GetRequiredService<DiscordSocketClient>(), interactionConfig))
                // Add remaining services to the provider
                .AddSingleton<CommandHandlerService>()
                .AddSingleton<LoggingService>()
                .AddSingleton<StartupService>()
                .AddSingleton<ShutdownService>()
                .AddSingleton<Random>()
                .AddSingleton(handleCommandCallback)
                .AddSingleton(handleTranslationCallback)
                .AddSingleton<IServerManagerBot>(this);

            // Create the service provider
            using (var provider = services.BuildServiceProvider())
            {
                // Initialize the logging service, startup service, and command handler
                provider?.GetRequiredService<LoggingService>();
                var startupService = provider?.GetRequiredService<StartupService>();
                await startupService.StartAsync();
                provider?.GetRequiredService<CommandHandlerService>();

                try
                {
                    // Instead of one indefinite wait, poll periodically so a
                    // connection stuck disconnected (Discord.Net's own
                    // internal reconnect/resume logic failing to recover,
                    // e.g. after a longer network outage) can be noticed and
                    // force-reconnected, rather than silently staying dead
                    // until the whole app is restarted.
                    const int reconnectThresholdSeconds = 120;

                    while (true)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30), token);

                        if (_disconnectedSinceUTC.HasValue &&
                            (DateTime.UtcNow - _disconnectedSinceUTC.Value).TotalSeconds >= reconnectThresholdSeconds)
                        {
                            Debug.WriteLine($"Discord bot has been disconnected for over {reconnectThresholdSeconds} seconds - forcing a reconnect.");

                            try
                            {
                                // Reconnect only, not a fresh login - the
                                // client is already logged in from the
                                // initial StartAsync above, and Discord.Net
                                // throws if LoginAsync is called again while
                                // already logged in. Calling startupService.
                                // StartAsync() here (which re-logs-in) would
                                // throw on that second login, leaving the
                                // gateway stopped from the line above with no
                                // successful reconnect - i.e. it would make a
                                // stuck connection permanently dead instead of
                                // reviving it.
                                await _client.StopAsync();
                                await _client.StartAsync();
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"Discord bot forced reconnect failed: {ex.Message}");
                            }
                            finally
                            {
                                // Reset the window regardless of outcome, so a
                                // failed retry doesn't immediately retry again
                                // every 30 seconds - it gets another full
                                // threshold before the next forced attempt.
                                _disconnectedSinceUTC = DateTime.UtcNow;
                            }
                        }
                    }
                }
                catch (TaskCanceledException)
                {
                    Debug.WriteLine("Task Canceled");
                }
                catch (OperationCanceledException)
                {
                    Debug.WriteLine("Operation Canceled");
                }

                await provider?.GetRequiredService<ShutdownService>().StopAsync();
                Started = false;
                _client = null;
            }
        }
    }
}

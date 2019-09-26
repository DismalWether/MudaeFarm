using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Discord.WebSocket;

namespace MudaeFarm
{
    public class AutoRoller
    {
        readonly DiscordSocketClient _client;
        readonly ConfigManager _config;
        readonly MudaeStateManager _state;

        public AutoRoller(DiscordSocketClient client, ConfigManager config, MudaeStateManager state)
        {
            _client = client;
            _config = config;
            _state  = state;
        }

        // guildId - cancellationTokenSource
        readonly ConcurrentDictionary<ulong, CancellationTokenSource> _cancellations
            = new ConcurrentDictionary<ulong, CancellationTokenSource>();

        public async Task InitializeAsync()
        {
            await ReloadWorkers();

            _client.JoinedGuild      += guild => ReloadWorkers();
            _client.LeftGuild        += guild => ReloadWorkers();
            _client.GuildAvailable   += guild => ReloadWorkers();
            _client.GuildUnavailable += guild => ReloadWorkers();
        }

        Task ReloadWorkers()
        {
            var guildIds = new HashSet<ulong>();

            // start worker for rolling in guilds, on separate threads
            foreach (var guild in _client.Guilds)
            {
                guildIds.Add(guild.Id);

                if (_cancellations.ContainsKey(guild.Id))
                    continue;

                var source = _cancellations[guild.Id] = new CancellationTokenSource();
                var token  = source.Token;

                _ = Task.Run(
                    async () =>
                    {
                        while (true)
                        {
                            try
                            {
                                await RunAsync(guild, token);
                                return;
                            }
                            catch (TaskCanceledException)
                            {
                                return;
                            }
                            catch (Exception e)
                            {
                                Log.Warning($"Error while rolling in guild '{guild}'.", e);

                                await Task.Delay(TimeSpan.FromSeconds(1), token);
                            }
                        }
                    },
                    token);
            }

            // stop workers for unavailable guilds
            foreach (var id in _cancellations.Keys)
            {
                if (!guildIds.Remove(id) && _cancellations.TryRemove(id, out var source))
                {
                    source.Cancel();
                    source.Dispose();

                    Log.Debug($"Stopped rolling worker for guild {id}.");
                }
            }

            return Task.CompletedTask;
        }

        async Task RunAsync(SocketGuild guild, CancellationToken cancellationToken = default)
        {
            Log.Debug($"Entering rolling loop for guild '{guild}'.");

            while (!cancellationToken.IsCancellationRequested)
            {
                var state = _state.Get(guild.Id);

                if (!_config.RollEnabled || state.RollsLeft == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                    continue;
                }

                foreach (var channel in guild.TextChannels)
                {
                    if (!_config.BotChannelIds.Contains(channel.Id))
                        continue;

                    using (channel.EnterTypingState())
                    {
                        await Task.Delay(_config.RollTypingDelay, cancellationToken);

                        try
                        {
                            await channel.SendMessageAsync(_config.RollCommand);

                            --state.RollsLeft;

                            Log.Debug($"{channel.Guild} {channel}: Rolled '{_config.RollCommand}'.");

                            // also roll $dk if we can
                            if (state.CanKakeraDailyReset)
                            {
                                await Task.Delay(_config.RollTypingDelay, cancellationToken);

                                await channel.SendMessageAsync(_config.DailyKakeraCommand);

                                state.CanKakeraDailyReset = false;

                                Log.Debug($"{channel.Guild} {channel}: Sent '{_config.DailyKakeraCommand}'.");
                            }
                        }
                        catch (Exception e)
                        {
                            Log.Warning($"{channel.Guild} {channel}: Could not send roll command.", e);
                        }
                    }

                    break;
                }

                var now = DateTime.Now;

                if (now > state.RollsReset || state.RollsLeft == 0)
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);

                else
                    await Task.Delay(new TimeSpan((state.RollsReset - now).Ticks / state.RollsLeft), cancellationToken);
            }
        }
    }
}
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using ServerManager.Events;

namespace ServerManager.Discord
{
    /// <summary>
    /// In-process adaptation of OrbOfDiscord's bounded webhook dispatcher and
    /// exact-match event router. No bot account, gateway, or external bridge is used.
    /// </summary>
    internal sealed class DiscordWebhooks : IDisposable
    {
        private const int QueueCapacity = 256;
        private const int DedupeCapacity = 4096;
        private const int MaximumRoutes = 32;
        private const int MaximumOperatorEventsPerMinute = 20;
        private const int MaximumOperatorKeys = 256;
        private const int MaximumAnonymousAccounts = 4096;
        private static readonly long OperatorWindowTicks = 60 * Stopwatch.Frequency;
        private static readonly long OperatorRepeatTicks = 30 * Stopwatch.Frequency;
        private readonly DiscordHttp _http;
        private readonly Action<string> _log;
        private Configuration _configuration;
        private readonly object _gate = new object();
        private readonly Queue<Delivery> _queue = new Queue<Delivery>();
        private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<string> _seenOrder = new Queue<string>();
        private readonly HashSet<string> _disabled = new HashSet<string>(StringComparer.Ordinal);
        // Separate nonblocking limits keep reconnect/diagnostic floods from
        // monopolizing gameplay routes. Route reloads retain these.
        private readonly Queue<long> _operatorTimes = new Queue<long>();
        private readonly Dictionary<string, long> _operatorLast = new Dictionary<string, long>(StringComparer.Ordinal);
        // Server/world lifetime only. Never keyed by a display name or a public
        // Actor.Id, which can mean different things for different producers.
        private readonly Dictionary<string, int> _anonymousAccounts = new Dictionary<string, int>(StringComparer.Ordinal);
        // A binary wake-up signal, not one permit per delivery. Reload can drop
        // an entire generation without leaving an unbounded stale permit count.
        private readonly SemaphoreSlim _available = new SemaphoreSlim(0, 1);
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private Task? _runner;
        private bool _accepting = true;
        private bool _disposed;
        private bool _resourcesReleased;
        private int _dropped;

        public DiscordWebhooks(DiscordSettings settings, DiscordHttp http, Action<string> log)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _log = log ?? delegate { };
            _configuration = BuildConfiguration(settings, strict: false);
        }

        internal void InheritRecentOperatorState(DiscordWebhooks previous)
        {
            if (previous == null || ReferenceEquals(previous, this)) return;
            long[] times;
            KeyValuePair<string, long>[] last;
            KeyValuePair<string, int>[] aliases;
            lock (previous._gate)
            {
                times = previous._operatorTimes.ToArray();
                last = previous._operatorLast.ToArray();
                aliases = previous._anonymousAccounts.ToArray();
            }
            long now = Stopwatch.GetTimestamp();
            lock (_gate)
            {
                if (!_accepting || _disposed) return;
                _operatorTimes.Clear();
                _operatorLast.Clear();
                // Called before the replacement admits events, including the
                // second copy after retirement. New worlds do not inherit this.
                _anonymousAccounts.Clear();
                foreach (KeyValuePair<string, int> entry in aliases)
                    if (_anonymousAccounts.Count < MaximumAnonymousAccounts)
                        _anonymousAccounts.Add(entry.Key, entry.Value);
                foreach (long time in times)
                    if (now - time < OperatorWindowTicks && _operatorTimes.Count < MaximumOperatorEventsPerMinute)
                        _operatorTimes.Enqueue(time);
                foreach (KeyValuePair<string, long> entry in last)
                    if (now - entry.Value < OperatorRepeatTicks && _operatorLast.Count < MaximumOperatorKeys)
                        _operatorLast[entry.Key] = entry.Value;
            }
        }

        /// <summary>
        /// Atomically replaces routes. Previously queued deliveries
        /// are discarded, and old in-flight work is cancelled without replay.
        /// A request already handed to Discord cannot be retracted. Invalid
        /// candidates throw before changing anything; stopping returns false.
        /// </summary>
        public bool Reload(DiscordSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            lock (_gate) { if (_disposed || !_accepting) return false; }
            Configuration candidate = BuildConfiguration(settings, strict: true);
            Configuration previous;
            lock (_gate)
            {
                if (_disposed || !_accepting)
                {
                    candidate.Stop.Dispose();
                    return false;
                }
                previous = _configuration;
                previous.Retired = true;
                _configuration = candidate;
                _queue.Clear();
                _disabled.Clear();
                // Preserve _seen: reloading is not permission to replay an event
                // whose earlier delivery may already have reached Discord.
                while (_available.Wait(0)) { }
            }
            Retire(previous);
            return true;
        }

        private Configuration BuildConfiguration(DiscordSettings settings, bool strict)
        {
            List<Route> routes = new List<Route>();
            HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
            if (strict && settings.WebhookRoutes != null && settings.WebhookRoutes.Count > MaximumRoutes)
                throw new ArgumentException("Discord webhook configuration has too many routes.", nameof(settings));
            foreach (DiscordWebhookRoute value in settings.WebhookRoutes ?? new List<DiscordWebhookRoute>())
            {
                if (value == null || routes.Count >= MaximumRoutes)
                {
                    if (strict) throw new ArgumentException("Discord webhook route is invalid.", nameof(settings));
                    continue;
                }
                if (string.IsNullOrWhiteSpace(value.Name) || value.Name.Length > 80 ||
                    value.Name.Any(char.IsControl) || !names.Add(value.Name))
                {
                    if (strict) throw new ArgumentException("Discord webhook route names must be unique and valid.", nameof(settings));
                    SafeLog("Discord webhook route skipped: a unique, nonempty name is required.");
                    continue;
                }
                try
                {
                    string anonymousPrefix = value.AnonymousPrefix ?? string.Empty;
                    if (!DiscordSettings.IsValidWebhookLanguage(value.Language))
                    {
                        if (strict) throw new ArgumentException("Discord webhook language is invalid.", nameof(settings));
                        SafeLog("Discord webhook route skipped: its language identifier is invalid.");
                        continue;
                    }
                    if (!ValidAnonymousPrefix(anonymousPrefix))
                    {
                        if (strict) throw new ArgumentException("Discord webhook anonymous prefix is invalid.", nameof(settings));
                        // Never silently turn an invalid anonymous route into a
                        // real-name route, even for programmatically built DTOs.
                        SafeLog("Discord webhook route skipped: its anonymous prefix is invalid.");
                        continue;
                    }
                    string destination = WebhookDestination(value.Url);
                    HashSet<string> events = new HashSet<string>(StringComparer.Ordinal);
                    if (strict && (value.Events == null || value.Events.Count == 0 ||
                        value.Events.Any(kind => !DiscordSettings.PublicEvents.Contains(kind))))
                        throw new ArgumentException("Discord webhook route event filters are invalid.", nameof(settings));
                    if (value.Events != null)
                        foreach (string kind in value.Events)
                            if (DiscordSettings.PublicEvents.Contains(kind))
                                events.Add(kind);
                    // No wildcards, prefix matching, or automatic default routes.
                    if (events.Count == 0) continue;
                    routes.Add(new Route(destination, Limit(Clean(value.Username), 80),
                        Avatar(value.AvatarUrl), anonymousPrefix.Trim(), value.Language, value.IncludeSteamId, events));
                }
                catch (DiscordHttpException)
                {
                    if (strict) throw;
                    SafeLog("Discord webhook route skipped: its Discord webhook URL is invalid.");
                }
            }
            return new Configuration(routes.ToArray());
        }

        public bool Enqueue(ServerManagerEvent value)
        {
            if (value == null || string.IsNullOrWhiteSpace(value.EventId) || value.EventId.Length > 160)
                return false;
            string? eventFilter = GetWebhookEventFilter(value);
            if (eventFilter == null) return false;

            Configuration configuration;
            lock (_gate)
            {
                if (!_accepting || _disposed || _seen.Contains(value.EventId)) return false;
                configuration = _configuration;
            }
            // The positive allowlist deliberately excludes normal, whisper,
            // clan and unknown chat types even if a route explicitly requests them.
            Route[] matches = configuration.Routes.Where(route => route.Events.Contains(eventFilter)).ToArray();
            if (matches.Length == 0) return false;
            bool isShout = value.Kind == "chat.shout" || value.Kind == "discord.shout";
            string content = isShout ? BuildShoutContent(value) : string.Empty;
            bool hasAnonymousText = value.Fields.TryGetValue("text", out string? shoutText) &&
                !string.IsNullOrWhiteSpace(Clean(shoutText));
            // Anonymous shouts require the standalone text. Falling back to
            // the preformatted message would disclose the original speaker.
            if (isShout)
                matches = matches.Where(route => route.AnonymousPrefix.Length == 0
                    ? !string.IsNullOrWhiteSpace(content) : hasAnonymousText).ToArray();
            if (matches.Length == 0) return false;
            int count = 0;
            bool dropped = false;
            lock (_gate)
            {
                // A concurrent reload may retire this already-built payload.
                // Never enqueue a retired route generation's payload.
                if (!_accepting || _disposed || !ReferenceEquals(configuration, _configuration) ||
                    _seen.Contains(value.EventId)) return false;
                matches = matches.Where(route => !_disabled.Contains(route.Url)).ToArray();
                if (matches.Length == 0) return false;
                if (_queue.Count + matches.Length > QueueCapacity)
                {
                    _dropped++;
                    dropped = _dropped == 1 || _dropped % 100 == 0;
                }
                else
                {
                    if (ServerManagerEventKinds.IsOperatorAudit(value.Kind) && !AdmitOperatorLocked(value))
                        return false;
                    _seen.Add(value.EventId);
                    _seenOrder.Enqueue(value.EventId);
                    while (_seenOrder.Count > DedupeCapacity) _seen.Remove(_seenOrder.Dequeue());
                    foreach (Route route in matches)
                    {
                        JObject payload = new JObject
                        {
                            ["allowed_mentions"] = new JObject
                            {
                                ["parse"] = new JArray(), ["users"] = new JArray(),
                                ["roles"] = new JArray(), ["replied_user"] = false
                            }
                        };
                        if (isShout)
                        {
                            payload["content"] = route.AnonymousPrefix.Length == 0 ? content :
                                RedactPrivateText(value, SafeText(
                                    AnonymousPlayerLocked(value.Actor, route.AnonymousPrefix) + ": " + shoutText, 2000));
                            payload["flags"] = 4; // SUPPRESS_EMBEDS: keep URLs as text, without preview cards.
                        }
                        else payload["embeds"] = BuildEmbedsLocked(value, route.AnonymousPrefix, route.Language, route.IncludeSteamId);
                        if (route.Username.Length > 0) payload["username"] = route.Username;
                        if (route.AvatarUrl.Length > 0) payload["avatar_url"] = route.AvatarUrl;
                        _queue.Enqueue(new Delivery(route.Url, payload, configuration));
                        count++;
                    }
                    SignalAvailableLocked();
                }
            }
            if (dropped) SafeLog("Discord webhook queue is full; an event was dropped.");
            return count > 0;
        }

        private static string? GetWebhookEventFilter(ServerManagerEvent value)
        {
            if (value.Kind == "command.executed" && Field(value, "source", 32) == "cron")
            {
                string action = OperatorToken(value.Fields, "command", 64);
                return action == "schedule" || action == "maintenance" ? "cron.executed" : null;
            }
            return DiscordSettings.GetWebhookEventFilter(value.Kind);
        }

        private bool AdmitOperatorLocked(ServerManagerEvent value)
        {
            long now = Stopwatch.GetTimestamp();
            while (_operatorTimes.Count > 0 && now - _operatorTimes.Peek() >= OperatorWindowTicks)
                _operatorTimes.Dequeue();
            if (_operatorTimes.Count >= MaximumOperatorEventsPerMinute) return false;
            string key = value.Kind + "\0" + OperatorAccount(value) + "\0" +
                OperatorToken(value.Fields, "reason_code", 96) + "\0" + OperatorToken(value.Fields, "outcome", 96);
            if (_operatorLast.TryGetValue(key, out long previous) && now - previous < OperatorRepeatTicks)
                return false;
            // Pruning is bounded independently of any input identity/attempt ID.
            foreach (string expired in _operatorLast.Where(entry => now - entry.Value >= OperatorRepeatTicks)
                         .Select(entry => entry.Key).ToArray())
                _operatorLast.Remove(expired);
            if (_operatorLast.Count >= MaximumOperatorKeys) return false;
            _operatorLast[key] = now;
            _operatorTimes.Enqueue(now);
            return true;
        }

        public Task RunAsync(CancellationToken ct)
        {
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(DiscordWebhooks));
                return _runner ?? (_runner = Task.Run(() => DrainAsync(ct)));
            }
        }

        private async Task DrainAsync(CancellationToken ct)
        {
            using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token))
            {
                try
                {
                    while (true)
                    {
                        Delivery? item = null;
                        lock (_gate)
                        {
                            if (_queue.Count == 0 && !_accepting) return;
                            if (_queue.Count > 0)
                            {
                                Delivery next = _queue.Dequeue();
                                if (ReferenceEquals(next.Configuration, _configuration) && !_disabled.Contains(next.Url))
                                {
                                    item = next;
                                    // Retired sources remain alive until this send has
                                    // disposed its linked cancellation registration.
                                    ++item.Configuration.InFlight;
                                }
                                else continue;
                            }
                        }
                        if (item == null)
                        {
                            await _available.WaitAsync(linked.Token).ConfigureAwait(false);
                            continue;
                        }
                        try
                        {
                            using CancellationTokenSource requestStop = CancellationTokenSource.CreateLinkedTokenSource(
                                linked.Token, item.Configuration.Token);
                            requestStop.Token.ThrowIfCancellationRequested();
                            // A lost reply can mean Discord already posted the notification.
                            // Retry only explicit 429 refusals, never ambiguous POST failures.
                            await _http.SendAsync(HttpMethod.Post, item.Url, item.Payload,
                                false, requestStop.Token, retry: false).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (linked.IsCancellationRequested) { return; }
                        catch (OperationCanceledException) when (item.Configuration.Token.IsCancellationRequested) { }
                        catch (DiscordHttpException exception)
                        {
                            if (exception.StatusCode == 404 || exception.StatusCode == 401 || exception.StatusCode == 403)
                            {
                                lock (_gate)
                                {
                                    if (ReferenceEquals(item.Configuration, _configuration))
                                        _disabled.Add(item.Url);
                                }
                                if (!item.Configuration.Token.IsCancellationRequested)
                                    SafeLog("Discord webhook disabled for this session after an authorization or missing-webhook response.");
                            }
                            else if (!item.Configuration.Token.IsCancellationRequested)
                                SafeLog("Discord webhook delivery failed; the notification was not replayed.");
                        }
                        catch (Exception)
                        {
                            if (!item.Configuration.Token.IsCancellationRequested)
                                SafeLog("Discord webhook delivery failed unexpectedly; the notification was not replayed.");
                        }
                        finally
                        {
                            lock (_gate)
                            {
                                --item.Configuration.InFlight;
                                ReleaseRetiredLocked(item.Configuration);
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
                finally
                {
                    lock (_gate) { _accepting = false; _queue.Clear(); }
                }
            }
        }

        public async Task StopAsync(TimeSpan timeout)
        {
            Task? runner;
            lock (_gate)
            {
                if (_disposed) return;
                _accepting = false;
                SignalAvailableLocked();
                runner = _runner;
            }
            if (runner == null) return;
            if (timeout < TimeSpan.Zero) timeout = TimeSpan.Zero;
            if (timeout > TimeSpan.FromSeconds(30)) timeout = TimeSpan.FromSeconds(30);
            if (await Task.WhenAny(runner, Task.Delay(timeout)).ConfigureAwait(false) != runner)
            {
                CancelLifetime();
                SafeLog("Discord webhook shutdown drain reached its time limit.");
                await Task.WhenAny(runner, Task.Delay(250)).ConfigureAwait(false);
            }
            if (runner.IsCompleted) await runner.ConfigureAwait(false);
        }

        private static string BuildShoutContent(ServerManagerEvent value)
        {
            // The local Discord audit line includes the author's ID. Public
            // chat uses only the display name and text; never fall back to it.
            if (value.Kind == "discord.shout")
            {
                if (!value.Fields.TryGetValue("text", out string? text) ||
                    string.IsNullOrWhiteSpace(Clean(text))) return string.Empty;
                return RedactPrivateText(value, SafeText(
                    (value.Actor?.Name ?? "[Discord]") + ": " + text, 2000));
            }
            // The event producer already builds "player name: text". Project only
            // that line; do not repeat fields or change the original logged event.
            if (!value.Fields.TryGetValue("message", out string? message)) return string.Empty;
            string content = SafeText(message, 2000);
            return RedactPrivateText(value, content);
        }

        private static string RedactPrivateText(ServerManagerEvent value, string content, bool hasRaidCoordinates = false)
        {
            // Preserve the existing protection against duplicated private values.
            string[] privateValues = value.Fields.Where(field => !MayRelay(field.Key, hasRaidCoordinates))
                .Select(field => SafeText(field.Value, 1024)).Where(text => text.Length >= 4)
                .Take(64).ToArray();
            return Limit(Redact(content, privateValues), 2000);
        }

        private string AnonymousPlayerLocked(ServerManagerActor? actor, string prefix)
        {
            string account = actor?.PlayerAccountId ?? string.Empty;
            if (account.Length == 0) return prefix + " player";
            if (!_anonymousAccounts.TryGetValue(account, out int number))
            {
                // Do not evict/reuse numbers: previously emitted aliases must
                // never identify a different account during the same world run.
                if (_anonymousAccounts.Count >= MaximumAnonymousAccounts) return prefix + " player";
                number = _anonymousAccounts.Count + 1;
                _anonymousAccounts.Add(account, number);
            }
            return prefix + " " + number.ToString(CultureInfo.InvariantCulture);
        }

        private JArray BuildEmbedsLocked(ServerManagerEvent value, string prefix, string language, bool includeSteamId)
        {
            // Both routes use one compact layout. Anonymous routes preserve
            // game subjects, but alias players and summarize operator details.
            bool anonymous = prefix.Length != 0;
            string player = DisplayPlayerLocked(value.Actor, prefix,
                Field(value, "player_name", 96, Field(value, "character", 96)));
            string title, description = string.Empty;
            switch (value.Kind)
            {
                case "server.ready": title = PlayerLocalizer.TextForLanguage(language, "sm_event_server_ready"); break;
                case "server.shutdown": title = PlayerLocalizer.TextForLanguage(language, "sm_event_server_shutdown"); break;
                case "server.saved": title = PlayerLocalizer.TextForLanguage(language, "sm_event_world_saved"); break;
                case "player.login":
                    title = PlayerLocalizer.TextForLanguage(language,
                        value.Fields.TryGetValue("first_join", out string? firstJoin) && firstJoin == "true"
                            ? "sm_event_player_first_join" : "sm_event_player_joined", player); break;
                case "player.leave": title = PlayerLocalizer.TextForLanguage(language, "sm_event_player_left", player); break;
                case "player.death":
                case "combat.pvp_kill":
                case "boss.killed":
                    // Choose the same EventId-bound template as the game, with
                    // identity projection BEFORE formatting on anonymous routes.
                    string storyPlayer = anonymous ? player : EventMessageText.PlayerName(value);
                    string storyOther = EventMessageText.OtherName(value);
                    if (anonymous)
                    {
                        string cause = Field(value, "cause", 64);
                        // Player attribution takes precedence over a conflicting
                        // creature/boss label. Never infer an account from a name.
                        bool otherIsPlayer = value.Kind == "combat.pvp_kill" ||
                            cause == "pvp" || cause == "playerhit" ||
                            string.Equals(value.Target?.Kind, "player", StringComparison.OrdinalIgnoreCase) ||
                            !string.IsNullOrEmpty(value.Target?.PlayerAccountId);
                        if (otherIsPlayer)
                            storyOther = DisplayPlayerLocked(value.Target, prefix, string.Empty);
                        else if (value.Kind != "boss.killed" && cause != "creature" &&
                            cause != "enemyhit" && cause != "boss")
                            storyOther = string.Empty;
                    }
                    if (!EventMessageText.TryFormat(value, storyPlayer, storyOther, language, out title))
                        title = "Server event";
                    description = value.Reliability == "client_reported"
                        ? PlayerLocalizer.TextForLanguage(language, "sm_event_client_report") : string.Empty;
                    break;
                case "raid.started": case "raid.ended":
                    // Bound external translations before appending the subject
                    // and center so later payload limits cannot cut that suffix.
                    title = OneLine(PlayerLocalizer.TextForLanguage(language,
                        value.Kind == "raid.started" ? "sm_event_raid_started" : "sm_event_raid_ended"), 500);
                    string raid = Field(value, "raid", 96);
                    if (raid.Length > 0) title += " — " + raid;
                    if (HasVerifiedRaidCoordinates(value)) title += " [" + value.Fields["raid_coordinates"] + "]";
                    break;
                case "server.announcement":
                    title = PlayerLocalizer.TextForLanguage(language, "sm_event_announcement");
                    description = value.Fields.TryGetValue("message", out string? announcement) ? announcement : string.Empty;
                    break;
                case "moderation.action":
                    string action = Field(value, "action");
                    title = (action == "kick" ? "Kick requested" : action == "ban" ? "Ban requested" :
                        action == "unban" ? "Unban requested" : "Moderation request") + " — " +
                        DisplayPlayerLocked(value.Target, prefix, Field(value, "target", 96));
                    if (!anonymous) description = Field(value, "reason", 300);
                    break;
                case "command.executed":
                    if (IsCronSummary(value))
                    {
                        title = CronTitle(value);
                        description = CronDescription(value);
                        break;
                    }
                    string command = OperatorToken(value.Fields, "command", 64);
                    if (anonymous && !KnownCommand(command)) command = string.Empty;
                    title = "Command" + (command.Length == 0 ? string.Empty : ": " + command);
                    string target = anonymous ? string.Empty :
                        OneLine(value.Target?.Name, 96, Field(value, "target", 96));
                    if (anonymous && value.Target?.PlayerAccountId.Length > 0)
                        target = AnonymousPlayerLocked(value.Target, prefix);
                    if (target.Length > 0) title += " — " + target;
                    description = CommandResult(value, anonymous);
                    break;
                case "security.detection":
                    title = "Security observation — " + player;
                    description = Diagnostic(value, anonymous, "Security finding") + ReportSuffix(value);
                    break;
                case "security.response":
                    title = "Security response — " + player;
                    string response = Field(value, "response");
                    description = (response == "Off" || response == "Log" || response == "Kick" || response == "Ban"
                        ? response + ": " : string.Empty) + ResponseResult(Field(value, "outcome"));
                    break;
                case "security.admin_bypass":
                    title = "Admin bypass — " + player;
                    description = Diagnostic(value, anonymous, "Policy exemption"); break;
                case "character.save_rejected":
                    title = "Character save rejected — " + player;
                    description = Diagnostic(value, anonymous, "Character validation failed"); break;
                case "character.shadow_stalled":
                    title = "Character upload delayed — " + player; break;
                case "character.validation_observed":
                    title = "Character validation notice — " + player;
                    description = Diagnostic(value, anonymous, "Policy observation") + " (observe only)"; break;
                case "character.revision_observed":
                    title = "Skill change observed — " + player;
                    description = Diagnostic(value, anonymous, "Skill progress observation"); break;
                case "connection.rejected":
                    string subject = Field(value, "identity_status") == "authenticated" ? player :
                        anonymous ? prefix + " player" : "Unverified player";
                    title = (Field(value, "outcome") == "client_aborted" ? "Connection stopped by client" :
                        "Connection rejected") + " — " + subject;
                    string category = Field(value, "category");
                    string fallback = category == "mod_policy" ? "Mod requirements not met" :
                        category == "character" ? "Character could not be admitted" :
                        category == "authentication" ? "Authentication failed" : "Connection protocol error";
                    description = Diagnostic(value, anonymous, fallback);
                    // Identifies mismatched mods on private/operator routes, but
                    // arbitrary names/IDs inside summaries stay out of anonymity.
                    string plugins = Field(value, "plugin_details", 1024);
                    if (!anonymous && category == "mod_policy" && plugins.Length > 0)
                        description += "\n" + plugins;
                    break;
                default: title = "Server event"; break;
            }

            JObject primary = CompactEmbed(value, title, description);
            if (includeSteamId && !anonymous && (value.Kind == "player.login" || value.Kind == "player.leave"))
            {
                string steamId = VerifiedPlayerSteamId(value.Actor);
                // Add only this validated numeric identity after generic private
                // text redaction. Never broaden field/message relay permissions.
                if (steamId.Length > 0) primary["description"] = "Steam ID: " + steamId;
            }
            JArray embeds = new JArray(primary);
            // World success is already verified by the producer. A partial
            // character checkpoint is a separate small card, not extra fields
            // on "World saved", and not a second event/rate-limit/queue item.
            if (value.Kind == "server.saved" && value.Reliability == "authoritative" &&
                value.Fields.TryGetValue("completion_scope", out string? scope) && scope == "world_disk_with_partial_retained_characters" &&
                value.Fields.TryGetValue("character_commit_scope", out string? characterScope) && characterScope == "partial_retained_shadows_at_cutoff" &&
                value.Fields.TryGetValue("pending_character_count", out string? rawCount) &&
                rawCount != null && rawCount.Length <= 10 &&
                int.TryParse(rawCount, NumberStyles.None, CultureInfo.InvariantCulture, out int pending) && pending > 0 &&
                rawCount == pending.ToString(CultureInfo.InvariantCulture))
            {
                JObject warning = CompactEmbed(value, "Character save pending — " + pending.ToString(CultureInfo.InvariantCulture), string.Empty);
                warning["color"] = 0xF1C40F;
                embeds.Add(warning);
            }
            return embeds;
        }

        private static JObject CompactEmbed(ServerManagerEvent value, string title, string description)
        {
            // Deliberately no fields, footer or embed timestamp. Keep Discord's
            // message timestamp, route identity, color and mention protection.
            bool hasRaidCoordinates = HasVerifiedRaidCoordinates(value);
            string safeTitle = RedactPrivateText(value, SafeText(title, 2000), hasRaidCoordinates);
            string body = RedactPrivateText(value, SafeText(description, 2000), hasRaidCoordinates);
            JObject embed = new JObject
            {
                ["color"] = ColorFor(value.Kind)
            };
            if (safeTitle.Length > 256 &&
                (value.Kind == "player.death" || value.Kind == "combat.pvp_kill" || value.Kind == "boss.killed" ||
                 value.Kind == "raid.started" || value.Kind == "raid.ended"))
            {
                // Escaping names or a long translation can exceed the title cap.
                // Keep the complete line in the body without a duplicate heading
                // or truncating its final attacker, boss or raid coordinates.
                body = safeTitle + (body.Length > 0 ? "\n" + body : string.Empty);
            }
            else embed["title"] = Limit(safeTitle, 256);
            if (!string.IsNullOrWhiteSpace(body)) embed["description"] = body;
            return embed;
        }

        private static string VerifiedPlayerSteamId(ServerManagerActor? actor)
        {
            // This adapter-only field is assigned by authenticated producers.
            // Recheck its shape at the disclosure boundary; raw Id, names and
            // public event fields are not substitutes for verified attribution.
            const string prefix = "steamworks:";
            string account = actor?.PlayerAccountId ?? string.Empty;
            if (account.Length != prefix.Length + 17 || !account.StartsWith(prefix, StringComparison.Ordinal))
                return string.Empty;
            string steamId = account.Substring(prefix.Length);
            return ulong.TryParse(steamId, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) &&
                id >= 76561197960265729UL && id <= 76561202255233023UL ? steamId : string.Empty;
        }

        private string DisplayPlayerLocked(ServerManagerActor? actor, string prefix, string fallback)
        {
            if (prefix.Length > 0) return AnonymousPlayerLocked(actor, prefix);
            return OneLine(actor?.Name, 96, fallback.Length > 0 ? fallback : "Unknown player");
        }

        private static string Field(ServerManagerEvent value, string key, int length = 128, string fallback = "") =>
            value.Fields.TryGetValue(key, out string? text) ? OneLine(text, length, fallback) : fallback;

        private static string OneLine(string? value, int length, string fallback = "")
        {
            string text = Clean(value).Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ').Trim();
            return text.Length == 0 ? fallback : Limit(text, length);
        }

        private static string ReportSuffix(ServerManagerEvent value) =>
            value.Reliability == "client_reported" ? " (client report)" : string.Empty;

        private static string Diagnostic(ServerManagerEvent value, bool anonymous, string fallback)
        {
            string evidence = OperatorToken(value.Fields, "evidence", 96);
            string reason = OperatorToken(value.Fields, "reason_code", 96);
            string known = DiagnosticLabel(evidence);
            if (known.Length == 0) known = DiagnosticLabel(reason);
            return known.Length > 0 ? known : anonymous ? fallback :
                reason.Length > 0 ? reason : evidence.Length > 0 ? evidence : fallback;
        }

        private static string DiagnosticLabel(string code)
        {
            switch (code)
            {
                case "CheatEngineProcess": return "Cheat Engine process";
                case "CheatEngineInjectedModule": return "Cheat Engine module";
                case "ArtMoneyProcess": return "ArtMoney process";
                case "WeModProcess": return "WeMod process";
                case "SharpMonoInjectorProcess": return "SharpMonoInjector process";
                case "GenericCheatProcess": case "GenericInjectProcess": case "GenericTrainerProcess": return "External-tool process signature";
                case "ValheimToolerDetected": return "ValheimTooler";
                case "CheatCommand": return "Cheat command";
                case "CarryWeightLimitExceeded": return "Carry-weight limit exceeded";
                case "MaximumDamageLimitExceeded": return "Damage limit exceeded";
                case "MaximumHealthLimitExceeded": return "Health limit exceeded";
                case "MaximumStaminaLimitExceeded": return "Stamina limit exceeded";
                case "MaximumEitrLimitExceeded": return "Eitr limit exceeded";
                case "MalformedGameplayTraffic": return "Malformed gameplay traffic";
                case "InvalidGameplayValue": return "Invalid gameplay value";
                case "ProcessDetectorUnsupported": return "Process detector unsupported";
                case "ProcessDetectorUnavailable": return "Process detector unavailable";
                case "AssemblyDetectorUnavailable": return "Assembly detector unavailable";
                case "forbidden_prefab": return "Forbidden item";
                case "used_cheats": return "Profile used-cheats flag";
                case "character_validation_failed": case "character_validation": return "Character validation failed";
                case "fresh_local_character_required": case "fresh_character_required": return "Create a new character";
                case "stored_policy_violation": return "Stored character policy observation";
                case "skill_gain": return "Unusual skill gain";
                case "skill_accumulator": return "Unusual skill progress";
                case "shadow_upload_stalled": return "Character upload delayed";
                case "ManifestRejected": return "Mod requirements not met";
                case "ManifestValidatorFailed": return "Mod check failed";
                case "HandshakeTimedOut": return "Connection handshake timed out";
                case "PeerIdentityUnavailable": case "PeerInfoAuthenticationIncomplete": return "Steam authentication incomplete";
                case "DuplicateConnection": return "Account already connected";
                case "ProtocolVersionMismatch": return "ServerManager protocol mismatch";
                case "CharacterTransferFailed": return "Character transfer failed";
                case "ClientCharacterRejected": return "Client rejected the character";
                default: return string.Empty;
            }
        }

        private static string ResponseResult(string outcome)
        {
            switch (outcome)
            {
                case "observed": return "Observed";
                case "diagnostic": return "Diagnostic report";
                case "response_requested": return "Response requested";
                case "processing_failed": return "Processing failed";
                case "banlist_add_returned": return "Ban-list update call completed";
                case "banlist_add_failed": return "Ban list update failed";
                case "kick_call_returned": return "Kick call completed";
                case "disconnect_fallback_scheduled": return "Disconnect fallback scheduled";
                case "bypassed": return "Admin exemption applied";
                case "would_reject": return "Observed only; save not rejected";
                case "save_rejected": return "Save rejected";
                case "rejected": return "Connection rejected";
                case "client_aborted": return "Connection stopped by client";
                default: return "Result unavailable";
            }
        }

        private static string CommandResult(ServerManagerEvent value, bool anonymous)
        {
            string code = OperatorToken(value.Fields, "result_code", 64);
            string success = Field(value, "success");
            if (!anonymous) return (success == "false" ? "Failed: " : "Result: ") +
                (code.Length > 0 ? code : success == "true" ? "Returned success" : "Unavailable");
            if (success == "false") return "Failed";
            if (success != "true") return "Result unavailable";
            switch (code)
            {
                case "save_scheduled": case "save_requested": case "kick_requested":
                case "ban_requested": case "unban_requested": case "reload_requested": return "Request accepted";
                case "rcon_dispatched": return "Forwarded to the server console";
                case "ram_accepted": return "RAM snapshot accepted";
                default: return "Command returned success";
            }
        }

        private static bool IsCronSummary(ServerManagerEvent value)
        {
            if (Field(value, "source", 32) != "cron") return false;
            string action = OperatorToken(value.Fields, "command", 64);
            return action == "schedule" || action == "maintenance";
        }

        private static string CronTitle(ServerManagerEvent value)
        {
            string code = OperatorToken(value.Fields, "result_code", 64);
            string marker = Field(value, "success") == "false" ? "\u274c" :
                code == "cron_skipped" ? "\u23ed" : code == "cron_dispatched_untracked" ? "\u2139" : "\u2705";
            string verb = OperatorToken(value.Fields, "cron_verb", 64);
            string count = OperatorToken(value.Fields, "cron_command_count", 2);
            string subject = verb.Length > 0 ? verb : count.Length > 0 ? count + " commands" : string.Empty;
            return marker + " Cron" + (subject.Length > 0 ? " · " + subject : string.Empty);
        }

        private static string CronDescription(ServerManagerEvent value)
        {
            string summary = StripValheimRichText(Field(value, "cron_summary", 1000));
            string verb = OperatorToken(value.Fields, "cron_verb", 64);
            if (summary == verb) summary = string.Empty;
            string schedule = Field(value, "cron_schedule", 256);
            string result = Field(value, "success") == "false" ? CommandResult(value, false) :
                Field(value, "result_code") == "cron_dispatched_untracked" ? "Dispatched; completion not tracked" : string.Empty;
            return string.Join(" · ", new[] { summary, schedule, result }.Where(text => text.Length > 0));
        }

        private static string StripValheimRichText(string value)
        {
            StringBuilder result = new StringBuilder(value.Length);
            for (int index = 0; index < value.Length; ++index)
            {
                if (value[index] == '<')
                {
                    int close = value.IndexOf('>', index + 1);
                    if (close > index && close - index <= 48 && IsValheimRichTextTag(value.Substring(index + 1, close - index - 1)))
                    {
                        index = close;
                        continue;
                    }
                }
                result.Append(value[index]);
            }
            return OneLine(result.ToString(), 1000);
        }

        private static bool IsValheimRichTextTag(string value)
        {
            string tag = value.Trim();
            if (tag.StartsWith("/", StringComparison.Ordinal)) tag = tag.Substring(1);
            int assignment = tag.IndexOf('=');
            if (assignment >= 0) tag = tag.Substring(0, assignment);
            switch (tag.ToLowerInvariant())
            {
                case "color": case "size": case "b": case "i": case "u": case "s": return true;
                default: return false;
            }
        }

        private static bool KnownCommand(string command)
        {
            // Privacy allowlist, not a second command registry: unknown future
            // names remain usable but get a generic anonymous notification.
            switch (command)
            {
                case "status": case "players": case "adminlist": case "adminadd": case "adminremove":
                case "accesslist": case "accessadd": case "accessremove": case "banlist": case "keylist": case "keyadd":
                case "keyremove": case "eventstart": case "eventstop": case "characterlist": case "characterinfo":
                case "characterbackups": case "characterrestore": case "giveitem": case "teleport": case "skillget":
                case "skillset": case "heal": case "damage": case "modsstatus":
                case "modsreload": case "discordstatus": case "discordtest": case "help": case "rcon": case "save":
                case "kick": case "ban": case "unban": case "announcement": return true;
                default: return false;
            }
        }

        private static bool ValidAnonymousPrefix(string raw)
        {
            if (raw.Trim().Length > 32) return false;
            // Defend direct DTO callers too; settings perform the same check
            // before trimming the YAML scalar and retaining the last good file.
            for (int index = 0; index < raw.Length; ++index)
            {
                char value = raw[index];
                bool pair = char.IsHighSurrogate(value) && index + 1 < raw.Length && char.IsLowSurrogate(raw[index + 1]);
                if ((char.IsSurrogate(value) && !pair) || char.IsControl(value)) return false;
                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(raw, index);
                if (category == UnicodeCategory.Format || category == UnicodeCategory.LineSeparator || category == UnicodeCategory.ParagraphSeparator) return false;
                if (pair) ++index;
            }
            return true;
        }

        private static string OperatorAccount(ServerManagerEvent value)
        {
            // Admission before authentication must not present a claimed name
            // or account as a verified player, or let claimed IDs evade limits.
            if (value.Kind == "connection.rejected" &&
                OperatorToken(value.Fields, "identity_status", 32) != "authenticated") return string.Empty;
            const string prefix = "steamworks:";
            if (!value.Fields.TryGetValue("account_id", out string? account) || account == null ||
                account.Length != prefix.Length + 17 || !account.StartsWith(prefix, StringComparison.Ordinal)) return string.Empty;
            for (int index = prefix.Length; index < account.Length; ++index)
                if (account[index] < '0' || account[index] > '9') return string.Empty;
            return account;
        }

        private static string OperatorToken(IReadOnlyDictionary<string, string> fields, string key, int maximum)
        {
            if (!fields.TryGetValue(key, out string? value) || string.IsNullOrEmpty(value) || value.Length > maximum)
                return string.Empty;
            foreach (char character in value)
                if (!(character >= 'a' && character <= 'z') && !(character >= 'A' && character <= 'Z') &&
                    !(character >= '0' && character <= '9') && character != '_' && character != '-' && character != '.')
                    return string.Empty;
            return value;
        }

        private static bool HasVerifiedRaidCoordinates(ServerManagerEvent value)
        {
            // This is the sole coordinate exception, populated from the server's
            // random-event center. Never widen it to player or client-report fields.
            if ((value.Kind != "raid.started" && value.Kind != "raid.ended") ||
                value.Reliability != "authoritative" ||
                !value.Fields.TryGetValue("raid_coordinates", out string? coordinates) ||
                string.IsNullOrEmpty(coordinates) || coordinates.Length > 37) return false;
            string[] parts = coordinates.Split(new[] { ", " }, StringSplitOptions.None);
            if (parts.Length != 3) return false;
            foreach (string part in parts)
                if (!int.TryParse(part, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int number) ||
                    part != number.ToString(CultureInfo.InvariantCulture)) return false;
            return true;
        }

        private static bool MayRelay(string key, bool hasRaidCoordinates)
        {
            if (hasRaidCoordinates && key == "raid_coordinates") return true;
            string normalized = new string((key ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            if (normalized.Contains("token") || normalized.Contains("password") || normalized.Contains("secret")
                || normalized.Contains("webhook") || normalized.Contains("authorization")
                || normalized.Contains("apikey") || normalized.Contains("credential")) return false;
            // All other coordinate and inventory fields remain local-only.
            if (normalized.Contains("coordinate") || normalized.Contains("position")
                || normalized == "location" || normalized == "pos" || normalized == "posx"
                || normalized == "posy" || normalized == "posz" || normalized == "x"
                || normalized == "y" || normalized == "z") return false;
            if (normalized.Contains("inventory") || normalized.Contains("itemdata")
                || normalized.Contains("equipment") || normalized == "items") return false;
            return true;
        }

        private static string WebhookDestination(string url)
        {
            Uri uri = DiscordHttp.ValidateEndpoint(url);
            string[] parts = DiscordHttp.ApiResourcePath(uri).Split('/');
            if (parts.Length != 3 || parts[0] != "webhooks" || !Digits(parts[1])
                || parts[2].Length < 1 || parts[2].Length > 256
                || parts[2].Any(character => !(char.IsLetterOrDigit(character) || character == '-' || character == '_')))
                throw new DiscordHttpException(0, "Discord webhook URL is invalid.");
            string thread = string.Empty;
            if (uri.Query.Length > 0)
            {
                foreach (string query in uri.Query.Substring(1).Split('&'))
                {
                    if (query == "wait=true" || query == "wait=false") continue;
                    if (query.StartsWith("thread_id=", StringComparison.Ordinal) && Digits(query.Substring(10)) && thread.Length == 0)
                        thread = "&" + query;
                    else throw new DiscordHttpException(0, "Discord webhook query is invalid.");
                }
            }
            return uri.GetLeftPart(UriPartial.Path) + "?wait=true" + thread;
        }

        private static bool Digits(string value) => value.Length > 0 && value.Length <= 20
            && value.All(character => character >= '0' && character <= '9');

        private static string Avatar(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Length <= 1024 && Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
                && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
                ? uri.AbsoluteUri : string.Empty;
        }

        private static string Clean(string? value)
        {
            if (value == null || value.Length == 0) return string.Empty;
            StringBuilder result = new StringBuilder(Math.Min(value.Length, 5000));
            foreach (char character in value)
            {
                if (result.Length >= 5000) break;
                if (!char.IsControl(character) || character == '\n' || character == '\t') result.Append(character);
            }
            return result.ToString();
        }

        private static string SafeText(string? value, int length)
        {
            string text = Limit(Clean(value), length);
            StringBuilder escaped = new StringBuilder(text.Length);
            foreach (char character in text)
            {
                if ("\\*_`~|>[]()".IndexOf(character) >= 0) escaped.Append('\\');
                escaped.Append(character);
                if (character == '@') escaped.Append('\u200b');
            }
            return Limit(escaped.ToString(), length);
        }

        private static string Limit(string value, int length)
        {
            if (value.Length <= length) return value;
            int count = Math.Max(0, length - 1);
            if (count > 0 && char.IsHighSurrogate(value[count - 1])) count--;
            return value.Substring(0, count) + "…";
        }

        private static string Redact(string value, IEnumerable<string> privateValues)
        {
            foreach (string secret in privateValues) value = value.Replace(secret, "[redacted]");
            return value;
        }

        private static int ColorFor(string kind)
        {
            switch (kind)
            {
                case "server.ready": return 0x57F287;
                case "server.shutdown": return 0x747F8D;
                case "player.death": case "combat.pvp_kill": return 0xED4245;
                case "boss.killed": return 0xFEE75C;
                case "raid.started": case "raid.ended": return 0xEB459E;
                default: return 0x5865F2;
            }
        }

        private void SafeLog(string message)
        {
            try { _log(message); } catch (Exception) { }
        }

        private void SignalAvailableLocked()
        {
            if (_available.CurrentCount == 0) _available.Release();
        }

        private void Retire(Configuration configuration)
        {
            // Cancellation callbacks may complete the asynchronous sender. Do
            // not run them under the queue lock or dispose a source while a
            // dequeued send is still about to register its linked token.
            try { configuration.Stop.Cancel(); }
            catch (Exception) { SafeLog("Discord webhook generation cancellation encountered a callback failure."); }
            finally
            {
                lock (_gate)
                {
                    configuration.CancellationFinished = true;
                    ReleaseRetiredLocked(configuration);
                }
            }
        }

        private static void ReleaseRetiredLocked(Configuration configuration)
        {
            if (configuration.Retired && configuration.CancellationFinished &&
                configuration.InFlight == 0 && !configuration.Disposed)
            {
                configuration.Disposed = true;
                configuration.Stop.Dispose();
            }
        }

        private void CancelLifetime()
        {
            lock (_gate)
            {
                if (_resourcesReleased) return;
                // ReleaseResources uses this same lock; a concurrent StopAsync
                // timeout cannot cancel an already-disposed lifetime source.
                try { _stop.Cancel(); }
                catch (AggregateException) { }
            }
        }

        public void Dispose()
        {
            Task? runner;
            Configuration configuration;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _accepting = false;
                _queue.Clear();
                runner = _runner;
                configuration = _configuration;
                configuration.Retired = true;
            }
            Retire(configuration);
            CancelLifetime();
            if (runner == null) ReleaseResources();
            else _ = runner.ContinueWith(_ => ReleaseResources(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private void ReleaseResources()
        {
            lock (_gate)
            {
                if (_resourcesReleased) return;
                _resourcesReleased = true;
                _available.Dispose();
                _stop.Dispose();
            }
        }

        private sealed class Configuration
        {
            internal Configuration(Route[] routes)
            {
                Routes = routes;
                Token = Stop.Token;
            }
            internal readonly Route[] Routes;
            internal readonly CancellationTokenSource Stop = new CancellationTokenSource();
            internal readonly CancellationToken Token;
            // Lifecycle fields are protected by the owning dispatcher's _gate.
            internal int InFlight;
            internal bool Retired, CancellationFinished, Disposed;
        }

        private sealed class Route
        {
            internal Route(string url, string username, string avatarUrl, string anonymousPrefix, string language, bool includeSteamId, HashSet<string> events)
            { Url = url; Username = username; AvatarUrl = avatarUrl; AnonymousPrefix = anonymousPrefix; Language = language; IncludeSteamId = includeSteamId; Events = events; }
            internal string Url { get; }
            internal string Username { get; }
            internal string AvatarUrl { get; }
            internal string AnonymousPrefix { get; }
            internal string Language { get; }
            internal bool IncludeSteamId { get; }
            internal HashSet<string> Events { get; }
        }

        private sealed class Delivery
        {
            internal Delivery(string url, JObject payload, Configuration configuration)
            { Url = url; Payload = payload; Configuration = configuration; }
            internal string Url { get; }
            internal JObject Payload { get; }
            internal Configuration Configuration { get; }
        }
    }
}

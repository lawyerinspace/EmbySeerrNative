using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Logging;

namespace EmbySeerrNative
{
    /// <summary>
    /// The single code path for submitting requests (Favourite-to-request in the Emby apps).
    /// Validates input, checks the caller's Seerr link, pre-checks availability to avoid
    /// duplicates, then POSTs /api/v1/request as the linked Seerr user.
    /// </summary>
    internal static class RequestService
    {
        public static ILogger Logger { get; set; }

        private const int MaxRequestsPerMinute = 8;
        private static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(10);

        private static readonly ConcurrentDictionary<string, Queue<DateTime>> RateLimits =
            new ConcurrentDictionary<string, Queue<DateTime>>();
        private static readonly ConcurrentDictionary<string, DateTime> Recent =
            new ConcurrentDictionary<string, DateTime>();
        private static readonly ConcurrentDictionary<string, byte> InFlight =
            new ConcurrentDictionary<string, byte>();

        /// <summary>
        /// Parses "all", "" or null (= every requestable season) or "1,2,3".
        /// Returns null for "all". Throws ArgumentException on invalid input.
        /// </summary>
        public static List<int> ParseSeasons(string seasons)
        {
            if (string.IsNullOrWhiteSpace(seasons) || seasons.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
                return null;
            var list = new List<int>();
            foreach (var part in seasons.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 0 || n > 1000)
                    throw new ArgumentException("Seasons must be 'all' or a comma-separated list of season numbers.");
                if (!list.Contains(n)) list.Add(n);
                if (list.Count > 200) throw new ArgumentException("Too many seasons.");
            }
            if (list.Count == 0) throw new ArgumentException("Choose at least one season.");
            list.Sort();
            return list;
        }

        public static List<int> ParseTagIds(string text)
        {
            var result = new List<int>();
            if (string.IsNullOrWhiteSpace(text)) return result;
            foreach (var part in text.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 && !result.Contains(n))
                    result.Add(n);
            }
            return result;
        }

        private static RequestOutcomeDto Fail(string code, string message)
            => new RequestOutcomeDto { Ok = false, Code = code, Message = message };

        public static async Task<RequestOutcomeDto> SubmitAsync(User user, string mediaType, int tmdbId, string seasonsText,
            string destination, CancellationToken ct)
        {
            if (!SeerrClient.IsConfigured(out var problem)) return Fail("not_configured", problem);
            if (user == null) return Fail("not_linked", "Requests must be made by a signed-in Emby user.");

            mediaType = (mediaType ?? "").Trim().ToLowerInvariant();
            if (mediaType != "movie" && mediaType != "tv") return Fail("invalid", "MediaType must be 'movie' or 'tv'.");
            if (tmdbId <= 0) return Fail("invalid", "A valid TMDB id is required.");

            List<int> chosenSeasons = null;
            if (mediaType == "tv")
            {
                try { chosenSeasons = ParseSeasons(seasonsText); }
                catch (ArgumentException ex) { return Fail("invalid", ex.Message); }
            }

            var cfg = Plugin.Config;
            // remote | local; anything else = the configured default.
            var destKey = string.Equals(destination, "local", StringComparison.OrdinalIgnoreCase) ? "local"
                : string.Equals(destination, "remote", StringComparison.OrdinalIgnoreCase) ? "remote"
                : (cfg.DefaultDestination == "local" ? "local" : "remote");
            var dest = destKey == "local" ? cfg.LocalDestination : cfg.RemoteDestination;

            var userKey = UserLinker.EmbyUserKey(user);
            if (!TakeRateLimitToken(userKey))
                return Fail("rate_limited", "Too many requests in a short time. Wait a minute and try again.");

            var dedupeKey = userKey + "|" + mediaType + "|" + tmdbId + "|" +
                            (chosenSeasons == null ? "all" : string.Join(",", chosenSeasons));
            PruneRecent();
            if (Recent.ContainsKey(dedupeKey))
                return Fail("already_requested", "You already sent this request a moment ago.");
            if (!InFlight.TryAdd(dedupeKey, 0))
                return Fail("already_requested", "This request is already being submitted.");

            try
            {
                SeerrLink link;
                try
                {
                    link = await UserLinker.ResolveAsync(user, ct).ConfigureAwait(false);
                }
                catch (SeerrException ex)
                {
                    return Fail(ex.HttpStatus == 0 ? "seerr_unreachable" : "seerr_error", ex.Message);
                }
                if (link.BlockedReason != null) return Fail("not_linked", link.BlockedReason);

                // Fresh (uncached) status check to avoid duplicate requests.
                TitleDetailsDto details;
                try
                {
                    details = await SeerrClient.GetDetailsAsync(mediaType, tmdbId, ct, useCache: false).ConfigureAwait(false);
                }
                catch (SeerrException ex)
                {
                    return Fail(ex.HttpStatus == 0 ? "seerr_unreachable" : "seerr_error", ex.Message);
                }

                var body = new Dictionary<string, object>
                {
                    ["mediaType"] = mediaType,
                    ["mediaId"] = tmdbId,
                    ["is4k"] = false
                };

                string seasonText = null;
                if (mediaType == "movie")
                {
                    if (EmbyLibrary.FindTmdbIds("movie", new[] { tmdbId }).ContainsKey(tmdbId))
                        return Fail("already_available", details.Title + " is already in your Emby library.");
                    switch (details.Status)
                    {
                        case Availability.Available:
                            return Fail("already_available", details.Title + " is already available.");
                        case Availability.Pending:
                            return Fail("already_requested", details.Title + " has already been requested and is awaiting approval.");
                        case Availability.Processing:
                            return Fail("already_requested", details.Title + " has already been requested and is being processed.");
                        case Availability.Blocklisted:
                            return Fail("forbidden", details.Title + " is blocklisted in Seerr.");
                    }
                }
                else
                {
                    if (details.Status == Availability.Blocklisted)
                        return Fail("forbidden", details.Title + " is blocklisted in Seerr.");

                    var requestable = details.Seasons.Where(s => s.CanRequest).Select(s => s.SeasonNumber).ToList();
                    List<int> toSend;
                    if (details.Seasons.Count == 0)
                    {
                        toSend = chosenSeasons; // no season list from TMDB: let Seerr decide ("all" when null)
                    }
                    else if (chosenSeasons == null)
                    {
                        toSend = requestable.Where(n => n > 0).ToList();
                        if (toSend.Count == 0) toSend = requestable; // specials-only shows
                        if (toSend.Count == 0)
                            return Fail("nothing_to_request", "Every season of " + details.Title + " is already available or requested.");
                    }
                    else
                    {
                        var unknown = chosenSeasons.Where(n => details.Seasons.All(s => s.SeasonNumber != n)).ToList();
                        if (unknown.Count > 0)
                            return Fail("invalid", details.Title + " has no season " + string.Join(", ", unknown) + ".");
                        toSend = chosenSeasons.Where(requestable.Contains).ToList();
                        if (toSend.Count == 0)
                            return Fail("nothing_to_request", "The selected seasons are already available or requested.");
                    }

                    if (toSend == null)
                    {
                        body["seasons"] = "all"; // documented MediaRequestBody: number[] | 'all'
                        seasonText = "all seasons";
                    }
                    else
                    {
                        body["seasons"] = toSend;
                        seasonText = (toSend.Count == 1 ? "season " : "seasons ") + string.Join(", ", toSend);
                    }
                }

                string destinationNote = null;
                if (dest != null)
                {
                    var root = (mediaType == "movie" ? dest.MovieRootFolder : dest.TvRootFolder)?.Trim();
                    var tags = ParseTagIds(mediaType == "movie" ? dest.MovieTagIds : dest.TvTagIds);
                    // Blank folder and tags = leave Seerr's own defaults for that server untouched.
                    if (!string.IsNullOrEmpty(root)) body["rootFolder"] = root;
                    if (tags.Count > 0) body["tags"] = tags;
                    if (!string.IsNullOrEmpty(root) || tags.Count > 0)
                        destinationNote = " to \"" + (dest.Label ?? destKey) + "\" (" +
                                          (string.IsNullOrEmpty(root) ? "default folder" : root) +
                                          (tags.Count > 0 ? ", tag ids " + string.Join(",", tags) : "") +
                                          "). Seerr override rules can still change this.";
                }

                SeerrClient.RequestResponse resp;
                try
                {
                    resp = await SeerrClient.PostRequestAsync(body, link.SeerrUserId, ct).ConfigureAwait(false);
                }
                catch (SeerrException ex)
                {
                    return Fail(ex.HttpStatus == 0 ? "seerr_unreachable" : "seerr_error", ex.Message);
                }

                Logger?.Info("Discover: Emby user '{0}' requested {1} {2} ({3}) as Seerr user {4} [{5}] -> HTTP {6}",
                    user.Name, mediaType, tmdbId, seasonText ?? "movie", link.SeerrUserId, link.Source, resp.Status);

                var what = details.Title + (seasonText != null ? " (" + seasonText + ")" : "");
                switch (resp.Status)
                {
                    case 200:
                    case 201:
                    {
                        Recent[dedupeKey] = DateTime.UtcNow;
                        SeerrClient.ClearCache();
                        // Let Emby re-crawl the channels so the title shows its Requested badge.
                        ChannelRefresh.Request(TimeSpan.FromSeconds(20));
                        var reqStatus = Json.Int(resp.Json, "status");
                        var outcome = new RequestOutcomeDto
                        {
                            Ok = true,
                            SeerrRequestId = Json.Int(resp.Json, "id"),
                            SeerrRequestStatus = reqStatus
                        };
                        if (reqStatus == (int)SeerrRequestStatus.Approved)
                        {
                            outcome.Code = "approved";
                            outcome.Message = "Requested " + what + ". Seerr approved it and is sending it to " +
                                              (mediaType == "movie" ? "Radarr" : "Sonarr") + ".";
                        }
                        else if (reqStatus == (int)SeerrRequestStatus.Pending)
                        {
                            outcome.Code = "submitted";
                            outcome.Message = "Requested " + what + ". It is waiting for approval in Seerr.";
                        }
                        else if (reqStatus == (int)SeerrRequestStatus.Declined || reqStatus == (int)SeerrRequestStatus.Failed)
                        {
                            outcome.Ok = false;
                            outcome.Code = "seerr_error";
                            outcome.Message = "Seerr created the request for " + what + " but marked it " +
                                              (reqStatus == (int)SeerrRequestStatus.Declined ? "declined" : "failed") + ".";
                        }
                        else
                        {
                            outcome.Code = "submitted";
                            outcome.Message = "Requested " + what + ".";
                        }
                        if (outcome.Ok && destinationNote != null) outcome.Message += " Sent" + destinationNote;
                        return outcome;
                    }
                    case 202:
                        return Fail("nothing_to_request", resp.Message ?? "Seerr found no seasons left to request.");
                    case 409:
                        return Fail("already_requested", resp.Message ?? what + " has already been requested.");
                    case 403:
                        return Fail("forbidden", "Seerr refused the request: " + (resp.Message ?? "permission denied or quota exceeded."));
                    case 401:
                        SeerrClient.ClearUserCache();
                        return Fail("seerr_error", "Seerr did not accept the request (HTTP 401). Check the API key and that the linked Seerr user still exists.");
                    case 400:
                        return Fail("invalid", "Seerr rejected the request as invalid" + (resp.Message != null ? ": " + resp.Message : "."));
                    default:
                        return Fail("seerr_error", "Seerr returned HTTP " + resp.Status + (resp.Message != null ? ": " + resp.Message : "."));
                }
            }
            finally
            {
                InFlight.TryRemove(dedupeKey, out _);
            }
        }

        /// <summary>Chooses seasons for a favourite-triggered TV request according to the admin setting.</summary>
        public static async Task<string> SeasonsForFavoriteAsync(int tmdbId, CancellationToken ct)
        {
            var mode = (Plugin.Config.FavoriteTvSeasons ?? "all").Trim().ToLowerInvariant();
            if (mode != "first" && mode != "latest") return "all";
            var details = await SeerrClient.GetDetailsAsync("tv", tmdbId, ct, useCache: false).ConfigureAwait(false);
            var candidates = details.Seasons.Where(s => s.CanRequest && s.SeasonNumber > 0).Select(s => s.SeasonNumber).ToList();
            if (candidates.Count == 0) return "all"; // SubmitAsync reports "nothing to request"
            return (mode == "first" ? candidates.Min() : candidates.Max()).ToString(CultureInfo.InvariantCulture);
        }

        private static bool TakeRateLimitToken(string userKey)
        {
            var q = RateLimits.GetOrAdd(userKey, _ => new Queue<DateTime>());
            lock (q)
            {
                var now = DateTime.UtcNow;
                while (q.Count > 0 && now - q.Peek() > TimeSpan.FromMinutes(1)) q.Dequeue();
                if (q.Count >= MaxRequestsPerMinute) return false;
                q.Enqueue(now);
                return true;
            }
        }

        private static void PruneRecent()
        {
            var cutoff = DateTime.UtcNow - RecentWindow;
            foreach (var kv in Recent)
                if (kv.Value < cutoff) Recent.TryRemove(kv.Key, out _);
        }
    }
}

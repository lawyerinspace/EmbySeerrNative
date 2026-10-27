using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EmbySeerrNative
{
    /// <summary>A person shown on a title's page (cast or crew).</summary>
    public class TitlePerson
    {
        public int TmdbId { get; set; }
        public string Name { get; set; }
        /// <summary>Actor | Director | Writer | Composer</summary>
        public string Type { get; set; }
        /// <summary>Character for actors, job for crew ("Screenplay", "Creator" …).</summary>
        public string Role { get; set; }
        public string ProfilePath { get; set; }
    }

    /// <summary>Everything the detail page shows beyond the list data, from Seerr (details + ratings).</summary>
    public class TitleInfo
    {
        public string TrailerKey { get; set; }
        public List<int> SeasonsNewestFirst { get; set; } = new List<int>();
        public string ImdbId { get; set; }
        public double? ImdbScore { get; set; }
        public int? ImdbVotes { get; set; }
        public int? RtCritics { get; set; }
        public string RtCriticsRating { get; set; }   // Certified Fresh | Fresh | Rotten
        public int? RtAudience { get; set; }
        public List<TitlePerson> People { get; set; } = new List<TitlePerson>();
        public List<string> Genres { get; set; } = new List<string>();
        public List<string> Studios { get; set; } = new List<string>();
        public int? RuntimeMinutes { get; set; }
        public string OriginalTitle { get; set; }
        /// <summary>Age rating per country ("US" → "PG-13").</summary>
        public Dictionary<string, string> AgeRatings { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Title details for the channels: trailer, IMDb / Rotten Tomatoes scores, cast and crew, genres,
    /// studios, runtime and age ratings. One Seerr details call plus one Seerr ratings call per title
    /// (Seerr gets IMDb scores for movies and Rotten Tomatoes scores for movies and shows; it has no
    /// IMDb scores for shows). Shows without their own trailer fall back to their seasons' videos from
    /// TMDB (newest first; needs the TMDB key). Cached: found 7 days, unknown title 1 day; errors are
    /// not cached.
    /// </summary>
    internal static class TitleIndex
    {
        private sealed class Entry
        {
            public TitleInfo Info;
            public DateTime Expires;
        }

        private static readonly ConcurrentDictionary<string, Entry> Cache = new ConcurrentDictionary<string, Entry>();
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(4, 4);

        internal static void Clear() => Cache.Clear();

        /// <summary>Cached details without a network call, or null.</summary>
        public static TitleInfo Peek(string mediaType, int tmdbId)
            => Cache.TryGetValue(mediaType + ":" + tmdbId, out var e) && e.Expires > DateTime.UtcNow ? e.Info : null;

        public static async Task<TitleInfo> GetAsync(string mediaType, int tmdbId, CancellationToken ct)
        {
            var cacheKey = mediaType + ":" + tmdbId;
            if (Cache.TryGetValue(cacheKey, out var hit) && hit.Expires > DateTime.UtcNow) return hit.Info;

            await Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (Cache.TryGetValue(cacheKey, out hit) && hit.Expires > DateTime.UtcNow) return hit.Info;
                TitleInfo info;
                var found = true;
                try
                {
                    info = await SeerrClient.GetTitleInfoAsync(mediaType, tmdbId, ct).ConfigureAwait(false);
                }
                catch (SeerrException ex) when (ex.HttpStatus == 404)
                {
                    info = new TitleInfo(); // unknown to Seerr/TMDB
                    found = false;
                }

                if (found)
                {
                    try
                    {
                        await SeerrClient.AddRatingsAsync(mediaType, tmdbId, info, ct).ConfigureAwait(false);
                    }
                    catch (SeerrException ex)
                    {
                        // No Rotten Tomatoes/IMDb match is normal (Seerr answers 404 or 500 then).
                        SeerrClient.Logger?.Debug("Discover: no scores for {0} {1}: {2}", mediaType, tmdbId, ex.Message);
                    }
                    if (info.TrailerKey == null && mediaType == "tv" && info.SeasonsNewestFirst.Count > 0)
                    {
                        try
                        {
                            info.TrailerKey = await TmdbClient.GetSeasonTrailerKeyAsync(tmdbId, info.SeasonsNewestFirst, ct).ConfigureAwait(false);
                        }
                        catch (SeerrException ex)
                        {
                            SeerrClient.Logger?.Debug("Discover: season trailers for tv {0}: {1}", tmdbId, ex.Message);
                        }
                    }
                }
                Cache[cacheKey] = new Entry { Info = info, Expires = DateTime.UtcNow.Add(found ? TimeSpan.FromDays(7) : TimeSpan.FromDays(1)) };
                if (Cache.Count > 20000) Prune();
                return info;
            }
            finally
            {
                Gate.Release();
            }
        }

        /// <summary>Attaches details to each item. Lookup failures leave it without details and are not cached.</summary>
        public static async Task AnnotateAsync(IList<DiscoverItemDto> items, CancellationToken ct)
        {
            var failures = 0;
            var tasks = items.Select(async i =>
            {
                if (failures > 5) return; // Seerr is struggling: stop for this folder, try again next refresh
                try
                {
                    i.Info = await GetAsync(i.MediaType, i.TmdbId, ct).ConfigureAwait(false);
                    i.HasTrailer = i.Info.TrailerKey != null;
                }
                catch (SeerrException ex)
                {
                    if (Interlocked.Increment(ref failures) == 1)
                        SeerrClient.Logger?.Warn("Discover: title details lookup failed ({0}); scores, cast and trailer badges are incomplete until the next refresh.", ex.Message);
                }
            }).ToList();
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        /// <summary>"IMDb 7.8 · RT 92% · Audience 85%" (TMDB score when there is no IMDb score), or null.</summary>
        public static string ScoresText(TitleInfo info, double? tmdbScore)
        {
            var parts = new List<string>();
            if (info?.ImdbScore != null) parts.Add("IMDb " + info.ImdbScore.Value.ToString("0.0", CultureInfo.InvariantCulture));
            else if (tmdbScore.HasValue && tmdbScore.Value > 0) parts.Add("TMDB " + tmdbScore.Value.ToString("0.0", CultureInfo.InvariantCulture));
            if (info?.RtCritics != null) parts.Add("Rotten Tomatoes " + info.RtCritics.Value + "%" + (info.RtCriticsRating == "Certified Fresh" ? " (Certified Fresh)" : ""));
            if (info?.RtAudience != null) parts.Add("Audience " + info.RtAudience.Value + "%");
            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }

        private static void Prune()
        {
            var now = DateTime.UtcNow;
            foreach (var kv in Cache)
                if (kv.Value.Expires <= now) Cache.TryRemove(kv.Key, out _);
        }
    }

    /// <summary>The trailer part of <see cref="TitleIndex"/>.</summary>
    internal static class TrailerIndex
    {
        internal static void Clear() => TitleIndex.Clear();

        /// <summary>Cached answer without a network call: true/false, or null when not looked up yet.</summary>
        public static bool? Known(string mediaType, int tmdbId)
        {
            var info = TitleIndex.Peek(mediaType, tmdbId);
            return info == null ? (bool?)null : info.TrailerKey != null;
        }

        /// <summary>The YouTube key to play, or null when the title has no trailer.</summary>
        public static async Task<string> GetKeyAsync(string mediaType, int tmdbId, CancellationToken ct)
            => (await TitleIndex.GetAsync(mediaType, tmdbId, ct).ConfigureAwait(false)).TrailerKey;

        public static Task AnnotateAsync(IList<DiscoverItemDto> items, CancellationToken ct) => TitleIndex.AnnotateAsync(items, ct);
    }
}

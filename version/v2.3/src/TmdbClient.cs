using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace EmbySeerrNative
{
    public class TmdbListSpec
    {
        public string Id { get; set; }
        /// <summary>Optional display name from the settings line ("id | name").</summary>
        public string CustomName { get; set; }
        /// <summary>
        /// For API query links (e.g. /discover/movie?...): the path and query relative to /3, without
        /// api_key or page. Null for ordinary lists.
        /// </summary>
        public string Query { get; set; }
        /// <summary>movie | tv when the endpoint implies it (discover/movie...), null when results carry media_type.</summary>
        public string ImpliedType { get; set; }
    }

    public class TmdbList
    {
        public string Id { get; set; }
        public string Name { get; set; }
        /// <summary>Shows left out by the age-rating filter.</summary>
        public int RemovedByRating { get; set; }
        public List<DiscoverItemDto> Items { get; set; } = new List<DiscoverItemDto>();
    }

    /// <summary>
    /// Reads public TMDB lists (themoviedb.org/list/{id}) with the administrator's own TMDB
    /// credentials. Seerr has no endpoint for TMDB lists; Seerr is still used for availability
    /// and for every request. Accepts a v3 API key (sent as api_key) or a v4 API Read Access
    /// Token (sent as a Bearer token). The credential is never logged.
    /// </summary>
    internal static class TmdbClient
    {
        /// <summary>Overridable only for tests.</summary>
        internal static string ApiBase { get; set; } = "https://api.themoviedb.org/3";

        private static readonly HttpClient Http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        private static readonly ConcurrentDictionary<string, Tuple<TmdbList, DateTime>> Cache =
            new ConcurrentDictionary<string, Tuple<TmdbList, DateTime>>();

        private static readonly Regex ListUrl = new Regex(@"themoviedb\.org/(?:[a-z]{2}(?:-[A-Z]{2})?/)?list/(\d+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static ILogger Logger { get; set; }

        public static void ClearCache()
        {
            Cache.Clear();
            OptionsCache.Clear();
        }

        /// <summary>Forgets cached show ratings (only needed by tests).</summary>
        internal static void ClearRatingCache() => TvRatings.Clear();

        private static readonly Regex ApiListUrl = new Regex(@"api\.themoviedb\.org/[34]/list/(\d+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// Parses the settings text. Each line is one of:
        ///   a TMDB list URL (themoviedb.org/list/123-name) or a numeric list id;
        ///   a TMDB API query URL (https://api.themoviedb.org/3/discover/movie?...), e.g. copied from the API docs;
        /// optionally followed by " | Display name". Blank lines and lines starting with # are ignored.
        /// </summary>
        public static List<TmdbListSpec> ParseSpecs(string text)
        {
            var result = new List<TmdbListSpec>();
            if (string.IsNullOrWhiteSpace(text)) return result;
            foreach (var raw in text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;

                SplitName(line, out line, out var name);
                TmdbListSpec spec = null;

                var m = ListUrl.Match(line);
                var api = ApiListUrl.Match(line);
                if (api.Success) spec = new TmdbListSpec { Id = api.Groups[1].Value };
                else if (line.IndexOf("api.themoviedb.org/3/", StringComparison.OrdinalIgnoreCase) >= 0) spec = ParseQuery(line);
                else if (m.Success) spec = new TmdbListSpec { Id = m.Groups[1].Value };
                else
                {
                    var digits = Regex.Match(line, @"^(\d+)$");
                    if (digits.Success) spec = new TmdbListSpec { Id = digits.Groups[1].Value };
                }

                if (spec == null || spec.Id.Length > 16 || result.Any(r => r.Id == spec.Id)) continue;
                spec.CustomName = string.IsNullOrWhiteSpace(name) ? null : name.Substring(0, Math.Min(80, name.Length));
                result.Add(spec);
            }
            return result;
        }

        /// <summary>Parses one list source (URL, id or API query link); null if not recognised.</summary>
        public static TmdbListSpec ParseSource(string source)
        {
            if (string.IsNullOrWhiteSpace(source)) return null;
            var line = source.Trim().Replace("\r", " ").Replace("\n", " ");
            return ParseSpecs(line.Contains("://") || !line.Contains("|") ? line : line.Replace("|", " ")).FirstOrDefault();
        }

        /// <summary>
        /// The display name follows the last '|' that has a space next to it ("url | Name"). A bare '|'
        /// inside a URL is part of the query (TMDB uses it for OR, e.g. with_genres=28|12).
        /// </summary>
        private static void SplitName(string line, out string target, out string name)
        {
            target = line;
            name = null;
            var isUrl = line.IndexOf("://", StringComparison.Ordinal) >= 0;
            for (var i = line.Length - 1; i >= 0; i--)
            {
                if (line[i] != '|') continue;
                var spaced = (i > 0 && char.IsWhiteSpace(line[i - 1])) || (i + 1 < line.Length && char.IsWhiteSpace(line[i + 1]));
                if (spaced || !isUrl)
                {
                    target = line.Substring(0, i).Trim();
                    name = line.Substring(i + 1).Trim();
                }
                if (spaced) return;
            }
        }

        /// <summary>
        /// Validates and normalises an API query URL. Only api.themoviedb.org/3 endpoints that return a
        /// paged "results" list are accepted; api_key and page are removed (the plugin adds its own key
        /// and pages through results itself). The request always goes to api.themoviedb.org.
        /// </summary>
        internal static TmdbListSpec ParseQuery(string url)
        {
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;
            if (!string.Equals(uri.Host, "api.themoviedb.org", StringComparison.OrdinalIgnoreCase)) return null;
            var path = uri.AbsolutePath;
            if (!path.StartsWith("/3/", StringComparison.Ordinal)) return null;
            path = path.Substring(2).TrimEnd('/'); // "/discover/movie"
            if (!Regex.IsMatch(path, @"^(/[A-Za-z0-9_]+){1,4}$")) return null;

            var segs = path.Trim('/').Split('/');
            string implied;
            switch (segs[0].ToLowerInvariant())
            {
                case "discover":
                case "movie":
                case "tv":
                    implied = segs[0] == "discover" ? (segs.Length > 1 ? segs[1].ToLowerInvariant() : null) : segs[0].ToLowerInvariant();
                    break;
                case "trending":
                    implied = segs.Length > 1 && (segs[1] == "movie" || segs[1] == "tv") ? segs[1] : null;
                    break;
                case "search":
                    implied = segs.Length > 1 && (segs[1] == "movie" || segs[1] == "tv") ? segs[1] : null;
                    break;
                default:
                    return null; // people, companies, configuration ... do not return titles
            }
            if (implied != null && implied != "movie" && implied != "tv") return null;

            var kept = new List<string>();
            foreach (var part in uri.Query.TrimStart('?').Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                var key = Uri.UnescapeDataString(eq < 0 ? part : part.Substring(0, eq));
                if (key.Equals("api_key", StringComparison.OrdinalIgnoreCase) || key.Equals("page", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!Regex.IsMatch(key, @"^[A-Za-z0-9_.]{1,60}$")) continue;
                var value = eq < 0 ? "" : Uri.UnescapeDataString(part.Substring(eq + 1));
                kept.Add(Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(value));
            }
            kept.Sort(StringComparer.Ordinal); // same query in a different order = same folder
            var query = path + (kept.Count > 0 ? "?" + string.Join("&", kept) : "");

            using (var sha = System.Security.Cryptography.SHA1.Create())
            {
                var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(query));
                var id = "q" + BitConverter.ToString(hash, 0, 6).Replace("-", "").ToLowerInvariant();
                return new TmdbListSpec { Id = id, Query = query, ImpliedType = implied };
            }
        }

        /// <summary>Server clock (local time of the Emby container). Overridable for tests.</summary>
        internal static Func<DateTime> Clock { get; set; } = () => DateTime.Now;

        private static readonly Regex Placeholder = new Regex(@"%7B(.{1,60}?)%7D", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex DateToken = new Regex(
            @"^(today|now|start_of_week|start_of_month|start_of_year|end_of_week|end_of_month|end_of_year)(?:([+-])(\d{1,4})([dwmy]))?$",
            RegexOptions.CultureInvariant);
        private static readonly Regex YearToken = new Regex(@"^year(?:([+-])(\d{1,3}))?$", RegexOptions.CultureInvariant);

        /// <summary>
        /// Replaces date placeholders in a stored query with dates from the server clock, e.g.
        /// {today} {today-7d} {today-1m} {start_of_month} {start_of_year} {end_of_year-1y} {year} {year-1}.
        /// Units: d days, w weeks, m months, y years. Weeks start on Monday. Dates are yyyy-MM-dd.
        /// </summary>
        internal static string ExpandDates(string query)
        {
            if (query == null || query.IndexOf("%7B", StringComparison.OrdinalIgnoreCase) < 0) return query;
            var now = Clock().Date;
            return Placeholder.Replace(query, m =>
            {
                var token = Uri.UnescapeDataString(m.Groups[1].Value).Trim().ToLowerInvariant().Replace(" ", "");
                var y = YearToken.Match(token);
                if (y.Success)
                {
                    var year = now.Year;
                    if (y.Groups[1].Success) year += (y.Groups[1].Value == "-" ? -1 : 1) * int.Parse(y.Groups[2].Value, CultureInfo.InvariantCulture);
                    return year.ToString(CultureInfo.InvariantCulture);
                }
                var d = DateToken.Match(token);
                if (!d.Success)
                    throw new SeerrException("Unknown date placeholder {" + token + "}. Use e.g. {today}, {today-7d}, {start_of_month}, {start_of_year}, {year}.");
                DateTime date;
                var monday = now.AddDays(-(((int)now.DayOfWeek + 6) % 7));
                switch (d.Groups[1].Value)
                {
                    case "start_of_week": date = monday; break;
                    case "end_of_week": date = monday.AddDays(6); break;
                    case "start_of_month": date = new DateTime(now.Year, now.Month, 1); break;
                    case "end_of_month": date = new DateTime(now.Year, now.Month, 1).AddMonths(1).AddDays(-1); break;
                    case "start_of_year": date = new DateTime(now.Year, 1, 1); break;
                    case "end_of_year": date = new DateTime(now.Year, 12, 31); break;
                    default: date = now; break;
                }
                if (d.Groups[2].Success)
                {
                    var n = int.Parse(d.Groups[3].Value, CultureInfo.InvariantCulture) * (d.Groups[2].Value == "-" ? -1 : 1);
                    switch (d.Groups[4].Value)
                    {
                        case "d": date = date.AddDays(n); break;
                        case "w": date = date.AddDays(7 * n); break;
                        case "m": date = date.AddMonths(n); break;
                        default: date = date.AddYears(n); break;
                    }
                }
                return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            });
        }

        public static bool IsConfigured => !string.IsNullOrWhiteSpace(Plugin.Config.TmdbApiKey);

        public static Task<TmdbList> GetListAsync(TmdbListSpec spec, CancellationToken ct)
            => GetListAsync(spec, 0, ct);

        public static async Task<TmdbList> GetListAsync(TmdbListSpec spec, int maxItemsOverride, CancellationToken ct)
        {
            var cacheMinutes = Math.Max(30, Math.Min(720, Plugin.Config.CacheMinutes * 3));
            var query = ExpandDates(spec.Query); // dates from the server clock, resolved on every fetch
            var maxItems = Math.Max(20, Math.Min(1000, maxItemsOverride > 0 ? maxItemsOverride : Plugin.Config.MaxItemsPerList));
            var cacheKey = spec.Id + "|" + maxItems + "|" + query;
            if (Cache.TryGetValue(cacheKey, out var hit) && hit.Item2 > DateTime.UtcNow)
                return WithName(hit.Item1, spec);

            var key = (Plugin.Config.TmdbApiKey ?? "").Trim();
            if (key.Length == 0) throw new SeerrException("Add a TMDB API key in the Discover settings to use TMDB lists.");

            var rating = ExtractRatingFilter(ref query);
            var list = new TmdbList { Id = spec.Id };
            var seen = new HashSet<string>();
            var totalPages = 1;
            for (var page = 1; page <= totalPages && page <= 50 && list.Items.Count < maxItems; page++)
            {
                var p = page.ToString(CultureInfo.InvariantCulture);
                var path = query == null
                    ? "/list/" + spec.Id + "?page=" + p
                    : query + (query.IndexOf('?') >= 0 ? "&" : "?") + "page=" + p;
                var doc = await GetJsonAsync(path, key, ct).ConfigureAwait(false);
                if (page == 1) list.Name = Json.Str(doc, "name");
                totalPages = Math.Min(500, Json.Int(doc, "total_pages") ?? 1);
                var items = Json.Arr(doc, "items") ?? Json.Arr(doc, "results");
                if (items == null) break;
                var pageItems = new List<DiscoverItemDto>();
                foreach (var it in items)
                {
                    var dto = MapItem(it, spec.ImpliedType);
                    if (dto == null || !seen.Add(dto.MediaType + dto.TmdbId)) continue;
                    pageItems.Add(dto);
                }
                if (rating != null)
                {
                    var before = pageItems.Count;
                    pageItems = await rating.ApplyAsync(pageItems, key, ct).ConfigureAwait(false);
                    list.RemovedByRating += before - pageItems.Count;
                }
                foreach (var dto in pageItems)
                {
                    list.Items.Add(dto);
                    if (list.Items.Count >= maxItems) break;
                }
                if (items.Count == 0) break;
            }
            if (string.IsNullOrWhiteSpace(list.Name))
                list.Name = spec.Query != null ? "TMDB " + spec.Query.Split('?')[0].Trim('/').Replace('/', ' ') : "TMDB list " + spec.Id;
            foreach (var stale in Cache.Keys.Where(k => k.StartsWith(spec.Id + "|", StringComparison.Ordinal) && k != cacheKey).ToList())
                Cache.TryRemove(stale, out _);
            Cache[cacheKey] = Tuple.Create(list, DateTime.UtcNow.AddMinutes(cacheMinutes));
            return WithName(list, spec);
        }

        private static TmdbList WithName(TmdbList list, TmdbListSpec spec)
        {
            return new TmdbList
            {
                Id = list.Id,
                Name = spec.CustomName ?? list.Name,
                RemovedByRating = list.RemovedByRating,
                Items = list.Items.Select(Clone).ToList() // callers annotate items; keep the cache pristine
            };
        }

        private static DiscoverItemDto Clone(DiscoverItemDto i) => new DiscoverItemDto
        {
            MediaType = i.MediaType, TmdbId = i.TmdbId, Title = i.Title, Year = i.Year, ReleaseDate = i.ReleaseDate,
            Overview = i.Overview, PosterPath = i.PosterPath, BackdropPath = i.BackdropPath, VoteAverage = i.VoteAverage,
            Status = i.Status, StatusLabel = i.StatusLabel, InEmbyLibrary = i.InEmbyLibrary, LibraryMatch = i.LibraryMatch,
            CanRequest = i.CanRequest, HasTrailer = i.HasTrailer, Info = i.Info
        };

        private static DiscoverItemDto MapItem(object it, string impliedType = null)
        {
            var type = Json.Str(it, "media_type") ?? impliedType;
            if (type != "movie" && type != "tv") return null;
            var id = Json.Int(it, "id");
            if (!id.HasValue || id.Value <= 0) return null;
            var title = type == "movie" ? Json.Str(it, "title") ?? Json.Str(it, "name") : Json.Str(it, "name") ?? Json.Str(it, "title");
            if (string.IsNullOrWhiteSpace(title)) return null;
            var date = type == "movie" ? Json.Str(it, "release_date") : Json.Str(it, "first_air_date");
            return new DiscoverItemDto
            {
                MediaType = type,
                TmdbId = id.Value,
                Title = title.Trim(),
                ReleaseDate = string.IsNullOrWhiteSpace(date) ? null : date,
                Year = SeerrClient.ParseYear(date),
                Overview = Json.Str(it, "overview"),
                PosterPath = SeerrClient.SafeImagePath(Json.Str(it, "poster_path")),
                BackdropPath = SeerrClient.SafeImagePath(Json.Str(it, "backdrop_path")),
                VoteAverage = Json.Num(it, "vote_average"),
                Status = Availability.NotRequested,
                StatusLabel = Availability.Label(Availability.NotRequested, false),
                CanRequest = true
            };
        }

        /// <summary>Applies Seerr availability (from the /api/v1/media map) to list items.</summary>
        public static void ApplySeerrStatus(IEnumerable<DiscoverItemDto> items, Dictionary<string, int> statusMap)
        {
            if (statusMap == null) return;
            foreach (var i in items)
            {
                if (!statusMap.TryGetValue(i.MediaType + ":" + i.TmdbId.ToString(CultureInfo.InvariantCulture), out var st)) continue;
                i.Status = Availability.FromSeerr(st);
                i.StatusLabel = Availability.Label(i.Status, false);
                i.CanRequest = Availability.CanRequest(i.Status, i.MediaType);
            }
        }

        // ------------------------------------------------------------------ age ratings

        private static readonly string[] RatingParams = { "certification_country", "certification", "certification.gte", "certification.lte" };

        /// <summary>
        /// TMDB filters movies by age rating itself (certification_country + certification.gte/.lte),
        /// but /discover/tv has no such filter. For shows the same parameters are taken out of the
        /// query and applied here, using each show's TMDB content rating for that country.
        /// include_unrated=true keeps shows that have no rating in that country. Returns null when
        /// there is nothing to do here.
        /// </summary>
        internal static RatingFilter ExtractRatingFilter(ref string query)
        {
            if (string.IsNullOrEmpty(query)) return null;
            var q = query.IndexOf('?');
            if (q < 0) return null;
            var path = query.Substring(0, q);
            var isTv = path.StartsWith("/discover/tv", StringComparison.OrdinalIgnoreCase);
            var filter = new RatingFilter();
            var kept = new List<string>();
            foreach (var part in query.Substring(q + 1).Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                var k = Uri.UnescapeDataString(eq < 0 ? part : part.Substring(0, eq));
                var v = eq < 0 ? "" : Uri.UnescapeDataString(part.Substring(eq + 1)).Trim();
                if (k.Equals("include_unrated", StringComparison.OrdinalIgnoreCase))
                {
                    filter.IncludeUnrated = v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1";
                    continue; // never a TMDB parameter
                }
                if (isTv && RatingParams.Contains(k.ToLowerInvariant()))
                {
                    switch (k.ToLowerInvariant())
                    {
                        case "certification_country": filter.Country = v.ToUpperInvariant(); break;
                        case "certification": filter.Exact = v; break;
                        case "certification.gte": filter.Min = v; break;
                        default: filter.Max = v; break;
                    }
                    continue;
                }
                kept.Add(part);
            }
            query = path + (kept.Count > 0 ? "?" + string.Join("&", kept) : "");
            if (!isTv || (filter.Min == null && filter.Max == null && filter.Exact == null)) return null;
            if (string.IsNullOrEmpty(filter.Country) || filter.Country.Length != 2) filter.Country = "US";
            return filter;
        }

        internal sealed class RatingFilter
        {
            public string Country;
            public string Exact;
            public string Min;
            public string Max;
            public bool IncludeUnrated;

            public async Task<List<DiscoverItemDto>> ApplyAsync(List<DiscoverItemDto> items, string key, CancellationToken ct)
            {
                var order = await TvRatingOrderAsync(Country, ct).ConfigureAwait(false);
                int? min = null, max = null;
                if (Min != null)
                {
                    if (!order.TryGetValue(Min, out var o)) throw new SeerrException("\"" + Min + "\" is not a TV rating in " + Country + ".");
                    min = o;
                }
                if (Max != null)
                {
                    if (!order.TryGetValue(Max, out var o)) throw new SeerrException("\"" + Max + "\" is not a TV rating in " + Country + ".");
                    max = o;
                }
                var ratings = await Task.WhenAll(items.Select(i => TvRatingAsync(i.TmdbId, Country, key, ct))).ConfigureAwait(false);
                var result = new List<DiscoverItemDto>();
                for (var n = 0; n < items.Count; n++)
                {
                    var r = ratings[n];
                    if (string.IsNullOrEmpty(r) || !order.TryGetValue(r, out var rank))
                    {
                        if (IncludeUnrated) result.Add(items[n]);
                        continue;
                    }
                    if (Exact != null && !string.Equals(r, Exact, StringComparison.OrdinalIgnoreCase)) continue;
                    if (min.HasValue && rank < min.Value) continue;
                    if (max.HasValue && rank > max.Value) continue;
                    result.Add(items[n]);
                }
                return result;
            }
        }

        /// <summary>TV rating → order (lowest = youngest audience) for a country.</summary>
        private static async Task<Dictionary<string, int>> TvRatingOrderAsync(string country, CancellationToken ct)
        {
            var doc = await GetReferenceAsync("/certification/tv/list", ct).ConfigureAwait(false);
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in Json.Arr(Json.Obj(doc, "certifications"), country) ?? new List<object>())
            {
                var code = Json.Str(c, "certification");
                var o = Json.Int(c, "order");
                if (!string.IsNullOrWhiteSpace(code) && o.HasValue) map[code.Trim()] = o.Value;
            }
            if (map.Count == 0) throw new SeerrException("TMDB has no TV age ratings for " + country + ".");
            return map;
        }

        private static readonly ConcurrentDictionary<int, Tuple<Dictionary<string, string>, DateTime>> TvRatings =
            new ConcurrentDictionary<int, Tuple<Dictionary<string, string>, DateTime>>();
        private static readonly SemaphoreSlim DetailGate = new SemaphoreSlim(6, 6);

        /// <summary>A show's content rating in a country ("TV-14"), or null. Cached for a week.</summary>
        private static async Task<string> TvRatingAsync(int tvId, string country, string key, CancellationToken ct)
        {
            if (!TvRatings.TryGetValue(tvId, out var hit) || hit.Item2 <= DateTime.UtcNow)
            {
                await DetailGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var doc = await GetJsonAsync("/tv/" + tvId.ToString(CultureInfo.InvariantCulture) + "/content_ratings?", key, ct).ConfigureAwait(false);
                    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var r in Json.Arr(doc, "results") ?? new List<object>())
                    {
                        var c = Json.Str(r, "iso_3166_1");
                        var v = Json.Str(r, "rating");
                        if (!string.IsNullOrWhiteSpace(c) && !string.IsNullOrWhiteSpace(v)) map[c] = v.Trim();
                    }
                    hit = Tuple.Create(map, DateTime.UtcNow.AddDays(7));
                    TvRatings[tvId] = hit;
                }
                catch (SeerrException ex) when (ex.HttpStatus == 404)
                {
                    hit = Tuple.Create(new Dictionary<string, string>(), DateTime.UtcNow.AddDays(1));
                    TvRatings[tvId] = hit;
                }
                finally
                {
                    DetailGate.Release();
                }
            }
            return hit.Item1.TryGetValue(country, out var rating) ? rating : null;
        }

        // ------------------------------------------------------------------ season trailers

        /// <summary>First YouTube trailer found among a show's seasons (newest season first), or null.</summary>
        public static async Task<string> GetSeasonTrailerKeyAsync(int tvId, IList<int> seasonsNewestFirst, CancellationToken ct)
        {
            var key = (Plugin.Config.TmdbApiKey ?? "").Trim();
            if (key.Length == 0 || seasonsNewestFirst == null) return null;
            foreach (var n in seasonsNewestFirst.Take(12))
            {
                object doc;
                await DetailGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    doc = await GetJsonAsync("/tv/" + tvId.ToString(CultureInfo.InvariantCulture) + "/season/" + n.ToString(CultureInfo.InvariantCulture) +
                                             "/videos?include_video_language=en,null", key, ct).ConfigureAwait(false);
                }
                catch (SeerrException ex) when (ex.HttpStatus == 404)
                {
                    continue;
                }
                finally
                {
                    DetailGate.Release();
                }
                var found = SeerrClient.BestYouTubeKey(Json.Arr(doc, "results"), "site", "key", "type", "size");
                if (found != null) return found;
            }
            return null;
        }

        // ------------------------------------------------------------------ connection test

        /// <summary>Light check of a TMDB key or token (GET /authentication), using the value typed on the page.</summary>
        public static async Task<ConnectionTestDto> TestAsync(string key, CancellationToken ct)
        {
            key = (key ?? "").Trim();
            if (key.Length == 0) return new ConnectionTestDto { Message = "Enter a TMDB API key or API Read Access Token." };
            try
            {
                var doc = await GetJsonAsync("/authentication?", key, ct).ConfigureAwait(false);
                if (Json.Get(doc, "success") is bool ok && ok)
                    return new ConnectionTestDto
                    {
                        Ok = true,
                        Message = "Connected to TMDB. The " + (key.StartsWith("eyJ", StringComparison.Ordinal) ? "API Read Access Token" : "API key") + " works."
                    };
                return new ConnectionTestDto { Message = "TMDB answered, but did not accept the key." };
            }
            catch (SeerrException ex)
            {
                return new ConnectionTestDto { Message = ex.Message };
            }
        }

        private static readonly ConcurrentDictionary<string, Tuple<object, DateTime>> OptionsCache =
            new ConcurrentDictionary<string, Tuple<object, DateTime>>();

        /// <summary>Cached GET of TMDB reference data (genres, providers, regions, languages) for the settings page.</summary>
        public static async Task<object> GetReferenceAsync(string pathAndQuery, CancellationToken ct)
        {
            if (OptionsCache.TryGetValue(pathAndQuery, out var hit) && hit.Item2 > DateTime.UtcNow) return hit.Item1;
            var key = (Plugin.Config.TmdbApiKey ?? "").Trim();
            if (key.Length == 0) throw new SeerrException("Add a TMDB API key on the Connection tab first.");
            var withQ = pathAndQuery.IndexOf('?') >= 0 ? pathAndQuery : pathAndQuery + "?";
            var doc = await GetJsonAsync(withQ, key, ct).ConfigureAwait(false);
            OptionsCache[pathAndQuery] = Tuple.Create(doc, DateTime.UtcNow.AddHours(12));
            return doc;
        }

        private static async Task<object> GetJsonAsync(string pathAndQuery, string key, CancellationToken ct)
        {
            var isBearer = key.StartsWith("eyJ", StringComparison.Ordinal);
            var url = ApiBase + pathAndQuery + (isBearer ? "" : "&api_key=" + Uri.EscapeDataString(key));
            var timeout = TimeSpan.FromSeconds(Math.Max(5, Math.Min(120, Plugin.Config.TimeoutSeconds)));
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                cts.CancelAfter(timeout);
                req.Headers.TryAddWithoutValidation("Accept", "application/json");
                if (isBearer) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
                try
                {
                    using (var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false))
                    {
                        var body = resp.Content == null ? "" : await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var status = (int)resp.StatusCode;
                        if (status == 401)
                            throw new SeerrException("TMDB rejected the API key (HTTP 401). Use the v3 API key or the API Read Access Token from themoviedb.org → Settings → API.", status);
                        if (status == 404)
                            throw new SeerrException(pathAndQuery.StartsWith("/list/", StringComparison.Ordinal)
                                ? "TMDB list " + StripToId(pathAndQuery) + " was not found or is private."
                                : "TMDB does not recognise the query " + pathAndQuery.Split('?')[0] + ".", status);
                        if (status == 422 || status == 400)
                            throw new SeerrException("TMDB rejected the query (HTTP " + status + "); check its parameters.", status);
                        if (status == 429)
                            throw new SeerrException("TMDB rate limit reached; try again shortly.", status);
                        if (status < 200 || status >= 300)
                            throw new SeerrException("TMDB returned HTTP " + status + ".", status);
                        try
                        {
                            return Json.Parse(body);
                        }
                        catch (FormatException)
                        {
                            throw new SeerrException("TMDB returned a response that is not JSON.", status);
                        }
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new SeerrException("TMDB did not respond within " + (int)timeout.TotalSeconds + " seconds.");
                }
                catch (HttpRequestException ex)
                {
                    var inner = ex;
                    Exception e = ex;
                    while (e.InnerException != null) e = e.InnerException;
                    throw new SeerrException("Emby could not reach api.themoviedb.org (" + e.Message + ").", 0, inner);
                }
            }
        }

        private static string StripToId(string pathAndQuery)
        {
            var m = Regex.Match(pathAndQuery, @"/list/(\d+)");
            return m.Success ? m.Groups[1].Value : "";
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace EmbySeerrNative
{
    public class SeerrUser
    {
        public int Id { get; set; }
        public string DisplayName { get; set; }
        public string JellyfinUserId { get; set; }   // Seerr stores Emby user IDs here, normalised
        public int UserType { get; set; }
    }

    /// <summary>
    /// Server-side Seerr API client. The API key never leaves this class: it is sent only to the
    /// configured Seerr URL, redirects are not followed, and it is never logged or returned.
    /// </summary>
    internal static class SeerrClient
    {
        // AllowAutoRedirect=false: an unexpected redirect must not carry the API key elsewhere.
        private static readonly HttpClient Http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        })
        {
            Timeout = Timeout.InfiniteTimeSpan // per-request timeouts via CancellationTokenSource
        };

        private static readonly ConcurrentDictionary<string, CacheEntry> Cache =
            new ConcurrentDictionary<string, CacheEntry>(StringComparer.Ordinal);

        private static readonly SemaphoreSlim UserListLock = new SemaphoreSlim(1, 1);
        private static List<SeerrUser> _users;
        private static DateTime _usersExpire = DateTime.MinValue;

        public static ILogger Logger { get; set; }

        private sealed class CacheEntry
        {
            public object Value;
            public DateTime Expires;
        }

        // ------------------------------------------------------------------ configuration

        public static bool IsConfigured(out string problem)
        {
            var cfg = Plugin.Config;
            if (string.IsNullOrWhiteSpace(cfg.ApiKey))
            {
                problem = "The Seerr API key has not been configured (Emby Dashboard → Plugins → Seerr Discover).";
                return false;
            }
            if (!TryNormaliseBaseUrl(cfg.SeerrUrl, out _, out problem)) return false;
            problem = null;
            return true;
        }

        public static bool TryNormaliseBaseUrl(string raw, out string baseUrl, out string problem)
        {
            baseUrl = null;
            if (string.IsNullOrWhiteSpace(raw))
            {
                problem = "Seerr URL is empty.";
                return false;
            }
            if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                problem = "Seerr URL must be an absolute http:// or https:// URL, e.g. http://seerr:5055";
                return false;
            }
            if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            {
                problem = "Seerr URL must not contain credentials, a query string or a fragment.";
                return false;
            }
            baseUrl = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
            problem = null;
            return true;
        }

        public static void ClearCache()
        {
            Cache.Clear();
            _mediaStatusExpire = DateTime.MinValue;
        }

        public static void ClearUserCache()
        {
            _usersExpire = DateTime.MinValue;
        }

        // ------------------------------------------------------------------ HTTP core

        private struct RawResponse
        {
            public int Status;
            public string Body;
        }

        private static async Task<RawResponse> SendAsync(HttpMethod method, string pathAndQuery, string jsonBody,
            int? actAsSeerrUserId, CancellationToken cancellationToken)
        {
            var cfg = Plugin.Config;
            if (!IsConfigured(out var problem)) throw new SeerrException(problem);
            TryNormaliseBaseUrl(cfg.SeerrUrl, out var baseUrl, out _);
            return await SendCoreAsync(baseUrl, cfg.ApiKey.Trim(), method, pathAndQuery, jsonBody, actAsSeerrUserId, cancellationToken)
                .ConfigureAwait(false);
        }

        private static async Task<RawResponse> SendCoreAsync(string baseUrl, string apiKey, HttpMethod method, string pathAndQuery,
            string jsonBody, int? actAsSeerrUserId, CancellationToken cancellationToken)
        {
            var cfg = Plugin.Config;
            var timeout = TimeSpan.FromSeconds(Math.Max(3, Math.Min(120, cfg.TimeoutSeconds)));
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            using (var request = new HttpRequestMessage(method, baseUrl + pathAndQuery))
            {
                cts.CancelAfter(timeout);
                if (!string.IsNullOrEmpty(apiKey)) request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                if (actAsSeerrUserId.HasValue)
                {
                    // Seerr acts on this user's behalf: their permissions, quotas and auto-approval apply.
                    request.Headers.TryAddWithoutValidation("X-API-User",
                        actAsSeerrUserId.Value.ToString(CultureInfo.InvariantCulture));
                }
                if (jsonBody != null)
                    request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                try
                {
                    using (var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token)
                               .ConfigureAwait(false))
                    {
                        var body = response.Content == null
                            ? ""
                            : await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var status = (int)response.StatusCode;
                        if (status >= 300 && status < 400)
                            throw new SeerrException(
                                "Seerr answered with a redirect (HTTP " + status + "). Check the Seerr URL — use the internal address, e.g. http://seerr:5055, without a trailing path.",
                                status);
                        Logger?.Debug("Discover: {0} {1} -> {2}", method.Method, StripQuery(pathAndQuery), status);
                        return new RawResponse { Status = status, Body = body };
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new SeerrException("Seerr did not respond within " + (int)timeout.TotalSeconds + " seconds.");
                }
                catch (HttpRequestException ex)
                {
                    throw new SeerrException(
                        "Emby could not reach Seerr at " + baseUrl + " (" + Innermost(ex).Message +
                        "). Check that both containers share a Docker network and the URL is correct.", 0, ex);
                }
            }
        }

        private static string StripQuery(string pathAndQuery)
        {
            var i = pathAndQuery.IndexOf('?');
            return i < 0 ? pathAndQuery : pathAndQuery.Substring(0, i);
        }

        private static Exception Innermost(Exception ex)
        {
            while (ex.InnerException != null) ex = ex.InnerException;
            return ex;
        }

        /// <summary>Extracts Seerr's {"message": "..."} error text, trimmed for display.</summary>
        public static string SeerrMessage(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return null;
            try
            {
                var msg = Json.Str(Json.Parse(body), "message") ?? Json.Str(Json.Parse(body), "error");
                if (string.IsNullOrWhiteSpace(msg)) return null;
                return msg.Length > 200 ? msg.Substring(0, 200) + "…" : msg;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        private static async Task<object> GetJsonAsync(string pathAndQuery, CancellationToken ct, bool useCache = true)
        {
            var cacheMinutes = Math.Max(0, Math.Min(240, Plugin.Config.CacheMinutes));
            if (useCache && cacheMinutes > 0 && Cache.TryGetValue(pathAndQuery, out var hit) && hit.Expires > DateTime.UtcNow)
                return hit.Value;

            var raw = await SendAsync(HttpMethod.Get, pathAndQuery, null, null, ct).ConfigureAwait(false);
            if (raw.Status == 401 || raw.Status == 403)
                throw new SeerrException("Seerr rejected the API key (HTTP " + raw.Status + "). Copy the key from Seerr → Settings → General.", raw.Status);
            if (raw.Status == 404)
                throw new SeerrException("Seerr returned 404 for " + StripQuery(pathAndQuery) + ".", 404);
            if (raw.Status < 200 || raw.Status >= 300)
                throw new SeerrException("Seerr returned HTTP " + raw.Status + (SeerrMessage(raw.Body) is string m ? ": " + m : "."), raw.Status);

            object doc;
            try
            {
                doc = Json.Parse(raw.Body);
            }
            catch (FormatException)
            {
                throw new SeerrException("Seerr returned a response that is not JSON. Is the Seerr URL pointing at Seerr?", raw.Status);
            }

            if (useCache && cacheMinutes > 0)
            {
                Cache[pathAndQuery] = new CacheEntry { Value = doc, Expires = DateTime.UtcNow.AddMinutes(cacheMinutes) };
                if (Cache.Count > 500) PruneCache();
            }
            return doc;
        }

        private static void PruneCache()
        {
            var now = DateTime.UtcNow;
            foreach (var kv in Cache)
                if (kv.Value.Expires <= now) Cache.TryRemove(kv.Key, out _);
            if (Cache.Count > 500) Cache.Clear();
        }

        // ------------------------------------------------------------------ discovery

        /// <summary>
        /// Type-specific discovery page for the Movies/Shows channels.
        /// kind: trending | popular | upcoming. Results of the other media type are dropped
        /// (older Seerr versions ignore trending's mediaType parameter and return a mixed list).
        /// </summary>
        public static async Task<DiscoverPageDto> GetTypedDiscoverAsync(string mediaType, string kind, int page, CancellationToken ct)
        {
            if (mediaType != "movie" && mediaType != "tv") throw new ArgumentException("MediaType must be movie or tv.");
            page = Math.Max(1, Math.Min(500, page));
            var p = page.ToString(CultureInfo.InvariantCulture);
            string path;
            switch (kind)
            {
                case "trending": path = "/api/v1/discover/trending?mediaType=" + mediaType + "&page=" + p; break;
                case "popular": path = (mediaType == "movie" ? "/api/v1/discover/movies" : "/api/v1/discover/tv") + "?page=" + p; break;
                case "upcoming": path = (mediaType == "movie" ? "/api/v1/discover/movies/upcoming" : "/api/v1/discover/tv/upcoming") + "?page=" + p; break;
                default: throw new ArgumentException("Unknown category.");
            }
            var doc = await GetJsonAsync(path, ct).ConfigureAwait(false);
            var dto = MapResultsPage(doc, kind == "trending" ? null : mediaType);
            dto.Items = dto.Items.Where(i => i.MediaType == mediaType).ToList();
            return dto;
        }

        private static Dictionary<string, int> _mediaStatus;
        private static DateTime _mediaStatusExpire = DateTime.MinValue;
        private static readonly SemaphoreSlim MediaStatusLock = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Every media record Seerr knows about ("movie:603" → MediaStatus), from GET /api/v1/media.
        /// Used to give TMDB-list titles their Seerr availability without one call per title.
        /// </summary>
        public static async Task<Dictionary<string, int>> GetMediaStatusMapAsync(CancellationToken ct)
        {
            if (_mediaStatus != null && _mediaStatusExpire > DateTime.UtcNow) return _mediaStatus;
            await MediaStatusLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_mediaStatus != null && _mediaStatusExpire > DateTime.UtcNow) return _mediaStatus;
                var map = new Dictionary<string, int>(StringComparer.Ordinal);
                const int take = 500;
                for (var skip = 0; skip < 50000; skip += take)
                {
                    var doc = await GetJsonAsync("/api/v1/media?take=" + take + "&skip=" + skip, ct, useCache: false).ConfigureAwait(false);
                    var results = Json.Arr(doc, "results");
                    if (results == null) throw new SeerrException("Seerr's media list response was not recognised.");
                    foreach (var m in results)
                    {
                        var id = Json.Int(m, "tmdbId");
                        var type = Json.Str(m, "mediaType");
                        var status = Json.Int(m, "status");
                        if (id.HasValue && status.HasValue && (type == "movie" || type == "tv"))
                            map[type + ":" + id.Value.ToString(CultureInfo.InvariantCulture)] = status.Value;
                    }
                    if (results.Count < take) break;
                }
                _mediaStatus = map;
                _mediaStatusExpire = DateTime.UtcNow.AddMinutes(Math.Max(1, Math.Min(240, Plugin.Config.CacheMinutes)));
                return map;
            }
            finally
            {
                MediaStatusLock.Release();
            }
        }

        private static DiscoverPageDto MapResultsPage(object doc, string impliedType)
        {
            var dto = new DiscoverPageDto
            {
                Page = Json.Int(doc, "page") ?? 1,
                TotalPages = Json.Int(doc, "totalPages") ?? 1,
                TotalResults = Json.Int(doc, "totalResults") ?? 0
            };
            var results = Json.Arr(doc, "results");
            if (results == null)
                throw new SeerrException("Seerr's response did not contain a results list.");
            foreach (var r in results)
            {
                var item = MapResult(r, impliedType);
                if (item != null) dto.Items.Add(item);
            }
            return dto;
        }

        /// <summary>Maps one Seerr MovieResult/TvResult. Person and collection results return null.</summary>
        public static DiscoverItemDto MapResult(object r, string impliedType)
        {
            var type = Json.Str(r, "mediaType") ?? impliedType;
            if (type != "movie" && type != "tv") return null;
            var id = Json.Int(r, "id");
            if (!id.HasValue || id.Value <= 0) return null;
            var title = type == "movie" ? Json.Str(r, "title") ?? Json.Str(r, "name") : Json.Str(r, "name") ?? Json.Str(r, "title");
            if (string.IsNullOrWhiteSpace(title)) return null;
            var date = type == "movie" ? Json.Str(r, "releaseDate") : Json.Str(r, "firstAirDate");
            var state = Availability.FromSeerr(Json.Int(Json.Obj(r, "mediaInfo"), "status"));
            return new DiscoverItemDto
            {
                MediaType = type,
                TmdbId = id.Value,
                Title = title.Trim(),
                ReleaseDate = string.IsNullOrWhiteSpace(date) ? null : date,
                Year = ParseYear(date),
                Overview = Json.Str(r, "overview"),
                PosterPath = SafeImagePath(Json.Str(r, "posterPath")),
                BackdropPath = SafeImagePath(Json.Str(r, "backdropPath")),
                VoteAverage = Json.Num(r, "voteAverage"),
                Status = state,
                StatusLabel = Availability.Label(state, false),
                CanRequest = Availability.CanRequest(state, type)
            };
        }

        public static int? ParseYear(string date)
        {
            if (string.IsNullOrEmpty(date) || date.Length < 4) return null;
            return int.TryParse(date.Substring(0, 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) && y > 1800 && y < 2300
                ? y
                : (int?)null;
        }

        public static DateTimeOffset? ParseDate(string date)
        {
            if (string.IsNullOrEmpty(date)) return null;
            return DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
                ? d
                : (DateTimeOffset?)null;
        }

        /// <summary>TMDB image paths look like "/abc123.jpg". Anything else is discarded.</summary>
        public static string SafeImagePath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length > 200 || path[0] != '/') return null;
            foreach (var c in path)
            {
                if (!(char.IsLetterOrDigit(c) || c == '/' || c == '.' || c == '_' || c == '-')) return null;
            }
            return path.Contains("..") ? null : path;
        }

        public static string TmdbImageUrl(string path, string size)
            => path == null ? null : "https://image.tmdb.org/t/p/" + size + path;

        // ------------------------------------------------------------------ details

        public static async Task<TitleDetailsDto> GetDetailsAsync(string mediaType, int tmdbId, CancellationToken ct, bool useCache = true)
        {
            if (mediaType != "movie" && mediaType != "tv") throw new ArgumentException("MediaType must be movie or tv.");
            if (tmdbId <= 0) throw new ArgumentException("A valid TMDB id is required.");
            var doc = await GetJsonAsync("/api/v1/" + mediaType + "/" + tmdbId.ToString(CultureInfo.InvariantCulture), ct, useCache)
                .ConfigureAwait(false);

            var basic = MapResult(doc, mediaType) ?? throw new SeerrException("Seerr returned incomplete details for this title.");
            var dto = new TitleDetailsDto
            {
                MediaType = basic.MediaType,
                TmdbId = basic.TmdbId,
                Title = basic.Title,
                Year = basic.Year,
                ReleaseDate = basic.ReleaseDate,
                Overview = basic.Overview,
                PosterPath = basic.PosterPath,
                BackdropPath = basic.BackdropPath,
                VoteAverage = basic.VoteAverage,
                Status = basic.Status,
                StatusLabel = basic.StatusLabel,
                CanRequest = basic.CanRequest,
                Tagline = Json.Str(doc, "tagline"),
                Runtime = Json.Int(doc, "runtime")
            };
            var genres = Json.Arr(doc, "genres");
            if (genres != null)
                dto.Genres = genres.Select(g => Json.Str(g, "name")).Where(n => !string.IsNullOrWhiteSpace(n)).Take(6).ToList();

            if (mediaType == "tv") MapSeasons(doc, dto);
            return dto;
        }

        private static void MapSeasons(object doc, TitleDetailsDto dto)
        {
            var mediaInfo = Json.Obj(doc, "mediaInfo");

            // Season availability from the media record.
            var seasonStatus = new Dictionary<int, int>();
            foreach (var s in Json.Arr(mediaInfo, "seasons") ?? new List<object>())
            {
                var n = Json.Int(s, "seasonNumber");
                var st = Json.Int(s, "status");
                if (n.HasValue && st.HasValue) seasonStatus[n.Value] = st.Value;
            }

            // Seasons covered by active requests (pending approval or approved), like Seerr's own UI.
            var requested = new Dictionary<int, string>();
            foreach (var req in Json.Arr(mediaInfo, "requests") ?? new List<object>())
            {
                if (Json.Get(req, "is4k") is bool is4k && is4k) continue;
                var rs = Json.Int(req, "status");
                string state;
                if (rs == (int)SeerrRequestStatus.Pending) state = Availability.Pending;
                else if (rs == (int)SeerrRequestStatus.Approved) state = Availability.Processing;
                else continue;
                foreach (var s in Json.Arr(req, "seasons") ?? new List<object>())
                {
                    var n = Json.Int(s, "seasonNumber");
                    if (n.HasValue && !requested.ContainsKey(n.Value)) requested[n.Value] = state;
                }
            }

            foreach (var s in Json.Arr(doc, "seasons") ?? new List<object>())
            {
                var n = Json.Int(s, "seasonNumber");
                if (!n.HasValue || n.Value < 0) continue;
                string state;
                if (seasonStatus.TryGetValue(n.Value, out var st) &&
                    (st == (int)SeerrMediaStatus.Available || st == (int)SeerrMediaStatus.PartiallyAvailable ||
                     st == (int)SeerrMediaStatus.Processing || st == (int)SeerrMediaStatus.Pending ||
                     st == (int)SeerrMediaStatus.Blocklisted))
                    state = Availability.FromSeerr(st);
                else if (requested.TryGetValue(n.Value, out var rState))
                    state = rState;
                else
                    state = Availability.NotRequested;

                dto.Seasons.Add(new SeasonDto
                {
                    SeasonNumber = n.Value,
                    Name = Json.Str(s, "name") ?? (n.Value == 0 ? "Specials" : "Season " + n.Value),
                    EpisodeCount = Json.Int(s, "episodeCount"),
                    AirDate = Json.Str(s, "airDate"),
                    Status = state,
                    StatusLabel = Availability.Label(state, false),
                    // Partially available seasons can be re-requested in Seerr to fetch missing episodes;
                    // keep it simple and only offer seasons with nothing requested yet.
                    CanRequest = state == Availability.NotRequested
                });
            }
            dto.Seasons.Sort((a, b) => a.SeasonNumber.CompareTo(b.SeasonNumber));

            var regularRequestable = dto.Seasons.Any(x => x.CanRequest && x.SeasonNumber > 0);
            if (dto.Seasons.Count > 0) dto.CanRequest = regularRequestable || dto.Seasons.Any(x => x.CanRequest);
        }

        /// <summary>
        /// Best YouTube trailer key for a title from Seerr's details (relatedVideos), or null.
        /// Preference: Trailer, then Teaser, then any other clip; larger resolution first.
        /// </summary>
        /// <summary>
        /// Details for the channels from Seerr's /api/v1/movie|tv/{id}: trailer, seasons, cast and crew,
        /// genres, studios/networks, runtime, original title, IMDb id and age ratings. Not cached here:
        /// TitleIndex caches.
        /// </summary>
        public static async Task<TitleInfo> GetTitleInfoAsync(string mediaType, int tmdbId, CancellationToken ct)
        {
            if (mediaType != "movie" && mediaType != "tv") return new TitleInfo();
            var doc = await GetJsonAsync("/api/v1/" + mediaType + "/" + tmdbId.ToString(CultureInfo.InvariantCulture), ct, useCache: false)
                .ConfigureAwait(false);
            var info = new TitleInfo
            {
                TrailerKey = BestYouTubeKey(Json.Arr(doc, "relatedVideos"), "site", "key", "type", "size"),
                SeasonsNewestFirst = (Json.Arr(doc, "seasons") ?? new List<object>())
                    .Select(x => Json.Int(x, "seasonNumber") ?? -1).Where(n => n > 0).OrderByDescending(n => n).ToList(),
                ImdbId = CleanImdbId(Json.Str(Json.Obj(doc, "externalIds"), "imdbId") ?? Json.Str(doc, "imdbId")),
                OriginalTitle = mediaType == "movie" ? Json.Str(doc, "originalTitle") : Json.Str(doc, "originalName"),
                RuntimeMinutes = mediaType == "movie" ? Json.Int(doc, "runtime") : (Json.Arr(doc, "episodeRunTime") ?? new List<object>()).Select(Json.ToInt).FirstOrDefault(n => n > 0)
            };
            if (info.RuntimeMinutes <= 0) info.RuntimeMinutes = null;

            info.Genres = (Json.Arr(doc, "genres") ?? new List<object>()).Select(g => Json.Str(g, "name")).Where(n => !string.IsNullOrWhiteSpace(n)).Take(6).ToList();
            info.Studios = (Json.Arr(doc, mediaType == "tv" ? "networks" : "productionCompanies") ?? new List<object>())
                .Select(c => Json.Str(c, "name")).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().Take(3).ToList();

            // Cast (in billing order) and the key crew.
            var credits = Json.Obj(doc, "credits");
            foreach (var c in (Json.Arr(credits, "cast") ?? new List<object>()).OrderBy(c => Json.Int(c, "order") ?? 999).Take(15))
                AddPerson(info, c, "Actor", Json.Str(c, "character"));
            if (mediaType == "tv")
                foreach (var c in (Json.Arr(doc, "createdBy") ?? new List<object>()).Take(4))
                    AddPerson(info, c, "Writer", "Creator");
            foreach (var c in Json.Arr(credits, "crew") ?? new List<object>())
            {
                var job = Json.Str(c, "job") ?? "";
                if (job == "Director" && info.People.Count(p => p.Type == "Director") < 3) AddPerson(info, c, "Director", null);
                else if ((job == "Screenplay" || job == "Writer" || job == "Story" || job == "Novel" || job == "Author" || job == "Teleplay") &&
                         info.People.Count(p => p.Type == "Writer") < 5) AddPerson(info, c, "Writer", job);
                else if (job == "Original Music Composer" && info.People.Count(p => p.Type == "Composer") < 1) AddPerson(info, c, "Composer", null);
            }

            // Age ratings by country.
            if (mediaType == "movie")
            {
                foreach (var r in Json.Arr(Json.Obj(doc, "releases"), "results") ?? new List<object>())
                {
                    var country = Json.Str(r, "iso_3166_1");
                    var cert = (Json.Arr(r, "release_dates") ?? new List<object>())
                        .Select(d => (Json.Str(d, "certification") ?? "").Trim()).FirstOrDefault(x => x.Length > 0);
                    if (!string.IsNullOrEmpty(country) && cert != null && !info.AgeRatings.ContainsKey(country)) info.AgeRatings[country] = cert;
                }
            }
            else
            {
                foreach (var r in Json.Arr(Json.Obj(doc, "contentRatings"), "results") ?? new List<object>())
                {
                    var country = Json.Str(r, "iso_3166_1");
                    var rating = (Json.Str(r, "rating") ?? "").Trim();
                    if (!string.IsNullOrEmpty(country) && rating.Length > 0 && !info.AgeRatings.ContainsKey(country)) info.AgeRatings[country] = rating;
                }
            }
            return info;
        }

        private static void AddPerson(TitleInfo info, object node, string type, string role)
        {
            var id = Json.Int(node, "id") ?? 0;
            var name = (Json.Str(node, "name") ?? "").Trim();
            if (name.Length == 0 || info.People.Any(p => p.Type == type && (p.TmdbId == id && id > 0 || p.Name == name))) return;
            info.People.Add(new TitlePerson
            {
                TmdbId = id,
                Name = name,
                Type = type,
                Role = string.IsNullOrWhiteSpace(role) ? null : role.Trim(),
                ProfilePath = SafeImagePath(Json.Str(node, "profilePath"))
            });
        }

        private static string CleanImdbId(string id)
            => !string.IsNullOrEmpty(id) && System.Text.RegularExpressions.Regex.IsMatch(id, "^tt\\d{5,10}$") ? id : null;

        /// <summary>
        /// Scores from Seerr: movies /api/v1/movie/{id}/ratingscombined (Rotten Tomatoes + IMDb), shows
        /// /api/v1/tv/{id}/ratings (Rotten Tomatoes only — Seerr has no IMDb scores for shows).
        /// Seerr answers 404 (or 500) when it finds no match; the caller treats that as "no scores".
        /// </summary>
        public static async Task AddRatingsAsync(string mediaType, int tmdbId, TitleInfo info, CancellationToken ct)
        {
            var id = tmdbId.ToString(CultureInfo.InvariantCulture);
            if (mediaType == "movie")
            {
                var doc = await GetJsonAsync("/api/v1/movie/" + id + "/ratingscombined", ct, useCache: false).ConfigureAwait(false);
                ApplyRt(Json.Obj(doc, "rt"), info);
                var imdb = Json.Obj(doc, "imdb");
                var score = Json.Num(imdb, "criticsScore");
                if (score.HasValue && score.Value > 0 && score.Value <= 10) info.ImdbScore = Math.Round(score.Value, 1);
                info.ImdbVotes = Json.Int(imdb, "criticsScoreCount");
            }
            else if (mediaType == "tv")
            {
                ApplyRt(await GetJsonAsync("/api/v1/tv/" + id + "/ratings", ct, useCache: false).ConfigureAwait(false), info);
            }
        }

        private static void ApplyRt(object rt, TitleInfo info)
        {
            if (rt == null) return;
            var critics = Json.Int(rt, "criticsScore");
            if (critics.HasValue && critics.Value >= 0 && critics.Value <= 100)
            {
                info.RtCritics = critics;
                info.RtCriticsRating = Json.Str(rt, "criticsRating");
            }
            var audience = Json.Int(rt, "audienceScore");
            if (audience.HasValue && audience.Value > 0 && audience.Value <= 100) info.RtAudience = audience;
        }

        /// <summary>Picks a YouTube video: Trailer, then Teaser, then any other clip; larger resolution first.</summary>
        public static string BestYouTubeKey(List<object> videos, string siteField, string keyField, string typeField, string sizeField)
        {
            if (videos == null) return null;
            return videos
                .Where(v => string.Equals(Json.Str(v, siteField), "YouTube", StringComparison.OrdinalIgnoreCase))
                .Select(v => new { Key = Json.Str(v, keyField), Type = Json.Str(v, typeField) ?? "", Size = Json.Int(v, sizeField) ?? 0 })
                .Where(v => IsYouTubeKey(v.Key))
                .OrderBy(v => v.Type == "Trailer" ? 0 : v.Type == "Teaser" ? 1 : 2)
                .ThenByDescending(v => v.Size)
                .Select(v => v.Key)
                .FirstOrDefault();
        }

        public static bool IsYouTubeKey(string key)
            => !string.IsNullOrEmpty(key) && key.Length >= 6 && key.Length <= 20 &&
               key.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_');

        // ------------------------------------------------------------------ requests

        public struct RequestResponse
        {
            public int Status;
            public object Json;
            public string Message;
        }

        public static async Task<RequestResponse> PostRequestAsync(Dictionary<string, object> body, int? actAsSeerrUserId,
            CancellationToken ct)
        {
            var raw = await SendAsync(HttpMethod.Post, "/api/v1/request", Json.Serialize(body), actAsSeerrUserId, ct)
                .ConfigureAwait(false);
            object parsed = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(raw.Body)) parsed = Json.Parse(raw.Body);
            }
            catch (FormatException)
            {
                // leave parsed null; caller reports a generic error
            }
            return new RequestResponse { Status = raw.Status, Json = parsed, Message = SeerrMessage(raw.Body) };
        }

        // ------------------------------------------------------------------ users / admin

        public static async Task<List<SeerrUser>> GetUsersAsync(CancellationToken ct, bool forceRefresh = false)
        {
            if (!forceRefresh && _users != null && _usersExpire > DateTime.UtcNow) return _users;
            await UserListLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!forceRefresh && _users != null && _usersExpire > DateTime.UtcNow) return _users;
                var list = new List<SeerrUser>();
                const int take = 100;
                for (var skip = 0; skip < 5000; skip += take)
                {
                    var doc = await GetJsonAsync("/api/v1/user?take=" + take + "&skip=" + skip, ct, useCache: false)
                        .ConfigureAwait(false);
                    var results = Json.Arr(doc, "results");
                    if (results == null) throw new SeerrException("Seerr's user list response was not recognised.");
                    foreach (var u in results)
                    {
                        var id = Json.Int(u, "id");
                        if (!id.HasValue) continue;
                        list.Add(new SeerrUser
                        {
                            Id = id.Value,
                            DisplayName = Json.Str(u, "displayName") ?? Json.Str(u, "username") ??
                                          Json.Str(u, "jellyfinUsername") ?? ("Seerr user " + id.Value),
                            JellyfinUserId = NormaliseGuid(Json.Str(u, "jellyfinUserId")),
                            UserType = Json.Int(u, "userType") ?? 0
                        });
                    }
                    if (results.Count < take) break;
                }
                _users = list;
                _usersExpire = DateTime.UtcNow.AddMinutes(5);
                return list;
            }
            finally
            {
                UserListLock.Release();
            }
        }

        public static string NormaliseGuid(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var n = value.Replace("-", "").Trim().ToLowerInvariant();
            return n.Length == 32 && n.All(Uri.IsHexDigit) ? n : null;
        }

        /// <summary>
        /// Root folders and tags of Seerr's default (non-4K) Radarr or Sonarr server, from
        /// GET /api/v1/service/{radarr|sonarr} and /service/{type}/{id}. No Arr API keys needed.
        /// </summary>
        public static async Task<ArrServiceDto> GetServiceAsync(string arr, CancellationToken ct)
        {
            if (arr != "radarr" && arr != "sonarr") throw new ArgumentException("arr");
            var dto = new ArrServiceDto { App = arr == "radarr" ? "Radarr" : "Sonarr" };
            var servers = await GetJsonAsync("/api/v1/service/" + arr, ct, useCache: false).ConfigureAwait(false) as List<object>;
            if (servers == null || servers.Count == 0)
            {
                dto.Error = "Seerr has no " + dto.App + " server configured.";
                return dto;
            }
            var chosen = servers.FirstOrDefault(x => Json.Get(x, "isDefault") is bool d && d && !(Json.Get(x, "is4k") is bool k && k))
                         ?? servers.FirstOrDefault(x => !(Json.Get(x, "is4k") is bool k && k))
                         ?? servers[0];
            var id = Json.Int(chosen, "id") ?? 0;
            var detail = await GetJsonAsync("/api/v1/service/" + arr + "/" + id.ToString(CultureInfo.InvariantCulture), ct, useCache: false)
                .ConfigureAwait(false);
            var server = Json.Obj(detail, "server") ?? chosen as Dictionary<string, object>;
            dto.ServerId = id;
            dto.ServerName = Json.Str(server, "name");
            dto.DefaultRootFolder = Json.Str(server, "activeDirectory");
            dto.DefaultTagIds = (Json.Arr(server, "activeTags") ?? new List<object>())
                .Select(t => t is double dd ? (int)dd : -1).Where(t => t > 0).ToList();
            foreach (var f in Json.Arr(detail, "rootFolders") ?? new List<object>())
            {
                var path = Json.Str(f, "path");
                if (string.IsNullOrWhiteSpace(path)) continue;
                var free = Json.Num(f, "freeSpace");
                dto.RootFolders.Add(new ArrFolderDto { Path = path, FreeSpaceGb = free.HasValue ? Math.Round(free.Value / 1073741824d, 1) : (double?)null });
            }
            foreach (var t in Json.Arr(detail, "tags") ?? new List<object>())
            {
                var tid = Json.Int(t, "id");
                var label = Json.Str(t, "label");
                if (tid.HasValue && !string.IsNullOrWhiteSpace(label)) dto.Tags.Add(new ArrTagDto { Id = tid.Value, Label = label });
            }
            return dto;
        }

        public static async Task<string> GetVersionAsync(CancellationToken ct)
        {
            var doc = await GetJsonAsync("/api/v1/status", ct, useCache: false).ConfigureAwait(false);
            return Json.Str(doc, "version");
        }

        public static async Task<string> GetApiKeyOwnerAsync(CancellationToken ct)
        {
            var doc = await GetJsonAsync("/api/v1/auth/me", ct, useCache: false).ConfigureAwait(false);
            return Json.Str(doc, "displayName") ?? Json.Str(doc, "username") ?? Json.Str(doc, "email");
        }

        /// <summary>
        /// Light connection test with the values typed on the settings page (not yet saved):
        /// GET /api/v1/status (version, no key needed) and GET /api/v1/auth/me (checks the key).
        /// </summary>
        public static async Task<ConnectionTestDto> TestAsync(string url, string apiKey, CancellationToken ct)
        {
            if (!TryNormaliseBaseUrl(url, out var baseUrl, out var problem)) return new ConnectionTestDto { Message = problem };
            apiKey = (apiKey ?? "").Trim();
            if (apiKey.Length == 0) return new ConnectionTestDto { Message = "Enter the Seerr API key (Seerr → Settings → General)." };
            try
            {
                var status = await SendCoreAsync(baseUrl, null, HttpMethod.Get, "/api/v1/status", null, null, ct).ConfigureAwait(false);
                string version = null;
                if (status.Status >= 200 && status.Status < 300)
                {
                    try { version = Json.Str(Json.Parse(status.Body), "version"); }
                    catch (FormatException) { }
                }
                if (version == null)
                    return new ConnectionTestDto { Message = "Something answered at " + baseUrl + " (HTTP " + status.Status + "), but it does not look like Seerr. Check the address and port." };

                var me = await SendCoreAsync(baseUrl, apiKey, HttpMethod.Get, "/api/v1/auth/me", null, null, ct).ConfigureAwait(false);
                if (me.Status == 401 || me.Status == 403)
                    return new ConnectionTestDto { Message = "Reached Seerr " + version + ", but it rejected the API key (HTTP " + me.Status + "). Copy the key again from Seerr → Settings → General." };
                if (me.Status < 200 || me.Status >= 300)
                    return new ConnectionTestDto { Message = "Reached Seerr " + version + ", but checking the API key returned HTTP " + me.Status + "." };
                string owner = null;
                try
                {
                    var doc = Json.Parse(me.Body);
                    owner = Json.Str(doc, "displayName") ?? Json.Str(doc, "username") ?? Json.Str(doc, "email");
                }
                catch (FormatException) { }
                return new ConnectionTestDto
                {
                    Ok = true,
                    Message = "Connected to Seerr " + version + ". The API key works" + (owner != null ? " (it belongs to \"" + owner + "\")" : "") + "."
                };
            }
            catch (SeerrException ex)
            {
                return new ConnectionTestDto { Message = ex.Message };
            }
        }
    }
}

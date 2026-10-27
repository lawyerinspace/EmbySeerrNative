using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;

namespace EmbySeerrNative
{
    /// <summary>Identifiers stored on channel items.</summary>
    public static class DiscoverIds
    {
        /// <summary>Private provider id: "movie:603" / "tv:1396". Used to recognise our items.</summary>
        public const string ProviderKey = "SeerrDiscover";
        /// <summary>Private provider id read by the poster badge enhancer: "requested" | "partial" | "none".</summary>
        public const string BadgeKey = "SeerrDiscoverBadge";
        /// <summary>Private provider id read by the poster badge enhancer: "1" when the title has a trailer.</summary>
        public const string TrailerKey = "SeerrDiscoverTrailer";

        public static bool TryParseProviderValue(string value, out string mediaType, out int tmdbId)
        {
            mediaType = null;
            tmdbId = 0;
            if (string.IsNullOrEmpty(value)) return false;
            var parts = value.Split(':');
            if (parts.Length != 2 || (parts[0] != "movie" && parts[0] != "tv")) return false;
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out tmdbId) || tmdbId <= 0) return false;
            mediaType = parts[0];
            return true;
        }

        /// <summary>Channel item ids are "folder|type|tmdbId".</summary>
        public static bool TryParseItemId(string id, out string mediaType, out int tmdbId)
        {
            mediaType = null;
            tmdbId = 0;
            var parts = string.IsNullOrEmpty(id) ? null : id.Split('|');
            return parts != null && parts.Length == 3 && TryParseProviderValue(parts[1] + ":" + parts[2], out mediaType, out tmdbId);
        }
    }

    /// <summary>
    /// Asks Emby to re-crawl the Discover channels (IHasChangeEvent.ContentChanged makes Emby's
    /// ChannelManager refresh that channel), debounced so a burst of requests causes one refresh.
    /// </summary>
    internal static class ChannelRefresh
    {
        private static readonly List<DiscoverChannelBase> Channels = new List<DiscoverChannelBase>();
        private static readonly object Gate = new object();
        private static Timer _timer;

        public static void Register(DiscoverChannelBase channel)
        {
            lock (Gate) Channels.Add(channel);
        }

        public static void Request(TimeSpan delay)
        {
            lock (Gate)
            {
                _timer?.Dispose();
                _timer = new Timer(_ => Fire(), null, delay, Timeout.InfiniteTimeSpan);
            }
        }

        private static void Fire()
        {
            DiscoverChannelBase[] targets;
            lock (Gate) targets = Channels.ToArray();
            DiscoverChannelBase.ClearResultCache();
            foreach (var c in targets)
            {
                try { c.RaiseContentChanged(); }
                catch (Exception ex) { SeerrClient.Logger?.Debug("Discover: channel refresh request failed: {0}", ex.Message); }
            }
        }
    }

    /// <summary>
    /// Native Emby channel (web, mobile and TV apps). One channel per media type so home-screen rows
    /// and "Latest" views are not mixed.
    ///
    /// Browsing never has side effects: Emby's "Refresh Internet Channels" task crawls these folders.
    /// Items are discovery metadata only (no media sources). Requests from native apps go through
    /// the opt-in Favourites mechanism (FavoriteRequestEntryPoint).
    /// </summary>
    public abstract class DiscoverChannelBase : IChannel, IHasChangeEvent, IRequiresMediaInfoCallback
    {
        private const int SeerrPageSize = 20;
        private static readonly ConcurrentDictionary<string, Tuple<List<DiscoverItemDto>, DateTime>> ResultCache =
            new ConcurrentDictionary<string, Tuple<List<DiscoverItemDto>, DateTime>>();

        protected readonly ILogger Logger;

        protected DiscoverChannelBase(ILogManager logManager)
        {
            Logger = logManager.GetLogger("Discover");
            ChannelRefresh.Register(this);
        }

        /// <summary>"movie" or "tv".</summary>
        protected abstract string MediaType { get; }
        protected abstract string TypeLabel { get; } // "Movies" / "Shows"

        public abstract string Name { get; }

        public event EventHandler ContentChanged;

        internal void RaiseContentChanged() => ContentChanged?.Invoke(this, EventArgs.Empty);

        internal static void ClearResultCache() => ResultCache.Clear();

        public string Description =>
            Plugin.Config.EnableFavoriteRequests
                ? "Trending, popular and upcoming " + TypeLabel.ToLowerInvariant() + " from Seerr, plus your TMDB lists. Add a title to Favourites to request it."
                : "Trending, popular and upcoming " + TypeLabel.ToLowerInvariant() + " from Seerr, plus your TMDB lists.";

        public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

        public IEnumerable<ImageType> GetSupportedChannelImages() => new[] { ImageType.Primary, ImageType.Thumb };

        public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
        {
            var name = (MediaType == "tv" ? "tv-" : "movies-") + (type == ImageType.Thumb ? "thumb.png" : "primary.png");
            var stream = typeof(Plugin).Assembly.GetManifestResourceStream(typeof(Plugin).Namespace + ".Images." + name);
            if (stream == null) return Task.FromResult<DynamicImageResponse>(null);
            return Task.FromResult(new DynamicImageResponse { Stream = stream, Format = ImageFormat.Png, Protocol = MediaProtocol.File });
        }

        // ------------------------------------------------------------------ folders

        private string FolderName(string kind)
        {
            switch (kind)
            {
                case "trending": return "Trending " + TypeLabel;
                case "popular": return "Popular " + TypeLabel;
                default: return "Upcoming " + TypeLabel;
            }
        }

        public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
        {
            var folderId = query.FolderId ?? "";
            try
            {
                if (folderId.Length == 0) return await RootFolders(cancellationToken).ConfigureAwait(false);
                if (Array.IndexOf(Plugin.SeerrKinds, folderId) < 0 && !folderId.StartsWith("list:", StringComparison.Ordinal))
                    return await SeriesChildren(folderId, cancellationToken).ConfigureAwait(false); // a show: its "Trailer" item

                var all = await GetFolderItems(folderId, cancellationToken).ConfigureAwait(false);
                var start = Math.Max(0, query.StartIndex ?? 0);
                var limit = query.Limit.HasValue && query.Limit.Value > 0 ? query.Limit.Value : all.Count;
                var cfg = Plugin.Config;
                var items = all.Skip(start).Take(limit).Select(i => ToChannelItem(folderId, i, cfg)).ToList();
                return new ChannelItemResult { Items = items, TotalRecordCount = all.Count };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A Seerr/TMDB outage must not break Emby: show an empty folder and log one line.
                Logger.Error("Discover channel '{0}': could not load '{1}': {2}", Name, folderId, ex.Message);
                return Empty();
            }
        }

        private static ChannelItemResult Empty() => new ChannelItemResult { Items = new List<ChannelItemInfo>(), TotalRecordCount = 0 };

        /// <summary>A configured list resolved for this channel.</summary>
        internal sealed class ResolvedList
        {
            public string FolderId;
            public ListDefinition Definition;
            public string SeerrKind;      // trending | popular | upcoming, or null
            public TmdbListSpec Tmdb;     // TMDB list / query, or null
        }

        /// <summary>
        /// The enabled lists for this channel, in the configured order. Folder ids stay stable
        /// across versions: Seerr lists use their kind, TMDB lists "list:" + the list/query id.
        /// </summary>
        internal List<ResolvedList> ChannelLists()
        {
            var result = new List<ResolvedList>();
            var seen = new HashSet<string>();
            foreach (var def in Plugin.Config.Lists ?? new ListDefinition[0])
            {
                if (def == null || !def.Enabled || string.IsNullOrWhiteSpace(def.Source)) continue;
                var type = (def.MediaType ?? "").ToLowerInvariant();
                if (type != MediaType) continue;
                var r = new ResolvedList { Definition = def };
                var src = def.Source.Trim();
                if (src.StartsWith("seerr:", StringComparison.OrdinalIgnoreCase))
                {
                    var kind = src.Substring(6).ToLowerInvariant();
                    if (Array.IndexOf(Plugin.SeerrKinds, kind) < 0) continue;
                    r.SeerrKind = kind;
                    r.FolderId = kind;
                }
                else
                {
                    r.Tmdb = TmdbClient.ParseSource(src);
                    if (r.Tmdb == null) continue;
                    r.FolderId = "list:" + r.Tmdb.Id;
                }
                if (seen.Add(r.FolderId)) result.Add(r);
            }
            return result;
        }

        private async Task<ChannelItemResult> RootFolders(CancellationToken ct)
        {
            var items = new List<ChannelItemInfo>();
            var seerrOk = SeerrClient.IsConfigured(out _);
            foreach (var r in ChannelLists())
            {
                string name = string.IsNullOrWhiteSpace(r.Definition.Name) ? null : r.Definition.Name.Trim();
                if (r.SeerrKind != null)
                {
                    if (!seerrOk) continue;
                    name = name ?? FolderName(r.SeerrKind);
                }
                else
                {
                    if (!TmdbClient.IsConfigured) continue;
                    try
                    {
                        var list = await TmdbClient.GetListAsync(r.Tmdb, r.Definition.MaxItems, ct).ConfigureAwait(false);
                        // A mixed TMDB list (movies and shows) only appears when it has titles of this type.
                        if (r.Tmdb.ImpliedType == null && list.Items.All(i => i.MediaType != MediaType)) continue;
                        name = name ?? list.Name;
                    }
                    catch (SeerrException ex)
                    {
                        Logger.Warn("Discover: TMDB list {0}: {1}", r.Tmdb.Id, ex.Message);
                        name = name ?? "TMDB list " + r.Tmdb.Id;
                    }
                }
                items.Add(new ChannelItemInfo { Id = r.FolderId, Name = name, Type = ChannelItemType.Folder, FolderType = ChannelFolderType.Container });
            }
            return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
        }

        // ------------------------------------------------------------------ trailers

        /// <summary>
        /// Inside a show (series folder) there is one playable "Trailer" item. Its stream is
        /// resolved only when Play is pressed (GetChannelItemMediaInfo), so channel refreshes stay cheap.
        /// </summary>
        private async Task<ChannelItemResult> SeriesChildren(string seriesItemId, CancellationToken ct)
        {
            if (!Plugin.Config.PlayTrailers || !DiscoverIds.TryParseItemId(seriesItemId, out var type, out var tmdbId) || type != "tv")
                return Empty();
            try
            {
                // Only offer a Trailer item when the show (or one of its seasons) has one.
                if (await TrailerIndex.GetKeyAsync("tv", tmdbId, ct).ConfigureAwait(false) == null) return Empty();
            }
            catch (SeerrException ex)
            {
                Logger.Debug("Discover: trailer lookup for tv {0} failed: {1}", tmdbId, ex.Message);
            }
            var known = ResultCache.Values.SelectMany(v => v.Item1).FirstOrDefault(i => i.MediaType == "tv" && i.TmdbId == tmdbId);
            var trailer = new ChannelItemInfo
            {
                Id = "trailer|tv|" + tmdbId.ToString(CultureInfo.InvariantCulture),
                Name = "Trailer",
                Type = ChannelItemType.Media,
                MediaType = ChannelMediaType.Video,
                ContentType = ChannelMediaContentType.Clip,
                Overview = "Plays the show's trailer (or the newest season's) from YouTube.",
                ImageUrl = SeerrClient.TmdbImageUrl(known?.BackdropPath ?? known?.PosterPath, known?.BackdropPath != null ? "w780" : "w500"),
                IndexNumber = 1,
                MediaSources = new List<MediaSourceInfo>()
            };
            return new ChannelItemResult { Items = new List<ChannelItemInfo> { trailer }, TotalRecordCount = 1 };
        }

        /// <summary>
        /// Called by Emby when Play is pressed on a Discover item: returns the title's YouTube trailer as a
        /// remote http source. Emby's web player hands youtube.com sources to its built-in YouTube player.
        /// </summary>
        public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
        {
            var none = new List<MediaSourceInfo>();
            try
            {
                if (!Plugin.Config.PlayTrailers || !SeerrClient.IsConfigured(out _)) return none;
                if (!DiscoverIds.TryParseItemId(id, out var type, out var tmdbId)) return none;
                var key = await TrailerIndex.GetKeyAsync(type, tmdbId, cancellationToken).ConfigureAwait(false);
                if (key == null)
                {
                    Logger.Info("Discover: no YouTube trailer found for {0} {1}", type, tmdbId);
                    return none;
                }
                return new List<MediaSourceInfo> { TrailerSource(key) };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Warn("Discover: could not look up a trailer for '{0}': {1}", id, ex.Message);
                return none;
            }
        }

        internal static MediaSourceInfo TrailerSource(string youtubeKey)
        {
            return new MediaSourceInfo
            {
                Id = "yt_" + youtubeKey,
                Name = "Trailer (YouTube)",
                Path = "https://www.youtube.com/watch?v=" + youtubeKey,
                Protocol = MediaProtocol.Http,
                IsRemote = true,
                SupportsDirectPlay = true,
                SupportsDirectStream = false,
                SupportsTranscoding = false,
                SupportsProbing = false, // ffprobe cannot read a YouTube page
                RequiresOpening = false,
                IsInfiniteStream = false,
                MediaStreams = new List<MediaStream>
                {
                    new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "h264" },
                    new MediaStream { Type = MediaStreamType.Audio, Index = 1, Codec = "aac" }
                }
            };
        }

        // ------------------------------------------------------------------ items

        /// <summary>The full, filtered contents of a folder (cached for the configured cache time).</summary>
        private async Task<List<DiscoverItemDto>> GetFolderItems(string folderId, CancellationToken ct)
        {
            var cacheKey = MediaType + "|" + folderId;
            if (ResultCache.TryGetValue(cacheKey, out var hit) && hit.Item2 > DateTime.UtcNow) return hit.Item1;

            var cfg = Plugin.Config;
            var resolved = ChannelLists().FirstOrDefault(r => r.FolderId == folderId);
            List<DiscoverItemDto> result;
            if (resolved == null) result = new List<DiscoverItemDto>(); // list removed from the settings
            else if (resolved.Tmdb != null) result = await ListItems(resolved, cfg, ct).ConfigureAwait(false);
            else result = await SeerrItems(resolved.SeerrKind, resolved.Definition.MaxItems, cfg, ct).ConfigureAwait(false);

            if (NeedsTitleDetails(cfg) && SeerrClient.IsConfigured(out _))
                await TitleIndex.AnnotateAsync(result, ct).ConfigureAwait(false);

            var minutes = Math.Max(1, Math.Min(240, cfg.CacheMinutes));
            ResultCache[cacheKey] = Tuple.Create(result, DateTime.UtcNow.AddMinutes(minutes));
            return result;
        }

        private async Task<List<DiscoverItemDto>> SeerrItems(string kind, int maxOverride, PluginConfiguration cfg, CancellationToken ct)
        {
            var max = Math.Max(SeerrPageSize, Math.Min(400, maxOverride > 0 ? maxOverride : cfg.MaxItemsPerCategory));
            var collected = new List<DiscoverItemDto>();
            var seen = new HashSet<int>();
            // Hiding owned titles removes some results, so keep paging until the folder is full
            // (bounded so a library that owns everything cannot cause runaway paging).
            var maxPages = Math.Min(25, (max / SeerrPageSize + 1) * 3);
            for (var page = 1; page <= maxPages && collected.Count < max; page++)
            {
                var result = await SeerrClient.GetTypedDiscoverAsync(MediaType, kind, page, ct).ConfigureAwait(false);
                var fresh = result.Items.Where(i => seen.Add(i.TmdbId)).ToList();
                EmbyLibrary.Annotate(fresh);
                collected.AddRange(fresh.Where(i => !IsHidden(i, cfg)));
                if (page >= result.TotalPages) break;
            }
            return collected.Take(max).ToList();
        }

        private async Task<List<DiscoverItemDto>> ListItems(ResolvedList r, PluginConfiguration cfg, CancellationToken ct)
        {
            var list = await TmdbClient.GetListAsync(r.Tmdb, r.Definition.MaxItems, ct).ConfigureAwait(false);
            var items = list.Items.Where(i => i.MediaType == MediaType).ToList();
            await AnnotateWithSeerr(items, ct).ConfigureAwait(false);
            return items.Where(i => !IsHidden(i, cfg)).ToList();
        }

        /// <summary>Adds Seerr availability and the Emby library check to TMDB-sourced titles.</summary>
        internal static async Task AnnotateWithSeerr(List<DiscoverItemDto> items, CancellationToken ct)
        {
            if (SeerrClient.IsConfigured(out _))
            {
                try
                {
                    TmdbClient.ApplySeerrStatus(items, await SeerrClient.GetMediaStatusMapAsync(ct).ConfigureAwait(false));
                }
                catch (SeerrException ex)
                {
                    SeerrClient.Logger?.Warn("Discover: Seerr status unavailable for a TMDB list: {0}", ex.Message);
                }
            }
            EmbyLibrary.Annotate(items);
        }

        /// <summary>
        /// "Owned" = available in Seerr, or in the Emby library (unless Seerr says a show is only
        /// partly available — those stay visible so missing seasons can be requested).
        /// </summary>
        public static bool IsOwned(DiscoverItemDto i)
        {
            if (i.Status == Availability.Available) return true;
            if (i.InEmbyLibrary && i.Status != Availability.Partial) return true;
            return false;
        }

        /// <summary>Details are looked up for the scores/cast option and for the trailer badge.</summary>
        internal static bool NeedsTitleDetails(PluginConfiguration cfg) => cfg.AddTitleDetails || (cfg.PlayTrailers && cfg.ShowTrailerBadge);

        /// <summary>Why a title counts as owned — shown by Preview on the settings page.</summary>
        public static string OwnedReason(DiscoverItemDto i)
        {
            if (!IsOwned(i)) return null;
            if (i.InEmbyLibrary) return "Found " + (i.LibraryMatch ?? "in the Emby library") + ".";
            return "Seerr marks it as available (Seerr → the title's page shows its status).";
        }

        private static bool IsHidden(DiscoverItemDto i, PluginConfiguration cfg) => cfg.HideOwned && IsOwned(i);

        public static string BadgeFor(DiscoverItemDto i)
        {
            if (IsOwned(i)) return null;
            if (i.Status == Availability.Pending || i.Status == Availability.Processing) return "requested";
            if (i.Status == Availability.Partial && i.MediaType == "tv") return "partial";
            return null;
        }

        private ChannelItemInfo ToChannelItem(string folderId, DiscoverItemDto item, PluginConfiguration cfg)
        {
            // The status line goes in the tagline, which Emby shows on its own line above the description.
            var status = new StringBuilder();
            status.Append(StatusPrefix(item)).Append(item.StatusLabel);
            if (item.CanRequest)
            {
                status.Append(cfg.EnableFavoriteRequests
                    ? " — add to Favourites (♥) to request" + (item.MediaType == "tv" ? " " + FavoriteSeasonText(cfg) : "") + "."
                    : " — request it in Seerr, or turn on Favourite-to-request.");
            }

            var details = cfg.AddTitleDetails ? item.Info : null;
            var scores = TitleIndex.ScoresText(details, details != null ? item.VoteAverage : null);
            if (scores != null) status.Append("  |  ").Append(scores);

            // The star is the IMDb score when Seerr has one (movies), otherwise TMDB's.
            var star = details?.ImdbScore ?? (item.VoteAverage.HasValue && item.VoteAverage.Value > 0 ? item.VoteAverage : null);
            var info = new ChannelItemInfo
            {
                Id = folderId + "|" + item.MediaType + "|" + item.TmdbId.ToString(CultureInfo.InvariantCulture),
                Name = item.Title,
                Tagline = status.ToString(),
                Overview = item.Overview ?? "",
                ProductionYear = item.Year,
                PremiereDate = SeerrClient.ParseDate(item.ReleaseDate),
                CommunityRating = star.HasValue ? (float?)star.Value : null,
                ImageUrl = SeerrClient.TmdbImageUrl(item.PosterPath, "w500"),
                ForceUpdate = true, // availability changes over time; update the stored item on refresh
                ProviderIds = new ProviderIdDictionary(),
                // Emby also runs its own metadata refresh on channel items; keep it from overwriting these.
                LockedFields = new[]
                {
                    MetadataFields.Name, MetadataFields.Overview, MetadataFields.Tagline, MetadataFields.CommunityRating,
                    MetadataFields.CriticRating, MetadataFields.Cast, MetadataFields.Genres, MetadataFields.Studios,
                    MetadataFields.OfficialRating, MetadataFields.Runtime, MetadataFields.OriginalTitle
                }
            };
            info.ProviderIds[DiscoverIds.ProviderKey] = item.MediaType + ":" + item.TmdbId.ToString(CultureInfo.InvariantCulture);
            // Real ids give the detail page its TMDB / IMDb links. (The library check ignores channel items.)
            info.ProviderIds[MetadataProviders.Tmdb.ToString()] = item.TmdbId.ToString(CultureInfo.InvariantCulture);
            if (details != null) ApplyDetails(info, details, cfg);
            var badge = BadgeFor(item);
            // Always write the keys so a cleared badge replaces an old value on refresh.
            info.ProviderIds[DiscoverIds.BadgeKey] = badge ?? "none";
            info.ProviderIds[DiscoverIds.TrailerKey] = item.HasTrailer == true ? "1" : "0";

            if (item.MediaType == "tv" && cfg.ShowTvAsSeries)
            {
                info.Type = ChannelItemType.Folder;
                info.FolderType = ChannelFolderType.Series;
            }
            else
            {
                info.Type = ChannelItemType.Media;
                info.MediaType = ChannelMediaType.Video;
                info.ContentType = ChannelMediaContentType.Movie;
                info.MediaSources = new List<MediaSourceInfo>(); // Play resolves the trailer via GetChannelItemMediaInfo
            }
            return info;
        }

        /// <summary>Cast and crew, genres, studios, runtime, original title and age rating.</summary>
        internal static void ApplyDetails(ChannelItemInfo info, TitleInfo d, PluginConfiguration cfg)
        {
            if (!string.IsNullOrEmpty(d.ImdbId)) info.ProviderIds[MetadataProviders.Imdb.ToString()] = d.ImdbId;
            if (d.Genres.Count > 0) info.Genres = d.Genres.ToList();
            if (d.Studios.Count > 0) info.Studios = d.Studios.ToList();
            if (d.RuntimeMinutes.HasValue) info.RunTimeTicks = TimeSpan.FromMinutes(d.RuntimeMinutes.Value).Ticks;
            if (!string.IsNullOrWhiteSpace(d.OriginalTitle) && !string.Equals(d.OriginalTitle, info.Name, StringComparison.Ordinal))
                info.OriginalTitle = d.OriginalTitle;
            var country = string.IsNullOrWhiteSpace(cfg.AgeRatingCountry) ? "US" : cfg.AgeRatingCountry.Trim().ToUpperInvariant();
            if (d.AgeRatings.TryGetValue(country, out var rating)) info.OfficialRating = rating;
            info.People = d.People.Select(p =>
            {
                var person = new MediaBrowser.Controller.Entities.PersonInfo
                {
                    Name = p.Name,
                    Role = p.Role,
                    Type = p.Type == "Director" ? PersonType.Director : p.Type == "Writer" ? PersonType.Writer
                         : p.Type == "Composer" ? PersonType.Composer : PersonType.Actor,
                    ImageUrl = SeerrClient.TmdbImageUrl(p.ProfilePath, "w185")
                };
                if (p.TmdbId > 0)
                {
                    person.ProviderIds = new ProviderIdDictionary();
                    person.ProviderIds[MetadataProviders.Tmdb.ToString()] = p.TmdbId.ToString(CultureInfo.InvariantCulture);
                }
                return person;
            }).ToList();
        }

        private static string StatusPrefix(DiscoverItemDto item)
        {
            switch (item.Status)
            {
                case Availability.Available: return "✔ ";
                case Availability.Partial: return "◐ ";
                case Availability.Pending:
                case Availability.Processing: return "⏳ ";
                case Availability.Blocklisted: return "⛔ ";
                default: return item.InEmbyLibrary ? "✔ " : "➕ ";
            }
        }

        private static string FavoriteSeasonText(PluginConfiguration cfg)
        {
            switch ((cfg.FavoriteTvSeasons ?? "all").Trim().ToLowerInvariant())
            {
                case "first": return "(first missing season)";
                case "latest": return "(latest season)";
                default: return "(all missing seasons)";
            }
        }
    }

    public class MoviesDiscoverChannel : DiscoverChannelBase
    {
        public MoviesDiscoverChannel(ILogManager logManager) : base(logManager) { }
        protected override string MediaType => "movie";
        protected override string TypeLabel => "Movies";
        public override string Name => string.IsNullOrWhiteSpace(Plugin.Config.MoviesChannelName) ? "Discover Movies" : Plugin.Config.MoviesChannelName.Trim();
    }

    public class TvDiscoverChannel : DiscoverChannelBase
    {
        public TvDiscoverChannel(ILogManager logManager) : base(logManager) { }
        protected override string MediaType => "tv";
        protected override string TypeLabel => "Shows";
        public override string Name => string.IsNullOrWhiteSpace(Plugin.Config.TvChannelName) ? "Discover Shows" : Plugin.Config.TvChannelName.Trim();
    }
}

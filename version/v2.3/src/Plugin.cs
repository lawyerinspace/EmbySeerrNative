using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace EmbySeerrNative
{
    /// <summary>One folder in the Discover Movies / Discover Shows channels.</summary>
    public class ListDefinition
    {
        /// <summary>Stable id used by the settings page (not by Emby).</summary>
        public string Id { get; set; }
        /// <summary>Folder name; empty = use the source's own name.</summary>
        public string Name { get; set; }
        public bool Enabled { get; set; } = true;
        /// <summary>
        /// seerr:trending | seerr:popular | seerr:upcoming,
        /// a TMDB list URL or id, or a TMDB API query URL (date placeholders allowed).
        /// </summary>
        public string Source { get; set; }
        /// <summary>movie | tv — which channel the list appears in ("both" from 2.0 is split on load).</summary>
        public string MediaType { get; set; } = "movie";
        /// <summary>0 = the default for this kind of list.</summary>
        public int MaxItems { get; set; }
        /// <summary>The list-builder form state, so the settings page can edit the list again.</summary>
        public string BuilderJson { get; set; }
    }

    /// <summary>Where requested titles are stored. Blank folder/tags = Seerr's own defaults.</summary>
    public class DestinationSettings
    {
        public string Label { get; set; }
        public string MovieRootFolder { get; set; } = "";
        /// <summary>Comma-separated Radarr tag ids.</summary>
        public string MovieTagIds { get; set; } = "";
        public string TvRootFolder { get; set; } = "";
        /// <summary>Comma-separated Sonarr tag ids.</summary>
        public string TvTagIds { get; set; } = "";
    }

    /// <summary>
    /// Persisted by Emby as XML in /config/plugins/configurations/EmbySeerrNative.xml.
    /// Settings from 1.x are migrated on first load (see Plugin.Migrate).
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        public int ConfigVersion { get; set; }

        // --- Connection ---
        public string SeerrUrl { get; set; } = "http://seerr:5055";
        public string ApiKey { get; set; } = "";
        /// <summary>Address of Seerr as your browser reaches it (for the "Open Seerr" button).</summary>
        public string SeerrPublicUrl { get; set; } = "";
        /// <summary>TMDB API key (v3) or API Read Access Token (v4).</summary>
        public string TmdbApiKey { get; set; } = "";
        public int TimeoutSeconds { get; set; } = 15;
        public int CacheMinutes { get; set; } = 10;

        // --- Users ---
        public bool RequestsAdminsOnly { get; set; } = false;
        public bool AllowUnlinkedAdmins { get; set; } = false;

        // --- Channels & Lists ---
        /// <summary>Emby identifies a channel by its name: renaming creates a new channel.</summary>
        public string MoviesChannelName { get; set; } = "Discover Movies";
        public string TvChannelName { get; set; } = "Discover Shows";
        public ListDefinition[] Lists { get; set; }
        /// <summary>Default size of a Seerr list (Trending / Popular / Upcoming).</summary>
        public int MaxItemsPerCategory { get; set; } = 60;
        /// <summary>Default size of a TMDB list or query.</summary>
        public int MaxItemsPerList { get; set; } = 200;

        // --- File Management ---
        /// <summary>Opt-in: adding a Discover title to Favourites submits a Seerr request.</summary>
        public bool EnableFavoriteRequests { get; set; } = false;
        /// <summary>all | first | latest — which seasons a favourited TV show requests.</summary>
        public string FavoriteTvSeasons { get; set; } = "all";
        public bool UnfavoriteAfterRequest { get; set; } = true;
        /// <summary>remote | local — which destination requests use.</summary>
        public string DefaultDestination { get; set; } = "remote";
        public DestinationSettings RemoteDestination { get; set; } = new DestinationSettings { Label = "Stream" };
        public DestinationSettings LocalDestination { get; set; } = new DestinationSettings { Label = "Download locally" };

        // --- Display ---
        public bool HideOwned { get; set; } = true;
        public bool CheckEmbyLibrary { get; set; } = true;
        /// <summary>Emby libraries (VirtualFolderInfo.ItemId) that count as "already have"; empty = all.</summary>
        public string[] OwnedLibraryIds { get; set; } = new string[0];
        public bool ShowRequestedBadge { get; set; } = true;
        public bool ShowPartialBadge { get; set; } = true;
        /// <summary>"▶ TRAILER" badge on titles that have a trailer (needs PlayTrailers).</summary>
        public bool ShowTrailerBadge { get; set; } = true;
        /// <summary>IMDb (star) and Rotten Tomatoes scores, cast and crew, genres, studios, runtime and age rating on each title.</summary>
        public bool AddTitleDetails { get; set; } = true;
        /// <summary>Country whose age rating is shown (two letters).</summary>
        public string AgeRatingCountry { get; set; } = "US";
        public bool PlayTrailers { get; set; } = true;
        /// <summary>
        /// Shows as series folders (with a Trailer item inside) instead of playable items. Off by default
        /// since 2.2: Play on a folder's poster has nothing to play until the folder has been opened.
        /// </summary>
        public bool ShowTvAsSeries { get; set; } = false;

        // --- 1.x settings, read once for migration ---
        public string TmdbLists { get; set; }
        public bool EnableDestinationChoice { get; set; }
        public string DefaultDestinationLabel { get; set; }
        public string LocalDestinationLabel { get; set; }
        public string LocalMovieRootFolder { get; set; }
        public string LocalTvRootFolder { get; set; }
        public string LocalMovieTagIds { get; set; }
        public string LocalTvTagIds { get; set; }
    }

    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage
    {
        // Retained from v0.1 so this installs as an upgrade of the existing plugin.
        public static readonly Guid PluginGuid = new Guid("ed685bed-a759-4d9a-9482-3987767c3e4c");

        public static Plugin Instance { get; private set; }

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public override string Name => "Discover";

        public override string Description =>
            "Discover movies and shows from Seerr and TMDB lists in Emby, and request them through Seerr.";

        public override Guid Id => PluginGuid;

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "SeerrDiscoverConfig",
                    DisplayName = "Discover",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.config.html",
                    IsMainConfigPage = true,
                    EnableInMainMenu = true,
                    MenuIcon = "explore"
                },
                new PluginPageInfo
                {
                    Name = "SeerrDiscoverConfigJs",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.config.js"
                }
            };
        }

        public Stream GetThumbImage()
            => GetType().Assembly.GetManifestResourceStream(GetType().Namespace + ".Images.thumb.png");

        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        private static PluginConfiguration _fallback;

        public static PluginConfiguration Config
        {
            get
            {
                var cfg = Instance?.Configuration ?? (_fallback ?? (_fallback = new PluginConfiguration()));
                if (cfg.ConfigVersion < CurrentConfigVersion) Migrate(cfg);
                return cfg;
            }
        }

        public const int CurrentConfigVersion = 4;

        public static readonly string[] SeerrKinds = { "trending", "popular", "upcoming" };

        public static ListDefinition[] DefaultLists()
        {
            var lists = new List<ListDefinition>();
            foreach (var type in new[] { "movie", "tv" })
                foreach (var kind in SeerrKinds)
                    lists.Add(new ListDefinition
                    {
                        Id = (type == "movie" ? "m-" : "t-") + kind,
                        Name = "",
                        Source = "seerr:" + kind,
                        MediaType = type
                    });
            return lists.ToArray();
        }

        internal static void Migrate(PluginConfiguration cfg)
        {
            lock (typeof(Plugin))
            {
                if (cfg.ConfigVersion >= CurrentConfigVersion) return;
                if (cfg.ConfigVersion < 2) MigrateFrom1(cfg);
                SplitMixedLists(cfg);
                if (cfg.OwnedLibraryIds == null) cfg.OwnedLibraryIds = new string[0];
                if (cfg.ConfigVersion < 4) cfg.ShowTvAsSeries = false; // 2.2: shows play their trailer from the poster
                cfg.ConfigVersion = CurrentConfigVersion;
            }
            try { Instance?.SaveConfiguration(); }
            catch { /* best effort: the migration is repeated in memory next time */ }
        }

        /// <summary>
        /// 2.1: the Movies and Shows channels have separate list tables, so a list set to "both"
        /// becomes one movie list and one show list (same source, same folder id in each channel).
        /// </summary>
        internal static void SplitMixedLists(PluginConfiguration cfg)
        {
            if (cfg.Lists == null) return;
            var result = new List<ListDefinition>();
            foreach (var l in cfg.Lists)
            {
                if (l == null) continue;
                var type = (l.MediaType ?? "both").Trim().ToLowerInvariant();
                if (type == "movie" || type == "tv") { l.MediaType = type; result.Add(l); continue; }
                foreach (var t in new[] { "movie", "tv" })
                {
                    result.Add(new ListDefinition
                    {
                        Id = (l.Id ?? "list") + (t == "movie" ? "-m" : "-t"),
                        Name = l.Name,
                        Enabled = l.Enabled,
                        Source = l.Source,
                        MediaType = t,
                        MaxItems = l.MaxItems,
                        BuilderJson = RetypeBuilderJson(l.BuilderJson, t)
                    });
                }
            }
            // Movie lists first, then shows, keeping the order within each type.
            cfg.Lists = result.Where(l => l.MediaType == "movie").Concat(result.Where(l => l.MediaType == "tv")).ToArray();
        }

        private static string RetypeBuilderJson(string json, string type)
        {
            if (string.IsNullOrWhiteSpace(json)) return json;
            try
            {
                if (Json.Parse(json) is Dictionary<string, object> d)
                {
                    d["type"] = type;
                    return Json.Serialize(d);
                }
            }
            catch (FormatException) { }
            return json;
        }

        /// <summary>
        /// Brings 1.x settings into the 2.0 shape: default Seerr lists + the old TMDB text box as
        /// list entries, and the old "local download" fields as the Local destination.
        /// </summary>
        private static void MigrateFrom1(PluginConfiguration cfg)
        {
            {
                if (cfg.Lists == null || cfg.Lists.Length == 0)
                {
                    var lists = new List<ListDefinition>(DefaultLists());
                    var n = 0;
                    foreach (var spec in TmdbClient.ParseSpecs(cfg.TmdbLists))
                    {
                        n++;
                        lists.Add(new ListDefinition
                        {
                            Id = "migrated-" + n,
                            Name = spec.CustomName ?? "",
                            Source = spec.Query != null ? "https://api.themoviedb.org/3" + spec.Query : spec.Id,
                            MediaType = spec.ImpliedType ?? "both"
                        });
                    }
                    cfg.Lists = lists.ToArray();
                }
                if (cfg.RemoteDestination == null) cfg.RemoteDestination = new DestinationSettings();
                if (cfg.LocalDestination == null) cfg.LocalDestination = new DestinationSettings();
                if (!string.IsNullOrWhiteSpace(cfg.DefaultDestinationLabel)) cfg.RemoteDestination.Label = cfg.DefaultDestinationLabel;
                if (!string.IsNullOrWhiteSpace(cfg.LocalDestinationLabel)) cfg.LocalDestination.Label = cfg.LocalDestinationLabel;
                if (cfg.EnableDestinationChoice || !string.IsNullOrWhiteSpace(cfg.LocalMovieTagIds) || !string.IsNullOrWhiteSpace(cfg.LocalTvTagIds))
                {
                    cfg.LocalDestination.MovieRootFolder = cfg.LocalMovieRootFolder ?? "";
                    cfg.LocalDestination.TvRootFolder = cfg.LocalTvRootFolder ?? "";
                    cfg.LocalDestination.MovieTagIds = cfg.LocalMovieTagIds ?? "";
                    cfg.LocalDestination.TvTagIds = cfg.LocalTvTagIds ?? "";
                }
                if (string.IsNullOrWhiteSpace(cfg.RemoteDestination.Label)) cfg.RemoteDestination.Label = "Stream";
                if (string.IsNullOrWhiteSpace(cfg.LocalDestination.Label)) cfg.LocalDestination.Label = "Download locally";
                if (cfg.DefaultDestination != "local") cfg.DefaultDestination = "remote";
                cfg.TmdbLists = null;
                cfg.EnableDestinationChoice = false;
                cfg.DefaultDestinationLabel = cfg.LocalDestinationLabel = null;
                cfg.LocalMovieRootFolder = cfg.LocalTvRootFolder = cfg.LocalMovieTagIds = cfg.LocalTvTagIds = null;
                cfg.ConfigVersion = 2;
            }
        }

        /// <summary>Raised after an administrator saves the settings.</summary>
        public static event EventHandler ConfigurationSaved;

        public override void UpdateConfiguration(BasePluginConfiguration configuration)
        {
            base.UpdateConfiguration(configuration);
            try { ConfigurationSaved?.Invoke(this, EventArgs.Empty); }
            catch { /* never break saving */ }
        }
    }
}

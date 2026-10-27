using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Session;

namespace EmbySeerrNative
{
    /// <summary>
    /// Wires up shared services and implements the opt-in "Favourite to request" feature for
    /// Emby's native TV and mobile apps, which cannot show custom buttons on channel items.
    ///
    /// Marking a Seerr Discover channel item as a Favourite (♥) is a deliberate user action that
    /// every Emby client supports. When enabled, it submits a Seerr request as the user's linked
    /// Seerr account, then shows the result as an on-screen message in that user's sessions.
    /// Opening or browsing an item never triggers a request.
    /// </summary>
    public class FavoriteRequestEntryPoint : IServerEntryPoint
    {
        private readonly IUserDataManager _userDataManager;
        private readonly ILibraryManager _libraryManager;
        private readonly ConcurrentDictionary<long, byte> _criticUpdates = new ConcurrentDictionary<long, byte>();
        private readonly ISessionManager _sessionManager;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        private readonly ConcurrentDictionary<string, DateTime> _recentFavorites = new ConcurrentDictionary<string, DateTime>();

        public FavoriteRequestEntryPoint(IUserDataManager userDataManager, ISessionManager sessionManager,
            ILibraryManager libraryManager, ILogManager logManager)
        {
            _userDataManager = userDataManager;
            _libraryManager = libraryManager;
            _sessionManager = sessionManager;
            _logger = logManager.GetLogger("Discover");

            SeerrClient.Logger = _logger;
            TmdbClient.Logger = _logger;
            RequestService.Logger = _logger;
            EmbyLibrary.Logger = _logger;
            EmbyLibrary.LibraryManager = libraryManager;
        }

        public void Run()
        {
            var cfg = Plugin.Config; // runs the 1.x -> 2.0 settings migration once
            _userDataManager.UserDataSaved += OnUserDataSaved;
            Plugin.ConfigurationSaved += OnConfigurationSaved;
            _libraryManager.ItemAdded += OnItemChanged;
            _libraryManager.ItemUpdated += OnItemChanged;
            _logger.Info("Discover: plugin started (favourite-to-request {0}).",
                cfg.EnableFavoriteRequests ? "enabled" : "disabled");
        }

        private void OnUserDataSaved(object sender, UserDataSaveEventArgs e)
        {
            try
            {
                if (!Plugin.Config.EnableFavoriteRequests) return;
                if (e == null || e.User == null || e.Item == null || e.UserData == null) return;
                if (e.SaveReason != UserDataSaveReason.UpdateUserRating) return; // favourite toggles
                if (!e.UserData.IsFavorite) return;

                var providerValue = e.Item.GetProviderId(DiscoverIds.ProviderKey);
                if (!DiscoverIds.TryParseProviderValue(providerValue, out var mediaType, out var tmdbId))
                {
                    // Fallback: channel item id "folder|type|tmdbId", if Emby stored it as ExternalId.
                    if (!DiscoverIds.TryParseItemId(e.Item.ExternalId, out mediaType, out tmdbId)) return;
                    providerValue = mediaType + ":" + tmdbId;
                }

                // Ignore repeated save events for the same favourite.
                var key = e.User.InternalId + "|" + providerValue;
                var now = DateTime.UtcNow;
                if (_recentFavorites.TryGetValue(key, out var last) && now - last < TimeSpan.FromMinutes(2)) return;
                _recentFavorites[key] = now;
                foreach (var kv in _recentFavorites)
                    if (now - kv.Value > TimeSpan.FromMinutes(10)) _recentFavorites.TryRemove(kv.Key, out _);

                var user = e.User;
                var item = e.Item;
                Task.Run(() => HandleFavoriteAsync(user, item, mediaType, tmdbId));
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Discover: error handling favourite", ex);
            }
        }

        private async Task HandleFavoriteAsync(MediaBrowser.Controller.Entities.User user, MediaBrowser.Controller.Entities.BaseItem item,
            string mediaType, int tmdbId)
        {
            var title = item.Name;
            RequestOutcomeDto outcome;
            try
            {
                var seasons = mediaType == "tv"
                    ? await RequestService.SeasonsForFavoriteAsync(tmdbId, _shutdown.Token).ConfigureAwait(false)
                    : null;
                outcome = await RequestService.SubmitAsync(user, mediaType, tmdbId, seasons, null, _shutdown.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Discover: favourite request failed", ex);
                outcome = new RequestOutcomeDto { Ok = false, Code = "seerr_error", Message = ex.Message };
            }

            _logger.Info("Discover: favourite by '{0}' for {1} {2}: {3} ({4})", user.Name, mediaType, tmdbId, outcome.Code,
                outcome.Message);

            // The favourite was only a "request this" gesture: once Seerr has the request (or the title
            // turns out to be requested/available already) remove it again. On errors it stays, so the
            // user can see nothing happened and try again.
            var settled = outcome.Ok || outcome.Code == "already_requested" || outcome.Code == "already_available" ||
                          outcome.Code == "nothing_to_request";
            if (settled && Plugin.Config.UnfavoriteAfterRequest) Unfavorite(user, item);
            await NotifyUserAsync(user.InternalId, outcome.Ok ? "Seerr request sent" : "Seerr request not sent",
                outcome.Message ?? title).ConfigureAwait(false);
        }

        private void Unfavorite(MediaBrowser.Controller.Entities.User user, MediaBrowser.Controller.Entities.BaseItem item)
        {
            try
            {
                var data = _userDataManager.GetUserData(user, item);
                if (data == null || !data.IsFavorite) return;
                data.IsFavorite = false;
                // Our UserDataSaved handler ignores un-favourites, so this cannot loop.
                _userDataManager.SaveUserData(user, item, data, UserDataSaveReason.UpdateUserRating, _shutdown.Token);
            }
            catch (Exception ex)
            {
                _logger.Warn("Discover: could not remove the favourite from '{0}': {1}", item.Name, ex.Message);
            }
        }

        private async Task NotifyUserAsync(long userInternalId, string header, string text)
        {
            try
            {
                var sessions = _sessionManager.Sessions
                    .Where(s => s != null && s.IsActive && s.UserInternalId == userInternalId)
                    .ToList();
                foreach (var session in sessions)
                {
                    try
                    {
                        await _sessionManager.SendMessageCommand(null, session.Id,
                            new MessageCommand { Header = header, Text = text, TimeoutMs = 10000 },
                            _shutdown.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // Some clients do not support on-screen messages.
                        _logger.Debug("Discover: could not message session {0}: {1}", session.Id, ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug("Discover: could not notify user: {0}", ex.Message);
            }
        }

        /// <summary>
        /// Channel items have no critic-rating field, so the Rotten Tomatoes critics score is written to
        /// the stored Discover item after Emby saves it. Emby then shows it with its fresh/rotten tomato
        /// icon next to the star. Only our own items are touched; a save that changes nothing is skipped,
        /// so this cannot loop.
        /// </summary>
        private void OnItemChanged(object sender, ItemChangeEventArgs e)
        {
            try
            {
                var item = e?.Item;
                if (item == null || !DiscoverIds.TryParseProviderValue(item.GetProviderId(DiscoverIds.ProviderKey), out var type, out var tmdbId)) return;
                var cfg = Plugin.Config;
                var details = cfg.AddTitleDetails ? TitleIndex.Peek(type, tmdbId) : null;
                if (details == null && cfg.AddTitleDetails) return; // not looked up yet: leave as is
                float? wanted = details?.RtCritics;
                if (item.CriticRating == wanted || !_criticUpdates.TryAdd(item.InternalId, 0)) return;
                Task.Run(() =>
                {
                    try
                    {
                        item.CriticRating = wanted;
                        item.UpdateToRepository(ItemUpdateType.MetadataEdit);
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug("Discover: could not store the critic rating of '{0}': {1}", item.Name, ex.Message);
                    }
                    finally
                    {
                        _criticUpdates.TryRemove(item.InternalId, out _);
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.Debug("Discover: critic rating hook: {0}", ex.Message);
            }
        }

        private void OnConfigurationSaved(object sender, EventArgs e)
        {
            // New URL/key/lists/filters: drop caches and let Emby re-crawl the channels.
            SeerrClient.ClearCache();
            SeerrClient.ClearUserCache();
            TmdbClient.ClearCache();
            ChannelRefresh.Request(TimeSpan.FromSeconds(3));
        }

        public void Dispose()
        {
            Plugin.ConfigurationSaved -= OnConfigurationSaved;
            _libraryManager.ItemAdded -= OnItemChanged;
            _libraryManager.ItemUpdated -= OnItemChanged;
            _userDataManager.UserDataSaved -= OnUserDataSaved;
            _shutdown.Cancel();
        }
    }
}

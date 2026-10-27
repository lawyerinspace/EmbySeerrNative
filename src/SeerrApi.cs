using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Services;
using MediaBrowser.Model.Tasks;

namespace EmbySeerrNative
{
    // ------------------------------------------------------------------------------------
    // Administrator-only endpoints used by the settings page. Everything users do happens in
    // the native channels (browse, ♥ to request, Play = trailer) or in Seerr itself.
    // ------------------------------------------------------------------------------------

    [Route("/SeerrDiscover/Admin/Status", "GET", Summary = "Emby users and the Seerr accounts they request as")]
    [Authenticated(Roles = "Admin")]
    public class GetSeerrAdminStatus : IReturn<AdminStatusDto>
    {
    }

    [Route("/SeerrDiscover/Admin/TestSeerr", "POST", Summary = "Light Seerr connection test with the values on the settings page")]
    [Authenticated(Roles = "Admin")]
    public class TestSeerr : IReturn<ConnectionTestDto>
    {
        public string SeerrUrl { get; set; }
        public string ApiKey { get; set; }
    }

    [Route("/SeerrDiscover/Admin/TestTmdb", "POST", Summary = "Light TMDB key test with the value on the settings page")]
    [Authenticated(Roles = "Admin")]
    public class TestTmdb : IReturn<ConnectionTestDto>
    {
        public string TmdbApiKey { get; set; }
    }

    [Route("/SeerrDiscover/Admin/RefreshChannels", "POST", Summary = "Run Emby's Refresh Internet Channels task now")]
    [Authenticated(Roles = "Admin")]
    public class StartChannelRefresh : IReturn<ChannelRefreshDto>
    {
    }

    [Route("/SeerrDiscover/Admin/RefreshChannels", "GET", Summary = "Progress of the Refresh Internet Channels task")]
    [Authenticated(Roles = "Admin")]
    public class GetChannelRefresh : IReturn<ChannelRefreshDto>
    {
    }

    [Route("/SeerrDiscover/Admin/Libraries", "GET", Summary = "Emby libraries that can count as already owned")]
    [Authenticated(Roles = "Admin")]
    public class GetOwnedLibraries : IReturn<List<LibraryDto>>
    {
    }

    [Route("/SeerrDiscover/Admin/Services", "GET", Summary = "Root folders and tags of Seerr's Radarr and Sonarr servers")]
    [Authenticated(Roles = "Admin")]
    public class GetSeerrServices : IReturn<ServicesDto>
    {
    }

    [Route("/SeerrDiscover/Admin/TmdbOptions", "GET", Summary = "TMDB genres, streaming services, regions and languages for the list builder")]
    [Authenticated(Roles = "Admin")]
    public class GetTmdbOptions : IReturn<TmdbOptionsDto>
    {
        public string MediaType { get; set; }
        public string Region { get; set; }
    }

    [Route("/SeerrDiscover/Admin/Preview", "POST", Summary = "Preview the titles a list source returns")]
    [Authenticated(Roles = "Admin")]
    public class PreviewList : IReturn<PreviewDto>
    {
        public string Source { get; set; }
        public string MediaType { get; set; }
        public int MaxItems { get; set; }
    }

    public class SeerrApi : IService, IRequiresRequest
    {
        private readonly IUserManager _userManager;
        private readonly ITaskManager _taskManager;

        public SeerrApi(IUserManager userManager, ITaskManager taskManager)
        {
            _userManager = userManager;
            _taskManager = taskManager;
        }

        // ---------------------------------------------------------------- channel refresh

        private const string RefreshTaskKey = "RefreshInternetChannels";

        private IScheduledTaskWorker RefreshTask()
            => _taskManager?.ScheduledTasks?.FirstOrDefault(t => t?.ScheduledTask != null && t.ScheduledTask.Key == RefreshTaskKey);

        private static ChannelRefreshDto Describe(IScheduledTaskWorker task, string message = null)
        {
            if (task == null) return new ChannelRefreshDto { Message = "Emby's \"Refresh Internet Channels\" task was not found." };
            var running = task.State != TaskState.Idle;
            var last = task.LastExecutionResult;
            return new ChannelRefreshDto
            {
                Ok = true,
                Running = running,
                Progress = running ? task.CurrentProgress : null,
                LastStatus = last?.Status.ToString(),
                LastEnded = last == null ? null : last.EndTimeUtc.ToString("o", CultureInfo.InvariantCulture),
                Message = message ?? (running
                    ? "Refreshing the Discover channels… " + (task.CurrentProgress.HasValue ? Math.Round(task.CurrentProgress.Value) + "%" : "")
                    : last != null && last.Status != TaskCompletionStatus.Completed
                        ? "The last refresh ended with: " + last.Status + (string.IsNullOrEmpty(last.ErrorMessage) ? "" : " (" + last.ErrorMessage + ")")
                        : "Channels are up to date.")
            };
        }

        public object Post(StartChannelRefresh request)
        {
            var task = RefreshTask();
            if (task == null) return Describe(null);
            if (task.State != TaskState.Idle) return Describe(task, "A refresh is already running.");
            // Fresh data from Seerr and TMDB, not the plugin's caches.
            SeerrClient.ClearCache();
            TmdbClient.ClearCache();
            DiscoverChannelBase.ClearResultCache();
            _taskManager.QueueScheduledTask(task.ScheduledTask, new TaskOptions { HasManualInteraction = true });
            var dto = Describe(task, "Refresh started.");
            dto.Running = true;
            return dto;
        }

        public object Get(GetChannelRefresh request) => Describe(RefreshTask());

        public IRequest Request { get; set; }

        private CancellationToken Ct => Request?.CancellationToken ?? CancellationToken.None;

        // ---------------------------------------------------------------- status

        public async Task<object> Get(GetSeerrAdminStatus request)
        {
            var cfg = Plugin.Config;
            var dto = new AdminStatusDto();
            if (!SeerrClient.IsConfigured(out var problem))
            {
                dto.Message = problem;
            }
            else
            {
                try
                {
                    SeerrClient.ClearCache();
                    SeerrClient.ClearUserCache();
                    dto.SeerrVersion = await SeerrClient.GetVersionAsync(Ct).ConfigureAwait(false);
                    dto.ApiKeyOwner = await SeerrClient.GetApiKeyOwnerAsync(Ct).ConfigureAwait(false);
                    var seerrUsers = await SeerrClient.GetUsersAsync(Ct, forceRefresh: true).ConfigureAwait(false);

                    foreach (var embyUser in _userManager.GetUserList(new UserQuery()).OrderBy(u => u.Name))
                    {
                        var row = new UserLinkRowDto
                        {
                            EmbyUserName = embyUser.Name,
                            EmbyUserId = UserLinker.EmbyUserKey(embyUser),
                            EmbyIsAdmin = UserLinker.IsAdmin(embyUser)
                        };
                        var link = await UserLinker.ResolveAsync(embyUser, Ct).ConfigureAwait(false);
                        row.SeerrUserId = link.SeerrUserId;
                        row.SeerrUserName = link.SeerrUserName;
                        row.LinkSource = link.Source;
                        row.Note = link.BlockedReason;
                        dto.Users.Add(row);
                    }

                    var unmatched = seerrUsers.Count(s => s.JellyfinUserId != null && dto.Users.All(r => r.EmbyUserId != s.JellyfinUserId));
                    dto.Ok = true;
                    dto.Message = "Connected to Seerr " + (dto.SeerrVersion ?? "") + ". " + seerrUsers.Count + " Seerr users found" +
                                  (unmatched > 0 ? ", " + unmatched + " of them linked to Emby IDs not on this server." : ".");
                }
                catch (SeerrException ex)
                {
                    dto.Ok = false;
                    dto.Message = ex.Message;
                }
            }

            return dto;
        }

        // ---------------------------------------------------------------- connection tests

        public async Task<object> Post(TestSeerr request)
            => await SeerrClient.TestAsync(request.SeerrUrl, request.ApiKey, Ct).ConfigureAwait(false);

        public async Task<object> Post(TestTmdb request)
            => await TmdbClient.TestAsync(request.TmdbApiKey, Ct).ConfigureAwait(false);

        public object Get(GetOwnedLibraries request)
            => EmbyLibrary.GetLibraries()
                .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
                .Select(l => new LibraryDto { Id = l.Id, Name = l.Name, CollectionType = l.CollectionType })
                .ToList();

        // ---------------------------------------------------------------- Radarr / Sonarr via Seerr

        public async Task<object> Get(GetSeerrServices request)
        {
            var dto = new ServicesDto();
            if (!SeerrClient.IsConfigured(out var problem))
            {
                dto.Error = problem;
                return dto;
            }
            dto.Radarr = await SafeService("radarr").ConfigureAwait(false);
            dto.Sonarr = await SafeService("sonarr").ConfigureAwait(false);
            dto.Ok = dto.Radarr.Error == null || dto.Sonarr.Error == null;
            return dto;
        }

        private async Task<ArrServiceDto> SafeService(string arr)
        {
            try
            {
                return await SeerrClient.GetServiceAsync(arr, Ct).ConfigureAwait(false);
            }
            catch (SeerrException ex)
            {
                return new ArrServiceDto { App = arr == "radarr" ? "Radarr" : "Sonarr", Error = ex.Message };
            }
        }

        // ---------------------------------------------------------------- TMDB reference data

        public async Task<object> Get(GetTmdbOptions request)
        {
            var dto = new TmdbOptionsDto();
            var type = request.MediaType == "tv" ? "tv" : "movie";
            var region = (request.Region ?? "US").Trim().ToUpperInvariant();
            if (region.Length != 2 || !region.All(char.IsLetter)) region = "US";
            try
            {
                var genres = await TmdbClient.GetReferenceAsync("/genre/" + type + "/list?language=en", Ct).ConfigureAwait(false);
                foreach (var g in Json.Arr(genres, "genres") ?? new List<object>())
                {
                    var id = Json.Int(g, "id");
                    var name = Json.Str(g, "name");
                    if (id.HasValue && name != null) dto.Genres.Add(new OptionDto { Id = id.Value.ToString(CultureInfo.InvariantCulture), Name = name });
                }
                dto.Genres = dto.Genres.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();

                var providers = await TmdbClient.GetReferenceAsync("/watch/providers/" + type + "?watch_region=" + region, Ct).ConfigureAwait(false);
                dto.Providers = (Json.Arr(providers, "results") ?? new List<object>())
                    .Select(p => new
                    {
                        Id = Json.Int(p, "provider_id"),
                        Name = (Json.Str(p, "provider_name") ?? "").Trim(),
                        Rank = Json.Int(Json.Obj(p, "display_priorities"), region) ?? Json.Int(p, "display_priority") ?? 9999
                    })
                    .Where(p => p.Id.HasValue && p.Name.Length > 0)
                    .OrderBy(p => p.Rank).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(p => new OptionDto { Id = p.Id.Value.ToString(CultureInfo.InvariantCulture), Name = p.Name })
                    .ToList();

                var regions = await TmdbClient.GetReferenceAsync("/watch/providers/regions", Ct).ConfigureAwait(false);
                dto.Regions = (Json.Arr(regions, "results") ?? new List<object>())
                    .Select(r => new OptionDto { Id = Json.Str(r, "iso_3166_1"), Name = Json.Str(r, "english_name") })
                    .Where(r => r.Id != null && r.Name != null)
                    .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

                var languages = await TmdbClient.GetReferenceAsync("/configuration/languages", Ct).ConfigureAwait(false);
                dto.Languages = ((languages as List<object>) ?? new List<object>())
                    .Select(l => new OptionDto { Id = Json.Str(l, "iso_639_1"), Name = Json.Str(l, "english_name") })
                    .Where(l => l.Id != null && !string.IsNullOrWhiteSpace(l.Name) && l.Name != "No Language")
                    .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList();
                // Age ratings for that country, lowest first (movies are filtered by TMDB, shows by the plugin).
                var certs = await TmdbClient.GetReferenceAsync("/certification/" + type + "/list", Ct).ConfigureAwait(false);
                dto.Certifications = (Json.Arr(Json.Obj(certs, "certifications"), region) ?? new List<object>())
                    .Select(c => new CertificationDto
                    {
                        Code = (Json.Str(c, "certification") ?? "").Trim(),
                        Meaning = Json.Str(c, "meaning") ?? "",
                        Order = Json.Int(c, "order") ?? 0
                    })
                    .Where(c => c.Code.Length > 0)
                    .OrderBy(c => c.Order).ThenBy(c => c.Code, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                dto.Ok = true;
            }
            catch (SeerrException ex)
            {
                dto.Error = ex.Message;
            }
            return dto;
        }

        // ---------------------------------------------------------------- preview

        public async Task<object> Post(PreviewList request)
        {
            var dto = new PreviewDto();
            var source = (request.Source ?? "").Trim();
            var type = (request.MediaType ?? "both").ToLowerInvariant();
            try
            {
                var items = new List<DiscoverItemDto>();
                if (source.StartsWith("seerr:", StringComparison.OrdinalIgnoreCase))
                {
                    var kind = source.Substring(6).ToLowerInvariant();
                    if (Array.IndexOf(Plugin.SeerrKinds, kind) < 0) throw new SeerrException("Unknown Seerr list.");
                    if (!SeerrClient.IsConfigured(out var problem)) throw new SeerrException(problem);
                    foreach (var t in type == "both" ? new[] { "movie", "tv" } : new[] { type })
                    {
                        var page = await SeerrClient.GetTypedDiscoverAsync(t, kind, 1, Ct).ConfigureAwait(false);
                        EmbyLibrary.Annotate(page.Items);
                        items.AddRange(page.Items);
                    }
                    dto.Name = "Seerr " + kind;
                    dto.ResolvedSource = source;
                }
                else
                {
                    var spec = TmdbClient.ParseSource(source);
                    if (spec == null) throw new SeerrException("That is not a TMDB list link, list id, or api.themoviedb.org/3 link.");
                    if (!TmdbClient.IsConfigured) throw new SeerrException("Add a TMDB API key on the Connection tab first.");
                    var list = await TmdbClient.GetListAsync(spec, request.MaxItems, Ct).ConfigureAwait(false);
                    items = list.Items.Where(i => type == "both" || i.MediaType == type).ToList();
                    dto.RemovedByRating = list.RemovedByRating;
                    await DiscoverChannelBase.AnnotateWithSeerr(items, Ct).ConfigureAwait(false);
                    dto.Name = list.Name;
                    dto.ResolvedSource = spec.Query != null
                        ? "https://api.themoviedb.org/3" + Uri.UnescapeDataString(TmdbClient.ExpandDates(spec.Query))
                        : "https://www.themoviedb.org/list/" + spec.Id;
                }

                var cfg = Plugin.Config;
                var shown = items.Take(24).ToList();
                if (DiscoverChannelBase.NeedsTitleDetails(cfg) && SeerrClient.IsConfigured(out _))
                    await TitleIndex.AnnotateAsync(shown, Ct).ConfigureAwait(false);
                dto.Movies = items.Count(i => i.MediaType == "movie");
                dto.Shows = items.Count(i => i.MediaType == "tv");
                dto.HiddenAsOwned = cfg.HideOwned ? items.Count(DiscoverChannelBase.IsOwned) : 0;
                dto.Items = shown.Select(i => new PreviewItemDto
                {
                    Title = i.Title,
                    Year = i.Year,
                    MediaType = i.MediaType,
                    PosterPath = i.PosterPath,
                    Status = i.InEmbyLibrary ? "In Emby" : i.StatusLabel,
                    Hidden = cfg.HideOwned && DiscoverChannelBase.IsOwned(i),
                    OwnedReason = DiscoverChannelBase.OwnedReason(i),
                    HasTrailer = i.HasTrailer,
                    Scores = TitleIndex.ScoresText(i.Info, i.Info != null ? i.VoteAverage : null)
                }).ToList();
                dto.Ok = true;
            }
            catch (SeerrException ex)
            {
                dto.Error = ex.Message;
            }
            return dto;
        }
    }
}

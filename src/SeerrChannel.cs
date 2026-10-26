using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using Newtonsoft.Json.Linq;

namespace EmbySeerrNative
{
    // Native Emby channel. Items deliberately have no playable stream:
    // Seerr discovery metadata is not media, and selecting an item must never trigger a request.
    public sealed class SeerrChannel : IChannel
    {
        public string Name => "Seerr Discover";
        public string Description => "Trending, popular and upcoming titles from Seerr";
        public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

        public IEnumerable<ImageType> GetSupportedChannelImages() => new ImageType[0];
        public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
            => Task.FromResult(new DynamicImageResponse { HasImage = false });

        public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
        {
            var items = new List<ChannelItemInfo>();
            if (string.IsNullOrEmpty(query.FolderId))
            {
                AddFolder(items, "trending", "Trending");
                AddFolder(items, "movies", "Popular Movies");
                AddFolder(items, "tv", "Popular TV");
                AddFolder(items, "upcoming-movies", "Upcoming Movies");
                AddFolder(items, "upcoming-tv", "Upcoming TV");
                return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
            }

            var endpoints = new Dictionary<string, string> {
                ["trending"] = "/api/v1/discover/trending",
                ["movies"] = "/api/v1/discover/movies",
                ["tv"] = "/api/v1/discover/tv",
                ["upcoming-movies"] = "/api/v1/discover/movies/upcoming",
                ["upcoming-tv"] = "/api/v1/discover/tv/upcoming"
            };
            if (!endpoints.TryGetValue(query.FolderId, out var endpoint))
                return new ChannelItemResult { Items = items, TotalRecordCount = 0 };

            var config = Plugin.Instance.Configuration;
            if (string.IsNullOrWhiteSpace(config.ApiKey))
                throw new InvalidOperationException("Set the Seerr API key in the plugin configuration before refreshing the channel.");
            var page = Math.Max(1, (query.StartIndex ?? 0) / Math.Max(1, config.PageSize) + 1);
            var uri = config.SeerrUrl.TrimEnd('/') + endpoint + "?page=" + page;
            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) })
            using (var request = new HttpRequestMessage(HttpMethod.Get, uri))
            {
                request.Headers.Add("X-Api-Key", config.ApiKey);
                using (var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    var obj = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    foreach (var media in (JArray)(obj["results"] ?? new JArray()))
                    {
                        var type = (string)media["mediaType"] ?? (query.FolderId.Contains("tv") ? "tv" : "movie");
                        if (type != "movie" && type != "tv") continue;
                        var tmdbId = (string)media["id"];
                        var title = (string)media["title"] ?? (string)media["name"];
                        if (string.IsNullOrWhiteSpace(tmdbId) || string.IsNullOrWhiteSpace(title)) continue;
                        var date = (string)media["releaseDate"] ?? (string)media["firstAirDate"];
                        int year;
                        var yearValue = date != null && date.Length >= 4 && int.TryParse(date.Substring(0,4), out year) ? (int?)year : null;
                        var status = (int?)media["mediaInfo"]?["status"];
                        var poster = (string)media["posterPath"];
                        var description = (string)media["overview"] ?? "";
                        if (status == 5) description = "AVAILABLE IN LIBRARY — " + description;
                        else if (status == 2 || status == 3) description = "REQUESTED / PROCESSING — " + description;
                        // Emby channel tiles are metadata-only. These cannot act as request buttons.
                        items.Add(new ChannelItemInfo {
                            Id = "seerr:" + type + ":" + tmdbId,
                            Name = title,
                            Type = ChannelItemType.Media,
                            MediaType = ChannelMediaType.Video,
                            ContentType = ChannelMediaContentType.Movie,
                            Overview = description,
                            ProductionYear = yearValue,
                            ImageUrl = !string.IsNullOrEmpty(poster) ? "https://image.tmdb.org/t/p/w500" + poster : null
                        });
                    }
                    return new ChannelItemResult { Items = items, TotalRecordCount = (int?)obj["totalResults"] ?? items.Count };
                }
            }
        }

        private static void AddFolder(List<ChannelItemInfo> items, string id, string title)
            => items.Add(new ChannelItemInfo { Id = id, Name = title, Type = ChannelItemType.Folder });
    }
}

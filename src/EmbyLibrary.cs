using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace EmbySeerrNative
{
    /// <summary>
    /// Optional cross-check against the Emby library: a title that Emby already has (matched by
    /// TMDB id) counts as owned even if Seerr has not synced it yet.
    ///
    /// Only items that live inside a real Emby library (a CollectionFolder) count. Channel items —
    /// including this plugin's own Discover items, which Emby's metadata refresh can tag with a TMDB
    /// id — are ignored, so a Discover title can never "find itself" in the library.
    /// </summary>
    internal static class EmbyLibrary
    {
        public static ILibraryManager LibraryManager { get; set; }
        public static ILogger Logger { get; set; }

        /// <summary>A library item that matched a TMDB id.</summary>
        public sealed class Hit
        {
            public string Library;
            public string ItemName;
            public int? Year;
        }

        public sealed class LibraryInfo
        {
            public string Id;            // VirtualFolderInfo.ItemId
            public string Name;
            public string CollectionType; // movies | tvshows | mixed (null) | ...
            public long InternalId;
        }

        /// <summary>The server's libraries that can hold movies or shows.</summary>
        public static List<LibraryInfo> GetLibraries()
        {
            var result = new List<LibraryInfo>();
            var lm = LibraryManager;
            if (lm == null) return result;
            try
            {
                foreach (var vf in lm.GetVirtualFolders() ?? new List<VirtualFolderInfo>())
                {
                    var type = (vf.CollectionType ?? "").ToLowerInvariant();
                    if (type.Length > 0 && type != "movies" && type != "tvshows" && type != "mixed" && type != "homevideos") continue;
                    var item = Resolve(lm, vf.ItemId);
                    if (item == null) continue;
                    result.Add(new LibraryInfo { Id = vf.ItemId, Name = vf.Name, CollectionType = type.Length == 0 ? "mixed" : type, InternalId = item.InternalId });
                }
            }
            catch (Exception ex)
            {
                Logger?.ErrorException("Discover: could not list Emby libraries", ex);
            }
            return result;
        }

        private static BaseItem Resolve(ILibraryManager lm, string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            try
            {
                if (long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return lm.GetItemById(n);
                if (Guid.TryParse(id, out var g)) return lm.GetItemById(g);
            }
            catch (Exception ex)
            {
                Logger?.Debug("Discover: library {0} not found: {1}", id, ex.Message);
            }
            return null;
        }

        /// <summary>Internal ids of the libraries that count, or null for "every library".</summary>
        private static HashSet<long> AllowedLibraries()
        {
            var chosen = Plugin.Config.OwnedLibraryIds;
            if (chosen == null || chosen.Length == 0) return null;
            var wanted = new HashSet<string>(chosen.Where(s => !string.IsNullOrWhiteSpace(s)), StringComparer.OrdinalIgnoreCase);
            var ids = new HashSet<long>(GetLibraries().Where(l => wanted.Contains(l.Id)).Select(l => l.InternalId));
            return ids.Count == 0 ? null : ids; // all chosen libraries were deleted: fall back to all
        }

        public static Dictionary<int, Hit> FindTmdbIds(string mediaType, IEnumerable<int> tmdbIds)
        {
            var found = new Dictionary<int, Hit>();
            var lm = LibraryManager;
            if (lm == null || !Plugin.Config.CheckEmbyLibrary) return found;
            var ids = tmdbIds.Where(i => i > 0).Distinct().ToList();
            if (ids.Count == 0) return found;

            try
            {
                var key = MetadataProviders.Tmdb.ToString();
                var allowed = AllowedLibraries();
                var query = new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { mediaType == "tv" ? "Series" : "Movie" },
                    Recursive = true,
                    IsVirtualItem = false,
                    AnyProviderIdEquals = ids
                        .Select(i => new KeyValuePair<string, string>(key, i.ToString(CultureInfo.InvariantCulture)))
                        .ToList()
                };
                foreach (var item in lm.GetItemList(query))
                {
                    if (!int.TryParse(item.GetProviderId(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || found.ContainsKey(id)) continue;
                    if (!string.IsNullOrEmpty(item.GetProviderId(DiscoverIds.ProviderKey))) continue; // our own channel item
                    var library = LibraryOf(lm, item, allowed);
                    if (library == null) continue; // a channel item or a library that does not count
                    found[id] = new Hit { Library = library, ItemName = item.Name, Year = item.ProductionYear };
                }
            }
            catch (Exception ex)
            {
                // Never let a library query break discovery.
                Logger?.ErrorException("Discover: Emby library lookup failed", ex);
            }
            return found;
        }

        /// <summary>Name of the real library the item belongs to, or null.</summary>
        private static string LibraryOf(ILibraryManager lm, BaseItem item, HashSet<long> allowed)
        {
            Folder[] folders;
            try { folders = lm.GetCollectionFolders(item); }
            catch { return null; }
            foreach (var f in folders ?? new Folder[0])
            {
                // Channels are collection-like folders too, but not CollectionFolder.
                if (!(f is CollectionFolder)) continue;
                if (allowed != null && !allowed.Contains(f.InternalId)) continue;
                return f.Name;
            }
            return null;
        }

        /// <summary>Marks items that already exist in the Emby library.</summary>
        public static void Annotate(IList<DiscoverItemDto> items)
        {
            if (items == null || items.Count == 0 || !Plugin.Config.CheckEmbyLibrary) return;
            foreach (var type in new[] { "movie", "tv" })
            {
                var subset = items.Where(i => i.MediaType == type).ToList();
                if (subset.Count == 0) continue;
                var found = FindTmdbIds(type, subset.Select(i => i.TmdbId));
                foreach (var item in subset)
                {
                    if (!found.TryGetValue(item.TmdbId, out var hit)) continue;
                    ApplyLibraryHit(item, hit);
                }
            }
        }

        public static void ApplyLibraryHit(DiscoverItemDto item, Hit hit = null)
        {
            item.InEmbyLibrary = true;
            if (hit != null)
                item.LibraryMatch = "\"" + hit.ItemName + "\"" + (hit.Year.HasValue ? " (" + hit.Year.Value.ToString(CultureInfo.InvariantCulture) + ")" : "") +
                                    " in the Emby library \"" + hit.Library + "\"";
            if (item.MediaType == "movie")
            {
                item.Status = Availability.Available;
                item.CanRequest = false;
            }
            // For TV the Seerr season states still decide what can be requested.
            item.StatusLabel = item.MediaType == "tv" && item.Status != Availability.Available
                ? "In your Emby library"
                : Availability.Label(item.Status, true);
        }
    }
}

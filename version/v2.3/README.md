# Discover — Emby plugin v2.3.0

Two native Emby channels, **Discover Movies** and **Discover Shows**, filled with lists from Seerr and TMDB.
They work in every Emby app (TV, phone, web). Titles you already have are hidden, requested titles get a
**REQUESTED** poster badge, titles with a trailer get a **▶ TRAILER** badge, **Play** plays the trailer, each title shows
IMDb / Rotten Tomatoes scores with its cast and crew, and — optionally — adding a title to **Favourites (♥)**
requests it through Seerr as that person's own Seerr account.

One DLL, no extra container, no third-party runtime dependencies. Plugin GUID `ed685bed-a759-4d9a-9482-3987767c3e4c`
is unchanged, so it installs over every earlier version and migrates their settings.

See **CHANGELOG.md** for what changed and **REVIEW.md** for the original code review.

## Install / upgrade

1. Back up Emby's `plugins` folder (inside Emby's config/data folder; at least `EmbySeerrNative.dll` and
   `configurations/EmbySeerrNative.xml`).
2. Stop Emby, copy `dist/EmbySeerrNative.dll` over the old one in that `plugins` folder, start Emby.
3. Dashboard → **Plugins** → "Discover" should show **2.3.0.0**. Open its settings.
4. **Connection** tab: check the Seerr address and API key, add the Seerr address you use in your browser (for the
   *Open Seerr* button) and your TMDB key. **Test Seerr connection** / **Test TMDB connection**, then **Save**.
5. **Channels & Lists**: your lists are already there (2.0 lists set to *Both* now appear in both tables). Add more with
   **+ Add a movie list** / **+ Add a show list**.
6. **File Management**: pick the folders and tags for each destination and which one is the default.
7. Save. The channels refresh on their own within a minute.

**Rollback:** stop Emby, restore the backed-up DLL *and* `configurations/EmbySeerrNative.xml`, start Emby
(2.1+ rewrites the settings file: mixed lists are split, manual user links are dropped, and 2.2 switches shows to playable items).

## The settings page

| Tab | What it covers |
|---|---|
| **Connection** | Seerr address + API key, Seerr address for the *Open Seerr* button, TMDB key, timeout/cache, **Test Seerr connection**, **Test TMDB connection** |
| **Users** | Who may request, admin fallback, **Check user links** table |
| **Channels & Lists** | Channel names; a Movies table and a Shows table with ↑ ↓ order, on/off, **Preview**, **Edit**, **Remove**; **+ Add** (builder) |
| **File Management** | Two destinations (folder + tags for movies and shows, loaded from Seerr's Radarr/Sonarr), the default destination, Favourite-to-request |
| **Display** | Hide owned titles, Emby-library check and which libraries count, scores / cast / crew and the age-rating country, REQUESTED / PARTIAL / TRAILER badges, trailers, shows as playable items (default) or series folders |
| **Help** | How it works, date placeholders, writing your own TMDB link, troubleshooting |

### The list builder

Pick a kind of list, then fill in the form — the TMDB link is generated for you and shown underneath.

* **From Seerr:** Trending, Popular, Upcoming.
* **From TMDB with filters:** Popular, Top rated, New releases, Coming soon, On air now (shows), On streaming services.
  Filters: release/air-date window (moves forward by itself), how titles are picked (popularity, rating, votes,
  newest, oldest), minimum votes, minimum TMDB score, country + streaming services (loaded live from TMDB, ordered by
  popularity in that country), subscription-only, lowest/highest age rating for that country, genres to include /
  leave out, leave out anime, original language.
* **From TMDB:** Trending, any public TMDB list (paste its address), or a custom `api.themoviedb.org/3/…` link.

**Preview titles** shows what the list returns today, how many are already owned (hidden — hover a poster to see why),
which have a trailer, how many shows the age-rating filter left out, and the link with the dates filled in.

**Age ratings:** TMDB filters movies by rating itself (`certification_country`, `certification.gte/.lte`). TMDB has no
such filter for shows, so for `/discover/tv` links Discover takes those parameters out of the request and checks each
show's TMDB content rating itself (cached for a week); `include_unrated=true` keeps shows without a rating in that country.

Titles inside a folder are ordered by Emby (normally alphabetically); a list's sort decides *which* titles are included.

### Destinations

Each destination has a Radarr folder + tags (movies) and a Sonarr folder + tags (shows). The dropdowns come from
Seerr's default Radarr and Sonarr servers, so no Radarr/Sonarr API keys are needed. *Seerr's default* leaves that part
to Seerr. Requests made from Emby (♥) use the **default destination**; requests made in Seerr itself use Seerr's own
choices. Tags replace Seerr's default tags for that request; Seerr override rules can still change folder or tags.

## Verification status

| Item | Status |
|---|---|
| Compiles against the Emby Server 4.10.2.0 assemblies (incl. SkiaSharp) | **Verified** |
| Emby runs a metadata refresh on channel items (the cause of the false "in library" matches) | **Checked in Emby 4.10.2's code** (ChannelManager queues a refresh for each item) |
| Seerr and TMDB behaviour (endpoints, payloads, statuses, `X-API-User`, services, anime keyword 210024) | **Checked against Seerr's source** |
| Channels, lists, TMDB links, date placeholders, destinations, preview, folders/tags via Seerr, settings migration, XML save/load, library check, connection tests, age ratings, trailers, badges | **Tested** — 219 automated checks against mock Seerr + mock TMDB + a fake Emby library |
| Settings page (tabs, both list tables, builder incl. ratings/score, tests, libraries, save) | **Tested** — 28 checks in headless Chromium with a stand-in for Emby's ApiClient |
| TMDB has no age-rating filter for `/discover/tv` | **Checked** against TMDB's API reference; handled by the plugin's own check |
| IMDb / Rotten Tomatoes from Seerr's `ratingscombined` and `tv/{id}/ratings` endpoints | **Checked against Seerr's source**; Rotten Tomatoes matching is Seerr's (by title and year) |
| Rotten Tomatoes score shown with Emby's tomato icon (stored after Emby saves the item) | **Not tested** — needs your server |
| Settings page inside your Emby dashboard; badges/trailers/favourites on your server | **Not tested** here — needs your server |

## Privacy

The source contains no server addresses, user names, API keys or other personal data. Your Seerr URL, API keys,
destinations and lists live only in Emby's settings file (`plugins/configurations/EmbySeerrNative.xml`) on your server,
which is not part of this source. Do not commit that file. Release builds contain no debug symbols or build paths.

## Building it yourself

```bash
cd src
dotnet build -c Release                       # NuGet Emby SDK + SkiaSharp (compile only) -> netstandard2.0
dotnet build -c Release -p:EmbyTargetFramework=net8.0 -p:EmbySystemDir=/path/to/emby/system   # against your server's DLLs
```
The supplied DLL was built the second way against the official 4.10.2.0 package. Only `EmbySeerrNative.dll` is installed.

## Troubleshooting

| Symptom | What to check |
|---|---|
| Channels out of date | **⟳ Refresh channels** at the top of the settings page |
| Channel or folder empty | Connection → **Test Seerr connection** / **Test TMDB connection**, **Preview** on the list; Dashboard → Logs → `embyserver.txt`, search `Discover` |
| A title is hidden as owned but you don't have it | **Preview** the list, hover the *owned* poster: it names the library item that matched (fix a wrong match with *Identify* in Emby) or says Seerr marks it available |
| TMDB list empty or wrong | **Preview** on that list shows TMDB's error or what it returns |
| Folders/tags dropdowns empty | File Management → **Reload folders & tags from Seerr**; Seerr must have Radarr/Sonarr configured |
| ♥ does nothing | File Management → Favourite-to-request on; Users → **Check user links** (the person must have a Seerr account from their Emby login) |
| Side-menu icon is a folder | Emby's web app uses that icon for every channel; a plugin cannot change it |
| Request in the wrong place | File Management destination + any Seerr override rules; check the item in Radarr/Sonarr |
| Settings page blank | Send the browser console output (the page uses Emby's `data-controller` loader) |
| "Today" changes at the wrong hour | Set `TZ=<your time zone>` (e.g. `TZ=Europe/London`) in the Emby container's environment |

## Source layout

```
src/
  Plugin.cs                    plugin, configuration (lists, destinations), 1.x → 2.0 migration
  DiscoverChannels.cs          the two channels, folder resolution, trailers, refresh trigger
  SeerrClient.cs               Seerr API (discover, details, users, media map, Radarr/Sonarr services)
  TmdbClient.cs                TMDB lists and links, date placeholders, reference data
  RequestService.cs            request rules + destinations
  UserLinker.cs                Emby user → Seerr user (automatic)
  FavoriteRequestEntryPoint.cs ♥-to-request, unfavourite, notifications, start-up wiring
  BadgeEnhancer.cs             REQUESTED / PARTIAL / ▶ TRAILER poster badges (SkiaSharp)
  EmbyLibrary.cs               "already in Emby" check by TMDB id (real libraries only)
  Trailers.cs                  which titles have a trailer (show or season), cached
  SeerrApi.cs                  admin-only endpoints for the settings page
  Models.cs, Json.cs
  Configuration/config.html, config.js   the tabbed settings page
  Images/, Fonts/
```

define([], function () {
    'use strict';

    var pluginId = 'ed685bed-a759-4d9a-9482-3987767c3e4c';
    var TMDB = 'https://api.themoviedb.org/3';
    var POSTER = 'https://image.tmdb.org/t/p/w154';
    var ANIME_KEYWORD = 210024; // TMDB "anime" keyword (same constant Seerr uses)

    // ------------------------------------------------------------------ list templates
    var TEMPLATES = [
        { id: 'seerr-trending', group: 'From Seerr', label: 'Trending', types: ['movie', 'tv'],
          help: 'Seerr\'s Trending list (TMDB trending this week).' },
        { id: 'seerr-popular', group: 'From Seerr', label: 'Popular', types: ['movie', 'tv'],
          help: 'Seerr\'s Popular list.' },
        { id: 'seerr-upcoming', group: 'From Seerr', label: 'Upcoming', types: ['movie', 'tv'],
          help: 'Seerr\'s Upcoming list.' },
        { id: 'popular', group: 'From TMDB (with filters)', label: 'Popular', types: ['movie', 'tv'], filters: true, sort: true, window: 'none',
          help: 'The most popular titles on TMDB, narrowed down by the filters below.' },
        { id: 'toprated', group: 'From TMDB (with filters)', label: 'Top rated', types: ['movie', 'tv'], filters: true, sort: false, window: 'none', minVotes: 300,
          help: 'Highest rated first. "Minimum votes" keeps out titles that only a handful of people rated.' },
        { id: 'new', group: 'From TMDB (with filters)', label: 'New releases', types: ['movie', 'tv'], filters: true, sort: true, window: 'last1m',
          help: 'Movies released, or shows that premiered, in the chosen window. The dates move forward by themselves.' },
        { id: 'upcoming', group: 'From TMDB (with filters)', label: 'Coming soon', types: ['movie', 'tv'], filters: true, sort: true, window: 'next1m',
          help: 'Movies releasing, or shows premiering, in the chosen window.' },
        { id: 'onair', group: 'From TMDB (with filters)', label: 'On air now', types: ['tv'], filters: true, sort: true, window: 'around',
          help: 'Shows with an episode airing in the chosen window — ongoing and returning series.' },
        { id: 'streaming', group: 'From TMDB (with filters)', label: 'On streaming services', types: ['movie', 'tv'], filters: true, sort: true, window: 'none', needsProviders: true,
          help: 'Titles included with the streaming services you tick, in the chosen country.' },
        { id: 'trending', group: 'From TMDB', label: 'Trending', types: ['movie', 'tv'], trend: true,
          help: 'TMDB\'s trending list. Filters do not apply to trending.' },
        { id: 'list', group: 'From TMDB', label: 'A TMDB list', types: ['movie', 'tv'], listUrl: true,
          help: 'Any public list on themoviedb.org. Paste its address (or just its number). Only the list\'s titles of this channel\'s type are used, so a mixed list can be added to both channels.' },
        { id: 'custom', group: 'From TMDB', label: 'Custom TMDB link', types: ['movie', 'tv'], customUrl: true,
          help: 'Any https://api.themoviedb.org/3/… link that returns titles. Date placeholders such as {today-1m} are allowed — see the Help tab.' }
    ];

    var WINDOWS = [
        ['none', 'Any time', null, null],
        ['last7', 'Last 7 days', '{today-7d}', '{today}'],
        ['last1m', 'Last month', '{today-1m}', '{today}'],
        ['last3m', 'Last 3 months', '{today-3m}', '{today}'],
        ['thisyear', 'This year so far', '{start_of_year}', '{today}'],
        ['lastyear', 'Last year', '{start_of_year-1y}', '{end_of_year-1y}'],
        ['next7', 'Next 7 days', '{today}', '{today+7d}'],
        ['next1m', 'Next month', '{today}', '{today+1m}'],
        ['next3m', 'Next 3 months', '{today}', '{today+3m}'],
        ['around', 'Last month to next month', '{today-1m}', '{today+1m}'],
        ['custom', 'Custom…', null, null]
    ];

    var SORTS = [['popularity', 'Most popular'], ['rating', 'Highest rated'], ['votes', 'Most votes'], ['newest', 'Newest first'], ['oldest', 'Oldest first']];

    function api() { return globalThis.ApiClient || window.ApiClient; }

    function tpl(id) { return TEMPLATES.filter(function (t) { return t.id === id; })[0] || TEMPLATES[0]; }

    function h(tag, attrs, children) {
        var e = document.createElement(tag);
        if (attrs) Object.keys(attrs).forEach(function (k) {
            var v = attrs[k];
            if (v == null || v === false) return;
            if (k === 'text') e.textContent = v;
            else if (k === 'class') e.className = v;
            else if (k === 'value') e.value = v;
            else if (k === 'checked') e.checked = !!v;
            else if (k.slice(0, 2) === 'on') e.addEventListener(k.slice(2), v);
            else e.setAttribute(k, v === true ? '' : v);
        });
        (children || []).forEach(function (c) { if (c != null) e.appendChild(typeof c === 'string' ? document.createTextNode(c) : c); });
        return e;
    }

    function intOr(value, fallback, min, max) {
        var n = parseInt(value, 10);
        if (isNaN(n)) n = fallback;
        return Math.max(min, Math.min(max, n));
    }

    function newId() { return 'l' + Date.now().toString(36) + Math.random().toString(36).slice(2, 6); }

    function describeError(err) {
        if (!err) return 'Unknown error';
        if (typeof err === 'string') return err;
        if (err.status) return 'HTTP ' + err.status + (err.statusText ? ' ' + err.statusText : '');
        return err.message || String(err);
    }

    function postJson(path, body) {
        return api().ajax({ type: 'POST', url: api().getUrl(path), data: JSON.stringify(body), contentType: 'application/json', dataType: 'json' });
    }

    function defaultLists() {
        var out = [];
        ['movie', 'tv'].forEach(function (type) {
            ['trending', 'popular', 'upcoming'].forEach(function (kind) {
                out.push({ Id: (type === 'movie' ? 'm-' : 't-') + kind, Name: '', Enabled: true, Source: 'seerr:' + kind, MediaType: type, MaxItems: 0,
                           BuilderJson: JSON.stringify({ template: 'seerr-' + kind, type: type }) });
            });
        });
        return out;
    }

    // ------------------------------------------------------------------ link generation

    function windowRange(st) {
        if (st.window === 'custom') return [st.from || null, st.to || null];
        var w = WINDOWS.filter(function (x) { return x[0] === st.window; })[0];
        return w && w[2] ? [w[2], w[3]] : null;
    }

    function buildSource(st) {
        var t = tpl(st.template);
        if (t.id.indexOf('seerr-') === 0) return 'seerr:' + t.id.substring(6);
        if (t.listUrl) return (st.listUrl || '').trim();
        if (t.customUrl) return (st.customUrl || '').trim();
        var type = st.type === 'tv' ? 'tv' : 'movie';
        if (t.trend) return TMDB + '/trending/' + (st.type === 'both' ? 'all' : type) + '/' + (st.trendWindow === 'day' ? 'day' : 'week');

        var releaseField = type === 'movie' ? 'primary_release_date' : 'first_air_date';
        var dateField = t.id === 'onair' ? 'air_date' : releaseField;
        var sort = t.id === 'toprated' ? 'rating' : (st.sort || 'popularity');
        var sortMap = { popularity: 'popularity.desc', rating: 'vote_average.desc', votes: 'vote_count.desc',
                        newest: releaseField + '.desc', oldest: releaseField + '.asc' };
        var p = ['include_adult=false', 'sort_by=' + sortMap[sort]];
        var w = windowRange(st);
        if (w) {
            if (w[0]) p.push(dateField + '.gte=' + w[0]);
            if (w[1]) p.push(dateField + '.lte=' + w[1]);
        }
        var minVotes = parseInt(st.minVotes, 10);
        if (isNaN(minVotes)) minVotes = t.minVotes || (sort === 'rating' ? 100 : 0);
        if (minVotes > 0) p.push('vote_count.gte=' + minVotes);
        var minScore = parseFloat(st.minScore);
        if (!isNaN(minScore) && minScore > 0) p.push('vote_average.gte=' + Math.min(10, minScore));
        if (st.certMin || st.certMax) {
            p.push('certification_country=' + (st.region || 'US'));
            if (st.certMin) p.push('certification.gte=' + encodeURIComponent(st.certMin));
            if (st.certMax) p.push('certification.lte=' + encodeURIComponent(st.certMax));
            if (type === 'tv' && st.keepUnrated) p.push('include_unrated=true');
        }
        if (st.providers && st.providers.length) {
            p.push('watch_region=' + (st.region || 'US'));
            p.push('with_watch_providers=' + st.providers.join('|'));
            if (st.subscriptionOnly !== false) p.push('with_watch_monetization_types=flatrate');
        }
        if (st.genresIn && st.genresIn.length) p.push('with_genres=' + st.genresIn.join('|'));
        if (st.genresOut && st.genresOut.length) p.push('without_genres=' + st.genresOut.join(','));
        if (st.noAnime) p.push('without_keywords=' + ANIME_KEYWORD);
        if (st.language) p.push('with_original_language=' + st.language);
        return TMDB + '/discover/' + type + '?' + p.join('&');
    }

    /** movie | tv | both from an api.themoviedb.org/3 link, or null when it cannot tell. */
    function inferType(url) {
        var m = /api\.themoviedb\.org\/3\/([a-z_]+)(?:\/([a-z_]+))?/i.exec(url || '');
        if (!m) return null;
        var a = m[1].toLowerCase(), b = (m[2] || '').toLowerCase();
        if (a === 'movie' || ((a === 'discover' || a === 'trending' || a === 'search') && b === 'movie')) return 'movie';
        if (a === 'tv' || ((a === 'discover' || a === 'trending' || a === 'search') && b === 'tv')) return 'tv';
        if (a === 'trending' && b === 'all') return 'both';
        return null;
    }

    function autoName(st, providerNames) {
        var t = tpl(st.template);
        var T = st.type === 'tv' ? 'Shows' : 'Movies';
        var on = providerNames && providerNames.length ? ' on ' + providerNames.slice(0, 2).join(' & ') + (providerNames.length > 2 ? ' +' + (providerNames.length - 2) : '') : '';
        switch (t.id) {
            case 'popular': return 'Popular ' + T + on;
            case 'toprated': return 'Top Rated ' + T + on;
            case 'new': return 'New ' + T + on;
            case 'upcoming': return (st.type === 'tv' ? 'New Shows Coming Soon' : 'Movies Coming Soon') + on;
            case 'onair': return 'On Air Now' + on;
            case 'streaming': return (providerNames && providerNames.length ? providerNames.slice(0, 2).join(' & ') : 'Streaming') + ' ' + T;
            case 'trending': return 'Trending on TMDB';
            default: return '';
        }
    }

    function stateFromList(l) {
        var st = null;
        try { st = l.BuilderJson ? JSON.parse(l.BuilderJson) : null; } catch (e) { st = null; }
        if (!st || !st.template) {
            var src = (l.Source || '').trim();
            st = {};
            if (src.indexOf('seerr:') === 0) st.template = 'seerr-' + src.substring(6);
            else if (/api\.themoviedb\.org\/3\//i.test(src)) { st.template = 'custom'; st.customUrl = src; }
            else { st.template = 'list'; st.listUrl = src; }
        }
        st.type = l.MediaType === 'tv' ? 'tv' : 'movie';
        st.name = l.Name || '';
        st.nameAuto = !l.Name;
        st.maxItems = l.MaxItems || '';
        st.enabled = l.Enabled !== false;
        st.id = l.Id;
        return st;
    }

    function describeList(l) {
        var st = stateFromList(l);
        var t = tpl(st.template);
        if (t.id.indexOf('seerr-') === 0) return 'Seerr · ' + t.label;
        if (t.listUrl) return 'TMDB list · ' + (l.Source || '');
        if (t.customUrl) return 'Custom link · ' + (l.Source || '');
        return 'TMDB · ' + t.label;
    }

    // ================================================================== controller
    return function (view) {
        var cfg = null;
        var lists = [];
        var services = null;
        var optionsCache = {};
        var builder = null;      // { st, type, providerNames: {} }
        var libraries = null;    // [{ Id, Name, CollectionType }] from the server
        var ownedLibraryIds = [];
        var loaded = false;
        var dirty = false;

        function el(id) { return view.querySelector('#' + id); }

        function status(target, kind, text) {
            var s = typeof target === 'string' ? el(target) : target;
            if (!s) return;
            s.className = 'sd-status ' + (kind || '');
            s.textContent = text || '';
        }

        function setSaveStatus(text) {
            el('sdSaveStatus').textContent = text || '';
            el('sdSaveStatusTop').textContent = text || '';
        }

        function markDirty() {
            dirty = true;
            setSaveStatus('Unsaved changes — click Save.');
        }

        // ---------------------------------------------------------------- tabs
        function showTab(name) {
            view.querySelectorAll('.sd-tab').forEach(function (b) { b.setAttribute('aria-selected', b.getAttribute('data-tab') === name ? 'true' : 'false'); });
            view.querySelectorAll('.sd-panel').forEach(function (p) { p.classList.toggle('sd-active', p.getAttribute('data-panel') === name); });
            try { sessionStorage.setItem('sdTab', name); } catch (e) { }
            if (name === 'files' && !services && loaded) loadServices();
            if (name === 'display' && !libraries && loaded) loadLibraries();
        }

        view.querySelectorAll('.sd-tab').forEach(function (b) {
            b.addEventListener('click', function () { showTab(b.getAttribute('data-tab')); });
        });

        // ---------------------------------------------------------------- load / save
        function updateOpenSeerr() {
            var v = el('sdSeerrPublicUrl').value.trim();
            el('sdOpenSeerr').href = v || (location.protocol + '//' + location.hostname + ':5055');
        }
        el('sdSeerrPublicUrl').addEventListener('input', updateOpenSeerr);

        function load() {
            api().getPluginConfiguration(pluginId).then(function (c) {
                cfg = c;
                el('sdSeerrUrl').value = c.SeerrUrl || 'http://seerr:5055';
                el('sdApiKey').value = c.ApiKey || '';
                el('sdSeerrPublicUrl').value = c.SeerrPublicUrl || '';
                el('sdTmdbKey').value = c.TmdbApiKey || '';
                el('sdTimeout').value = c.TimeoutSeconds || 15;
                el('sdCache').value = c.CacheMinutes == null ? 10 : c.CacheMinutes;
                el('sdAdminsOnly').checked = !!c.RequestsAdminsOnly;
                el('sdUnlinkedAdmins').checked = !!c.AllowUnlinkedAdmins;
                el('sdMoviesName').value = c.MoviesChannelName || 'Discover Movies';
                el('sdTvName').value = c.TvChannelName || 'Discover Shows';
                el('sdMaxItems').value = c.MaxItemsPerCategory || 60;
                el('sdMaxList').value = c.MaxItemsPerList || 200;
                el('sdFavorites').checked = !!c.EnableFavoriteRequests;
                el('sdUnfavorite').checked = c.UnfavoriteAfterRequest !== false;
                el('sdFavSeasons').value = c.FavoriteTvSeasons || 'all';
                el('sdHideOwned').checked = c.HideOwned !== false;
                el('sdCheckLibrary').checked = c.CheckEmbyLibrary !== false;
                el('sdTvAsSeries').checked = !!c.ShowTvAsSeries;
                el('sdBadgeRequested').checked = c.ShowRequestedBadge !== false;
                el('sdBadgePartial').checked = c.ShowPartialBadge !== false;
                el('sdBadgeTrailer').checked = c.ShowTrailerBadge !== false;
                el('sdTitleDetails').checked = c.AddTitleDetails !== false;
                el('sdAgeCountry').value = c.AgeRatingCountry || 'US';
                ownedLibraryIds = (c.OwnedLibraryIds || []).slice();
                el('sdPlayTrailers').checked = c.PlayTrailers !== false;
                lists = splitMixed((c.Lists || []).map(function (l) { return Object.assign({}, l); }));
                if (!c.Lists) lists = defaultLists();
                updateOpenSeerr();
                renderLists();
                renderDestinations();
                loaded = true;
                dirty = false;
                setSaveStatus('');
                var tab = 'connection';
                try { tab = sessionStorage.getItem('sdTab') || tab; } catch (e) { }
                showTab(tab);
            }, function (err) {
                setSaveStatus('Could not load the settings: ' + describeError(err));
            });
        }

        function collectDestination(key) {
            var d = Object.assign({}, (cfg && cfg[key === 'remote' ? 'RemoteDestination' : 'LocalDestination']) || {});
            var card = el('sdDest-' + key);
            if (!card) return d;
            d.Label = card.querySelector('[data-f=label]').value.trim() || (key === 'remote' ? 'Stream' : 'Download locally');
            ['Movie', 'Tv'].forEach(function (kind) {
                d[kind + 'RootFolder'] = card.querySelector('[data-f=' + kind + 'Root]').value.trim();
                var tagBox = card.querySelector('[data-f=' + kind + 'Tags]');
                if (tagBox.tagName === 'INPUT') d[kind + 'TagIds'] = tagBox.value.trim();
                else d[kind + 'TagIds'] = Array.prototype.map.call(tagBox.querySelectorAll('input:checked'), function (i) { return i.value; }).join(',');
            });
            return d;
        }

        function save(e) {
            e.preventDefault();
            if (!loaded) { setSaveStatus('Settings have not loaded yet.'); return false; }
            var url = el('sdSeerrUrl').value.trim();
            if (!/^https?:\/\/[^\s\/?#@]+(:\d+)?(\/[^\s?#]*)?$/i.test(url)) {
                showTab('connection');
                setSaveStatus('Enter the Seerr address as http://host:port, e.g. http://seerr:5055');
                return false;
            }
            if (builder) {
                showTab('lists');
                setSaveStatus('Finish or cancel the list you are editing first.');
                return false;
            }
            setSaveStatus('Saving…');
            var remote = collectDestination('remote');
            var local = collectDestination('local');
            var defaultDest = (view.querySelector('input[name=sdDefaultDest]:checked') || {}).value || 'remote';
            api().getPluginConfiguration(pluginId).then(function (c) {
                c.ConfigVersion = 4;
                c.SeerrUrl = url.replace(/\/+$/, '');
                c.ApiKey = el('sdApiKey').value.trim();
                c.SeerrPublicUrl = el('sdSeerrPublicUrl').value.trim().replace(/\/+$/, '');
                c.TmdbApiKey = el('sdTmdbKey').value.trim();
                c.TimeoutSeconds = intOr(el('sdTimeout').value, 15, 3, 120);
                c.CacheMinutes = intOr(el('sdCache').value, 10, 0, 240);
                c.RequestsAdminsOnly = el('sdAdminsOnly').checked;
                c.AllowUnlinkedAdmins = el('sdUnlinkedAdmins').checked;
                delete c.ManualUserLinks;
                c.MoviesChannelName = el('sdMoviesName').value.trim() || 'Discover Movies';
                c.TvChannelName = el('sdTvName').value.trim() || 'Discover Shows';
                c.MaxItemsPerCategory = intOr(el('sdMaxItems').value, 60, 20, 400);
                c.MaxItemsPerList = intOr(el('sdMaxList').value, 200, 20, 1000);
                c.Lists = lists;
                c.EnableFavoriteRequests = el('sdFavorites').checked;
                c.UnfavoriteAfterRequest = el('sdUnfavorite').checked;
                c.FavoriteTvSeasons = el('sdFavSeasons').value;
                c.DefaultDestination = defaultDest;
                c.RemoteDestination = remote;
                c.LocalDestination = local;
                c.HideOwned = el('sdHideOwned').checked;
                c.CheckEmbyLibrary = el('sdCheckLibrary').checked;
                c.ShowTvAsSeries = el('sdTvAsSeries').checked;
                c.ShowRequestedBadge = el('sdBadgeRequested').checked;
                c.ShowPartialBadge = el('sdBadgePartial').checked;
                c.ShowTrailerBadge = el('sdBadgeTrailer').checked;
                c.AddTitleDetails = el('sdTitleDetails').checked;
                c.AgeRatingCountry = /^[A-Za-z]{2}$/.test(el('sdAgeCountry').value.trim()) ? el('sdAgeCountry').value.trim().toUpperCase() : 'US';
                c.OwnedLibraryIds = collectLibraries();
                c.PlayTrailers = el('sdPlayTrailers').checked;
                cfg = c;
                return api().updatePluginConfiguration(pluginId, c);
            }).then(function () {
                dirty = false;
                optionsCache = {};
                setSaveStatus('Saved. The Discover channels refresh in a few seconds.');
            }, function (err) {
                setSaveStatus('Saving failed: ' + describeError(err));
            });
            return false;
        }
        el('sdForm').addEventListener('submit', save);
        el('sdSaveTop').addEventListener('click', save);
        view.querySelectorAll('#sdForm input, #sdForm select, #sdForm textarea').forEach(function (i) {
            i.addEventListener('change', function () { if (loaded && !(builder && builder.root && builder.root.contains(i))) markDirty(); });
        });

        // ---------------------------------------------------------------- connection test + users
        function renderUsers(rows) {
            var host = el('sdUsers');
            host.textContent = '';
            if (!rows || !rows.length) return;
            var table = h('table', { class: 'sd-table' }, [h('tr', {}, ['Emby user', 'Seerr account', 'Can request?'].map(function (t) { return h('th', { text: t }); }))]);
            rows.forEach(function (r) {
                var acct = h('td', {});
                if (r.SeerrUserId) {
                    acct.appendChild(document.createTextNode((r.SeerrUserName || '') + ' — Seerr user ' + r.SeerrUserId + ' '));
                    acct.appendChild(h('span', { class: 'sd-pill ' + (r.LinkSource === 'admin-fallback' ? 'warn' : 'good'),
                        text: r.LinkSource === 'auto' ? 'linked automatically' : 'owner fallback' }));
                } else acct.textContent = '—';
                table.appendChild(h('tr', {}, [h('td', { text: r.EmbyUserName + (r.EmbyIsAdmin ? ' (admin)' : '') }), acct, h('td', { text: r.Note ? 'No — ' + r.Note : 'Yes' })]));
            });
            host.appendChild(table);
        }

        function runTest(path, body, target, waitText) {
            status(target, 'info', waitText);
            postJson(path, body).then(function (r) {
                status(target, r.Ok ? 'ok' : 'err', r.Message || (r.Ok ? 'Connected.' : 'Failed.'));
            }, function (err) { status(target, 'err', 'Test failed: ' + describeError(err)); });
        }
        el('sdTestSeerr').addEventListener('click', function () {
            runTest('SeerrDiscover/Admin/TestSeerr', { SeerrUrl: el('sdSeerrUrl').value.trim(), ApiKey: el('sdApiKey').value.trim() },
                'sdTestSeerrResult', 'Contacting Seerr…');
        });
        el('sdTestTmdb').addEventListener('click', function () {
            runTest('SeerrDiscover/Admin/TestTmdb', { TmdbApiKey: el('sdTmdbKey').value.trim() }, 'sdTestTmdbResult', 'Contacting TMDB…');
        });
        el('sdCheckUsers').addEventListener('click', function () {
            var s = el('sdUsersStatus');
            if (!s) { s = h('div', { class: 'sd-status', id: 'sdUsersStatus' }); el('sdUsers').parentNode.insertBefore(s, el('sdUsers')); }
            status(s, 'info', 'Asking Seerr for its users…');
            api().getJSON(api().getUrl('SeerrDiscover/Admin/Status')).then(function (r) {
                status(s, r.Ok ? 'ok' : 'err', r.Message || (r.Ok ? 'Connected.' : 'Failed.'));
                renderUsers(r.Users);
            }, function (err) { status(s, 'err', 'Check failed: ' + describeError(err)); });
        });

        // ---------------------------------------------------------------- lists table
        function channelName(type) {
            return type === 'tv' ? (el('sdTvName').value.trim() || 'Discover Shows') : (el('sdMoviesName').value.trim() || 'Discover Movies');
        }

        function renderLists() {
            el('sdMoviesTitle').textContent = channelName('movie');
            el('sdTvTitle').textContent = channelName('tv');
            ['movie', 'tv'].forEach(renderListTable);
        }

        function renderListTable(type) {
            var body = el('sdListTable-' + type).querySelector('tbody');
            body.textContent = '';
            var mine = lists.filter(function (l) { return l.MediaType === type; });
            if (!mine.length) {
                body.appendChild(h('tr', {}, [h('td', { colspan: '5', class: 'sd-muted', text: 'No lists yet — this channel will be empty. Add a list or restore the defaults.' })]));
            }
            mine.forEach(function (l, i) {
                var st = stateFromList(l);
                var name = l.Name || autoName(st) || (st.template.indexOf('seerr-') === 0 ? tpl(st.template).label + ' ' + (type === 'tv' ? 'Shows' : 'Movies') : '(name from TMDB)');
                body.appendChild(h('tr', {}, [
                    h('td', { class: 'sd-actions', style: 'text-align:left' }, [
                        h('button', { type: 'button', class: 'sd-btn sd-small', title: 'Move up', text: '↑', disabled: i === 0, onclick: function () { move(l, -1); } }),
                        ' ',
                        h('button', { type: 'button', class: 'sd-btn sd-small', title: 'Move down', text: '↓', disabled: i === mine.length - 1, onclick: function () { move(l, 1); } })
                    ]),
                    h('td', {}, [h('input', { type: 'checkbox', checked: l.Enabled !== false, title: 'Show this folder',
                        onchange: function () { l.Enabled = this.checked; markDirty(); } })]),
                    h('td', {}, [h('strong', { text: name })]),
                    h('td', { class: 'sd-hide-sm' }, [h('div', { class: 'sd-src', text: describeList(l) })]),
                    h('td', { class: 'sd-actions' }, [
                        h('button', { type: 'button', class: 'sd-btn sd-small', text: 'Preview', onclick: function () { previewStandalone(l); } }), ' ',
                        h('button', { type: 'button', class: 'sd-btn sd-small', text: 'Edit', onclick: function () { openBuilder(l, type); } }), ' ',
                        h('button', { type: 'button', class: 'sd-btn sd-small sd-danger', text: 'Remove', onclick: function () {
                            if (!confirm('Remove "' + name + '" from ' + channelName(type) + '?')) return;
                            if (builder && builder.existing === l) closeBuilder();
                            lists.splice(lists.indexOf(l), 1); renderLists(); markDirty();
                        } })
                    ])
                ]));
            });
        }

        /** Moves a list up/down among the lists of the same channel. */
        function move(l, delta) {
            var same = lists.filter(function (x) { return x.MediaType === l.MediaType; });
            var j = same.indexOf(l) + delta;
            if (j < 0 || j >= same.length) return;
            var a = lists.indexOf(l), b = lists.indexOf(same[j]);
            lists[a] = same[j]; lists[b] = l;
            renderLists(); markDirty();
        }

        /** Lists saved as "both" by 2.0 become one movie list and one show list (the server does the same). */
        function splitMixed(arr) {
            var out = [];
            arr.forEach(function (l) {
                if (l.MediaType === 'movie' || l.MediaType === 'tv') { out.push(l); return; }
                ['movie', 'tv'].forEach(function (t) {
                    var c = Object.assign({}, l, { Id: (l.Id || newId()) + (t === 'movie' ? '-m' : '-t'), MediaType: t });
                    try { var b = JSON.parse(l.BuilderJson || 'null'); if (b) { b.type = t; c.BuilderJson = JSON.stringify(b); } } catch (e) { }
                    out.push(c);
                });
            });
            return out;
        }

        el('sdAddList-movie').addEventListener('click', function () { openBuilder(null, 'movie'); });
        el('sdAddList-tv').addEventListener('click', function () { openBuilder(null, 'tv'); });
        el('sdRestoreDefaults').addEventListener('click', function () {
            var added = 0;
            defaultLists().reverse().forEach(function (d) {
                if (!lists.some(function (l) { return l.Source === d.Source && l.MediaType === d.MediaType; })) { lists.unshift(d); added++; }
            });
            renderLists();
            if (added) markDirty();
            setSaveStatus(added ? added + ' default list(s) added at the top — click Save.' : 'All default Seerr lists are already present.');
        });
        el('sdMoviesName').addEventListener('input', renderLists);
        el('sdTvName').addEventListener('input', renderLists);

        // ---------------------------------------------------------------- preview
        function renderPreview(host, r) {
            host.textContent = '';
            if (!r.Ok) { host.appendChild(h('div', { class: 'sd-status err', text: r.Error || 'Preview failed.' })); return; }
            var count = r.Movies && r.Shows ? r.Movies + ' movies, ' + r.Shows + ' shows' : r.Shows ? r.Shows + ' shows' : r.Movies + ' movies';
            var summary = (r.Name ? '"' + r.Name + '": ' : '') + count +
                (r.HiddenAsOwned ? ' — ' + r.HiddenAsOwned + ' already owned and hidden in the channel (hover an owned poster to see why)' : '') + '.' +
                (r.RemovedByRating ? ' ' + r.RemovedByRating + ' shows were left out by the age-rating filter.' : '');
            host.appendChild(h('div', { class: 'sd-status ok', text: summary }));
            if (r.ResolvedSource && r.ResolvedSource.indexOf('seerr:') !== 0) {
                host.appendChild(h('div', { class: 'sd-help', text: 'Link as sent today (dates filled in):' }));
                host.appendChild(h('div', { class: 'sd-link', text: r.ResolvedSource }));
            }
            var grid = h('div', { class: 'sd-posters' });
            (r.Items || []).forEach(function (it) {
                var img = h('div', { class: 'img' }, [
                    it.Hidden ? h('span', { class: 'tag', text: 'owned' }) : null,
                    it.HasTrailer ? h('span', { class: 'tag tr', text: '▶ trailer' }) : null
                ]);
                if (it.PosterPath && /^\/[A-Za-z0-9._\/-]+$/.test(it.PosterPath)) img.style.backgroundImage = 'url("' + POSTER + it.PosterPath + '")';
                grid.appendChild(h('div', { class: 'sd-poster' + (it.Hidden ? ' hidden' : ''), title: it.OwnedReason || it.Status || '' }, [
                    img,
                    h('div', { text: it.Title + (it.Year ? ' (' + it.Year + ')' : '') }),
                    it.Scores ? h('div', { class: 'why', text: it.Scores }) : null,
                    it.OwnedReason ? h('div', { class: 'why', text: it.OwnedReason }) : null
                ]));
            });
            host.appendChild(grid);
        }

        function runPreview(source, mediaType, maxItems, host) {
            host.textContent = '';
            host.appendChild(h('div', { class: 'sd-status info', text: 'Loading preview…' }));
            return postJson('SeerrDiscover/Admin/Preview', { Source: source, MediaType: mediaType, MaxItems: maxItems || 0 })
                .then(function (r) { renderPreview(host, r); }, function (err) { host.textContent = ''; host.appendChild(h('div', { class: 'sd-status err', text: 'Preview failed: ' + describeError(err) })); });
        }

        function previewStandalone(l) {
            if (builder) closeBuilder();
            var host = el('sdBuilderHost-' + (l.MediaType === 'tv' ? 'tv' : 'movie'));
            host.textContent = '';
            var card = h('div', { class: 'sd-card' }, [
                h('div', { class: 'sd-buttons', style: 'margin-top:0;justify-content:space-between' }, [
                    h('strong', { text: 'Preview: ' + (l.Name || describeList(l)) }),
                    h('button', { type: 'button', class: 'sd-btn sd-small', text: 'Close', onclick: function () { host.textContent = ''; } })
                ])
            ]);
            var out = h('div', {});
            card.appendChild(out);
            host.appendChild(card);
            runPreview(l.Source, l.MediaType, l.MaxItems, out);
        }

        // ---------------------------------------------------------------- TMDB options
        function getOptions(type, region) {
            var key = type + '|' + region;
            if (!optionsCache[key]) {
                optionsCache[key] = api().getJSON(api().getUrl('SeerrDiscover/Admin/TmdbOptions', { MediaType: type, Region: region }))
                    .then(function (r) { if (!r.Ok) delete optionsCache[key]; return r; },
                          function (err) { delete optionsCache[key]; return { Ok: false, Error: describeError(err) }; });
            }
            return optionsCache[key];
        }

        // ---------------------------------------------------------------- list builder
        function closeBuilder() {
            builder = null;
            el('sdBuilderHost-movie').textContent = '';
            el('sdBuilderHost-tv').textContent = '';
        }

        function openBuilder(existing, type) {
            closeBuilder();
            el('sdBuilderHost-movie').textContent = '';
            el('sdBuilderHost-tv').textContent = '';
            var st = existing ? stateFromList(existing) : {
                template: 'popular', type: type, name: '', nameAuto: true, window: null, sort: 'popularity', region: 'US',
                providers: [], subscriptionOnly: true, genresIn: [], genresOut: [], noAnime: false, language: '', minVotes: '',
                minScore: '', certMin: '', certMax: '', keepUnrated: false,
                maxItems: '', trendWindow: 'week', listUrl: '', customUrl: '', enabled: true
            };
            st.type = type;
            ['providers', 'genresIn', 'genresOut'].forEach(function (k) { if (!Array.isArray(st[k])) st[k] = []; });
            if (!st.region) st.region = 'US';
            if (st.subscriptionOnly == null) st.subscriptionOnly = true;
            builder = { st: st, type: type, existing: existing, providerNames: st.providerNames || {} };
            renderBuilder();
            el('sdBuilderHost-' + type).scrollIntoView({ behavior: 'smooth', block: 'start' });
        }

        function syncTemplateDefaults(st) {
            if (tpl(st.template).types.indexOf(st.type) < 0) st.template = 'popular';
            var t = tpl(st.template);
            if (t.filters && (st.window == null || st._windowFor !== t.id)) { st.window = t.window; st._windowFor = t.id; }
        }

        function providerNameList(st) {
            return (st.providers || []).map(function (id) { return builder.providerNames[id] || ('#' + id); });
        }

        function refreshLink() {
            if (!builder) return;
            var st = builder.st;
            if (st.nameAuto) {
                var n = autoName(st, providerNameList(st));
                builder.nameInput.value = n;
                builder.nameInput.placeholder = n || (tpl(st.template).listUrl ? 'Leave blank to use the list\'s TMDB name' : 'Folder name');
            }
            var src = buildSource(st);
            builder.link.textContent = src || '—';
            builder.linkRow.style.display = tpl(st.template).id.indexOf('seerr-') === 0 ? 'none' : '';
        }

        function chipGroup(options, selected, onToggle, cls) {
            var box = h('div', { class: 'sd-chips' });
            options.forEach(function (o) {
                var on = selected.indexOf(o.Id) >= 0;
                var input = h('input', { type: 'checkbox', checked: on, value: o.Id });
                var chip = h('label', { class: 'sd-chip' + (cls ? ' ' + cls : '') + (on ? ' on' : ''), title: 'id ' + o.Id }, [input, o.Name]);
                input.addEventListener('change', function () { chip.classList.toggle('on', input.checked); onToggle(o, input.checked); });
                box.appendChild(chip);
            });
            if (!options.length) box.appendChild(h('span', { class: 'sd-muted', text: 'Nothing to choose from.' }));
            return box;
        }

        function filterBox(box) {
            return h('input', { type: 'text', class: 'sd-filter', placeholder: 'Type to filter…', oninput: function () {
                var q = this.value.toLowerCase();
                box.querySelectorAll('.sd-chip').forEach(function (c) { c.style.display = c.textContent.toLowerCase().indexOf(q) >= 0 ? '' : 'none'; });
            } });
        }

        function field(label, control, help) {
            return h('div', { class: 'sd-field' }, [h('label', { class: 'sd-label', text: label }), control, help ? h('div', { class: 'sd-help', text: help }) : null]);
        }

        function select(options, value, onchange) {
            var s = h('select', { onchange: function () { onchange(this.value); } });
            options.forEach(function (o) { s.appendChild(h('option', { value: o[0], text: o[1] })); });
            s.value = value;
            return s;
        }

        function renderBuilder() {
            var st = builder.st;
            syncTemplateDefaults(st);
            var t = tpl(st.template);
            var host = el('sdBuilderHost-' + builder.type);
            host.textContent = '';
            var root = h('div', { class: 'sd-builder' });
            builder.root = root;
            root.appendChild(h('h4', { text: (builder.existing ? 'Edit a list in ' : 'Add a list to ') + channelName(builder.type) }));

            // template
            var tsel = h('select', { onchange: function () { st.template = this.value; renderBuilder(); } });
            var groups = {};
            TEMPLATES.forEach(function (x) {
                if (x.types.indexOf(st.type) < 0) return;
                if (!groups[x.group]) { groups[x.group] = h('optgroup', { label: x.group }); tsel.appendChild(groups[x.group]); }
                groups[x.group].appendChild(h('option', { value: x.id, text: x.label }));
            });
            tsel.value = t.id;
            root.appendChild(field('Kind of list', tsel, t.help));

            // name
            var nameInput = h('input', { type: 'text', maxlength: '80', value: st.nameAuto ? '' : st.name, oninput: function () {
                st.name = this.value; st.nameAuto = this.value.trim() === ''; refreshLinkSoon();
            } });
            builder.nameInput = nameInput;
            root.appendChild(field('Folder name', nameInput, t.id.indexOf('seerr-') === 0 ? 'Leave blank for the standard name (e.g. "Trending Movies").' : 'Shown as the folder name in the channel and as the home-screen row title.'));

            if (t.listUrl) {
                root.appendChild(field('TMDB list address', h('input', { type: 'text', value: st.listUrl || '', placeholder: 'https://www.themoviedb.org/list/8259231-best-heist-movies', oninput: function () { st.listUrl = this.value; refreshLinkSoon(); } }),
                    'Open the list on themoviedb.org and copy the address from the browser, or type just its number. The list must be public.'));
            }
            if (t.customUrl) {
                var example = st.type === 'tv'
                    ? 'https://api.themoviedb.org/3/discover/tv?sort_by=popularity.desc&first_air_date.gte={today-1m}'
                    : 'https://api.themoviedb.org/3/discover/movie?sort_by=popularity.desc&primary_release_date.gte={today-1m}';
                root.appendChild(field('TMDB link', h('textarea', { rows: '3', spellcheck: 'false', placeholder: example, oninput: function () {
                        st.customUrl = this.value; refreshLinkSoon();
                    } }, [st.customUrl || '']),
                    'Must return ' + (st.type === 'tv' ? 'shows (/discover/tv, /tv/…, /trending/tv/…)' : 'movies (/discover/movie, /movie/…, /trending/movie/…)') +
                    ' — /trending/all works too; only its ' + (st.type === 'tv' ? 'shows' : 'movies') + ' are used. Placeholders such as {today}, {today-7d}, {start_of_month} and {year} are filled in from the server clock (see Help). api_key and page are ignored.'));
            }
            if (t.trend) {
                root.appendChild(h('div', { style: 'max-width:20em' }, [field('Trending over', select([['week', 'This week'], ['day', 'Today']], st.trendWindow || 'week', function (v) { st.trendWindow = v; refreshLinkSoon(); }))]));
            }

            if (t.filters) {
                var winRow = h('div', { class: 'sd-row' });
                winRow.appendChild(field(t.id === 'onair' ? 'Episodes airing' : st.type === 'tv' ? 'First aired' : 'Released',
                    select(WINDOWS.map(function (w) { return [w[0], w[1]]; }), st.window || 'none', function (v) { st.window = v; renderBuilder(); }),
                    'Windows move forward by themselves (filled in from the server clock each time).'));
                if (t.sort) winRow.appendChild(field('Pick titles by', select(SORTS, st.sort || 'popularity', function (v) { st.sort = v; refreshLinkSoon(); }),
                    'Decides which titles make the list; Emby sorts them on screen.'));
                winRow.appendChild(field('Minimum votes', h('input', { type: 'number', min: '0', step: '10', value: st.minVotes === '' || st.minVotes == null ? '' : st.minVotes,
                    placeholder: String(t.minVotes || (st.sort === 'rating' ? 100 : 0)), oninput: function () { st.minVotes = this.value; refreshLinkSoon(); } }), 'Skips little-known titles.'));
                winRow.appendChild(field('Minimum TMDB score', h('input', { type: 'number', min: '0', max: '10', step: '0.5', value: st.minScore || '',
                    placeholder: 'any, e.g. 6.5', oninput: function () { st.minScore = this.value; refreshLinkSoon(); } }), 'Out of 10. Combine with minimum votes, as a few votes can give any score.'));
                root.appendChild(winRow);
                if (st.window === 'custom') {
                    root.appendChild(h('div', { class: 'sd-row' }, [
                        field('From', h('input', { type: 'text', value: st.from || '', placeholder: '{today-14d} or 2026-01-01', oninput: function () { st.from = this.value; refreshLinkSoon(); } })),
                        field('To', h('input', { type: 'text', value: st.to || '', placeholder: '{today} or 2026-12-31', oninput: function () { st.to = this.value; refreshLinkSoon(); } }))
                    ]));
                }

                var optHost = h('div', {}, [h('div', { class: 'sd-status info', text: 'Loading genres and streaming services from TMDB…' })]);
                root.appendChild(optHost);
                getOptions(st.type === 'tv' ? 'tv' : 'movie', st.region || 'US').then(function (o) {
                    if (!builder || builder.st !== st || !optHost.isConnected) return;
                    optHost.textContent = '';
                    if (!o.Ok) {
                        optHost.appendChild(h('div', { class: 'sd-status err', text: (o.Error || 'Could not load TMDB options.') + ' (Save the TMDB key on the Connection tab first.)' }));
                        return;
                    }
                    (o.Providers || []).forEach(function (p) { builder.providerNames[p.Id] = p.Name; });

                    // streaming
                    var regionSel = select((o.Regions || []).map(function (r) { return [r.Id, r.Name]; }), st.region || 'US', function (v) { st.region = v; renderBuilder(); });
                    var provBox = chipGroup(o.Providers || [], st.providers, function (p, on) {
                        if (on) st.providers.push(p.Id); else st.providers = st.providers.filter(function (x) { return x !== p.Id; });
                        refreshLinkSoon();
                    });
                    optHost.appendChild(h('div', { class: 'sd-row' }, [
                        field('Country', regionSel, 'Used for the streaming services and the age ratings below.'),
                        h('div', { class: 'sd-field' }, [h('label', { class: 'sd-check' }, [
                            h('input', { type: 'checkbox', checked: st.subscriptionOnly !== false, onchange: function () { st.subscriptionOnly = this.checked; refreshLinkSoon(); } }),
                            h('span', { text: 'Included with the subscription only (no rent or buy)' })])])
                    ]));
                    optHost.appendChild(field('Streaming services' + (t.needsProviders ? '' : ' (optional)'), h('div', {}, [filterBox(provBox), provBox]),
                        'Tick one or more (any of them). Listed in order of popularity in the chosen country.'));

                    // age ratings of the same country
                    var certs = o.Certifications || [];
                    builder.certs = certs;
                    var codes = certs.map(function (c) { return c.Code; });
                    if (st.certMin && codes.indexOf(st.certMin) < 0) st.certMin = '';
                    if (st.certMax && codes.indexOf(st.certMax) < 0) st.certMax = '';
                    var certOpts = certs.map(function (c) { return [c.Code, c.Code + (c.Meaning ? ' — ' + c.Meaning.slice(0, 70) + (c.Meaning.length > 70 ? '…' : '') : '')]; });
                    var regionName = ((o.Regions || []).filter(function (r) { return r.Id === (st.region || 'US'); })[0] || {}).Name || st.region || 'US';
                    if (certs.length) {
                        var certRow = h('div', { class: 'sd-row' }, [
                            field('Lowest age rating', select([['', 'No minimum']].concat(certOpts), st.certMin || '', function (v) { st.certMin = v; refreshLinkSoon(); }),
                                'Ratings in ' + regionName + ', youngest audience first.'),
                            field('Highest age rating', select([['', 'No maximum']].concat(certOpts), st.certMax || '', function (v) { st.certMax = v; refreshLinkSoon(); }),
                                st.type === 'tv' ? 'E.g. TV-PG for a family list. TMDB cannot filter shows by rating, so Discover checks each show\'s rating itself.'
                                                 : 'E.g. PG-13 for a family list. Movies without a rating in this country are left out by TMDB.')
                        ]);
                        optHost.appendChild(certRow);
                        if (st.type === 'tv') {
                            optHost.appendChild(h('label', { class: 'sd-check' }, [
                                h('input', { type: 'checkbox', checked: !!st.keepUnrated, onchange: function () { st.keepUnrated = this.checked; refreshLinkSoon(); } }),
                                h('span', {}, [h('strong', { text: 'Keep shows without a rating in ' + regionName }), h('br'),
                                    h('span', { class: 'sd-muted', text: 'Only used with an age-rating filter. Many shows have no rating outside the US.' })])]));
                        }
                    } else {
                        optHost.appendChild(h('div', { class: 'sd-help', text: 'TMDB has no ' + (st.type === 'tv' ? 'TV' : 'movie') + ' age ratings for ' + regionName + ', so the age-rating filter is not available for this country.' }));
                    }

                    // genres
                    var inBox = chipGroup(o.Genres || [], st.genresIn, function (g, on) {
                        if (on) st.genresIn.push(g.Id); else st.genresIn = st.genresIn.filter(function (x) { return x !== g.Id; });
                        refreshLinkSoon();
                    });
                    var outBox = chipGroup(o.Genres || [], st.genresOut, function (g, on) {
                        if (on) st.genresOut.push(g.Id); else st.genresOut = st.genresOut.filter(function (x) { return x !== g.Id; });
                        refreshLinkSoon();
                    }, 'off');
                    optHost.appendChild(h('div', { class: 'sd-row' }, [
                        field('Only these genres (optional)', inBox, 'A title needs at least one of them.'),
                        field('Leave out these genres', outBox, st.type === 'tv' ? 'E.g. Kids, Soap, News, Talk.' : 'E.g. Family, Animation, TV Movie.')
                    ]));

                    var langSel = select([['', 'Any language']].concat((o.Languages || []).map(function (l) { return [l.Id, l.Name]; })), st.language || '', function (v) { st.language = v; refreshLinkSoon(); });
                    optHost.appendChild(h('div', { class: 'sd-row' }, [
                        field('Original language', langSel, 'E.g. English to leave out foreign-language daily shows.'),
                        h('div', { class: 'sd-field' }, [h('label', { class: 'sd-check' }, [
                            h('input', { type: 'checkbox', checked: !!st.noAnime, onchange: function () { st.noAnime = this.checked; refreshLinkSoon(); } }),
                            h('span', {}, [h('strong', { text: 'Leave out anime' }), h('br'), h('span', { class: 'sd-muted', text: 'Uses TMDB\'s "anime" keyword, so other animation stays.' })])])])
                    ]));
                    refreshLink();
                });
            }

            root.appendChild(h('div', { style: 'max-width:20em' }, [field('Maximum titles', h('input', { type: 'number', min: '0', max: '1000', step: '20', value: st.maxItems || '',
                placeholder: 'default (' + (t.id.indexOf('seerr-') === 0 ? (el('sdMaxItems').value || 60) : (el('sdMaxList').value || 200)) + ')',
                oninput: function () { st.maxItems = this.value; } }), 'Leave blank for the default on this tab.')]));

            var link = h('div', { class: 'sd-link' });
            var linkRow = h('div', { class: 'sd-field' }, [h('label', { class: 'sd-label', text: 'TMDB link this creates' }), link]);
            builder.link = link;
            builder.linkRow = linkRow;
            root.appendChild(linkRow);

            var previewHost = h('div', {});
            root.appendChild(h('div', { class: 'sd-buttons' }, [
                h('button', { type: 'button', class: 'sd-btn', text: 'Preview titles', onclick: function () {
                    var src = buildSource(st);
                    if (!src) { previewHost.textContent = ''; previewHost.appendChild(h('div', { class: 'sd-status err', text: 'Fill in the list first.' })); return; }
                    runPreview(src, st.type, parseInt(st.maxItems, 10) || 0, previewHost);
                } }),
                h('button', { type: 'button', class: 'sd-btn sd-primary', text: builder.existing ? 'Update list' : 'Add list', onclick: commitBuilder }),
                h('button', { type: 'button', class: 'sd-btn', text: 'Cancel', onclick: closeBuilder })
            ]));
            root.appendChild(previewHost);
            host.appendChild(root);
            refreshLink();
        }

        var refreshTimer = null;
        function refreshLinkSoon() {
            clearTimeout(refreshTimer);
            refreshTimer = setTimeout(refreshLink, 80);
        }

        function commitBuilder() {
            var st = builder.st;
            var t = tpl(st.template);
            var src = buildSource(st);
            var err = null;
            if (!src) err = t.listUrl ? 'Paste the TMDB list address.' : 'Paste the TMDB link.';
            else if (t.listUrl && !/^\d+$/.test(src) && !/themoviedb\.org\/(?:[a-z]{2}(?:-[A-Z]{2})?\/)?list\/\d+/i.test(src) && !/api\.themoviedb\.org\/[34]\/list\/\d+/i.test(src))
                err = 'That does not look like a TMDB list address (themoviedb.org/list/12345…).';
            else if (t.customUrl && !/^https:\/\/api\.themoviedb\.org\/3\//i.test(src)) err = 'A custom link must start with https://api.themoviedb.org/3/';
            else if (t.customUrl && inferType(src) && inferType(src) !== 'both' && inferType(src) !== st.type)
                err = 'This link returns ' + (inferType(src) === 'tv' ? 'shows' : 'movies') + '. Add it under ' + channelName(inferType(src)) + ' instead.';
            else if (t.filters && st.certMin && st.certMax && certOrder(st.certMin) > certOrder(st.certMax))
                err = 'The lowest age rating (' + st.certMin + ') is above the highest (' + st.certMax + ').';
            else if (t.needsProviders && !st.providers.length) err = 'Tick at least one streaming service.';
            if (err) { alert(err); return; }

            var name = st.nameAuto ? autoName(st, providerNameList(st)) : (st.name || '').trim();
            var saveState = Object.assign({}, st, { providerNames: builder.providerNames ? pick(builder.providerNames, st.providers) : {} });
            delete saveState.id; delete saveState.name; delete saveState.enabled; delete saveState.maxItems; delete saveState._windowFor;
            var entry = {
                Id: (builder.existing && builder.existing.Id) || newId(),
                Name: name,
                Enabled: builder.existing ? builder.existing.Enabled !== false : true,
                Source: src,
                MediaType: st.type,
                MaxItems: parseInt(st.maxItems, 10) || 0,
                BuilderJson: JSON.stringify(saveState)
            };
            if (builder.existing) {
                var idx = lists.indexOf(builder.existing);
                if (idx >= 0) lists[idx] = entry; else lists.push(entry);
            } else lists.push(entry);
            closeBuilder();
            renderLists();
            markDirty();
            setSaveStatus('List ' + (name ? '"' + name + '" ' : '') + 'ready — click Save to apply.');
        }

        function certOrder(code) {
            var c = ((builder && builder.certs) || []).filter(function (x) { return x.Code === code; })[0];
            return c ? c.Order : 0;
        }

        function pick(map, keys) {
            var o = {};
            (keys || []).forEach(function (k) { if (map[k]) o[k] = map[k]; });
            return o;
        }

        // ---------------------------------------------------------------- destinations
        function loadServices() {
            el('sdServicesInfo').textContent = 'Loading from Seerr…';
            api().getJSON(api().getUrl('SeerrDiscover/Admin/Services')).then(function (r) {
                services = r;
                var parts = [];
                if (r.Error) parts.push(r.Error);
                [r.Radarr, r.Sonarr].forEach(function (s) {
                    if (!s) return;
                    parts.push(s.Error ? s.App + ': ' + s.Error : s.App + ' "' + (s.ServerName || '') + '": ' + s.RootFolders.length + ' folders, ' + s.Tags.length + ' tags');
                });
                el('sdServicesInfo').textContent = parts.join(' · ');
                renderDestinations();
            }, function (err) {
                services = { Error: describeError(err) };
                el('sdServicesInfo').textContent = 'Could not load from Seerr: ' + describeError(err);
                renderDestinations();
            });
        }
        el('sdLoadServices').addEventListener('click', loadServices);

        function folderControl(svc, value) {
            if (!svc || svc.Error) {
                return h('input', { type: 'text', 'data-f': 'x', value: value || '', placeholder: 'Blank = Seerr\'s default folder' });
            }
            var s = h('select', {});
            s.appendChild(h('option', { value: '', text: 'Seerr\'s default' + (svc.DefaultRootFolder ? ' (' + svc.DefaultRootFolder + ')' : '') }));
            svc.RootFolders.forEach(function (f) {
                s.appendChild(h('option', { value: f.Path, text: f.Path + (f.FreeSpaceGb != null ? '  — ' + f.FreeSpaceGb + ' GB free' : '') }));
            });
            if (value && !svc.RootFolders.some(function (f) { return f.Path === value; })) s.appendChild(h('option', { value: value, text: value + ' (saved, not found in ' + svc.App + ')' }));
            s.value = value || '';
            return s;
        }

        function tagControl(svc, value) {
            var ids = (value || '').split(',').map(function (x) { return x.trim(); }).filter(Boolean);
            if (!svc || svc.Error) return h('input', { type: 'text', value: ids.join(','), placeholder: 'Tag ids, e.g. 3 (blank = Seerr\'s default tags)' });
            var opts = svc.Tags.map(function (t) { return { Id: String(t.Id), Name: t.Label }; });
            ids.forEach(function (id) { if (!opts.some(function (o) { return o.Id === id; })) opts.push({ Id: id, Name: 'tag ' + id + ' (not found)' }); });
            var box = chipGroup(opts, ids, function () { markDirty(); });
            if (!opts.length) box.appendChild(h('span', { class: 'sd-muted', text: ' (no tags in ' + svc.App + ')' }));
            return box;
        }

        function renderDestinations() {
            var host = el('sdDestinations');
            if (!host || !cfg) return;
            var current = { remote: collectDestination('remote'), local: collectDestination('local') };
            var defaultDest = (view.querySelector('input[name=sdDefaultDest]:checked') || {}).value || cfg.DefaultDestination || 'remote';
            host.textContent = '';
            var radarr = services && services.Radarr, sonarr = services && services.Sonarr;
            [['remote', 'RemoteDestination', 'Stream'], ['local', 'LocalDestination', 'Download locally']].forEach(function (d) {
                var key = d[0];
                var saved = host.isConnected && el('sdDest-' + key) ? current[key] : (cfg[d[1]] || {});
                var card = h('div', { class: 'sd-card', id: 'sdDest-' + key });
                var radio = h('input', { type: 'radio', name: 'sdDefaultDest', value: key, checked: defaultDest === key, onchange: markDirty });
                card.appendChild(h('div', { class: 'sd-buttons', style: 'margin-top:0;justify-content:space-between' }, [
                    h('h4', { style: 'margin:0', text: key === 'remote' ? 'Destination 1' : 'Destination 2' }),
                    h('label', { class: 'sd-check', style: 'margin:0' }, [radio, h('span', { text: 'Default for requests from Emby' })])
                ]));
                var label = h('input', { type: 'text', 'data-f': 'label', value: saved.Label || d[2], maxlength: '40' });
                card.appendChild(field('Name', label));
                var mRoot = folderControl(radarr, saved.MovieRootFolder); mRoot.setAttribute('data-f', 'MovieRoot');
                var mTags = tagControl(radarr, saved.MovieTagIds); mTags.setAttribute('data-f', 'MovieTags');
                var tRoot = folderControl(sonarr, saved.TvRootFolder); tRoot.setAttribute('data-f', 'TvRoot');
                var tTags = tagControl(sonarr, saved.TvTagIds); tTags.setAttribute('data-f', 'TvTags');
                card.appendChild(h('div', { class: 'sd-row' }, [
                    field('Movies — Radarr folder', mRoot), field('Movies — Radarr tags', mTags)
                ]));
                card.appendChild(h('div', { class: 'sd-row' }, [
                    field('Shows — Sonarr folder', tRoot), field('Shows — Sonarr tags', tTags)
                ]));
                card.querySelectorAll('input,select').forEach(function (i) { i.addEventListener('change', markDirty); });
                host.appendChild(card);
            });
        }

        // ---------------------------------------------------------------- libraries that count as owned
        function loadLibraries() {
            libraries = [];
            api().getJSON(api().getUrl('SeerrDiscover/Admin/Libraries')).then(function (r) {
                libraries = r || [];
                renderLibraries();
            }, function (err) {
                libraries = null;
                el('sdLibraries').textContent = 'Could not load the libraries: ' + describeError(err);
            });
        }

        function renderLibraries() {
            var host = el('sdLibraries');
            host.textContent = '';
            var typeName = { movies: 'movies', tvshows: 'shows', mixed: 'mixed', homevideos: 'home videos' };
            var opts = (libraries || []).map(function (l) { return { Id: l.Id, Name: l.Name + ' (' + (typeName[l.CollectionType] || l.CollectionType) + ')' }; });
            ownedLibraryIds.forEach(function (id) { if (!opts.some(function (o) { return o.Id === id; })) opts.push({ Id: id, Name: 'library ' + id + ' (no longer exists)' }); });
            if (!opts.length) { host.appendChild(h('span', { class: 'sd-muted', text: 'No movie or TV libraries found.' })); return; }
            var box = chipGroup(opts, ownedLibraryIds, function (o, on) {
                ownedLibraryIds = ownedLibraryIds.filter(function (x) { return x !== o.Id; });
                if (on) ownedLibraryIds.push(o.Id);
                markDirty();
            });
            box.style.maxHeight = 'none';
            host.appendChild(box);
        }

        function collectLibraries() {
            return ownedLibraryIds.slice();
        }

        // ---------------------------------------------------------------- channel refresh
        var refreshTimer2 = null, refreshStarted = 0, sawRunning = false;
        function showRefresh(r) {
            if (r.Running) sawRunning = true;
            // The task can take a moment to start: keep waiting up to 10 s before calling it finished.
            if (r.Ok && !r.Running && !sawRunning && Date.now() - refreshStarted < 10000) {
                clearTimeout(refreshTimer2); refreshTimer2 = setTimeout(pollRefresh, 1500); return;
            }
            var box = el('sdRefreshStatus');
            status(box, r.Ok ? (r.Running ? 'info' : 'ok') : 'err', r.Message || '');
            el('sdRefresh').disabled = !!r.Running;
            clearTimeout(refreshTimer2);
            if (r.Running) refreshTimer2 = setTimeout(pollRefresh, 2000);
            else if (r.Ok) {
                el('sdRefresh').textContent = '⟳ Refresh channels';
                status(box, 'ok', 'Refresh finished' + (r.LastStatus && r.LastStatus !== 'Completed' ? ' (' + r.LastStatus + ')' : '') + '. Reload the channel in Emby to see the changes.');
                setTimeout(function () { if (!el('sdRefresh').disabled) status(box, '', ''); }, 15000);
            }
            if (r.Running) el('sdRefresh').textContent = '⟳ Refreshing…' + (r.Progress != null ? ' ' + Math.round(r.Progress) + '%' : '');
        }
        function pollRefresh() {
            api().getJSON(api().getUrl('SeerrDiscover/Admin/RefreshChannels')).then(showRefresh, function (err) {
                el('sdRefresh').disabled = false; el('sdRefresh').textContent = '⟳ Refresh channels';
                status('sdRefreshStatus', 'err', 'Could not read the refresh progress: ' + describeError(err));
            });
        }
        el('sdRefresh').addEventListener('click', function () {
            if (dirty) setSaveStatus('Unsaved changes — the refresh uses the saved settings. Click Save first to include them.');
            el('sdRefresh').disabled = true;
            refreshStarted = Date.now(); sawRunning = false;
            status('sdRefreshStatus', 'info', 'Starting the refresh…');
            postJson('SeerrDiscover/Admin/RefreshChannels', {}).then(function (r) {
                if (!r.Ok) { el('sdRefresh').disabled = false; status('sdRefreshStatus', 'err', r.Message); return; }
                showRefresh({ Ok: true, Running: true, Message: r.Message || 'Refresh started.' }); sawRunning = false;
                status('sdRefreshStatus', 'info', 'Refreshing the Discover channels. This can take a minute or two (longer the first time, while trailers are looked up).');
            }, function (err) {
                el('sdRefresh').disabled = false;
                status('sdRefreshStatus', 'err', 'Could not start the refresh: ' + describeError(err));
            });
        });

        // ---------------------------------------------------------------- lifecycle
        window.addEventListener('beforeunload', function (e) { if (dirty) { e.preventDefault(); e.returnValue = ''; } });
        view.addEventListener('viewshow', function () { if (!loaded) load(); });
        setTimeout(function () { if (!loaded) load(); }, 0);
    };
});

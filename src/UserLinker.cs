using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;

namespace EmbySeerrNative
{
    public class SeerrLink
    {
        /// <summary>Seerr user to act as (sent as X-API-User). Null when no link exists.</summary>
        public int? SeerrUserId { get; set; }
        public string SeerrUserName { get; set; }
        /// <summary>auto | admin-fallback | none</summary>
        public string Source { get; set; } = "none";
        /// <summary>Null when the user may request; otherwise a message explaining why not.</summary>
        public string BlockedReason { get; set; }
    }

    /// <summary>
    /// Maps an Emby user to a Seerr user so that requests are made with that user's own Seerr
    /// permissions, quotas and approval rules (via the X-API-User header), instead of as the
    /// all-powerful API-key owner.
    ///
    /// Order: automatic link (the Seerr user whose jellyfinUserId equals the Emby user's ID, which
    /// is how Seerr stores users imported from Emby or signed in with Emby) → optional admin fallback.
    /// </summary>
    internal static class UserLinker
    {
        public static string EmbyUserKey(User user) => user.Id.ToString("N");

        public static bool IsAdmin(User user) => user?.Policy?.IsAdministrator == true;

        public static async Task<SeerrLink> ResolveAsync(User user, CancellationToken ct)
        {
            var cfg = Plugin.Config;
            if (user == null)
                return new SeerrLink { BlockedReason = "Requests must be made by a signed-in Emby user." };
            if (user.Policy != null && user.Policy.IsDisabled)
                return new SeerrLink { BlockedReason = "This Emby account is disabled." };
            if (cfg.RequestsAdminsOnly && !IsAdmin(user))
                return new SeerrLink { BlockedReason = "Only Emby administrators can request titles from Emby on this server." };

            var users = await SeerrClient.GetUsersAsync(ct).ConfigureAwait(false);

            var key = EmbyUserKey(user);
            var auto = users.FirstOrDefault(u => u.JellyfinUserId == key);
            if (auto != null)
                return new SeerrLink { SeerrUserId = auto.Id, SeerrUserName = auto.DisplayName, Source = "auto" };

            if (IsAdmin(user) && cfg.AllowUnlinkedAdmins)
            {
                var owner = users.FirstOrDefault(u => u.Id == 1);
                return new SeerrLink
                {
                    SeerrUserId = 1,
                    SeerrUserName = owner?.DisplayName ?? "Seerr administrator",
                    Source = "admin-fallback"
                };
            }

            return new SeerrLink
            {
                BlockedReason = "Your Emby account is not linked to a Seerr account. Ask the server admin to import " +
                                "your Emby user in Seerr (Settings → Users → Import Emby Users), or sign in to Seerr once with your Emby account."
            };
        }
    }
}

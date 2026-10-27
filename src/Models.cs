using System;
using System.Collections.Generic;

namespace EmbySeerrNative
{
    /// <summary>Seerr MediaStatus (server/constants/media.ts).</summary>
    public enum SeerrMediaStatus
    {
        Unknown = 1,
        Pending = 2,
        Processing = 3,
        PartiallyAvailable = 4,
        Available = 5,
        Blocklisted = 6,
        Deleted = 7
    }

    /// <summary>Seerr MediaRequestStatus (server/constants/media.ts).</summary>
    public enum SeerrRequestStatus
    {
        Pending = 1,
        Approved = 2,
        Declined = 3,
        Failed = 4,
        Completed = 5
    }

    /// <summary>The state shown to Emby users.</summary>
    public static class Availability
    {
        public const string Available = "available";
        public const string Partial = "partial";
        public const string Processing = "processing";
        public const string Pending = "pending";
        public const string Blocklisted = "blocklisted";
        public const string NotRequested = "not_requested";

        public static string FromSeerr(int? status)
        {
            switch (status)
            {
                case (int)SeerrMediaStatus.Available: return Available;
                case (int)SeerrMediaStatus.PartiallyAvailable: return Partial;
                case (int)SeerrMediaStatus.Processing: return Processing;
                case (int)SeerrMediaStatus.Pending: return Pending;
                case (int)SeerrMediaStatus.Blocklisted: return Blocklisted;
                default: return NotRequested; // null, Unknown, Deleted
            }
        }

        /// <summary>Whether a new request could make sense for this state.</summary>
        public static bool CanRequest(string state, string mediaType)
        {
            if (state == NotRequested) return true;
            // A partially available show can still have missing seasons requested.
            if (state == Partial && mediaType == "tv") return true;
            // A show that is pending/processing may have other seasons that are not yet requested;
            // the details view decides per season.
            if ((state == Pending || state == Processing) && mediaType == "tv") return true;
            return false;
        }

        public static string Label(string state, bool inLibrary)
        {
            if (inLibrary && state != Partial) return "Available in Emby";
            switch (state)
            {
                case Available: return "Available";
                case Partial: return inLibrary ? "Partially available in Emby" : "Partially available";
                case Processing: return "Requested · processing";
                case Pending: return "Requested · awaiting approval";
                case Blocklisted: return "Blocklisted in Seerr";
                default: return "Not requested";
            }
        }
    }

    public class SeerrException : Exception
    {
        public SeerrException(string message, int httpStatus = 0, Exception inner = null)
            : base(message, inner)
        {
            HttpStatus = httpStatus;
        }

        /// <summary>HTTP status returned by Seerr, or 0 when Seerr could not be reached.</summary>
        public int HttpStatus { get; }
    }

    // ----------------------------------------------------------------------------------------
    // DTOs returned by the plugin's own REST endpoints. Emby serialises these (PascalCase).
    // They deliberately contain only display data: no Seerr user details, no API key.
    // ----------------------------------------------------------------------------------------

    public class DiscoverItemDto
    {
        public string MediaType { get; set; }          // movie | tv
        public int TmdbId { get; set; }
        public string Title { get; set; }
        public int? Year { get; set; }
        public string ReleaseDate { get; set; }
        public string Overview { get; set; }
        public string PosterPath { get; set; }        // TMDB relative path, e.g. /abc.jpg
        public string BackdropPath { get; set; }
        public double? VoteAverage { get; set; }
        public string Status { get; set; }             // Availability.*
        public string StatusLabel { get; set; }
        public bool InEmbyLibrary { get; set; }
        /// <summary>Which Emby library item matched, for the settings-page preview.</summary>
        public string LibraryMatch { get; set; }
        public bool CanRequest { get; set; }
        /// <summary>A YouTube trailer exists (show or season for TV). Null = not looked up.</summary>
        public bool? HasTrailer { get; set; }
        /// <summary>Scores, cast and crew etc. from TitleIndex; null when not looked up.</summary>
        [System.Runtime.Serialization.IgnoreDataMember]
        public TitleInfo Info { get; set; }
    }

    public class DiscoverPageDto
    {
        public bool Ok { get; set; } = true;
        public string Error { get; set; }
        public int Page { get; set; }
        public int TotalPages { get; set; }
        public int TotalResults { get; set; }
        public List<DiscoverItemDto> Items { get; set; } = new List<DiscoverItemDto>();
    }

    public class SeasonDto
    {
        public int SeasonNumber { get; set; }
        public string Name { get; set; }
        public int? EpisodeCount { get; set; }
        public string AirDate { get; set; }
        public string Status { get; set; }
        public string StatusLabel { get; set; }
        public bool CanRequest { get; set; }
    }

    public class TitleDetailsDto : DiscoverItemDto
    {
        public bool Ok { get; set; } = true;
        public string Error { get; set; }
        public string Tagline { get; set; }
        public int? Runtime { get; set; }
        public List<string> Genres { get; set; } = new List<string>();
        public List<SeasonDto> Seasons { get; set; } = new List<SeasonDto>();
    }

    public class RequestOutcomeDto
    {
        public bool Ok { get; set; }
        /// <summary>
        /// submitted | approved | already_requested | already_available | nothing_to_request |
        /// not_linked | forbidden | rate_limited | invalid | seerr_unreachable | seerr_error | not_configured
        /// </summary>
        public string Code { get; set; }
        public string Message { get; set; }
        public int? SeerrRequestId { get; set; }
        public int? SeerrRequestStatus { get; set; }
    }

    public class UserLinkRowDto
    {
        public string EmbyUserName { get; set; }
        public string EmbyUserId { get; set; }
        public bool EmbyIsAdmin { get; set; }
        public int? SeerrUserId { get; set; }
        public string SeerrUserName { get; set; }
        public string LinkSource { get; set; }    // auto | admin-fallback | none
        public string Note { get; set; }
    }

    public class AdminStatusDto
    {
        public bool Ok { get; set; }
        public string Message { get; set; }
        public string SeerrVersion { get; set; }
        public string ApiKeyOwner { get; set; }
        public List<UserLinkRowDto> Users { get; set; } = new List<UserLinkRowDto>();
    }

    public class ChannelRefreshDto
    {
        public bool Ok { get; set; }
        public bool Running { get; set; }
        public double? Progress { get; set; }
        public string LastStatus { get; set; }
        public string LastEnded { get; set; }
        public string Message { get; set; }
    }

    public class ConnectionTestDto
    {
        public bool Ok { get; set; }
        public string Message { get; set; }
    }

    public class LibraryDto
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string CollectionType { get; set; }
    }

    public class CertificationDto
    {
        public string Code { get; set; }
        public string Meaning { get; set; }
        public int Order { get; set; }
    }

    public class ArrFolderDto
    {
        public string Path { get; set; }
        public double? FreeSpaceGb { get; set; }
    }

    public class ArrTagDto
    {
        public int Id { get; set; }
        public string Label { get; set; }
    }

    public class ArrServiceDto
    {
        public string App { get; set; }
        public int ServerId { get; set; }
        public string ServerName { get; set; }
        public string DefaultRootFolder { get; set; }
        public List<int> DefaultTagIds { get; set; } = new List<int>();
        public List<ArrFolderDto> RootFolders { get; set; } = new List<ArrFolderDto>();
        public List<ArrTagDto> Tags { get; set; } = new List<ArrTagDto>();
        public string Error { get; set; }
    }

    public class ServicesDto
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        public ArrServiceDto Radarr { get; set; }
        public ArrServiceDto Sonarr { get; set; }
    }

    public class OptionDto
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }

    public class TmdbOptionsDto
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        public List<OptionDto> Genres { get; set; } = new List<OptionDto>();
        public List<OptionDto> Providers { get; set; } = new List<OptionDto>();
        public List<OptionDto> Regions { get; set; } = new List<OptionDto>();
        public List<OptionDto> Languages { get; set; } = new List<OptionDto>();
        /// <summary>Age ratings of the requested country, lowest first.</summary>
        public List<CertificationDto> Certifications { get; set; } = new List<CertificationDto>();
        public int AnimeKeywordId { get; set; } = 210024;
    }

    public class PreviewItemDto
    {
        public string Title { get; set; }
        public int? Year { get; set; }
        public string MediaType { get; set; }
        public string PosterPath { get; set; }
        public string Status { get; set; }
        public bool Hidden { get; set; }
        /// <summary>Why the title counts as owned (Emby library item or Seerr).</summary>
        public string OwnedReason { get; set; }
        public bool? HasTrailer { get; set; }
        /// <summary>"IMDb 7.8 · Rotten Tomatoes 92% · Audience 85%".</summary>
        public string Scores { get; set; }
    }

    public class PreviewDto
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        public string Name { get; set; }
        public string ResolvedSource { get; set; }
        public int Movies { get; set; }
        public int Shows { get; set; }
        public int HiddenAsOwned { get; set; }
        /// <summary>Shows left out by the age-rating filter (TMDB cannot filter shows by rating).</summary>
        public int RemovedByRating { get; set; }
        public List<PreviewItemDto> Items { get; set; } = new List<PreviewItemDto>();
    }
}

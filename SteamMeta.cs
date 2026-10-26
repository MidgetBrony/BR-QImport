using System;
using System.Collections.Generic;

namespace BR_QImport
{
    public class OwnedGamesCachePayload
    {
        public int Version { get; set; }

        public List<int> AppIds { get; set; } = new();

        public Dictionary<int, string> Names { get; set; } = new();

        public Dictionary<int, int> Playtime { get; set; } = new();
    }

    public class SteamMeta
    {
        public int PlayTimeMinutes { get; set; }
        public string AppType { get; set; } = "";
        public string? GameType { get; set; }
        public string Name { get; set; } = "";
        public string ShortDescription { get; set; } = "";
        public string DetailedDescription { get; set; } = "";
        public string AboutTheGame { get; set; } = "";
        public string BoxArtUrlBase { get; set; } = "";
        public string FallbackHeaderUrl { get; set; } = "";
        public string ReleaseDate { get; set; } = "";
        public List<string> Developers { get; set; } = new();
        public List<string> Publishers { get; set; } = new();
        public List<string> Genres { get; set; } = new();
        public string LaunchExePath { get; set; } = "";
        public string LaunchArguments { get; set; } = "";
        public List<string> ScreenshotUrls { get; set; } = new();
        public int AppId { get; set; }
    }


    [Flags]
    public enum FailureFlags
    {
        None = 0,

        NoMetadata = 1,

        NoCoverArt = 2,

        NoScreenshots = 4
    }
    public class SteamFetchResult
    {
        public SteamFetchStatus Status { get; init; }

        public SteamMeta? Meta { get; init; }
    }

    public class CacheFailures
    {
        public HashSet<int> NoMetadata { get; set; } = new();

        public HashSet<int> NoCoverArt { get; set; } = new();

        public HashSet<int> NoScreenshots { get; set; } = new();

        public HashSet<int> ServerError { get; set; } = new();
    }
}

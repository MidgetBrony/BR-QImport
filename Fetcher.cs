using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace BR_QImport;

public enum SteamFetchStatus
{
    Success,
    NoMetadata,
    NetworkError,
    InvalidResponse,
    RateLimited,
    ServerError
}

public class ProgressInfo
{
    public int Current { get; init; }
    public int Total { get; init; }

    public string Status { get; init; } = "";
    public string? CurrentGame { get; init; }

    public double Percent =>
    Total > 0 ? (double)Current / Total * 100 : 0;
}

public class Fetcher
{
    private const int FirstFamilyCustomAppId = -500000;
    private const int OwnedGamesCacheVersion = 2;
    private readonly HttpClient _client = new();
    public event Action<ProgressInfo>? ProgressChanged;
    private readonly string _failureFile = "failures.json";

    public async Task<List<int>> GetFamilySharedAppIdsAsync(
        string tokenInput,
        IReadOnlySet<int> ownedAppIds)
    {
        string token = ExtractWebApiToken(tokenInput);
        string steamId = GetSteamIdFromToken(token);

        ProgressChanged?.Invoke(new ProgressInfo
        {
            Status = "Checking Steam Family library"
        });

        using JsonDocument familyDocument = await GetSteamApiResponseAsync(
            "IFamilyGroupsService/GetFamilyGroupForUser/v1",
            new Dictionary<string, string>
            {
                ["access_token"] = token,
                ["steamid"] = steamId,
                ["include_family_group_response"] = "true"
            });

        JsonElement familyResponse = familyDocument.RootElement.GetProperty("response");
        if (familyResponse.TryGetProperty("is_not_member_of_any_group", out JsonElement notMember) &&
            notMember.ValueKind == JsonValueKind.True)
        {
            throw new InvalidOperationException("This Steam account is not a member of a Steam Family.");
        }

        if (!familyResponse.TryGetProperty("family_groupid", out JsonElement familyGroupIdElement))
            throw new InvalidOperationException("Steam did not return a Family group ID.");

        string familyGroupId = familyGroupIdElement.ValueKind == JsonValueKind.String
            ? familyGroupIdElement.GetString() ?? ""
            : familyGroupIdElement.GetRawText();

        if (string.IsNullOrWhiteSpace(familyGroupId) || familyGroupId == "0")
            throw new InvalidOperationException("This Steam account is not a member of a Steam Family.");

        using JsonDocument libraryDocument = await GetSteamApiResponseAsync(
            "IFamilyGroupsService/GetSharedLibraryApps/v1",
            new Dictionary<string, string>
            {
                ["access_token"] = token,
                ["family_groupid"] = familyGroupId,
                ["include_own"] = "false",
                ["include_excluded"] = "true",
                ["include_free"] = "false",
                ["include_non_games"] = "false",
                ["language"] = "english"
            });

        JsonElement libraryResponse = libraryDocument.RootElement.GetProperty("response");
        if (!libraryResponse.TryGetProperty("apps", out JsonElement apps) ||
            apps.ValueKind != JsonValueKind.Array)
        {
            return new List<int>();
        }

        return apps.EnumerateArray()
            .Where(app => !app.TryGetProperty("exclude_reason", out JsonElement reason) ||
                          reason.GetInt32() == 0)
            .Select(app => app.GetProperty("appid").GetInt32())
            .Where(appId => !ownedAppIds.Contains(appId))
            .Distinct()
            .ToList();
    }

    private async Task<JsonDocument> GetSteamApiResponseAsync(
        string method,
        IReadOnlyDictionary<string, string> parameters)
    {
        string query = string.Join("&", parameters.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

        using HttpResponseMessage response = await _client.GetAsync(
            $"https://api.steampowered.com/{method}/?{query}");

        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or
            System.Net.HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException(
                "Steam rejected the Family token. Open the token page again and paste a fresh token.");
        }

        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync();

        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Steam returned an invalid Family library response.");
        }
    }

    private static string ExtractWebApiToken(string input)
    {
        string value = input.Trim().Trim('"', '\'');
        if (value.StartsWith('{'))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(value);
                value = document.RootElement
                    .GetProperty("data")
                    .GetProperty("webapi_token")
                    .GetString() ?? "";
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                throw new InvalidOperationException(
                    "The token JSON does not contain data.webapi_token.");
            }
        }

        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("Enter a Steam Family token.");

        return value;
    }

    private static string GetSteamIdFromToken(string token)
    {
        string[] parts = token.Split('.');
        if (parts.Length != 3)
            throw new InvalidOperationException("The Steam Family token is not a valid token.");

        try
        {
            string payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using JsonDocument document = JsonDocument.Parse(
                Encoding.UTF8.GetString(Convert.FromBase64String(payload)));

            JsonElement root = document.RootElement;
            string steamId = root.GetProperty("sub").ToString();

            if (root.TryGetProperty("exp", out JsonElement expires) &&
                expires.TryGetInt64(out long expiresAt) &&
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= expiresAt)
            {
                throw new InvalidOperationException(
                    "The Steam Family token has expired. Open the token page again.");
            }

            return steamId;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch
        {
            throw new InvalidOperationException("The Steam Family token could not be read.");
        }
    }

    private async Task WriteMetaAsync(string folder, SteamMeta meta)
    {
        Directory.CreateDirectory(folder);

        await File.WriteAllTextAsync(
            Path.Combine(folder, "meta.json"),
            JsonSerializer.Serialize(meta, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
    }

    public async Task ImportFamilySharedGamesAsync(
        IEnumerable<int> steamAppIds,
        string cacheFolder)
    {
        Directory.CreateDirectory(cacheFolder);

        List<int> familyIds = steamAppIds.Distinct().OrderBy(id => id).ToList();
        Dictionary<int, int> existingMappings = await LoadFamilyCustomMappingsAsync(cacheFolder);
        HashSet<int> currentFamilyIds = familyIds.ToHashSet();

        foreach ((int steamAppId, int customAppId) in existingMappings.ToList())
        {
            if (currentFamilyIds.Contains(steamAppId))
                continue;

            string staleDirectory = Path.Combine(cacheFolder, customAppId.ToString());
            if (Directory.Exists(staleDirectory))
                Directory.Delete(staleDirectory, recursive: true);

            existingMappings.Remove(steamAppId);
        }

        string? boxroomDataDirectory = Directory.GetParent(cacheFolder)?.FullName;
        if (boxroomDataDirectory != null)
        {
            string legacyFamilyFile = Path.Combine(boxroomDataDirectory, "familyowned.json");
            if (File.Exists(legacyFamilyFile))
                File.Delete(legacyFamilyFile);
        }

        HashSet<int> usedCustomIds = Directory.EnumerateDirectories(cacheFolder)
            .Select(Path.GetFileName)
            .Where(name => int.TryParse(name, out int id) && id <= FirstFamilyCustomAppId)
            .Select(name => int.Parse(name!))
            .ToHashSet();

        int nextCustomId = FirstFamilyCustomAppId;
        int current = 0;

        foreach (int steamAppId in familyIds)
        {
            current++;
            bool isNewMapping = !existingMappings.TryGetValue(steamAppId, out int customAppId);
            if (isNewMapping)
            {
                while (usedCustomIds.Contains(nextCustomId))
                    nextCustomId--;

                customAppId = nextCustomId;
            }

            ProgressChanged?.Invoke(new ProgressInfo
            {
                Current = current,
                Total = familyIds.Count,
                Status = "Importing family game as custom",
                CurrentGame = steamAppId.ToString()
            });

            string oldSteamDirectory = Path.Combine(cacheFolder, steamAppId.ToString());
            string customDirectory = Path.Combine(cacheFolder, customAppId.ToString());

            SteamMeta? meta = await TryReadMetaAsync(customDirectory)
                ?? await TryReadMetaAsync(oldSteamDirectory);

            if (meta == null || string.IsNullOrWhiteSpace(meta.Name))
            {
                SteamFetchResult result = await GetSteamData(steamAppId);
                if (result.Status != SteamFetchStatus.Success || result.Meta == null)
                {
                    ProgressChanged?.Invoke(new ProgressInfo
                    {
                        Current = current,
                        Total = familyIds.Count,
                        Status = $"Family metadata failed ({result.Status})",
                        CurrentGame = steamAppId.ToString()
                    });
                    continue;
                }

                meta = result.Meta;
            }

            if (isNewMapping)
            {
                usedCustomIds.Add(customAppId);
                nextCustomId--;
            }

            Directory.CreateDirectory(customDirectory);
            CopyCachedMedia(oldSteamDirectory, customDirectory);

            if (!meta.AppType.Equals("custom", StringComparison.OrdinalIgnoreCase))
                meta.GameType = meta.AppType;
            meta.AppType = "custom";
            meta.AppId = customAppId;
            meta.LaunchExePath = $"steam://run//{steamAppId}";
            meta.LaunchArguments = "";
            await WriteMetaAsync(customDirectory, meta);

            if (Directory.Exists(oldSteamDirectory))
                Directory.Delete(oldSteamDirectory, recursive: true);
        }
    }

    private static async Task<Dictionary<int, int>> LoadFamilyCustomMappingsAsync(string cacheFolder)
    {
        Dictionary<int, int> mappings = new();

        foreach (string directory in Directory.EnumerateDirectories(cacheFolder))
        {
            if (!int.TryParse(Path.GetFileName(directory), out int customId) ||
                customId > FirstFamilyCustomAppId)
                continue;

            SteamMeta? meta = await TryReadMetaAsync(directory);
            if (meta != null && TryGetSteamLaunchAppId(meta, out int steamAppId))
                mappings[steamAppId] = customId;
        }

        return mappings;
    }

    private static async Task<SteamMeta?> TryReadMetaAsync(string directory)
    {
        string path = Path.Combine(directory, "meta.json");
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonSerializer.Deserialize<SteamMeta>(await File.ReadAllTextAsync(path));
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetSteamLaunchAppId(SteamMeta meta, out int steamAppId)
    {
        const string Prefix = "steam://run//";
        steamAppId = 0;
        return meta.LaunchExePath.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) &&
               int.TryParse(meta.LaunchExePath[Prefix.Length..].TrimEnd('/'), out steamAppId);
    }

    private static void CopyCachedMedia(string sourceDirectory, string targetDirectory)
    {
        if (!Directory.Exists(sourceDirectory))
            return;

        foreach (string sourcePath in Directory.EnumerateFiles(sourceDirectory))
        {
            if (Path.GetFileName(sourcePath).Equals("meta.json", StringComparison.OrdinalIgnoreCase))
                continue;

            string targetPath = Path.Combine(targetDirectory, Path.GetFileName(sourcePath));
            if (!File.Exists(targetPath))
                File.Copy(sourcePath, targetPath);
        }
    }
    public async Task DownloadBoxArtAsync(string boxRoomCachePath)
    {
        if (!Directory.Exists(boxRoomCachePath))
            return;

        CacheFailures failures = await LoadFailuresAsync();
        bool failuresChanged = false;

        
        string[] gameFolders = Directory.GetDirectories(boxRoomCachePath);

        int total = gameFolders.Length;
        int current = 0;

        foreach (string gameFolder in gameFolders)
        {
            current++;
            string metaPath = Path.Combine(gameFolder, "meta.json");

            if (!File.Exists(metaPath))
                continue;

            SteamMeta? meta = JsonSerializer.Deserialize<SteamMeta>(
                await File.ReadAllTextAsync(metaPath));

            if (meta == null)
                continue;

            int appId = int.Parse(Path.GetFileName(gameFolder));

            if (failures.NoCoverArt.Contains(appId))
            {
                ProgressChanged?.Invoke(new ProgressInfo
                {
                    Status = $"Skipping cover art for {meta.Name} (known unavailable)",
                    Current = current,
                    Total = total,
                    CurrentGame = meta.Name
                });

                continue;
            }

            string output = Path.Combine(gameFolder, "boxart.jpg");

            if (File.Exists(output))
                continue;

            ProgressChanged?.Invoke(new ProgressInfo
            {
                Current = current,
                Total = total,
                Status = "Downloading cover art",
                CurrentGame = meta.Name
            });

            try
            {
                int storeAppId = TryGetSteamLaunchAppId(meta, out int familySteamAppId)
                    ? familySteamAppId
                    : appId;
                string url =
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{storeAppId}/library_600x900.jpg";

                byte[] image = await _client.GetByteArrayAsync(url);

                await File.WriteAllBytesAsync(output, image);
            }
            catch
            {
                failures.NoCoverArt.Add(appId);
                failuresChanged = true;

                ProgressChanged?.Invoke(new ProgressInfo
                {
                    Current = current,
                    Total = total,
                    Status = "Cover art unavailable",
                    CurrentGame = meta.Name
                });
            }

            await Task.Delay(100);
        }

        if (failuresChanged)
            await SaveFailuresAsync(failures);
    }

    public async Task<SteamFetchResult> GetSteamData(int steamId)
    {
        string steamUrl =
            $"https://store.steampowered.com/api/appdetails?appids={steamId}";

        int serverRetries = 0;
        const int MaxServerRetries = 3;

        while (true)
        {
            HttpResponseMessage response;

            try
            {
                response = await _client.GetAsync(steamUrl);
            }
            catch
            {
                return new SteamFetchResult
                {
                    Status = SteamFetchStatus.NetworkError
                };
            }

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                TimeSpan waitTime =
                    response.Headers.RetryAfter?.Delta
                    ?? TimeSpan.FromSeconds(30);

                ProgressChanged?.Invoke(new ProgressInfo
                {
                    Status = $"Rate limited by Steam API. Waiting {waitTime.TotalSeconds} seconds before retrying..."
                });

                await Task.Delay(waitTime);
                continue;
            }

            if ((int)response.StatusCode >= 500)
            {
                serverRetries++;

                if (serverRetries >= MaxServerRetries)
                {
                    return new SteamFetchResult
                    {
                        Status = SteamFetchStatus.ServerError
                    };
                }

                ProgressChanged?.Invoke(new ProgressInfo
                {
                    Status = $"Steam server returned {(int)response.StatusCode}. Retry {serverRetries}/{MaxServerRetries} in 30 seconds..."
                });

                await Task.Delay(TimeSpan.FromSeconds(30));
                continue;
            }

            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync();

            JsonNode? root;

            try
            {
                root = JsonNode.Parse(json);
            }
            catch
            {
                return new SteamFetchResult
                {
                    Status = SteamFetchStatus.InvalidResponse
                };
            }

            if (root?[steamId.ToString()]?["success"]?.GetValue<bool>() != true)
            {
                return new SteamFetchResult
                {
                    Status = SteamFetchStatus.NoMetadata
                };
            }

            JsonNode gameData = root[steamId.ToString()]!["data"]!;

            return new SteamFetchResult
            {
                Status = SteamFetchStatus.Success,

                Meta = new SteamMeta
                {
                    PlayTimeMinutes = 0,
                    AppId = steamId,
                    Name = gameData["name"]?.ToString() ?? "",
                    AppType = gameData["type"]?.ToString() ?? "",
                    ShortDescription = gameData["short_description"]?.ToString() ?? "",
                    DetailedDescription = gameData["detailed_description"]?.ToString() ?? "",
                    AboutTheGame = gameData["about_the_game"]?.ToString() ?? "",
                    BoxArtUrlBase = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{steamId}/",
                    FallbackHeaderUrl = gameData["header_image"]?.ToString() ?? "",
                    ReleaseDate = gameData["release_date"]?["date"]?.ToString() ?? "",
                    Developers = gameData["developers"]?.AsArray()
                        .Select(x => x?.ToString() ?? "")
                        .ToList() ?? new(),

                    Publishers = gameData["publishers"]?.AsArray()
                        .Select(x => x?.ToString() ?? "")
                        .ToList() ?? new(),

                    Genres = gameData["genres"]?.AsArray()
                        .Select(x => x?["description"]?.ToString() ?? "")
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .ToList() ?? new(),

                    ScreenshotUrls = gameData["screenshots"]?.AsArray()
                        .Select(x => x?["path_full"]?.ToString() ?? "")
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .ToList() ?? new()
                }
            };
        }
    }

    public async Task DownloadScreenshotsAsync(string boxRoomCachePath)
    {
        if (!Directory.Exists(boxRoomCachePath))
            return;

        CacheFailures failures = await LoadFailuresAsync();
        bool failuresChanged = false;

        string[] gameFolders = Directory.GetDirectories(boxRoomCachePath);

        int total = gameFolders.Length;
        int current = 0;

        foreach (string gameFolder in gameFolders)
        {
            current++;
            string metaPath = Path.Combine(gameFolder, "meta.json");

            if (!File.Exists(metaPath))
                continue;

            SteamMeta? meta = JsonSerializer.Deserialize<SteamMeta>(
                await File.ReadAllTextAsync(metaPath));

            if (meta == null)
                continue;

            int appId = int.Parse(Path.GetFileName(gameFolder));

            if (failures.NoScreenshots.Contains(appId))
            {
                ProgressChanged?.Invoke(new ProgressInfo
                {
                    Current = current,
                    Total = total,
                    Status = "Skipping screenshots",
                    CurrentGame = meta.Name
                });

                continue;
            }

            if (meta.ScreenshotUrls.Count == 0)
            {
                failures.NoScreenshots.Add(appId);
                failuresChanged = true;

                ProgressChanged?.Invoke(new ProgressInfo
                {
                    Status = "No screenshots available",
                    Current = current,
                    Total = total,
                    CurrentGame = meta.Name
                });

                continue;
            }

            ProgressChanged?.Invoke(new ProgressInfo
            {
                Current = current,
                Total = total,
                Status = "Downloading screenshots",
                CurrentGame = meta.Name
            });

            List<Task> tasks = new();

            for (int i = 0; i < Math.Min(3, meta.ScreenshotUrls.Count); i++)
            {
                int index = i;

                string url = meta.ScreenshotUrls[index];
                string output = Path.Combine(gameFolder, $"screen_{index}.jpg");

                if (File.Exists(output))
                    continue;

                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        byte[] image = await _client.GetByteArrayAsync(url);
                        await File.WriteAllBytesAsync(output, image);
                    }
                    catch
                    {
                        // Ignore individual screenshot failures.
                        // Some screenshots occasionally disappear from Steam.
                    }
                }));
            }

            await Task.WhenAll(tasks);
        }

        if (failuresChanged)
            await SaveFailuresAsync(failures);
    }

    public async Task UpdateOwnedGamesAsync(string cacheFolder)
    {
        ProgressChanged?.Invoke(new ProgressInfo
        {
            Current = 1,
            Total = 1,
            Status = "Updating owned_games.json"
        });

        string ownedGamesPath = Path.Combine(cacheFolder, "owned_games.json");
        OwnedGamesCachePayload existing = await LoadOwnedGamesCacheAsync(ownedGamesPath);
        OwnedGamesCachePayload owned = new()
        {
            Version = OwnedGamesCacheVersion
        };

        foreach (string directory in Directory.EnumerateDirectories(cacheFolder)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (!int.TryParse(Path.GetFileName(directory), out int appId))
                continue;

            SteamMeta? meta = await TryReadMetaAsync(directory);
            string name = meta?.Name ?? "";
            if (string.IsNullOrWhiteSpace(name) &&
                existing.Names.TryGetValue(appId, out string? existingName))
            {
                name = existingName;
            }

            int playtime = meta?.PlayTimeMinutes ?? 0;
            if (existing.Playtime.TryGetValue(appId, out int existingPlaytime))
                playtime = Math.Max(playtime, existingPlaytime);

            owned.AppIds.Add(appId);
            owned.Names[appId] = name;
            owned.Playtime[appId] = playtime;
        }

        owned.AppIds.Sort();

        await File.WriteAllTextAsync(ownedGamesPath, JsonSerializer.Serialize(owned));
    }

    private static async Task<OwnedGamesCachePayload> LoadOwnedGamesCacheAsync(string path)
    {
        if (!File.Exists(path))
            return new OwnedGamesCachePayload();

        try
        {
            OwnedGamesCachePayload payload =
                JsonSerializer.Deserialize<OwnedGamesCachePayload>(
                    await File.ReadAllTextAsync(path))
                ?? new OwnedGamesCachePayload();

            payload.AppIds ??= new List<int>();
            payload.Names ??= new Dictionary<int, string>();
            payload.Playtime ??= new Dictionary<int, int>();
            return payload;
        }
        catch
        {
            return new OwnedGamesCachePayload();
        }
    }

    public async Task ScrapeGamesAsync(IEnumerable<int> appIds, string targetFolder)
    {
        Directory.CreateDirectory(targetFolder);

        CacheFailures failures = await LoadFailuresAsync();

        int total = appIds.Count();
        int current = 0;
        bool failuresChanged = false;

        foreach (int appId in appIds.Distinct())
        {
            current++;

            // Permanent failure
            if (failures.NoMetadata.Contains(appId))
            {
                ProgressChanged?.Invoke(new ProgressInfo
                {
                    Current = current,
                    Total = total,
                    Status = "Skipping (no metadata)",
                    CurrentGame = appId.ToString()
                });

                continue;
            }

            // Retry previous Steam 500's once per scrape.
            if (failures.ServerError.Remove(appId))
            {
                failuresChanged = true;

                ProgressChanged?.Invoke(new ProgressInfo
                {
                    Current = current,
                    Total = total,
                    Status = "Retrying previous server error",
                    CurrentGame = appId.ToString()
                });
            }

            string targetDir = Path.Combine(targetFolder, appId.ToString());
            string metaPath = Path.Combine(targetDir, "meta.json");

            if (File.Exists(metaPath))
            {
                try
                {
                    SteamMeta? existing = JsonSerializer.Deserialize<SteamMeta>(
                        await File.ReadAllTextAsync(metaPath));

                    if (existing != null &&
                        !string.IsNullOrWhiteSpace(existing.Name) &&
                        !string.IsNullOrWhiteSpace(existing.AppType))
                    {
                        ProgressChanged?.Invoke(new ProgressInfo
                        {
                            Current = current,
                            Total = total,
                            Status = "Skipping",
                            CurrentGame = existing.Name
                        });

                        continue;
                    }

                    ProgressChanged?.Invoke(new ProgressInfo
                    {
                        Current = current,
                        Total = total,
                        Status = "Repairing metadata",
                        CurrentGame = appId.ToString()
                    });
                }
                catch
                {
                    ProgressChanged?.Invoke(new ProgressInfo
                    {
                        Current = current,
                        Total = total,
                        Status = "Repairing corrupt metadata",
                        CurrentGame = appId.ToString()
                    });
                }
            }
            else
            {
                ProgressChanged?.Invoke(new ProgressInfo
                {
                    Current = current,
                    Total = total,
                    Status = "Fetching metadata",
                    CurrentGame = appId.ToString()
                });
            }

            SteamFetchResult result = await GetSteamData(appId);

            switch (result.Status)
            {
                case SteamFetchStatus.NoMetadata:

                    failures.NoMetadata.Add(appId);
                    failuresChanged = true;

                    ProgressChanged?.Invoke(new ProgressInfo
                    {
                        Current = current,
                        Total = total,
                        Status = "No metadata available",
                        CurrentGame = appId.ToString()
                    });

                    continue;

                case SteamFetchStatus.ServerError:

                    failures.ServerError.Add(appId);
                    failuresChanged = true;

                    ProgressChanged?.Invoke(new ProgressInfo
                    {
                        Current = current,
                        Total = total,
                        Status = "Steam server error (will retry next scrape)",
                        CurrentGame = appId.ToString()
                    });

                    continue;

                case SteamFetchStatus.Success:
                    break;

                default:

                    ProgressChanged?.Invoke(new ProgressInfo
                    {
                        Current = current,
                        Total = total,
                        Status = $"Failed ({result.Status})",
                        CurrentGame = appId.ToString()
                    });

                    continue;
            }

            SteamMeta meta = result.Meta!;

            await WriteMetaAsync(targetDir, meta);

            ProgressChanged?.Invoke(new ProgressInfo
            {
                Current = current,
                Total = total,
                Status = "Saved metadata",
                CurrentGame = meta.Name
            });

            await Task.Delay(Random.Shared.Next(200, 500));
        }

        if (failuresChanged)
        {
            await SaveFailuresAsync(failures);
        }

        await UpdateOwnedGamesAsync(targetFolder);
    }

    private async Task SaveFailuresAsync(CacheFailures failures)
    {
        await File.WriteAllTextAsync(
            _failureFile,
            JsonSerializer.Serialize(
                failures,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                }));
    }

    private async Task<CacheFailures> LoadFailuresAsync()
    {
        if (!File.Exists(_failureFile))
            return new CacheFailures();

        try
        {
            return JsonSerializer.Deserialize<CacheFailures>(
                       await File.ReadAllTextAsync(_failureFile))
                   ?? new CacheFailures();
        }
        catch
        {
            return new CacheFailures();
        }
    }
}

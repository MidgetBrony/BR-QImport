using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
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
    private readonly HttpClient _client = new();
    public event Action<ProgressInfo>? ProgressChanged;
    private readonly string _failureFile = "failures.json";

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
                string url =
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/library_600x900.jpg";

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

        List<int> ids = Directory.EnumerateDirectories(cacheFolder)
            .Select(Path.GetFileName)
            .Where(x => int.TryParse(x, out _))
            .Select(int.Parse)
            .OrderBy(x => x)
            .ToList();

        var owned = new
        {
            AppIds = ids
        };

        await File.WriteAllTextAsync(
            Path.Combine(cacheFolder, "owned_games.json"),
            JsonSerializer.Serialize(owned));
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

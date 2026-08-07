using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace BR_QImport
{
    public partial class MainWindow : Window
    {
        private readonly Fetcher fetcher = new();

        public MainWindow()
        {
            InitializeComponent();

            CacheDirectoryTextBox.Text = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "..",
                "LocalLow",
                "NestedLoop",
                "BOXROOM",
                "steam_cache_v2"));

            fetcher.ProgressChanged += progress =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    StatusLabel.Text = progress.Status;
                    CurrentGameLabel.Text = progress.CurrentGame ?? "-";

                    ImportProgressBar.Maximum = Math.Max(1, progress.Total);
                    ImportProgressBar.Value = progress.Current;

                    ProgressLabel.Text = $"{progress.Current} / {progress.Total}";
                });
            };
        }

        private void CloseButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            Close();
        }

        private async void ImportButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            ImportButton.IsEnabled = false;

            try
            {
                StatusLabel.Text = "Preparing...";
                CurrentGameLabel.Text = "-";
                ImportProgressBar.Maximum = 100;
                ImportProgressBar.Value = 0;
                ProgressLabel.Text = "";

                if (string.IsNullOrWhiteSpace(JsonTextBox.Text))
                {
                    StatusLabel.Text = "No JSON supplied.";
                    return;
                }

                if (string.IsNullOrWhiteSpace(CacheDirectoryTextBox.Text))
                {
                    StatusLabel.Text = "No cache directory selected.";
                    return;
                }

                string cacheDir = CacheDirectoryTextBox.Text;

                Directory.CreateDirectory(cacheDir);

                await using FileStream stream = File.OpenRead(JsonTextBox.Text);

                using JsonDocument doc = JsonDocument.Parse(stream);

                List<int> ownedAppIds = doc.RootElement
                    .GetProperty("rgOwnedApps")
                    .EnumerateArray()
                    .Select(x => x.GetInt32())
                    .Distinct()
                    .ToList();

                List<int> familyAppIds = new();

                if (IncludeFamilyCheckBox.IsChecked == true)
                {
                    HashSet<int> ownedSet = ownedAppIds.ToHashSet();
                    familyAppIds = await fetcher.GetFamilySharedAppIdsAsync(
                        FamilyTokenTextBox.Text ?? "",
                        ownedSet);

                }

                if (ScrapeCheckBox.IsChecked == true)
                {
                    await fetcher.ScrapeGamesAsync(ownedAppIds, cacheDir);
                }
                else
                {
                    foreach (int appId in ownedAppIds)
                    {
                        Directory.CreateDirectory(
                            Path.Combine(cacheDir, appId.ToString()));
                    }

                }

                await fetcher.ImportFamilySharedGamesAsync(familyAppIds, cacheDir);
                await fetcher.UpdateOwnedGamesAsync(cacheDir);

                if (CoverArtCheckBox.IsChecked == true)
                {
                    await fetcher.DownloadBoxArtAsync(cacheDir);
                }

                if (ScreenshotCheckBox.IsChecked == true)
                {
                    await fetcher.DownloadScreenshotsAsync(cacheDir);
                }

                StatusLabel.Text = "Import complete";
                int processedCount = ownedAppIds.Count + familyAppIds.Count;
                CurrentGameLabel.Text = $"{processedCount} games processed";

                ImportProgressBar.Maximum = 1;
                ImportProgressBar.Value = 1;

                ProgressLabel.Text = $"{processedCount} games";
            }
            catch (Exception ex)
            {
                StatusLabel.Text = "Error";
                CurrentGameLabel.Text = ex.Message;
            }
            finally
            {
                ImportButton.IsEnabled = true;
            }
        }

        private void OpenSteamButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://store.steampowered.com/dynamicstore/userdata/",
                UseShellExecute = true
            });
        }

        private void OpenFamilyTokenButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://store.steampowered.com/pointssummary/ajaxgetasyncconfig",
                UseShellExecute = true
            });
        }

        private async void BrowseJsonButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Steam UserData JSON",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("JSON")
                    {
                        Patterns = ["*.json"]
                    }
                ]
            });

            if (files.Count == 0)
                return;

            JsonTextBox.Text = files[0].Path.LocalPath;
        }

        private async void BrowseCacheButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions
                {
                    Title = "Select BOXROOM steam_cache_v2 folder",
                    AllowMultiple = false
                });

            if (folders.Count == 0)
                return;

            CacheDirectoryTextBox.Text = folders[0].Path.LocalPath;
        }

        public class SteamUserData
        {
            public List<int>? rgOwnedApps { get; set; }
        }
    }
}

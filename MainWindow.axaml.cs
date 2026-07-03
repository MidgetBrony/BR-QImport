using Avalonia.Controls;
using Avalonia.Platform.Storage;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia.Threading;

namespace BR_QImport
{
    public partial class MainWindow : Window
    {
        Fetcher fetcher = new();

        public MainWindow()
        {
            InitializeComponent();

            CacheDirectoryTextBox.Text = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "..",
    "LocalLow",
    "NestedLoop",
    "BOXROOM",
    "steam_cache_v2");

            CacheDirectoryTextBox.Text = Path.GetFullPath(CacheDirectoryTextBox.Text);


            fetcher.StatusChanged += text =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    StatusTextBox.Text += $"{DateTime.Now:HH:mm:ss}  {text}{Environment.NewLine}";

                    StatusTextBox.CaretIndex = StatusTextBox.Text.Length;
                });
            };
        }
        private void Log(string message)
        {
            Dispatcher.UIThread.Post(() =>
            {
                StatusTextBox.Text += $"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}";
                StatusTextBox.CaretIndex = StatusTextBox.Text.Length;
            });
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
                StatusTextBox.Text = "Preparing...\n";

                if (string.IsNullOrWhiteSpace(JsonTextBox.Text))
                {
                    StatusTextBox.Text = "No JSON supplied.";
                    return;
                }

                if (string.IsNullOrWhiteSpace(CacheDirectoryTextBox.Text))
                {
                    StatusTextBox.Text = "No cache directory selected.";
                    return;
                }

                Directory.CreateDirectory(CacheDirectoryTextBox.Text);

                using JsonDocument doc = JsonDocument.Parse(JsonTextBox.Text);

                List<int> appIds = doc.RootElement
                    .GetProperty("rgOwnedApps")
                    .EnumerateArray()
                    .Select(x => x.GetInt32())
                    .Distinct()
                    .ToList();

                StatusTextBox.Clear();

                StatusTextBox.Text =
                    $"Started: {DateTime.Now:G}{Environment.NewLine}" +
                    $"Games: {appIds.Count}{Environment.NewLine}{Environment.NewLine}";

                Log("Starting import...");

                if (ScrapeCheckBox.IsChecked == true)
                {
                    Log($"Scraping {appIds.Count} games from Steam...");

                    await fetcher.ScrapeGamesAsync(
                        appIds,
                        CacheDirectoryTextBox.Text);
                }
                else
                {
                    Log("Creating folders...");

                    foreach (int appId in appIds)
                    {
                        Directory.CreateDirectory(
                            Path.Combine(CacheDirectoryTextBox.Text, appId.ToString()));
                    }

                    await fetcher.UpdateOwnedGamesAsync(
                        CacheDirectoryTextBox.Text);
                }
                if (CoverArtCheckBox.IsChecked == true)
                {
                    Log("Downloading cover art...");

                    await fetcher.DownloadBoxArtAsync(
                        CacheDirectoryTextBox.Text);
                }

                if (ScreenshotCheckBox.IsChecked == true)
                {
                    Log($"Downloading screenshots for {appIds.Count} games...");
                    await fetcher.DownloadScreenshotsAsync(
                        CacheDirectoryTextBox.Text);
                }

                Log($"Done!\nImported {appIds.Count} games.");
            }
            catch (Exception ex)
            {
                Log(ex.ToString());
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

            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream);

            JsonTextBox.Text = await reader.ReadToEndAsync();
        }

        private void BrowseCacheButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
        }

        public class SteamUserData
        {
            public List<int>? rgOwnedApps { get; set; }
        }
    }
}
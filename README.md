# BR-QImport

BR-QImport quickly builds a complete **BOXROOM** Steam cache from the games owned by your Steam account.

It reads your Steam library from Steam's Dynamic Store userdata page, creates BOXROOM's `owned_games.json`, and can download game metadata, cover art, and screenshots.

## Features

- Imports the games owned by the currently signed-in Steam account
- Generates BOXROOM's `owned_games.json`
- Downloads Steam Store metadata
- Downloads library cover art when available
- Optionally downloads up to three screenshots per game
- Resumes interrupted imports and skips completed work
- Handles Steam rate limits automatically
- Remembers unavailable downloads to avoid repeatedly requesting them

## Requirements

- Windows or 64-bit Linux
- A Steam account signed in through your web browser
- BOXROOM
- The .NET Desktop Runtime for framework-dependent builds; the Linux release is self-contained

## Running on Linux

Download and extract the `linux-x64` release. Linux may not preserve the executable permission when the archive is extracted, so open a terminal in the extracted folder and run:

```bash
chmod +x BR-QImport
./BR-QImport
```

BR-QImport works with Steam libraries whose Windows games run through **Proton**. The importer reads the Steam userdata JSON and creates BOXROOM's cache; it does not require the imported games themselves to have native Linux versions.

If BOXROOM also runs through Proton, use **Browse** beside **BOXROOM Cache** and select BOXROOM's `steam_cache_v2` directory inside the appropriate Proton prefix. Its path will typically resemble:

```text
~/.local/share/Steam/steamapps/compatdata/<APP-ID>/pfx/drive_c/users/steamuser/AppData/LocalLow/NestedLoop/BOXROOM/steam_cache_v2
```

The Steam library or prefix may be elsewhere if you use a custom Steam library location, Flatpak Steam, or another compatibility manager.

## Quick start

### 1. Open your Steam userdata page

Start BR-QImport and click **Steam Page** (or **Open Steam Link**, depending on the version).

Your browser will open:

```text
https://store.steampowered.com/dynamicstore/userdata/
```

Steam generates this page for the account currently signed in through your browser. If you are asked to sign in, do so and then open the link again.

### 2. Save the current page as JSON

The page should display JSON containing entries such as `rgWishlist` and `rgOwnedApps`. BR-QImport uses `rgOwnedApps` to identify your owned games.

Save the **current browser page** as a JSON file:

1. Click the browser's **Save** button if its JSON viewer provides one, or press `Ctrl+S`.
2. Name the file `userdata.json`.
3. Make sure it is saved as a `.json` file, not `.html` or `.txt`.

Do not copy and paste the JSON into BR-QImport. Save the page itself and select the saved file.

> [!IMPORTANT]
> The saved userdata may reveal information about your Steam account and library. Keep it private and do not upload it to GitHub or share it publicly.

### 3. Select the saved file

Return to BR-QImport, click **Browse** beside **Steam UserData JSON**, and select the `userdata.json` file you just saved.

### 4. Confirm the BOXROOM cache folder

BR-QImport automatically selects BOXROOM's usual cache location:

```text
%USERPROFILE%\AppData\LocalLow\NestedLoop\BOXROOM\steam_cache_v2
```

Use **Browse** to select another `steam_cache_v2` folder if your BOXROOM data is stored elsewhere.

On Linux, select the cache inside BOXROOM's Proton prefix as described in [Running on Linux](#running-on-linux).

### 5. Choose what to download

| Option | Description |
| --- | --- |
| **Metadata** | Downloads game information from the Steam Store. |
| **Cover Art** | Downloads `library_600x900.jpg` artwork when available. |
| **Screenshots** | Downloads up to three screenshots per game. This can considerably increase the import time. |

### 6. Import your library

Click **Import**. BR-QImport will create the required game folders, download the selected content, and create or update `owned_games.json`.

The status, current game, and overall progress are displayed in the application. You can safely run the importer again; completed work is skipped automatically.

## Output structure

```text
steam_cache_v2/
├── owned_games.json
├── 570/
│   ├── meta.json
│   ├── boxart.jpg
│   ├── screen_0.jpg
│   ├── screen_1.jpg
│   └── screen_2.jpg
└── 730/
    ├── meta.json
    ├── boxart.jpg
    └── screen_0.jpg
```

Each numbered directory corresponds to a Steam application ID.

## Metadata

Depending on what Steam provides, each `meta.json` may include:

- Name and application type
- Release date
- Developers and publishers
- Genres
- Short and detailed descriptions
- About-the-game text
- Screenshot URLs

## Failed downloads

BR-QImport maintains a local `failures.json` file that remembers games for which metadata, cover art, or screenshots were unavailable. This prevents the same missing content from being requested on every run.

Delete `failures.json` if you want BR-QImport to retry all previously failed downloads.

## Steam rate limiting

Steam may temporarily limit requests during a large import. When that happens, BR-QImport waits for the requested retry interval and then continues automatically.

## Troubleshooting

### The JSON file is rejected or `rgOwnedApps` is missing

- Confirm that you were signed into the correct Steam account when the page was opened.
- Open the Steam userdata link again after signing in.
- Save the JSON page directly instead of saving a screenshot or copying formatted text.
- Confirm that the filename ends in `.json` rather than `.json.txt` or `.html`.

### Some games have no metadata or artwork

Tools, dedicated servers, demos, delisted games, and some other Steam applications may not have public Store metadata or artwork. This is expected and does not prevent the rest of the library from importing.

## Privacy

BR-QImport uses the userdata file locally to obtain application IDs. Store metadata and artwork are downloaded from publicly available Steam endpoints. Your saved userdata file is not intended to be committed to a repository or distributed with a release.

## License

This project is licensed under the MIT License. See [LICENSE](LICENSE) for details.

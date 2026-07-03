# BR-QImport

Quickly build a complete **BOXROOM** Steam cache from your Steam library.

BR-QImport imports your owned Steam games into a BOXROOM-compatible `steam_cache_v2` folder and can optionally download:

- 📦 Game metadata
- 🖼️ Box art
- 📸 Screenshots

This is intended as a companion tool for BOXROOM users who want to populate their library without manually launching every game.

---

## Features

- Import directly from Steam's Dynamic Store JSON
- Generate `owned_games.json`
- Download Steam metadata
- Download library cover art
- Download up to 3 screenshots per game
- Resume interrupted imports
- Automatically skips already completed games
- Handles Steam API rate limits automatically
- Caches failed downloads to avoid repeated requests

---

## Requirements

- Windows
- .NET
- BOXROOM

---

## Getting Started

### 1. Download your Steam Library JSON

Open:

https://store.steampowered.com/dynamicstore/userdata/

Save the page contents as a `.json` file.

### 2. Open BR-QImport

Browse to your downloaded JSON file.

The default cache folder is:

```text
%LocalAppData%\..\LocalLow\NestedLoop\BOXROOM\steam_cache_v2
```

You can change this if your BOXROOM cache lives elsewhere.

### 3. Choose what to import

| Option | Description |
|---------|-------------|
| Scrape Metadata | Downloads game information from the Steam Store API |
| Download Cover Art | Downloads `library_600x900.jpg` when available |
| Download Screenshots | Downloads up to 3 screenshots per game |

### 4. Click Import

The program will:

- Create missing game folders
- Download metadata
- Download artwork
- Create/update `owned_games.json`

Progress is shown in the log window.

---

# Output Structure

```text
steam_cache_v2/
│
├── owned_games.json
│
├── 570/
│   ├── meta.json
│   ├── boxart.jpg
│   ├── screen_0.jpg
│   ├── screen_1.jpg
│   └── screen_2.jpg
│
└── 730/
    ├── meta.json
    ├── boxart.jpg
    └── screen_0.jpg
```

---

# Metadata

Each `meta.json` contains:

- Name
- Type
- Release Date
- Developers
- Publishers
- Genres
- Short Description
- Detailed Description
- About The Game
- Screenshot URLs

---

# Failure Cache

The importer maintains a local `failures.json` file.

It remembers games that are known to be missing:

- Metadata
- Cover Art
- Screenshots

Delete this file if you wish to retry failed downloads.

---

# Rate Limiting

Steam occasionally rate limits large imports.

When this happens BR-QImport automatically:

- Waits for Steam's requested retry interval
- Retries automatically
- Continues where it left off

---

# Notes

- Only publicly available Steam Store metadata is downloaded.
- Cover art availability depends on the Steam Store.
- Some applications (tools, dedicated servers, demos, or delisted titles) may not have metadata or artwork.
- Existing valid metadata is skipped automatically, making repeated runs safe.

---

# License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

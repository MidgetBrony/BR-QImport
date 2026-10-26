# BR-QImport 1.2.2

Released 26 October 2026.

## Fixed

- Writes BOXROOM's version 2 `owned_games.json` schema so imported libraries are
  not mistaken for legacy caches and replaced by a smaller Steam Web API result.
- Populates the cache name and playtime dictionaries from each game's
  `meta.json`.
- Preserves existing names when metadata is unavailable and keeps the highest
  known playtime while rebuilding the owned-games index.

# Changelog

## Unreleased

### Changed
- Filter chips in the main and preview windows now show a usage tooltip and support right-click exclusive selection: left-click still toggles a chip, while right-click toggles the clicked chip and clears the other filters.

### Fixed
- Fixed custom threshold persistence in settings profiles, including custom score threshold values and score auto/manual state.
- Hardened startup against incompatible migrated app settings by normalizing persisted settings data before applying it.
- Added startup recovery that backs up incompatible settings and retries launch with fresh defaults instead of failing to start.

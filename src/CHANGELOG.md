# Changelog

## Unreleased

### Fixed
- Fixed custom threshold persistence in settings profiles, including custom score threshold values and score auto/manual state.
- Hardened startup against incompatible migrated app settings by normalizing persisted settings data before applying it.
- Added startup recovery that backs up incompatible settings and retries launch with fresh defaults instead of failing to start.

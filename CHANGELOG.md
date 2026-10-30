# Changelog

## v1.2.0 - 2026-10-30 - Better preview and copies list

### Added
- Author and version ("Cyberponk • v1.2.0") shown next to the Help button.
- WebP, HEIC/HEIF, AVIF, JPEG XL, JPEG XR, DDS and camera RAW pictures are previewed at full size when Windows has a codec for them (before: only Explorer's thumbnail).
- When a preview is only Explorer's thumbnail (no preview handler for the file type, e.g. .ppt on a server without Office), the details box says so and gives the thumbnail's size.

### Changed
- Copies list (above the preview): the clicked copy is listed first.
- Copies list: its height can be dragged, and it fits its contents when a file is clicked (up to half the pane).
- Copies list: horizontal and vertical scrollbars appear only when needed; long paths are no longer cut off.
- Pictures are shrunk to the preview pane with high-quality smoothing, so large photos are no longer jagged or grainy.
- Pictures GDI+ can't read fall back to the Windows image codecs at full size, not to a thumbnail.
- Thumbnails are requested at the pane's size (up to 2560 px) instead of 1024 px.
- Build: `build.cmd` also references WPF's image decoder (ships with .NET Framework 4.8).

## v1.1.0 - 2026-10-28 - Add release versioning

### Added
- Release versioning: each release is tagged and gets a folder in `Releases\` with its exe and changelog.
- The exe's version number now follows the release (this one is 1.1.0).
- CLAUDE.md with project notes for development sessions.

### Changed
- `build.cmd` now writes the exe to `bin\DuplicateFileFinder.exe` instead of the project root.


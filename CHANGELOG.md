# Changelog

## v1.4.0 - 2026-11-05 - Fast loading of large results

### Changed
- Loading saved results is much faster: about 12 seconds for 850,000 files, where it took minutes or froze the window.
  - Reading and building the list show progress, and the window keeps responding.
  - Files are compared with the disk in the background: the list can be used meanwhile, and only deleting waits until the check is done.
  - The disk check looks at each archive and each missing folder only once.
- Large results (over 20,000 files) start collapsed, with only the top folders open. A folder's items are made when it is opened, and "Expand all" asks you to narrow the list first.
- Checking, unchecking and filtering are much faster with large results.
- Tooltips of files are made when the mouse rests on them.
- Letter and digit keys no longer move the tree selection.
- The details above the preview follow every change of checkmarks ([keep] / [delete], folder counts).

### Added
- Folder right-click menu: "Keep the copies in this folder (check all others)" and "... and its subfolders".
- The underlined letters of the right-click menu work on the selected item without opening the menu (O opens a file, K keeps only that copy, ...).

### Fixed
- The "could not be read" and failed-deletion tables hid the Path column when a reason was long.
- A confirmation box opened by a menu letter let that letter move the tree selection.

## v1.3.0 - 2026-10-30 - Problem tables and empty folders

### Added
- Option "Remove folders if empty after deleting" (bottom of the window, remembered): folders that deleting leaves completely empty are removed, and their parent folders too if that empties them. Only folders inside the searched folders; a searched folder itself is never removed. Removed folders are counted in the summary and listed in the deletion log.
- When some files could not be deleted, a window shows a summary with a warning icon and a table of every file not deleted (before: a message box listing the first 15).
- Shortcut letters in the tree's right-click menus, underlined: with the menu open, press the letter (e.g. K = Keep only this copy).

### Changed
- "Items that could not be read" is a maximized window with a table: Reason (auto-sized) and Path. Clicking the path opens Explorer with the item selected.
- "Show in folder" opens Explorer with the file selected, also when that folder is already open in Explorer.
- Clicking Scan clears the "N item(s) could not be read" link right away.

### Fixed
- A '&' in a file path in the right-click "Copies" submenu disappeared.
- Opening a drive root folder (e.g. E:\) from the folder menu went to the wrong place.

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


# Duplicate File Finder

A free Windows app that finds duplicate files, including copies inside archives, and lets
you choose which copies to delete.

## Features

- Compares files by content (SHA-256), so copies are found even when renamed.
- Scans several folders at once and compares them with each other.
- Looks inside zip, rar, 7z, cab and tar archives. Copies inside an archive can be removed
  from it; an archive left empty is deleted.
- Two views: by folder, or by group of identical files.
- Auto-select rules (keep the newest, oldest, shortest path...) for everything, or for one
  folder only. "Keep the copies in this folder" checks every copy elsewhere.
- One copy of every file always stays.
- Preview pane for pictures, text, documents and videos, with the list of all copies.
- Deletes to the Recycle Bin by default, checks each file again just before deleting, and
  keeps a log of what was deleted.
- Saves the results and checkmarks to continue later. Results with hundreds of thousands
  of files load in seconds.
- Keyboard: the underlined letters of the right-click menu work on the selected item.

## Download

Get `DuplicateFileFinder.exe` from the
[latest release](https://github.com/cyberponk/DuplicateFileFinder/releases/latest).
It is a single file with nothing to install. It needs .NET Framework 4.8, which comes with
Windows 10 (version 1903 and later), Windows 11 and Windows Server 2022.

Optional, for more archive types: [7-Zip](https://www.7-zip.org/) or WinRAR, used when
installed. Without them, the app reads zip files itself and other archives with the
tar.exe that comes with Windows 10 (version 1803 and later).

## Build

Run `build.cmd`. It uses the C# compiler that comes with .NET Framework, so nothing needs
to be installed. The program is written to `bin\DuplicateFileFinder.exe`.

## License

[MIT](LICENSE). The software is provided as is, without warranty: it deletes files, so
check what you select, and keep backups of anything important.

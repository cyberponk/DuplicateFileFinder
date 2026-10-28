# CLAUDE.md

Duplicate File Finder: a Windows app (WinForms, .NET Framework 4.8) that finds duplicate
files, including copies inside archives, and lets the user choose which copies to delete.
Target PCs include Windows Server 2022 (the user copies the exe to the server's I:\).

## Build and test
- Build: `build.cmd` (uses the csc.exe that ships with .NET Framework 4.x; no install needed).
  Output: `bin\DuplicateFileFinder.exe`.
  From Git Bash: `cmd //c "$(cygpath -w "$PWD/build.cmd")"`.
- No test suite. Test by loading the exe with `[Reflection.Assembly]::LoadFrom` in a hidden
  PowerShell harness (run with `timeout`), or start the exe and capture it with PrintWindow.
  Put test scripts and demo data in the session scratchpad, not here.
- Never leave a dialog open on the user's screen: auto-answer message boxes from a timer.
- Clean up `%APPDATA%\Duplicate File Finder` after tests. Guard every `rm` with `${VAR:?}`.

## Code rules
- C# 5 only (the built-in compiler); the source must stay ASCII (use \u escapes).
  Check with: `LC_ALL=C grep -c '[^[:print:][:space:]]' DuplicateFileFinder.cs` (expect 0).
- Tree: state-image checkboxes (0 unchecked, 1 checked, 2 mixed, 3 disabled) and
  OwnerDrawText with cached fonts. Never use NodeFont: it leaked GDI handles and crashed
  the server. Dispose context-menu items when rebuilding menus.
- One copy of every file must stay. The kept copy may be inside an archive, but automatic
  choices prefer ordinary files.
- Archive edits are verified by re-extracting, and the original is replaced via a new file
  beside it. Readers: Rar.exe, 7-Zip, WinRAR, tar.exe, with an I/O watchdog.
- `.dupes` header stays "DuplicateFinder results v1" so older results files still load.
  Settings fall back to the old `%APPDATA%\Duplicate Finder` folder.
- Video preview on Windows Server needs the Server-Media-Foundation feature. The app offers
  to install it, restarts itself on success, and only tells the user if Windows must restart.

## Git
- Local repo only, branch `main`. v1.0.0 is tagged. Personal test files (.dupes, .mp4,
  .jpg, crash.txt) and the exe are ignored; never commit them.

<!-- repo-versioning -->
## Versioning

This project uses the `repo-versioning` skill (config: `.versioning.json`).
- Every build goes through the skill's `version.py build`, which bumps the patch version.
- When asked to commit, use the skill's release flow (minor bump, release folder,
  changelog, git commit and tag) instead of a plain `git commit`.

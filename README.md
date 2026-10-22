# UltraUninstaller

Part of the Ultra Creative Suite, by Demir Ajvazi.

An accessible Windows uninstaller/cleaner built for screen reader users (JAWS, NVDA)
first. Its flagship feature is the **Force Uninstall engine**: many installers ship
their own uninstall GUI that is not accessible, which can leave a screen reader user
completely unable to remove a program through normal means. UltraUninstaller reads
the same registry data those GUIs use, and drives the removal silently — no
inaccessible window ever needs to appear.

## Current status

Working so far: the Force Uninstall engine, an accessible right-click context menu on
the program list, the Disk Space Analyzer (with Force Delete), an Explorer "Analyze
size with UltraUninstaller" right-click entry, a Registry Cleaner, a Startup Manager,
and a Junk Cleaner. All planned core features are now in. The whole app - and its
installer - is available in **English and Serbian (Latin script)**.

### Program list context menu

Right-click (or press the Menu key / Shift+F10 with keyboard focus on a row — this
opens automatically, no extra setup needed) on a program for:
- **Uninstall (silent)** — same as the Uninstall button, respects the "allow force
  removal" checkbox.
- **Force uninstall now** — tries silent removal first, then force-removes
  (Layer 3) if that fails, regardless of the checkbox state.
- **Open install folder**, **Copy uninstall string**, **Show details**.

### Disk Space Analyzer (Tools menu, or Explorer right-click)

Scans one folder level at a time (not a visual treemap), sorted largest-first, in a
`ListView` — the same list control, multi-select, and right-click context menu
pattern already used for the program list. Selection is Ctrl+Click / Ctrl+Space, same
as the program list; right-click (or the Menu key / Shift+F10) gives "Open this
folder" and "Delete selected". Enter or double-click on a single selected row drills
into that folder; "Up one level" goes back.

**Note:** an earlier version of this window used a `DataGrid` with a checkbox column,
on the theory that it gives better keyboard cell navigation. In real testing with
JAWS, folder names weren't being read reliably and the checkbox/delete flow wasn't
reachable — so it was replaced with the plain `ListView` pattern above, matching what
was already confirmed working for the program list. If a future Ultra app needs a
multi-select grid, prefer this ListView+ContextMenu pattern over `DataGrid` unless
it's specifically re-tested with JAWS first.

Scanning shows a modal "Scanning..." dialog with a real determinate progress bar
("X of Y folders"), a live-announced status line, and Cancel (button or Escape).
Opening the analyzer from the Tools menu never scans automatically — it waits for
you to press Scan or Enter. Opening it via the Explorer right-click entry (a specific
folder was requested) does scan immediately, since that's the one thing you asked for.

Right-clicking any folder in Windows Explorer and choosing **"Analyze size with
UltraUninstaller"** launches the app straight into the analyzer for that folder. This
is registered per-user (no admin rights needed) via **Tools → Enable right-click
"Analyze size" in Explorer** in the app's menu, and can be turned off the same way.

### A note on "unhandled exception" dialogs while debugging in Visual Studio

If you're running with F5 (Debug), Visual Studio's default setting breaks on *any*
thrown exception the moment it's thrown — including ones the code already catches a
few lines later. Cancelling a scan intentionally throws an `OperationCanceledException`
internally (that's the normal, correct way .NET represents "this was cancelled"), and
the code does catch it — but the debugger may still interrupt with a dialog showing
that exception first. Pressing **Continue (F5)** lets it proceed normally.

To stop this from happening: **Debug → Windows → Exception Settings**, find
`OperationCanceledException` under "Common Language Runtime Exceptions", and
uncheck it. A `DispatcherUnhandledException` safety net has also been added at the
app level, so if something ever does slip past a local `try/catch` for real (in a
normal, non-debugger run), you'll see one readable message box instead of a raw
crash.

### Startup Manager (Tools menu)

Checks four sources most basic startup managers don't all cover:

1. **Registry Run entries** (HKCU, HKLM, and the WOW6432Node variant)
2. **Startup folder shortcuts** (both your own and the all-users folder)
3. **Scheduled Tasks that trigger at logon** (not every scheduled task - only ones
   with an actual logon trigger, detected by reading each task's real definition,
   not by guessing from its name)
4. **Services set to start automatically** - often the real cause of a slow boot,
   and something most "startup manager" tools skip entirely since they only look at
   the classic Run keys

Each item shows a friendly description and publisher read from the file itself
(when available) instead of a bare filename, whether it's digitally signed, and a
flag if it sits in a location like Temp or Downloads that's unusual for something
that's supposed to launch automatically - a heads-up, not an accusation.
UltraUninstaller doesn't try to tell you whether something is safe to disable -
that's a judgment call it shouldn't make for you - but its context menu has
**"Search online for information about this"**, one click to look up the item by
name in your browser.

**On disabling:** Registry Run entries and Startup-folder shortcuts are switched on
and off through Windows' own **StartupApproved** setting - the same switch Task
Manager's Startup tab and Settings → Apps → Startup use - so a change made in
UltraUninstaller shows up there too, and something disabled in Task Manager is shown
as disabled here. Scheduled tasks are enabled/disabled with `schtasks`, and services
are switched between Automatic and Manual (never Disabled, so anything that really
needs them can still start them on demand). Services set to Manual by UltraUninstaller
stay in the list, marked disabled, so they can be switched back. Everything can be
reversed the same way it was changed.

Changes take effect at the next sign-in or restart: a program that is already running
keeps running until then. After every enable/disable the list is re-scanned and the
result ("Done. 2 items disabled.", plus any failures) is announced last, so screen
reader users hear the actual outcome.

*Fixed in 1.1:* version 1.0 disabled Registry Run entries by renaming the registry
value, but Windows runs every value in a Run key whatever its name - so those items
kept starting, and an item already disabled in Task Manager could even be switched
back on. Such renamed entries are converted automatically the next time Startup
Manager scans (their original name is restored and they are properly switched off).
Services switched to Manual also used to disappear from the list; they now stay.

### Registry Cleaner (Tools menu)

Deliberately narrow and conservative rather than a general-purpose "fix everything"
scrubber - a registry cleaner that guesses wrong breaks things, so this only flags
entries where the answer is unambiguous:

1. **Orphaned Uninstall entries** - Programs and Features still lists something
   whose own uninstaller `.exe` no longer exists on disk (it was deleted by hand
   rather than properly uninstalled). MSI entries are never flagged this way, since
   `msiexec.exe` always exists regardless of whether the product registration is
   still valid - checking that would need a different, riskier kind of test this
   version doesn't attempt.
2. **Orphaned Startup entries** - a "Run at Windows startup" entry pointing at an
   `.exe` that no longer exists.
3. **Security review - known hijack techniques.** Three specific, well-documented
   persistence/hijack mechanisms with essentially zero legitimate use on a normal
   consumer PC: **Image File Execution Options Debugger redirection** (makes
   Windows launch a different program in place of a system tool - the classic
   "sticky keys" backdoor technique), **Winlogon Shell/Userinit tampering** (Shell
   should always be exactly `explorer.exe`, Userinit exactly the standard
   `userinit.exe` path - anything appended or different is the classic desktop-
   hijack technique), and a **non-empty AppInit_DLLs** (a deprecated DLL-injection
   mechanism basically nothing legitimate uses anymore). These are MITRE
   ATT&CK-documented techniques, not guesses - but a pattern match still isn't a
   verdict, so these are always framed as "review this," and "removing" a Shell,
   Userinit, or AppInit_DLLs issue resets it to Windows' own correct default value
   rather than deleting it outright (Windows expects Shell/Userinit to always have
   *a* value).
4. **Security review - suspicious startup commands.** A Run-at-startup entry whose
   command line matches a known "living off the land" pattern: encoded PowerShell
   (`-enc`/`-EncodedCommand`), or `mshta`/`regsvr32`/`certutil`/`rundll32` fetching
   from a web address instead of running a local file. These are real, specific
   techniques attackers actually use precisely because they abuse legitimate
   Windows tools - not a malware signature database, just a short, deliberately
   narrow list of high-confidence patterns.

This is intentionally **not** the kind of registry cleaner that reports thousands
of "problems" by flagging every harmless leftover entry it can find (the file-
association, shared-DLL, and "sound event" categories other cleaners pad their
counts with do essentially nothing when cleaned, and flagging that many things
teaches you to stop reading them). Every category above is either provably
unambiguous (orphaned entries) or a specific, named, real-world technique
(security review) - nothing here exists just to inflate a number.

**Two real false-positive bugs were found and fixed** during testing, both worth
recording since they explain why several actively-used programs were briefly
misreported as orphaned:

- Plain `File.Exists()` silently treats *every* kind of failure - including "access
  denied" when UltraUninstaller isn't running elevated - as "the file is gone". The
  check now distinguishes "genuinely not found" from "can't tell" and only flags on
  the former, expands environment variables in the path first, and also skips
  flagging anything whose InstallLocation folder still exists with content, as
  corroborating evidence the program is still really there.
- The bigger one: many installers write an **unquoted** `UninstallString` even when
  the install path itself contains a space (e.g. under "Program Files"). The old
  parser split on the first space, silently truncating a perfectly valid path like
  `C:\Program Files\Vendor\uninstall.exe` down to `C:\Program` - which of course
  "doesn't exist". This affected any installed program with a space in its path and
  an unquoted uninstall string, which is common - explaining why several unrelated,
  actively-used programs were flagged at once. The parser now tries progressively
  longer space-joined prefixes against the real filesystem and takes the longest one
  that actually exists, the standard robust way to resolve this ambiguity. This fix
  is shared with Force Uninstall and Startup Manager, since all three parse the same
  kind of command-line string.

When in doubt, this no longer flags. Every issue's context menu also has **"Search
online for information about this"**, which opens your default browser with a
search for the item's name - UltraUninstaller doesn't try to make the final call on
your behalf about whether something is safe to remove, especially for the security
review categories; this just makes it one click to go look it up yourself.

There is intentionally no "scan and clean the entire registry automatically" mode,
and no free-form registry browser either - an earlier version had one, on the Disk
Space Analyzer's "browse anywhere" principle, but it was removed: unlike a folder
you can see is huge, a registry key with no context gives you nothing to act on
without already knowing what's broken, so it added complexity without adding real
value. Every removal (or reset) exports the affected registry key to a `.reg`
backup file first (shown in the step log), so anything changed can be restored by
double-clicking that file, exactly like Force Uninstall's Layer 3 backup.

**Ignoring a finding on purpose.** Some findings are intentional - for example, tools
like Defender Control disable Microsoft Defender partly by adding an Image File
Execution Options "Debugger" entry for `MpCmdRun.exe`, which the Security review
correctly reports. Select such an item and choose **Ignore selected (don't report
again)** from the context menu: nothing is removed from the registry, the item is just
no longer reported (and no longer counted by the System Health Check). The choice is
tied to the entry's current data, so if that entry ever changes - say the Debugger
starts pointing at a different program - it is reported again. **Show ignored items**
lists them again, marked "ignored", and **Stop ignoring selected** brings them back.
The list is stored per user under `HKCU\Software\UltraUninstaller\IgnoredRegistryIssues`.

### Junk Cleaner (Tools menu)

Checks a short, fixed list of locations that are well known to be safe to clear -
deliberately not a "scan everywhere for anything that looks unused" sweep, for the
same reason Registry Cleaner stays narrow: guessing wrong at what's safe to delete
is how junk cleaners earn their bad reputation. Every category here is something
Windows or the browser that owns it already knows how to rebuild on its own:

1. **Your Temp folder and the Windows Temp folder** - files programs are supposed
   to clean up after themselves but often don't.
2. **Windows Update download cache** - installers Windows Update already applied;
   re-downloaded automatically if ever needed again.
3. **Browser caches** - every profile found for Chrome, Edge, Brave, and Firefox.
   Never touches bookmarks, saved passwords, or history - only the cache.
4. **Thumbnail and icon cache** - Explorer regenerates these the next time you
   browse a folder.
5. **Delivery Optimization cache** - Windows' peer-to-peer update-sharing cache.
6. **Windows Error Reporting queue** - queued crash reports, pure diagnostic data.
7. **Recycle Bin** - shown separately since emptying it is the one category here
   that's genuinely permanent (a Windows Explorer "Delete", not an app cache);
   flagged clearly as such in the confirmation dialog.

Same list/scan/multi-select/clean pattern as Registry Cleaner and Disk Analyzer:
scan shows each category's size and item count in a `ListView`, select the ones
you want, "Clean selected" removes them. A category is only shown at all if it
actually found something - an empty category isn't listed, the same reasoning
behind not padding Registry Cleaner's count with harmless clutter. Cleaning a
folder-based category removes its contents but keeps the folder itself (Windows
expects %TEMP% and similar locations to always exist); a file locked by a program
that currently has it open is skipped and reported, not treated as a failure of
the whole category.

### Force Delete (for folders that won't delete normally)

Right-click a folder in the Disk Space Analyzer (or select it and press the "Force
delete selected" button) for a layered approach to folders a normal delete can't
touch:

1. Normal Recycle Bin delete (in case it actually works this time).
2. Detach an orphaned Windows image mount at that exact folder
   (`dism /Unmount-Image /MountDir:... /Discard`) — this is the specific, common fix
   for an old WinPE/WIM/VHD mount left over from a deployment or repair tool that
   didn't clean up after itself. Falls back to a system-wide
   `dism /Cleanup-Mountpoints` if the folder is too old for DISM to still recognize
   as a specific tracked mount.
3. Take ownership and reset permissions (`takeown` + `icacls /reset`), then retry.
4. A permanent delete, bypassing the Recycle Bin.
5. Last resort: schedule the folder for deletion the next time Windows restarts
   (the same mechanism Windows Installer itself uses for files it can't remove while
   they're in use) — guaranteed to work eventually, even for genuinely locked files.

Each attempt is reported in a step log so you always know which method actually
worked, not just "done".

### About and supporting development

**File → About UltraUninstaller** opens a small window with the version, author and
license, plus buttons that open **GitHub Sponsors**, **Ko-fi**, **PayPal**, this
project's GitHub page and the Ultra Creative Suite on GitHub in your browser. The text
at the top is a read-only text box that gets focus first, so JAWS/NVDA can read it line
by line with the arrow keys; Escape closes the window.

### Exit

**File → Exit** in the main window's menu bar closes UltraUninstaller entirely
(including any open Disk Space Analyzer windows).

## Installing

Download `UltraUninstaller-Setup-<version>.exe` from the
[Releases](https://github.com/demirajvazi10-max/Ultra-Uninstaller/releases) page, or the
portable `.zip` if you'd rather not install anything.

When Setup starts, it asks how to install:

- **Install for all users** - into Program Files. Needs administrator rights, so
  Windows shows a UAC prompt next.
- **Install only for me** - into your own user folder. No administrator rights and
  no UAC prompt needed.

You can also skip the question from the command line with `/ALLUSERS` or
`/CURRENTUSER`. Setup picks English or Serbian automatically from your Windows
display language (it only asks if your Windows language is something else).

If Setup ever closes without installing, it leaves a log file in your Temp folder
(`%TEMP%`, named `Setup Log <date> #<number>.txt`) - please attach it when
reporting the problem. Earlier versions always required administrator rights, so a
cancelled or unnoticed UAC prompt made Setup close silently; the "only for me"
option above is the fix for that.

## Language

UltraUninstaller starts in Serbian (Latin script) when Windows is set to Serbian,
Croatian, Bosnian or Montenegrin, and in English otherwise. To change it, open
**File → Language** and pick **English** or **Srpski (latinica)**; the choice is
remembered and applied after a restart (the app offers to restart right away).
Everything is translated - menus, buttons, list columns, status messages, step logs,
confirmation dialogs, and the names screen readers announce for each control - so
JAWS/NVDA users and sighted users always get the same language.

## Changes in 1.1

New:
- The whole app and its installer in **Serbian (Latin script)**, chosen automatically
  from the Windows language or from **File → Language**.
- A real **About** window with links to GitHub Sponsors, Ko-fi and PayPal.
- Setup can install **only for the current user**, without administrator rights.
- Registry Cleaner can **ignore** findings you keep on purpose (see Registry Cleaner).

Fixed:
- **Startup Manager** now really disables things (see "On disabling" above).
- **Status messages are now actually announced by screen readers.** WPF needs the app
  to raise a "live region changed" event itself; before, results only appeared in the
  status line silently. Results of an uninstall, delete or enable/disable are also
  announced *after* the list refreshes, so the refresh no longer talks over them.
- **Silent uninstall waited too little.** Many uninstallers (NSIS, Inno Setup...) hand
  the work to a copy of themselves and exit at once; that was reported as a failure
  and, with force removal allowed, files were deleted while the real uninstall was
  still running. UltraUninstaller now keeps checking until the program is really gone.
- **MSI programs registered as `MsiExec /I{...}` were repaired instead of removed.**
  `/I` is now switched to `/X` for silent removal.
- **Force removal could delete far too much:** a folder named after the *publisher*
  (e.g. all of "Mozilla" when removing one Mozilla program), any shortcut whose name
  merely contained the program's name, and a bogus install location such as
  "C:\Program Files". It now removes only the program's own folders and shortcuts,
  and a safety check refuses Windows folders, drive roots and the main user folders
  everywhere something can be deleted (including Disk Analyzer and Force Delete).
- **Disk Analyzer:** deleting the "(files directly in this folder)" row tried to delete
  the whole folder being viewed. It now deletes only those loose files.
- **Registry Cleaner** no longer removes anything if its `.reg` backup could not be
  made first, no longer flags Windows tools and ClickOnce apps whose command has no
  full path (e.g. `rundll32.exe`), and no longer flags Userinit/Shell on systems where
  Windows is not on drive C:.
- Tools whose output is long (`takeown`, `schtasks`, uninstallers) could hang until
  their timeout because their output was only read after they finished; it is now
  read while they run. This also fixes scheduled tasks sometimes missing from Startup
  Manager.
- "Restore point created" was announced even when Windows skipped creating one.
- Restart-scheduled deletion and folder backups no longer follow junctions/symlinks.

## How Force Uninstall works

1. **Layer 1 — Native silent.** Reads `QuietUninstallString` (or `UninstallString`)
   from the registry and, if the installer type is recognized (MSI, Inno Setup, NSIS,
   InstallShield, Squirrel/Electron), appends the correct silent switch automatically.
2. **Layer 2 — MSI direct.** For Windows Installer packages, calls
   `msiexec /x {ProductCode} /quiet /norestart` directly.
3. **Layer 3 — Force removal.** Only runs if you check "Allow force removal" and
   Layers 1–2 didn't actually remove the program (re-checked against the registry,
   not just the exit code). Creates a System Restore point, backs up the install
   folder and the registry entry, then manually deletes the install folder, the
   registry Uninstall entry, shortcuts, and known AppData/ProgramData remnants.

Every step is reported through an accessible, assertive live-region status line and
a step log, so the whole process is followable without looking at a progress bar.

## Project structure

- `src/UltraUninstaller.Core` — all engine logic (registry scanning, installer-type
  detection, silent uninstall, force removal, backups). Targets plain `net8.0`, no
  UI dependencies, so it can be built and unit-tested on any platform.
- `src/UltraUninstaller.App` — the WPF UI. Targets `net8.0-windows` and only builds
  on Windows (WPF itself is Windows-only).

## Translations

All user-facing text goes through a small helper in
`src/UltraUninstaller.Core/Localization`:

- In C#: `L.T("text")` for fixed text, `L.F("text with {0}", value)` for text with
  values, and `L.N(count, "{0} item", "{0} items")` for count-dependent text.
- In XAML: `Content="{l:Tr '_Scan'}"` (the `l` namespace is
  `clr-namespace:UltraUninstaller.App`), used for visible labels and for every
  `AutomationProperties.Name`.

The English text itself is the lookup key, so a missing translation simply shows
English rather than a blank. Serbian lives in `SerbianStrings.cs`; count-dependent
entries hold three forms separated by `|` (1 / 2-4 / 5+, e.g.
`"{0} stavka|{0} stavke|{0} stavki"`). Access keys (the `_` letter) are chosen per
language so they never clash within one window or menu. The saved language choice is
stored in `%AppData%\UltraUninstaller\language.txt`.

## Building and running (on Windows)

Requirements: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) with
the **Desktop Development with .NET** workload (installed automatically if you use
Visual Studio's installer, or via `winget install Microsoft.DotNet.SDK.8`).

```
dotnet build
dotnet run --project src/UltraUninstaller.App
```

Or open `UltraUninstaller.sln` in Visual Studio and press F5.

The app requests elevation (`highestAvailable`) via its manifest, since deleting
registry Uninstall entries and some AppData locations typically requires it. You may
see a UAC prompt on launch.

## Releasing

Pushing a tag matching `v*.*.*` (e.g. `v1.0.0`) triggers
`.github/workflows/release.yml`, which publishes a self-contained, single-file
build, packages it into an installer with the script in `installer/`, and
attaches both the installer and a portable .zip to a new GitHub release. Nothing
else needs to be done by hand.

## A note on this build environment

This project was scaffolded and the Core engine was compiled and verified in a Linux
sandbox (the Core project builds clean there — 0 warnings, 0 errors). The WPF App
project cannot be compiled here, because WPF requires the Windows Desktop SDK, which
only exists on Windows. It has been written carefully by hand and reviewed, but its
first real compile and the first real accessibility test with JAWS/NVDA will happen
on your machine — CapCut is a good first real-world test case for Force Uninstall.

## Accessibility notes

- The program list and the Disk Space Analyzer's folder list are both native
  `ListView` controls (real Windows list controls, not custom drawn), multi-selectable
  with the keyboard (Ctrl+Space to toggle a row), each with a right-click/Menu-key
  context menu for their actions. `DataGrid` was tried for the Disk Analyzer's
  multi-select but did not read reliably with JAWS in practice — see the note above.
- Status updates are posted to an `AutomationProperties.LiveSetting="Assertive"`
  text region, so screen readers announce each step as it happens.
- All interactive controls have explicit `AutomationProperties.Name` values and
  keyboard access keys (Alt+underlined letter).

## Author

Demir Ajvazi - part of the [Ultra Creative Suite](https://github.com/demirajvazi10-max/Ultra-Creative-suite).

## License

GPL-3.0 — see the [LICENSE](./LICENSE) file.

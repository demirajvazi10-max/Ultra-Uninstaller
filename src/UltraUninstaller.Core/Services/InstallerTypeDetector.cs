using System.Text.RegularExpressions;
using UltraUninstaller.Core.Models;

namespace UltraUninstaller.Core.Services;

/// <summary>
/// Parsed, ready-to-run command line for an uninstall attempt.
/// </summary>
public sealed record UninstallCommand(string FileName, string Arguments, InstallerKind Kind);

/// <summary>
/// Guesses which installer technology produced a given UninstallString, and knows
/// the silent/quiet switch each technology understands. This is what lets us turn
/// an uninstaller whose own GUI is a black box (like CapCut's) into something that
/// runs with zero visible window.
/// </summary>
public static class InstallerTypeDetector
{
    public static InstallerKind Detect(InstalledApp app)
    {
        if (app.LooksLikeMsi)
            return InstallerKind.Msi;

        var raw = (app.UninstallString ?? app.QuietUninstallString ?? string.Empty).ToLowerInvariant();

        if (raw.Contains("msiexec"))
            return InstallerKind.Msi;

        if (raw.Contains("unins000.exe") || raw.Contains("unins") && raw.Contains(".exe"))
            return InstallerKind.InnoSetup; // Inno Setup always names its uninstaller uninsXXX.exe

        if (raw.Contains("update.exe") && raw.Contains("--uninstall"))
            return InstallerKind.Squirrel; // Electron/Squirrel apps (common for Chromium-based creator tools)

        if (raw.Contains("uninstall.exe") && (app.InstallLocation?.Length ?? 0) > 0)
            return InstallerKind.NsisSetup; // Very common NSIS convention

        if (raw.Contains("installshield") || raw.Contains("isuninst"))
            return InstallerKind.InstallShield;

        return InstallerKind.Unknown;
    }

    /// <summary>
    /// Given a detected kind, returns the best silent uninstall command we can build —
    /// preferring the app's own QuietUninstallString when present, then falling back
    /// to appending the standard silent switch for that installer family.
    /// </summary>
    public static UninstallCommand? BuildSilentCommand(InstalledApp app)
    {
        var kind = app.DetectedInstallerKind == InstallerKind.Unknown
            ? Detect(app)
            : app.DetectedInstallerKind;

        // Layer 1: the app told us how to be quiet. Trust it as-is.
        if (!string.IsNullOrWhiteSpace(app.QuietUninstallString))
        {
            var (file, args) = SplitCommandLine(app.QuietUninstallString!);
            return new UninstallCommand(file, args, kind);
        }

        if (string.IsNullOrWhiteSpace(app.UninstallString))
            return null;

        var (baseFile, baseArgs) = SplitCommandLine(app.UninstallString!);

        return kind switch
        {
            InstallerKind.Msi => BuildMsiQuietCommand(app) ?? new UninstallCommand(baseFile, AppendIfMissing(baseArgs, "/quiet /norestart"), kind),
            InstallerKind.InnoSetup => new UninstallCommand(baseFile, AppendIfMissing(baseArgs, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART"), kind),
            InstallerKind.NsisSetup => new UninstallCommand(baseFile, AppendIfMissing(baseArgs, "/S"), kind),
            InstallerKind.Squirrel => new UninstallCommand(baseFile, AppendIfMissing(baseArgs, "--silent"), kind),
            InstallerKind.InstallShield => new UninstallCommand(baseFile, AppendIfMissing(baseArgs, "/s /v/qn"), kind),
            _ => new UninstallCommand(baseFile, baseArgs, kind), // Unknown: try it verbatim, no switch guessing.
        };
    }

    private static UninstallCommand? BuildMsiQuietCommand(InstalledApp app)
    {
        if (!app.LooksLikeMsi)
            return null;

        // Registry key name for MSI entries is the ProductCode itself, e.g. {A1B2C3D4-...}
        return new UninstallCommand("msiexec.exe", $"/x {app.RegistryKeyName} /quiet /norestart", InstallerKind.Msi);
    }

    private static string AppendIfMissing(string existingArgs, string switchToAdd)
    {
        if (existingArgs.Contains(switchToAdd.Split(' ')[0], StringComparison.OrdinalIgnoreCase))
            return existingArgs; // Already has some form of a silent switch, don't double up.

        return string.IsNullOrWhiteSpace(existingArgs) ? switchToAdd : $"{existingArgs} {switchToAdd}";
    }

    /// <summary>
    /// Splits a raw registry UninstallString ("path\to\exe.exe" /arg1 /arg2) into an
    /// executable path and its argument string, handling the quoted-path case that's
    /// almost always present when the install path contains spaces.
    ///
    /// For an UNQUOTED path that itself contains spaces (a common but poorly-formed
    /// UninstallString - many installers never quote it even when the install path has
    /// spaces, e.g. under "Program Files"), naively splitting at the first space would
    /// truncate the real path and make a perfectly-installed program look like its
    /// uninstaller is missing. Instead this tries progressively longer space-joined
    /// prefixes against the real filesystem and takes the longest one that actually
    /// exists, which is the standard robust way to resolve this genuinely ambiguous case.
    /// </summary>
    internal static (string FileName, string Arguments) SplitCommandLine(string commandLine)
    {
        commandLine = commandLine.Trim();

        if (commandLine.StartsWith('"'))
        {
            var match = Regex.Match(commandLine, "^\"(?<path>[^\"]+)\"(?<rest>.*)$");
            if (match.Success)
                return (match.Groups["path"].Value, match.Groups["rest"].Value.Trim());
        }

        if (!commandLine.Contains(' '))
            return (commandLine, string.Empty);

        var tokens = commandLine.Split(' ');
        for (var take = tokens.Length; take >= 1; take--)
        {
            var candidate = string.Join(' ', tokens.Take(take));
            var expanded = Environment.ExpandEnvironmentVariables(candidate);
            if (File.Exists(expanded))
            {
                var rest = string.Join(' ', tokens.Skip(take)).Trim();
                return (candidate, rest);
            }
        }

        // Nothing resolved against the real filesystem - fall back to the naive
        // first-space split so callers still get something, even though for a path
        // like this it may be wrong. This only happens when the file genuinely can't
        // be found under any prefix, which is a much rarer case than before.
        var firstSpace = commandLine.IndexOf(' ');
        return (commandLine[..firstSpace], commandLine[(firstSpace + 1)..].Trim());
    }
}

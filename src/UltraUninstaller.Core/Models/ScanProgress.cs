namespace UltraUninstaller.Core.Models;

/// <summary>
/// Reported once per folder as a Disk Space Analyzer scan proceeds. Carries the
/// count/total explicitly (not just a message string) so the UI can drive a real
/// determinate progress bar and an accurate "X of Y" announcement, rather than an
/// indeterminate spinner that a screen reader can only describe as "busy".
/// </summary>
public sealed record ScanProgress(int FoldersCompleted, int FoldersTotal, string CurrentFolderName);

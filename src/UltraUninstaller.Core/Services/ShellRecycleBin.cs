using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace UltraUninstaller.Core.Services;

/// <summary>
/// Sends a file or folder to the Recycle Bin using the same Shell API Windows Explorer
/// itself uses for a Delete command (not a permanent delete). Implemented via direct
/// P/Invoke so this has no dependency on Microsoft.VisualBasic / Windows Forms, keeping
/// UltraUninstaller.Core buildable outside the Windows Desktop framework.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ShellRecycleBin
{
    public static (bool Success, string? Error) SendToRecycleBin(string path)
    {
        try
        {
            var shf = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                // pFrom must be double-null-terminated.
                pFrom = path + '\0' + '\0',
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT,
            };

            var result = SHFileOperation(ref shf);

            if (result != 0)
                return (false, $"Shell delete failed with code {result}.");

            if (shf.fAnyOperationsAborted)
                return (false, "Operation was aborted.");

            return (true, null);
        }
        catch (Exception ex)
        {
            // Any low-level failure (a missing/blocked shell32.dll, a marshaling
            // error, etc.) should read as "this layer didn't work", not crash the
            // caller - callers like ForceDeleteService rely on that to move on to
            // the next layer instead of the whole operation blowing up.
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Reads the current total size and item count of the Recycle Bin (all drives)
    /// via the Shell API, without touching anything - used by Junk Cleaner's scan
    /// step to show what emptying it would free up before the user commits to it.
    /// </summary>
    public static (bool Success, long SizeBytes, long ItemCount) QueryRecycleBin()
    {
        try
        {
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            var result = SHQueryRecycleBin(null, ref info);
            return result == 0 ? (true, info.i64Size, info.i64NumItems) : (false, 0, 0);
        }
        catch (Exception)
        {
            return (false, 0, 0);
        }
    }

    /// <summary>
    /// Empties the Recycle Bin across all drives via the Shell API - the same
    /// permanent action as Explorer's own "Empty Recycle Bin", suppressing the
    /// confirmation dialog and progress UI since UltraUninstaller shows its own
    /// confirmation before calling this.
    /// </summary>
    public static (bool Success, string? Error) EmptyRecycleBin()
    {
        try
        {
            const uint SHERB_NOCONFIRMATION = 0x00000001;
            const uint SHERB_NOPROGRESSUI = 0x00000002;
            const uint SHERB_NOSOUND = 0x00000004;

            var result = SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);

            // 0 = success. -2147418113 (E_UNEXPECTED)/0x800704D2 is what Windows returns
            // when the Recycle Bin is already empty on every drive - not a real failure.
            const int RecycleBinAlreadyEmpty = unchecked((int)0x800704D2);
            if (result == 0 || result == RecycleBinAlreadyEmpty)
                return (true, null);

            return (false, $"Emptying the Recycle Bin failed with code {result}.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    private const int FO_DELETE = 0x0003;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_NOERRORUI = 0x0400;
    private const ushort FOF_SILENT = 0x0004;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public int wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT fileOp);
}

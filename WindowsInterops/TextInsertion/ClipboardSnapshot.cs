using System.Runtime.InteropServices;

namespace WinFlow.WindowsInterops.TextInsertion;

public record ClipboardEntry(uint Format, object Data);

/// <summary>
/// A best-effort copy of every HGLOBAL-backed clipboard format.
/// <see cref="Capture"/> returns null when the clipboard cannot be opened — callers must
/// treat that as "do not touch the clipboard", never as an empty snapshot to restore over.
/// </summary>
public class ClipboardSnapshot
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    public List<ClipboardEntry> Entries { get; } = new();

    /// <summary>True when the clipboard was opened and fully enumerated (may legitimately be empty).</summary>
    public bool CapturedSuccessfully { get; private set; }

    public static ClipboardSnapshot? Capture()
    {
        var snapshot = new ClipboardSnapshot();
        if (!OpenClipboardWithRetry())
            return null;

        try
        {
            uint format = 0;
            while ((format = EnumClipboardFormats(format)) != 0)
            {
                // Only HGLOBAL-backed formats can be snapshotted this way. GDI-object
                // formats (CF_BITMAP etc.) and delayed-render handles must be skipped:
                // GlobalLock on them is undefined behavior.
                if (!IsGlobalBackedFormat(format)) continue;

                var hData = GetClipboardData(format);
                if (hData == IntPtr.Zero) continue;

                var size = GlobalSize(hData);
                if (size == 0 || size > 10 * 1024 * 1024) continue; // cap at 10MB per entry

                var ptr = GlobalLock(hData);
                if (ptr == IntPtr.Zero) continue;

                try
                {
                    if (format == CF_UNICODETEXT)
                    {
                        var text = Marshal.PtrToStringUni(ptr);
                        if (text != null)
                        {
                            snapshot.Entries.Add(new ClipboardEntry(format, text));
                        }
                    }
                    else
                    {
                        var buffer = new byte[(int)size];
                        Marshal.Copy(ptr, buffer, 0, buffer.Length);
                        snapshot.Entries.Add(new ClipboardEntry(format, buffer));
                    }
                }
                finally
                {
                    GlobalUnlock(hData);
                }
            }

            snapshot.CapturedSuccessfully = true;
        }
        catch
        {
            // Partial snapshot: restore only what was safely captured.
            snapshot.CapturedSuccessfully = true;
        }
        finally
        {
            CloseClipboard();
        }

        return snapshot;
    }

    public void Restore()
    {
        if (!CapturedSuccessfully || Entries.Count == 0) return;

        if (!OpenClipboardWithRetry()) return;

        try
        {
            EmptyClipboard();

            foreach (var entry in Entries)
            {
                byte[] bytes;
                if (entry.Data is string text)
                {
                    bytes = new byte[(text.Length + 1) * 2];
                    var chars = text.ToCharArray();
                    Buffer.BlockCopy(chars, 0, bytes, 0, chars.Length * 2);
                    // trailing null terminator already zeroed
                }
                else if (entry.Data is byte[] raw)
                {
                    bytes = raw;
                }
                else
                {
                    continue;
                }

                SetGlobalData(entry.Format, bytes);
            }
        }
        catch
        {
            // Ignore restore exceptions
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>
    /// Allocates an HGLOBAL, copies the payload, and hands ownership to the clipboard.
    /// The handle is unlocked BEFORE SetClipboardData (required by the clipboard contract)
    /// and freed only when the transfer fails.
    /// </summary>
    internal static bool SetGlobalData(uint format, byte[] bytes)
    {
        var hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
        if (hGlobal == IntPtr.Zero) return false;

        var target = GlobalLock(hGlobal);
        if (target == IntPtr.Zero)
        {
            GlobalFree(hGlobal);
            return false;
        }

        Marshal.Copy(bytes, 0, target, bytes.Length);
        GlobalUnlock(hGlobal); // unlock by HANDLE, before ownership transfer

        if (SetClipboardData(format, hGlobal) == IntPtr.Zero)
        {
            GlobalFree(hGlobal); // transfer failed: we still own it
            return false;
        }

        return true; // clipboard owns the memory now; never free or unlock again
    }

    private static bool IsGlobalBackedFormat(uint format)
    {
        // Standard HGLOBAL formats. CF_BITMAP(2), CF_METAFILEPICT(3), CF_ENHMETAFILE(14),
        // CF_PALETTE(9) etc. are GDI handles, not HGLOBAL.
        return format switch
        {
            1 /* CF_TEXT */ or 4 /* CF_SYLK */ or 5 /* CF_DIF */ or 6 /* CF_TIFF */
            or 7 /* CF_OEMTEXT */ or 8 /* CF_DIB */ or 13 /* CF_UNICODETEXT */
            or 15 /* CF_DIBV5 */ or 16 /* CF_LOCALE */ => true,
            _ => format >= 0xC000 // registered clipboard formats are HGLOBAL by convention
        };
    }

    private static bool OpenClipboardWithRetry(int attempts = 5, int delayMs = 15)
    {
        for (int i = 0; i < attempts; i++)
        {
            if (OpenClipboard(IntPtr.Zero)) return true;
            Thread.Sleep(delayMs);
        }
        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint EnumClipboardFormats(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr GlobalSize(IntPtr hMem);
}

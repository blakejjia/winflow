using System.Runtime.InteropServices;

namespace WinFlow.WindowsInterops.TextInsertion;

public record ClipboardEntry(uint Format, object Data);

public class ClipboardSnapshot
{
    private const uint CF_UNICODETEXT = 13;
    public List<ClipboardEntry> Entries { get; } = new();

    public static ClipboardSnapshot Capture()
    {
        var snapshot = new ClipboardSnapshot();
        if (!OpenClipboard(IntPtr.Zero))
            return snapshot;

        try
        {
            uint format = 0;
            while ((format = EnumClipboardFormats(format)) != 0)
            {
                var hData = GetClipboardData(format);
                if (hData == IntPtr.Zero) continue;

                if (format == CF_UNICODETEXT)
                {
                    var ptr = GlobalLock(hData);
                    if (ptr != IntPtr.Zero)
                    {
                        try
                        {
                            var text = Marshal.PtrToStringUni(ptr);
                            if (text != null)
                            {
                                snapshot.Entries.Add(new ClipboardEntry(format, text));
                            }
                        }
                        finally
                        {
                            GlobalUnlock(hData);
                        }
                    }
                }
                else
                {
                    var size = GlobalSize(hData);
                    if (size > 0 && size < 10 * 1024 * 1024) // Cap at 10MB per entry
                    {
                        var ptr = GlobalLock(hData);
                        if (ptr != IntPtr.Zero)
                        {
                            try
                            {
                                var buffer = new byte[size];
                                Marshal.Copy(ptr, buffer, 0, (int)size);
                                snapshot.Entries.Add(new ClipboardEntry(format, buffer));
                            }
                            finally
                            {
                                GlobalUnlock(hData);
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore capture exceptions
        }
        finally
        {
            CloseClipboard();
        }

        return snapshot;
    }

    public void Restore()
    {
        if (Entries.Count == 0) return;

        if (!OpenClipboard(IntPtr.Zero)) return;

        try
        {
            EmptyClipboard();

            foreach (var entry in Entries)
            {
                if (entry.Data is string text)
                {
                    var bytes = (text.Length + 1) * 2;
                    var hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
                    if (hGlobal != IntPtr.Zero)
                    {
                        var target = GlobalLock(hGlobal);
                        if (target != IntPtr.Zero)
                        {
                            try
                            {
                                Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                                Marshal.WriteInt16(target, text.Length * 2, 0); // null terminator
                                SetClipboardData(entry.Format, hGlobal);
                                hGlobal = IntPtr.Zero; // Ownership transferred to clipboard
                            }
                            finally
                            {
                                if (target != IntPtr.Zero) GlobalUnlock(hGlobal != IntPtr.Zero ? hGlobal : target);
                            }
                        }
                        if (hGlobal != IntPtr.Zero) GlobalFree(hGlobal);
                    }
                }
                else if (entry.Data is byte[] raw)
                {
                    var hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)raw.Length);
                    if (hGlobal != IntPtr.Zero)
                    {
                        var target = GlobalLock(hGlobal);
                        if (target != IntPtr.Zero)
                        {
                            try
                            {
                                Marshal.Copy(raw, 0, target, raw.Length);
                                SetClipboardData(entry.Format, hGlobal);
                                hGlobal = IntPtr.Zero;
                            }
                            finally
                            {
                                if (target != IntPtr.Zero) GlobalUnlock(hGlobal != IntPtr.Zero ? hGlobal : target);
                            }
                        }
                        if (hGlobal != IntPtr.Zero) GlobalFree(hGlobal);
                    }
                }
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

    private const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
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

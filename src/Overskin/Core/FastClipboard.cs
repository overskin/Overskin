using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Overskin;

/// <summary>
/// Puts a picture (and its file) on the clipboard straight through Windows, which is several
/// times quicker than WPF's own clipboard: WPF converts the picture into every format up front
/// (about a third of a second for a big screenshot), this hands Windows the pixels once.
/// Formats: the picture with its transparency (DIBV5, which Windows turns into the older
/// bitmap formats on request), the PNG file's bytes when there's a file, and the file itself,
/// so pasting into Discord or a folder keeps its name.
/// </summary>
internal static class FastClipboard
{
    private const uint CF_DIBV5 = 17, CF_HDROP = 15, GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr mem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr mem);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr mem);

    /// <summary>The picture, and the file when there is one. False when another app kept the clipboard busy.</summary>
    public static bool Copy(BitmapSource image, string? file = null)
    {
        var dib = Dib(image);
        byte[]? png = null;
        if (file != null && File.Exists(file) && Path.GetExtension(file).Equals(".png", StringComparison.OrdinalIgnoreCase))
            try { png = File.ReadAllBytes(file); } catch { }
        byte[]? drop = file != null && File.Exists(file) ? DropFiles(Path.GetFullPath(file)) : null;

        for (int attempt = 0; attempt < 10; attempt++)
        {
            if (!OpenClipboard(IntPtr.Zero))
            {
                Thread.Sleep(25); // another app has it open for a moment
                continue;
            }
            try
            {
                EmptyClipboard();
                bool ok = Put(CF_DIBV5, dib);
                if (png != null) Put(RegisterClipboardFormat("PNG"), png);
                if (drop != null)
                {
                    Put(CF_HDROP, drop);
                    Put(RegisterClipboardFormat("Preferred DropEffect"), BitConverter.GetBytes(1)); // a copy, not a cut
                }
                return ok;
            }
            finally
            {
                CloseClipboard();
            }
        }
        return false;
    }

    private static bool Put(uint format, byte[] bytes)
    {
        var mem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
        if (mem == IntPtr.Zero) return false;
        var at = GlobalLock(mem);
        Marshal.Copy(bytes, 0, at, bytes.Length);
        GlobalUnlock(mem);
        if (SetClipboardData(format, mem) != IntPtr.Zero) return true; // Windows owns it now
        GlobalFree(mem);
        return false;
    }

    /// <summary>A BITMAPV5HEADER and the pixels, bottom row first, with alpha.</summary>
    private static byte[] Dib(BitmapSource image)
    {
        var src = image.Format == PixelFormats.Bgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
        const int header = 124;
        var bytes = new byte[header + stride * h];
        using (var m = new MemoryStream(bytes))
        using (var b = new BinaryWriter(m))
        {
            b.Write(header);           // bV5Size
            b.Write(w);                // width
            b.Write(h);                // height: positive, so bottom row first
            b.Write((short)1);         // planes
            b.Write((short)32);        // bits per pixel
            b.Write(3);                // BI_BITFIELDS
            b.Write(stride * h);       // image size
            b.Write(3780); b.Write(3780); // 96 dpi
            b.Write(0); b.Write(0);    // colours used, important
            b.Write(0x00FF0000); b.Write(0x0000FF00); b.Write(0x000000FF); unchecked { b.Write((int)0xFF000000); } // R, G, B, A masks
            b.Write(0x73524742);       // LCS_sRGB
            // endpoints, gamma, intent, profile: zero (already)
        }
        // intent: LCS_GM_IMAGES
        BitConverter.GetBytes(4).CopyTo(bytes, 108);
        var rows = new byte[stride * h];
        src.CopyPixels(rows, stride, 0);
        for (int y = 0; y < h; y++)
            Buffer.BlockCopy(rows, y * stride, bytes, header + (h - 1 - y) * stride, stride);
        return bytes;
    }

    /// <summary>A DROPFILES list holding one file.</summary>
    private static byte[] DropFiles(string path)
    {
        var names = Encoding.Unicode.GetBytes(path + "\0\0");
        var bytes = new byte[20 + names.Length];
        BitConverter.GetBytes(20).CopyTo(bytes, 0);  // where the names start
        BitConverter.GetBytes(1).CopyTo(bytes, 16);  // wide characters
        names.CopyTo(bytes, 20);
        return bytes;
    }
}

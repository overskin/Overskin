# Builds Overskin for macOS on Windows: Overskin.app for Apple silicon and for Intel Macs,
# each zipped with Mac file permissions, into dist\mac\.
#
#   ./build-mac.ps1
#
# The program is signed ad hoc here (Apple silicon won't run unsigned code), and the zips are marked
# as made on Unix so the Mac keeps the program runnable. It isn't signed by a registered developer,
# so on the Mac, once after unzipping and moving it to Applications, run (Terminal):
#   xattr -cr /Applications/Overskin.app
# which clears the "downloaded" flag. (Or open it, then System Settings > Privacy & Security > Open Anyway.)

param([string] $Out = "")

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

# ---- Mac helpers: an ad-hoc code signature (Apple silicon won't run unsigned code), a check of it,
# ---- and marking a zip as made on Unix (otherwise the Mac ignores the "runnable" permission) ----
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

public static class MacTools
{
    static uint U32(byte[] b, int o) { return BitConverter.ToUInt32(b, o); }
    static ulong U64(byte[] b, int o) { return BitConverter.ToUInt64(b, o); }
    static void P32(byte[] b, int o, uint v) { Array.Copy(BitConverter.GetBytes(v), 0, b, o, 4); }
    static void P64(byte[] b, int o, ulong v) { Array.Copy(BitConverter.GetBytes(v), 0, b, o, 8); }
    static uint B32(byte[] b, int o) { return (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]); }
    static void W32(List<byte> l, uint v) { l.Add((byte)(v >> 24)); l.Add((byte)(v >> 16)); l.Add((byte)(v >> 8)); l.Add((byte)v); }
    static void W64(List<byte> l, ulong v) { W32(l, (uint)(v >> 32)); W32(l, (uint)v); }
    static long Align(long v, long a) { return (v + a - 1) / a * a; }

    /// Signs a thin 64-bit Mach-O program ad hoc (as "codesign --sign -" does), in place.
    public static void AdhocSign(string path, string identifier, bool mainBinary)
    {
        byte[] b = File.ReadAllBytes(path);
        if (U32(b, 0) != 0xfeedfacf) throw new Exception(path + " is not a 64-bit Mach-O program");
        int ncmds = (int)U32(b, 16), sizeofcmds = (int)U32(b, 20);
        int off = 32, linkedit = -1, text = -1;
        long firstSection = long.MaxValue;
        for (int i = 0; i < ncmds; i++)
        {
            uint cmd = U32(b, off), size = U32(b, off + 4);
            if (cmd == 0x1d) throw new Exception(path + " is already signed");
            if (cmd == 0x19)
            {
                string name = Encoding.ASCII.GetString(b, off + 8, 16).TrimEnd('\0');
                if (name == "__LINKEDIT") linkedit = off;
                if (name == "__TEXT") text = off;
                int nsect = (int)U32(b, off + 64);
                for (int k = 0; k < nsect; k++)
                {
                    uint fo = U32(b, off + 72 + 80 * k + 48);
                    if (fo != 0 && fo < firstSection) firstSection = fo;
                }
            }
            off += (int)size;
        }
        int cmdsEnd = 32 + sizeofcmds;
        if (linkedit < 0 || text < 0) throw new Exception("no __LINKEDIT or __TEXT in " + path);
        if (firstSection - cmdsEnd < 16) throw new Exception("no room for a signature in " + path);
        ulong leOff = U64(b, linkedit + 40), leSize = U64(b, linkedit + 48);
        if ((long)(leOff + leSize) != b.Length) throw new Exception("__LINKEDIT doesn't reach the end of " + path);

        long sigOff = Align(b.Length, 16);
        const int page = 4096;
        int nSlots = (int)((sigOff + page - 1) / page);
        byte[] ident = Encoding.ASCII.GetBytes(identifier + "\0");
        byte[] req = { 0xfa, 0xde, 0x0c, 0x01, 0, 0, 0, 12, 0, 0, 0, 0 }; // an empty requirements set
        const int nSpecial = 2, cdHeader = 88;
        int hashOff = cdHeader + ident.Length + nSpecial * 32;
        int cdLen = hashOff + nSlots * 32;
        int indexLen = 12 + 3 * 8;
        int total = indexLen + cdLen + req.Length + 8;
        long dataSize = Align(total, 16);

        byte[] o = new byte[sigOff + dataSize];
        Array.Copy(b, o, b.Length);
        P32(o, cmdsEnd, 0x1d); P32(o, cmdsEnd + 4, 16); P32(o, cmdsEnd + 8, (uint)sigOff); P32(o, cmdsEnd + 12, (uint)dataSize);
        P32(o, 16, (uint)(ncmds + 1)); P32(o, 20, (uint)(sizeofcmds + 16));
        ulong newLeSize = (ulong)(sigOff + dataSize) - leOff;
        P64(o, linkedit + 48, newLeSize);
        P64(o, linkedit + 32, (ulong)Align((long)newLeSize, 0x4000));

        var cd = new List<byte>();
        W32(cd, 0xfade0c02); W32(cd, (uint)cdLen); W32(cd, 0x20400); W32(cd, 0x2 /* adhoc */);
        W32(cd, (uint)hashOff); W32(cd, (uint)cdHeader); W32(cd, nSpecial); W32(cd, (uint)nSlots); W32(cd, (uint)sigOff);
        cd.Add(32); cd.Add(2 /* SHA-256 */); cd.Add(0); cd.Add(12 /* 4 KB pages */);
        W32(cd, 0); W32(cd, 0); W32(cd, 0); W32(cd, 0); W64(cd, 0);
        W64(cd, U64(o, text + 40)); W64(cd, U64(o, text + 48)); W64(cd, mainBinary ? 1UL : 0UL);
        if (cd.Count != cdHeader) throw new Exception("code directory header is " + cd.Count);
        cd.AddRange(ident);
        using (var sha = SHA256.Create())
        {
            cd.AddRange(sha.ComputeHash(req));   // slot -2: requirements
            cd.AddRange(new byte[32]);           // slot -1: Info.plist (not bound)
            for (int i = 0; i < nSlots; i++)
            {
                int start = i * page;
                int len = (int)Math.Min(page, sigOff - start);
                cd.AddRange(sha.ComputeHash(o, start, len));
            }
        }
        var sb = new List<byte>();
        W32(sb, 0xfade0cc0); W32(sb, (uint)total); W32(sb, 3);
        W32(sb, 0); W32(sb, (uint)indexLen);
        W32(sb, 2); W32(sb, (uint)(indexLen + cdLen));
        W32(sb, 0x10000); W32(sb, (uint)(indexLen + cdLen + req.Length));
        sb.AddRange(cd); sb.AddRange(req);
        W32(sb, 0xfade0b01); W32(sb, 8); // an empty signature wrapper, as ad-hoc signatures have
        sb.CopyTo(o, (int)sigOff);
        string tmp = path + ".signing";
        File.WriteAllBytes(tmp, o);
        File.Copy(tmp, path, true);
        File.Delete(tmp);
    }

    /// Checks every code signature in a Mach-O file (thin or universal) by hashing its pages again.
    public static string Verify(string path)
    {
        byte[] b = File.ReadAllBytes(path);
        if (B32(b, 0) == 0xcafebabe)
        {
            var parts = new List<string>();
            int n = (int)B32(b, 4);
            for (int i = 0; i < n; i++) parts.Add(VerifyThin(b, (int)B32(b, 8 + 20 * i + 8)));
            return string.Join("; ", parts);
        }
        return VerifyThin(b, 0);
    }

    static string VerifyThin(byte[] b, int @base)
    {
        if (U32(b, @base) != 0xfeedfacf) return "not a 64-bit Mach-O";
        int ncmds = (int)U32(b, @base + 16), off = @base + 32;
        for (int i = 0; i < ncmds; i++)
        {
            uint cmd = U32(b, off), size = U32(b, off + 4);
            if (cmd == 0x1d)
            {
                int sig = @base + (int)U32(b, off + 8);
                if (B32(b, sig) != 0xfade0cc0) return "bad signature blob";
                int count = (int)B32(b, sig + 8);
                for (int k = 0; k < count; k++)
                {
                    uint type = B32(b, sig + 12 + 8 * k);
                    int cd = sig + (int)B32(b, sig + 16 + 8 * k);
                    if (type != 0) continue;
                    if (B32(b, cd) != 0xfade0c02) return "bad code directory";
                    uint flags = B32(b, cd + 12);
                    int hashOff = (int)B32(b, cd + 16), nSpecial = (int)B32(b, cd + 24), nSlots = (int)B32(b, cd + 28);
                    long limit = B32(b, cd + 32);
                    int hashSize = b[cd + 36], hashType = b[cd + 37], pageSize = 1 << b[cd + 39];
                    HashAlgorithm h = hashType == 2 ? (HashAlgorithm)SHA256.Create() : SHA1.Create();
                    for (int s = 0; s < nSlots; s++)
                    {
                        int start = s * pageSize;
                        int len = (int)Math.Min(pageSize, limit - start);
                        byte[] got = h.ComputeHash(b, @base + start, len);
                        for (int x = 0; x < hashSize; x++)
                            if (got[x] != b[cd + hashOff + s * hashSize + x]) return "page " + s + " does not match";
                    }
                    if (nSpecial >= 2)
                        for (int q = 0; q < count; q++)
                            if (B32(b, sig + 12 + 8 * q) == 2)
                            {
                                int r = sig + (int)B32(b, sig + 16 + 8 * q);
                                byte[] got = h.ComputeHash(b, r, (int)B32(b, r + 4));
                                for (int x = 0; x < hashSize; x++)
                                    if (got[x] != b[cd + hashOff - 2 * hashSize + x]) return "requirements do not match";
                            }
                    return "ok (" + nSlots + " pages, flags 0x" + flags.ToString("x") + ")";
                }
                return "no code directory";
            }
            off += (int)size;
        }
        return "UNSIGNED";
    }

    /// Marks every entry in a zip as made on Unix, so its permissions (like "runnable") are kept on the Mac.
    public static void MarkUnix(string zip)
    {
        byte[] b = File.ReadAllBytes(zip);
        int eocd = -1;
        for (int i = b.Length - 22; i >= Math.Max(0, b.Length - 65557); i--)
            if (U32(b, i) == 0x06054b50) { eocd = i; break; }
        if (eocd < 0) throw new Exception("no end record in " + zip);
        int entries = BitConverter.ToUInt16(b, eocd + 10);
        int p = (int)U32(b, eocd + 16);
        for (int e = 0; e < entries; e++)
        {
            if (U32(b, p) != 0x02014b50) throw new Exception("bad central directory in " + zip);
            b[p + 4] = 20; b[p + 5] = 3; // version made by: 2.0, Unix
            p += 46 + BitConverter.ToUInt16(b, p + 28) + BitConverter.ToUInt16(b, p + 30) + BitConverter.ToUInt16(b, p + 32);
        }
        string tmp = zip + ".marking";
        File.WriteAllBytes(tmp, b);
        File.Copy(tmp, zip, true);
        File.Delete(tmp);
    }
}
'@

$here = $PSScriptRoot
$root = Resolve-Path (Join-Path $here "..\..")
$dist = if ($Out) { [IO.Path]::GetFullPath($Out) } else { Join-Path $root "dist\mac" }
$work = Join-Path $root "build\mac"
New-Item -ItemType Directory -Force $dist, $work | Out-Null

[xml] $proj = Get-Content (Join-Path $here "Overskin.Linux.csproj")
$version = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version

# ---- the icon: an .icns holding PNGs at the sizes macOS asks for ---------------------------------
function New-Icns([string] $png, [string] $icns) {
    $source = [System.Drawing.Image]::FromFile($png)
    $parts = New-Object System.Collections.Generic.List[byte[]]
    foreach ($entry in @(@("ic11", 32), @("ic12", 64), @("ic07", 128), @("ic13", 256), @("ic08", 256))) {
        $size = $entry[1]
        $bmp = New-Object System.Drawing.Bitmap $size, $size
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.DrawImage($source, 0, 0, $size, $size)
        $g.Dispose()
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        $data = $ms.ToArray()
        $head = [Text.Encoding]::ASCII.GetBytes($entry[0]) + [BitConverter]::GetBytes([uint32]($data.Length + 8))[3..0]
        $parts.Add([byte[]]($head + $data))
    }
    $source.Dispose()
    $total = 8
    foreach ($p in $parts) { $total += $p.Length }
    $file = New-Object System.IO.MemoryStream
    $file.Write([Text.Encoding]::ASCII.GetBytes("icns"), 0, 4)
    $file.Write([BitConverter]::GetBytes([uint32]$total)[3..0], 0, 4)
    foreach ($p in $parts) { $file.Write($p, 0, $p.Length) }
    [IO.File]::WriteAllBytes($icns, $file.ToArray())
}

$plist = @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Overskin</string>
  <key>CFBundleDisplayName</key><string>Overskin</string>
  <key>CFBundleIdentifier</key><string>app.overskin</string>
  <key>CFBundleExecutable</key><string>overskin</string>
  <key>CFBundleIconFile</key><string>overskin</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.productivity</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSSupportsAutomaticGraphicsSwitching</key><true/>
  <key>NSMicrophoneUsageDescription</key><string>Overskin records your microphone only when you choose to record sound with a video.</string>
  <key>NSAppleEventsUsageDescription</key><string>Overskin shows a notification when a capture is saved.</string>
</dict>
</plist>
"@

# ---- a zip that keeps the Mac's file permissions (the program must stay runnable) ----------------
function New-MacZip([string] $folder, [string] $zip) {
    if (Test-Path $zip) { Remove-Item $zip -Force }
    $archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $base = (Get-Item $folder).Parent.FullName.TrimEnd('\') + '\'
        foreach ($item in Get-ChildItem $folder -Recurse -Force | Sort-Object FullName) {
            if ($item.PSIsContainer) { continue }
            $name = $item.FullName.Substring($base.Length).Replace('\', '/')
            $entry = $archive.CreateEntry($name, [System.IO.Compression.CompressionLevel]::Optimal)
            # Unix permissions in the top half: rwxr-xr-x for programs and libraries, rw-r--r-- for the rest
            $exec = $name -match '/Contents/MacOS/(overskin|createdump)$' -or $name.EndsWith('.dylib')
            $mode = if ($exec) { 0x81ED } else { 0x81A4 }
            $entry.ExternalAttributes = $mode -shl 16
            $entry.LastWriteTime = $item.LastWriteTime
            $stream = $entry.Open()
            $bytes = [IO.File]::ReadAllBytes($item.FullName)
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Dispose()
        }
    }
    finally { $archive.Dispose() }
}

foreach ($target in @(@("osx-arm64", "AppleSilicon"), @("osx-x64", "Intel"))) {
    $rid = $target[0]; $label = $target[1]
    $publish = Join-Path $work "publish-$rid"
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
    Write-Host "Building Overskin $version for macOS ($label)..."
    dotnet publish (Join-Path $here "Overskin.Linux.csproj") -c Release -r $rid --self-contained true `
        -p:DebugType=none -o $publish --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "the $label build failed" }

    $app = Join-Path $work "$label\Overskin.app"
    if (Test-Path (Split-Path $app)) { Remove-Item (Split-Path $app) -Recurse -Force }
    $macos = Join-Path $app "Contents\MacOS"
    $resources = Join-Path $app "Contents\Resources"
    New-Item -ItemType Directory -Force $macos, $resources | Out-Null
    Copy-Item (Join-Path $publish "*") $macos -Recurse
    # Linux and Windows parts that a Mac never loads
    Get-ChildItem $macos -Filter "*.pdb" | Remove-Item
    # sign the program ad hoc (the libraries come signed), then check every piece of code in the app
    [MacTools]::AdhocSign((Join-Path $macos "overskin"), "app.overskin", $true)
    foreach ($lib in Get-ChildItem $macos -Filter "*.dylib" -Recurse) {
        if ([MacTools]::Verify($lib.FullName) -eq "UNSIGNED") { [MacTools]::AdhocSign($lib.FullName, $lib.BaseName, $false) }
    }
    foreach ($code in @(Get-Item (Join-Path $macos "overskin"), (Join-Path $macos "createdump")) + @(Get-ChildItem $macos -Filter "*.dylib" -Recurse)) {
        $check = [MacTools]::Verify($code.FullName)
        if ($check -notmatch '^ok' -or $check -match 'UNSIGNED|match|bad|no ') { throw "$($code.Name) isn't signed properly: $check" }
    }
    [IO.File]::WriteAllText((Join-Path $app "Contents\Info.plist"), $plist.Replace("`r`n", "`n"), (New-Object Text.UTF8Encoding $false))
    [IO.File]::WriteAllText((Join-Path $app "Contents\PkgInfo"), "APPL????")
    New-Icns (Join-Path $here "Assets\overskin.png") (Join-Path $resources "overskin.icns")

    $zip = Join-Path $dist "Overskin-mac-$label.zip"
    New-MacZip $app $zip
    [MacTools]::MarkUnix($zip)
    Write-Host ("Wrote {0} ({1:0.0} MB)" -f $zip, ((Get-Item $zip).Length / 1MB))
}

Write-Host ""
Write-Host "On the Mac, after unzipping and moving Overskin to Applications, run once in Terminal:"
Write-Host "  xattr -cr /Applications/Overskin.app"

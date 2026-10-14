using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace PvzBackupTool;

public static class Fmt
{
    public static string Bytes(long n)
    {
        string[] u = ["B", "KB", "MB", "GB", "TB"];
        double d = n;
        int i = 0;
        while (d >= 1024 && i < u.Length - 1) { d /= 1024; i++; }
        return i == 0 ? $"{n} {u[i]}" : $"{d:0.##} {u[i]}";
    }
}

public sealed record BackupInfo(string ZipPath, List<string> Packages, string? BakEntryName, long BakSize);

/// <summary>
/// Reads a MIUI "AllBackup" zip. Each zip holds descript.xml plus one .bak per app.
/// A .bak is a small MIUI text header wrapping a standard ANDROID BACKUP stream:
///
///     MIUI BACKUP\n 2\n &lt;pkg&gt; &lt;app label&gt;\n &lt;feature&gt;\n &lt;flag&gt;\n
///     ANDROID BACKUP\n &lt;version&gt;\n &lt;compressed 0|1&gt;\n &lt;encryption&gt;\n
///     &lt;ustar tar stream&gt;
///
/// The header length varies with the package and app-label length, so the tar offset
/// is found by parsing rather than by a fixed constant.
/// </summary>
public static class BackupArchive
{
    /// <summary>Opens a zip and reports which packages it contains. Throws if it is not a valid MIUI backup.</summary>
    public static BackupInfo Inspect(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);

        var descript = zip.GetEntry("descript.xml")
            ?? throw new InvalidDataException("Not a MIUI backup: descript.xml is missing.");

        var packages = new List<string>();
        string? bakName = null;
        long bakSize = 0;

        using (var s = descript.Open())
        {
            var doc = XDocument.Load(s);
            foreach (var pkg in doc.Descendants("package"))
            {
                var name = pkg.Element("packageName")?.Value;
                if (!string.IsNullOrWhiteSpace(name)) packages.Add(name!);
            }
        }

        foreach (var e in zip.Entries)
        {
            if (e.FullName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
            {
                bakName = e.FullName;
                bakSize = e.Length;
                break;
            }
        }

        if (bakName is null)
            throw new InvalidDataException("Not a MIUI backup: no .bak payload inside the zip.");

        return new BackupInfo(zipPath, packages, bakName, bakSize);
    }

    /// <summary>
    /// Extracts the save-relevant trees (files/, shared_prefs/, databases/) out of the
    /// .bak inside the zip. The APK and the dex/oat caches are deliberately skipped -
    /// they are the bulk of the archive and are worthless for reading progress.
    /// Returns the number of files written, or -1 if the payload is compressed/encrypted.
    /// </summary>
    public static int ExtractSaveData(string zipPath, string outDir, Action<string> log, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(zipPath);

        var bak = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("No .bak payload inside the zip.");

        using var raw = bak.Open();
        using var stream = new BufferedStream(raw, 1 << 20);

        // --- Parse the MIUI + ANDROID BACKUP text headers, byte-exactly. ---
        string? line;
        int guard = 0;
        bool found = false;

        while ((line = ReadLine(stream)) != null && guard++ < 32)
        {
            if (line == "ANDROID BACKUP") { found = true; break; }
        }

        if (!found)
            throw new InvalidDataException("ANDROID BACKUP header not found inside the .bak.");

        var version = ReadLine(stream);
        var compressed = ReadLine(stream);
        var encryption = ReadLine(stream);

        log($"  payload: ANDROID BACKUP v{version}, compressed={compressed}, encryption={encryption}");

        if (compressed != "0")
        {
            log("  payload is deflate-compressed - skipping extraction (the zip is still restorable).");
            return -1;
        }
        if (!string.Equals(encryption, "none", StringComparison.OrdinalIgnoreCase))
        {
            log($"  payload is encrypted ({encryption}) - skipping extraction (the zip is still restorable).");
            return -1;
        }

        // Stream position is now exactly at the start of the ustar data.
        var root = Path.GetFullPath(outDir);
        Directory.CreateDirectory(root);

        int written = 0;
        using var tar = new TarReader(stream, leaveOpen: true);

        while (tar.GetNextEntry() is { } entry)
        {
            ct.ThrowIfCancellationRequested();

            var mapped = MapEntry(entry.Name);
            if (mapped is null) continue;

            var dest = Path.GetFullPath(Path.Combine(root, mapped));

            // Reject tar-slip: anything resolving outside the output root.
            if (!dest.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                log($"  ! skipped suspicious path: {entry.Name}");
                continue;
            }

            if (entry.EntryType is TarEntryType.Directory)
            {
                Directory.CreateDirectory(dest);
                continue;
            }

            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                continue;

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

            using (var outFile = File.Create(dest))
                entry.DataStream?.CopyTo(outFile);

            written++;
        }

        return written;
    }

    /// <summary>
    /// Maps an android-backup tar path to a friendly output path, or null to skip.
    /// Layout is apps/&lt;pkg&gt;/&lt;bucket&gt;/... where a=apk, f=files, sp=shared_prefs,
    /// db=databases, r=app root, ef=external files.
    /// </summary>
    static string? MapEntry(string name)
    {
        var parts = name.Replace('\\', '/').Split('/', StringSplitOptions.None);
        if (parts.Length < 4) return null;
        if (parts[0] != "apps") return null;

        var bucket = parts[2];

        // Android filenames legally contain characters Windows forbids - Firebase
        // config files, for example, are named frc_1:1234:android:abcd_....json.
        // Sanitise every segment or File.Create throws on the colon.
        var segments = parts.Skip(3).Select(SanitizeSegment).Where(s => s.Length > 0).ToArray();
        if (segments.Length == 0) return null;

        var rest = string.Join(Path.DirectorySeparatorChar, segments);

        return bucket switch
        {
            "f" => Path.Combine("files", rest),
            "sp" => Path.Combine("shared_prefs", rest),
            "db" => Path.Combine("databases", rest),
            _ => null, // a/ (apk) and r/ (dex + webview caches) are intentionally dropped
        };
    }

    static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    /// <summary>Makes one path segment safe for NTFS without collapsing distinct names.</summary>
    static string SanitizeSegment(string segment)
    {
        if (segment is "." or "..") return "";

        var sb = new StringBuilder(segment.Length);
        foreach (var c in segment)
            sb.Append(Array.IndexOf(InvalidChars, c) >= 0 ? '_' : c);

        // Windows silently strips trailing dots and spaces; strip them ourselves so
        // the name on disk matches what we think we wrote.
        var s = sb.ToString().TrimEnd('.', ' ');
        return s.Length == 0 ? "_" : s;
    }

    /// <summary>Reads one LF-terminated ASCII line, consuming exactly its bytes and no more.</summary>
    static string? ReadLine(Stream s)
    {
        var sb = new StringBuilder(64);
        int b;
        while ((b = s.ReadByte()) != -1)
        {
            if (b == '\n') return sb.ToString().TrimEnd('\r');
            sb.Append((char)b);
            if (sb.Length > 4096) return null; // not a text header; bail out
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }
}

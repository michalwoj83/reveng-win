using System.Diagnostics;

using System.Reflection.PortableExecutable;
using System.Text;
using ReaWin.Models;

namespace ReaWin.Services;

/// <summary>
/// Finds .exe/.dll files under an application directory and reads PE header facts.
/// Files are only parsed, never loaded or executed.
/// </summary>
public static class PeScanner
{
    private static readonly System.IO.EnumerationOptions Walk = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        // Skip junctions and symlinks so a link cannot lead the scan outside the chosen folder.
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        MatchCasing = MatchCasing.CaseInsensitive,
    };

    public static Task<List<PeFileInfo>> ScanAsync(
        string root, IProgress<string>? progress, CancellationToken ct) =>
        Task.Run(() =>
        {
            var results = new List<PeFileInfo>();
            foreach (var path in Directory.EnumerateFiles(root, "*", Walk))
            {
                ct.ThrowIfCancellationRequested();
                var ext = Path.GetExtension(path);
                if (!ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".dll", StringComparison.OrdinalIgnoreCase))
                    continue;
                progress?.Report(Path.GetRelativePath(root, path));
                results.Add(Read(root, path));
            }
            return results;
        }, ct);

    public static PeFileInfo Read(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        long size = 0;
        try { size = new FileInfo(path).Length; } catch (IOException) { }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var pe = new PEReader(stream);
            var headers = pe.PEHeaders;
            var optional = headers.PEHeader
                ?? throw new BadImageFormatException("No PE optional header.");

            var version = TryVersion(path);
            return new PeFileInfo(
                path, relative, size,
                IsDll: headers.CoffHeader.Characteristics.HasFlag(Characteristics.Dll),
                IsManaged: headers.CorHeader is not null,
                Machine: headers.CoffHeader.Machine.ToString(),
                Subsystem: optional.Subsystem.ToString(),
                EntryPointRva: (uint)optional.AddressOfEntryPoint,
                Imports: ReadImports(pe, optional),
                Exports: ReadExports(pe, optional),
                ProductName: version?.ProductName,
                FileDescription: version?.FileDescription,
                CompanyName: version?.CompanyName,
                OriginalFilename: version?.OriginalFilename,
                Error: null);
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new PeFileInfo(path, relative, size, path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase),
                false, "unknown", "unknown", 0, Array.Empty<string>(), Array.Empty<string>(),
                null, null, null, null, ex.Message);
        }
    }

    private static FileVersionInfo? TryVersion(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return string.IsNullOrWhiteSpace(info.ProductName) && string.IsNullOrWhiteSpace(info.FileDescription)
                && string.IsNullOrWhiteSpace(info.CompanyName) ? null : info;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return null; }
    }

    private static List<string> ReadImports(PEReader pe, PEHeader optional)
    {
        var names = new List<string>();
        var dir = optional.ImportTableDirectory;
        if (dir.Size == 0 || dir.RelativeVirtualAddress == 0) return names;
        try
        {
            var reader = pe.GetSectionData(dir.RelativeVirtualAddress).GetReader();
            // IMAGE_IMPORT_DESCRIPTOR is 20 bytes; a zeroed Name field terminates the table.
            for (var i = 0; i < 4096 && reader.RemainingBytes >= 20; i++)
            {
                reader.ReadUInt32(); reader.ReadUInt32(); reader.ReadUInt32();
                var nameRva = reader.ReadUInt32();
                reader.ReadUInt32();
                if (nameRva == 0) break;
                var name = ReadAscii(pe, (int)nameRva);
                if (name.Length > 0) names.Add(name);
            }
        }
        catch (BadImageFormatException) { }
        return names;
    }

    private static List<string> ReadExports(PEReader pe, PEHeader optional)
    {
        var names = new List<string>();
        var dir = optional.ExportTableDirectory;
        if (dir.Size == 0 || dir.RelativeVirtualAddress == 0) return names;
        try
        {
            var reader = pe.GetSectionData(dir.RelativeVirtualAddress).GetReader();
            if (reader.RemainingBytes < 40) return names;
            reader.Offset += 24; // Characteristics..NumberOfFunctions precede NumberOfNames.
            var nameCount = reader.ReadUInt32();
            reader.ReadUInt32(); // AddressOfFunctions
            var namesRva = reader.ReadUInt32();
            if (namesRva == 0) return names;

            var table = pe.GetSectionData((int)namesRva).GetReader();
            for (var i = 0u; i < nameCount && i < 2000 && table.RemainingBytes >= 4; i++)
            {
                var name = ReadAscii(pe, (int)table.ReadUInt32());
                if (name.Length > 0) names.Add(name);
            }
        }
        catch (BadImageFormatException) { }
        return names;
    }

    private static string ReadAscii(PEReader pe, int rva)
    {
        var reader = pe.GetSectionData(rva).GetReader();
        var sb = new StringBuilder();
        while (reader.RemainingBytes > 0 && sb.Length < 260)
        {
            var b = reader.ReadByte();
            if (b == 0) break;
            sb.Append((char)b);
        }
        return sb.ToString();
    }
}

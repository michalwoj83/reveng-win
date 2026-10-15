using System.Text.Json;
using ReaWin.Models;

namespace ReaWin.Services;

/// <summary>
/// The app's own folder of analysis projects. Each project is a `.reaproj` file plus a small `.json` catalog entry,
/// so listing projects never has to decompress a large file. A project is keyed by the file name and SHA-256 of
/// the analyzed binary, so analyzing the same binary again updates its project instead of adding another.
/// </summary>
public sealed class ProjectLibrary(string folder)
{
    private const string MetaExtension = ".json";

    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReaWin", "projects");

    public string Folder => folder;

    public static string IdFor(string targetPath, string sha256)
    {
        var name = Path.GetFileNameWithoutExtension(targetPath);
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var hash = sha256.Length >= 12 ? sha256[..12].ToLowerInvariant() : "nohash";
        return $"{safe}-{hash}";
    }

    public string ProjectPath(string id) => Path.Combine(folder, id + ProjectStore.Extension);

    private string MetaPath(string id) => Path.Combine(folder, id + MetaExtension);

    /// <summary>Catalog entries, newest first. Unreadable entries are skipped rather than failing the list.</summary>
    public List<ProjectMeta> List()
    {
        var result = new List<ProjectMeta>();
        if (!Directory.Exists(folder)) return result;
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*" + MetaExtension))
            {
                try
                {
                    var meta = JsonSerializer.Deserialize<ProjectMeta>(File.ReadAllText(file));
                    if (meta is not null && File.Exists(ProjectPath(meta.Id))) result.Add(meta);
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return result.OrderByDescending(m => m.SavedUtc).ToList();
    }

    /// <summary>Stores <paramref name="project"/> under <paramref name="id"/>, replacing any earlier version.</summary>
    public async Task<ProjectMeta> SaveAsync(string id, ProjectFile project, CancellationToken ct = default)
    {
        try { Directory.CreateDirectory(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ReaException($"Could not create the project folder {folder}: {ex.Message}", -1);
        }

        var path = ProjectPath(id);
        await ProjectStore.SaveAsync(path, project, ct).ConfigureAwait(false);

        var meta = new ProjectMeta
        {
            Id = id,
            Name = Path.GetFileName(project.TargetPath),
            TargetPath = project.TargetPath,
            Sha256 = project.Overview.Sha256,
            SavedUtc = project.SavedUtc,
            Functions = project.Procedures.Count(p => !p.IsImport),
            Decompiled = project.Decompiled.Count,
            SizeBytes = new FileInfo(path).Length,
        };
        var metaPath = MetaPath(id);
        var temp = metaPath + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(meta), ct).ConfigureAwait(false);
            File.Move(temp, metaPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ReaException($"Could not update the project catalog: {ex.Message}", -1);
        }
        return meta;
    }

    public void Delete(string id)
    {
        try
        {
            File.Delete(MetaPath(id));
            File.Delete(ProjectPath(id));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ReaException($"Could not delete the project: {ex.Message}", -1);
        }
    }
}

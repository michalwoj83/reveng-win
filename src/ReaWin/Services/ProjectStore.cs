using System.IO.Compression;
using System.Text.Json;
using ReaWin.Models;

namespace ReaWin.Services;

/// <summary>
/// Reads and writes `.reaproj` files: the results of one analysis (overview, functions, strings, decompiled
/// code) so the user can reopen them without running Ghidra again. The file is gzip-compressed JSON.
/// </summary>
public static class ProjectStore
{
    public const int CurrentVersion = 1;
    public const string Extension = ".reaproj";

    /// <summary>Writes atomically: a failed save never leaves a half-written project in place of a good one.</summary>
    public static async Task SaveAsync(string path, ProjectFile project, CancellationToken ct = default)
    {
        var temp = path + ".tmp";
        try
        {
            await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            // Fastest: the file is mostly repetitive source text, and saving should not make the user wait.
            await using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
                await JsonSerializer.SerializeAsync(gzip, project, cancellationToken: ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw new ReaException($"Could not save the project: {ex.Message}", -1);
        }
    }

    public static async Task<ProjectFile> LoadAsync(string path, CancellationToken ct = default)
    {
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            var project = await JsonSerializer.DeserializeAsync<ProjectFile>(gzip, cancellationToken: ct).ConfigureAwait(false);
            if (project is null || project.Overview is null)
                throw new ReaException($"'{path}' is not a REA project.", -1);
            if (project.Version > CurrentVersion)
                throw new ReaException(
                    $"'{path}' was saved by a newer version of this app (project format {project.Version}).", -1);
            return project;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            throw new ReaException($"'{path}' is not a readable REA project: {ex.Message}", -1);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ReaException($"Could not open the project: {ex.Message}", -1);
        }
    }
}

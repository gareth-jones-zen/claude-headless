using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RepoCommit;

namespace ClaudeHeadlessTool;

/// <summary>What a job that edits in place changed (0.6.0: commit_changed). Before the job touches anything, the files git
/// already sees as changed (modified, new or deleted) are recorded with a hash of each; afterwards, a file counts as the
/// job's if git sees it as changed and it wasn't, or its content is different. So someone's uncommitted work elsewhere
/// in the repository is never committed with the job's, and the job's own edits are, even in a file that already had
/// uncommitted changes.</summary>
public static class Changes
{
    /// <summary>Where a run's snapshot is kept, under the queue's folder (not the repository, not the work folder,
    /// which Claude reads): one per job and input, until the job finishes.</summary>
    public static string SnapshotFile(string home, string job, string input) =>
        Path.Combine(home, "snapshots", $"{Jobs.Slug(job)}-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input.ToLowerInvariant())))[..12]}.json");

    /// <summary>The snapshot from before the job began: the one an earlier attempt at the same run saved (a usage limit
    /// cut it off, maybe half way through Claude's edits), or one taken and saved now.</summary>
    public static IReadOnlyDictionary<string, string> Before(Committer git, string file)
    {
        try
        {
            if (File.Exists(file) && JsonNode.Parse(File.ReadAllText(file, Encoding.UTF8)) is JsonObject saved)
                return saved.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? "", StringComparer.Ordinal);
        }
        catch (JsonException)
        {
        }
        var now = Take(git);
        var json = new JsonObject();
        foreach (var (path, hash) in now)
            json[path] = hash;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, json.ToJsonString(), new UTF8Encoding(false));
        return now;
    }

    /// <summary>The files git sees as changed now (repository paths), each with a hash of its content ("(gone)" for one
    /// deleted). Nothing when git isn't set up for the folder. git failing is an IOException.</summary>
    public static Dictionary<string, string> Take(Committer git)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (git.Skipped is not null)
                return result;
            var top = git.Top;
            var entries = Directory.EnumerateFileSystemEntries(top)
                .Where(e => !string.Equals(Path.GetFileName(e), ".git", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var path in git.Changed(entries))
                result[path] = Hash(Path.Combine(top, path));
            return result;
        }
        catch (Exception error) when (error is GitException or InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            throw new IOException($"git status: {error.Message}", error);
        }
    }

    /// <summary>What changed since the snapshot: inside the globs (to commit) and outside them (left alone, reported).</summary>
    public static (List<string> Inside, List<string> Outside) Since(Committer git, IReadOnlyDictionary<string, string> before, IReadOnlyList<Regex> globs)
    {
        var inside = new List<string>();
        var outside = new List<string>();
        foreach (var (path, hash) in Take(git))
        {
            if (before.TryGetValue(path, out var was) && was == hash)
                continue;
            (globs.Any(g => g.IsMatch(path)) ? inside : outside).Add(path);
        }
        return (inside, outside);
    }

    private static string Hash(string file) =>
        File.Exists(file) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))) : "(gone)";
}

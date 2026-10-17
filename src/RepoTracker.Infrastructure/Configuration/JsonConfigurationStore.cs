using RepoTracker.Core.Localization;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using RepoTracker.Core.Tracking;
using RepoTracker.Core.Sources;

namespace RepoTracker.Infrastructure.Configuration;

public sealed class JsonConfigurationStore(string profileDirectory) : IConfigurationStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private const int MaxBytes = 4 * 1024 * 1024;
    private readonly string path = Path.Combine(profileDirectory, "state.json");
    private byte[]? lastGood;
    private bool writable;

    public async Task<ConfigurationSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        writable = false;
        lastGood = null;
        byte[]? bytes;
        try { bytes = await ReadAsync(path, cancellationToken); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return new(new(), "unreadable", false, Text.Get("Config.ReadFailed")); }
        var revision = Fingerprint(bytes);
        if (bytes is null) { writable = true; return new(new(), revision); }
        try
        {
            var state = Deserialize(bytes);
            lastGood = Serialize(state);
            writable = true;
            return new(state, revision);
        }
        catch (FutureSchemaException)
        { return new(new(), revision, false, Text.Get("Config.NewerVersion")); }
        catch (Exception error) when (error is JsonException or TrackerException or InvalidDataException or ArgumentException)
        {
            try
            {
                var preserved = Path.Combine(profileDirectory, $"state.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
                await AtomicWriteAsync(preserved, bytes, cancellationToken);
                TrackerState recovered = new();
                var backup = await ReadAsync(path + ".bak", cancellationToken);
                if (backup is not null)
                {
                    try { recovered = Deserialize(backup); lastGood = Serialize(recovered); }
                    catch (Exception backupError) when (backupError is JsonException or TrackerException or InvalidDataException or ArgumentException or FutureSchemaException)
                    { return new(new(), revision, false, Text.Get("Config.BackupFailed")); }
                }
                writable = true;
                return new(recovered, revision, true, backup is null
                    ? Text.Format("Config.CorruptEmpty", Path.GetFileName(preserved))
                    : Text.Get("Config.Recovered"));
            }
            catch (Exception backupError) when (backupError is IOException or UnauthorizedAccessException)
            { return new(new(), revision, false, Text.Get("Config.CannotRecover")); }
        }
    }

    public async Task<string> SaveAsync(TrackerState state, string expectedRevision, CancellationToken cancellationToken = default)
    {
        if (!writable) throw new TrackerException(Text.Get("Config.NotWritable"));
        Directory.CreateDirectory(profileDirectory);
        await using var lease = new FileStream(Path.Combine(profileDirectory, "state.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous);
        var current = await ReadAsync(path, cancellationToken);
        if (Fingerprint(current) != expectedRevision) throw new TrackerException(Text.Get("Config.ExternalChange"));
        var bytes = Serialize(Validate(state));
        if (bytes.Length > MaxBytes) throw new TrackerException(Text.Get("Config.TooLarge"));
        if (lastGood is not null) await AtomicWriteAsync(path + ".bak", lastGood, cancellationToken);
        await AtomicWriteAsync(path, bytes, cancellationToken);
        lastGood = bytes;
        return Fingerprint(bytes);
    }

    private static async Task AtomicWriteAsync(string destination, byte[] bytes, CancellationToken cancellationToken)
    {
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await file.WriteAsync(bytes, cancellationToken);
                file.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(destination)) File.Replace(temporary, destination, null);
            else File.Move(temporary, destination);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task<byte[]?> ReadAsync(string file, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            if (stream.Length > MaxBytes) throw new IOException("Configuration exceeds size limit.");
            using var bytes = new MemoryStream();
            await stream.CopyToAsync(bytes, cancellationToken);
            return bytes.ToArray();
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static TrackerState Deserialize(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected an object.");
        var version = 1;
        if (root.TryGetProperty("schemaVersion", out var value) &&
            (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out version)))
            throw new InvalidDataException("Invalid schema number.");
        if (version > TrackerState.CurrentSchema) throw new FutureSchemaException();
        if (version is < 1) throw new InvalidDataException("Unknown schema.");
        if (!root.TryGetProperty("repositories", out var repositories) || repositories.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Missing repository collection.");
        var state = JsonSerializer.Deserialize<TrackerState>(bytes, Json) ?? throw new InvalidDataException("Empty state.");
        return Validate(state with { SchemaVersion = TrackerState.CurrentSchema,
            RemoteEnabled = version >= 3 && state.RemoteEnabled, Sources = version >= 3 ? state.Sources : SourceRules.Defaults,
            Preferences = version >= 4 ? state.Preferences : new() });
    }

    private static TrackerState Validate(TrackerState state)
    {
        if (state.Preferences is null) throw new InvalidDataException("Missing preferences.");
        // Unknown optional language codes must not invalidate an otherwise usable repository list.
        if (!Text.IsSupportedPreference(state.Preferences.Language))
            state = state with { Preferences = state.Preferences with { Language = Text.SystemLanguage } };
        state.Preferences.Validate();
        if (state.Repositories.IsDefault || state.Groups.IsDefault || state.Sources.IsDefault) throw new InvalidDataException("Null collection.");
        var sourceIds = new HashSet<Guid>();
        var sources = ImmutableArray<SourceRule>.Empty;
        foreach (var rule in state.Sources)
        {
            if (rule is null || !sourceIds.Add(rule.Id)) throw new InvalidDataException("Invalid source identity.");
            sources = sources.Add(SourceRules.Validate(rule, sources));
        }
        var groupIds = new HashSet<Guid>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groups = state.Groups.Select(group =>
        {
            if (group is null || group.Id == Guid.Empty || !groupIds.Add(group.Id) || group.Name is null)
                throw new InvalidDataException("Invalid group identity.");
            var name = GroupNames.Normalize(group.Name);
            if (!names.Add(name)) throw new InvalidDataException("Duplicate group name.");
            return group with { Name = name };
        }).OrderBy(group => group.Order).ToImmutableArray();
        var repoIds = new HashSet<Guid>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var repos = state.Repositories.Select(repo =>
        {
            if (repo is null || repo.Id == Guid.Empty || !repoIds.Add(repo.Id) || string.IsNullOrWhiteSpace(repo.WorktreeRoot) ||
                !Path.IsPathFullyQualified(repo.WorktreeRoot) || !paths.Add(repo.WorktreeRoot) ||
                string.IsNullOrWhiteSpace(repo.GitDir) || !Path.IsPathFullyQualified(repo.GitDir) ||
                string.IsNullOrWhiteSpace(repo.CommonDir) || !Path.IsPathFullyQualified(repo.CommonDir) || string.IsNullOrWhiteSpace(repo.DisplayName))
                throw new InvalidDataException("Invalid repository.");
            return repo.GroupId is not null && !groupIds.Contains(repo.GroupId.Value) ? repo with { GroupId = null } : repo;
        }).ToImmutableArray();
        return state with { SchemaVersion = TrackerState.CurrentSchema, Groups = groups, Repositories = repos, Sources = sources };
    }

    private static byte[] Serialize(TrackerState state) => JsonSerializer.SerializeToUtf8Bytes(state, Json);
    private static string Fingerprint(byte[]? bytes) => bytes is null ? "missing" : Convert.ToHexString(SHA256.HashData(bytes));
    private sealed class FutureSchemaException : Exception;
}

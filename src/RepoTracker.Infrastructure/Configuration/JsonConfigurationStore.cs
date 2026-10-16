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
        { return new(new(), "unreadable", false, "无法读取配置，原文件已保留。请检查 profile 目录权限后重新启动。"); }
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
        { return new(new(), revision, false, "配置来自更新版本的 RepoTracker。请使用对应版本打开；当前不会覆盖文件。"); }
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
                    { return new(new(), revision, false, "配置和备份均无法恢复；原文件及损坏副本已保留，当前禁止保存。"); }
                }
                writable = true;
                return new(recovered, revision, true, backup is null
                    ? $"配置损坏，已保留 {Path.GetFileName(preserved)}，当前使用空列表。"
                    : "配置损坏，已保留原文件副本并读回上一次有效备份；下次修改时保存恢复后的列表。");
            }
            catch (Exception backupError) when (backupError is IOException or UnauthorizedAccessException)
            { return new(new(), revision, false, "配置无法恢复或无法备份，原文件未覆盖，当前禁止保存。"); }
        }
    }

    public async Task<string> SaveAsync(TrackerState state, string expectedRevision, CancellationToken cancellationToken = default)
    {
        if (!writable) throw new TrackerException("配置尚未加载或不可写，原文件未覆盖。");
        Directory.CreateDirectory(profileDirectory);
        await using var lease = new FileStream(Path.Combine(profileDirectory, "state.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous);
        var current = await ReadAsync(path, cancellationToken);
        if (Fingerprint(current) != expectedRevision) throw new TrackerException("配置已被其他程序修改。请重新启动后再操作，外部内容未覆盖。");
        var bytes = Serialize(Validate(state));
        if (bytes.Length > MaxBytes) throw new TrackerException("配置过大，无法保存；原列表未改变。");
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

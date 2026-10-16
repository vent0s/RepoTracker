using Microsoft.Win32;
using RepoTracker.Core.Tracking;

namespace RepoTracker.Infrastructure.Runtime;

public interface IStartupValueStore
{
    string? Read(string name);
    void Write(string name, string? command);
}

public sealed class RegistryStartupValueStore : IStartupValueStore
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public string? Read(string name)
    { using var key = Registry.CurrentUser.OpenSubKey(Key); return key?.GetValue(name) as string; }
    public void Write(string name, string? command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key, writable: true);
        if (command is null) key.DeleteValue(name, throwOnMissingValue: false);
        else key.SetValue(name, command, RegistryValueKind.String);
    }
}

/// <summary>The OS entry is the source of truth. No duplicate desired flag or automatic registration at app startup.</summary>
public sealed class StartupRegistration(IStartupValueStore store, string executable, string profile) : IStartupRegistration
{
    private readonly string name = "RepoTracker." + InstanceIdentity.ForProfile(profile);
    public bool IsEnabled => store.Read(name) == Command(executable, profile);
    public void SetEnabled(bool enabled)
    {
        var expected = Command(executable, profile);
        var previous = store.Read(name);
        if (!enabled && previous is not null && previous != expected)
            throw new TrackerException("启动项来自其他程序位置，未删除；请在 Windows 启动设置中检查。");
        store.Write(name, enabled ? expected : null);
        if (store.Read(name) != (enabled ? expected : null)) throw new TrackerException("Windows 启动项未保存，请检查当前用户权限。");
    }
    public static string Command(string executable, string profile)
    {
        if (!Path.IsPathFullyQualified(executable) || !Path.IsPathFullyQualified(profile) || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            executable.Any(c => c == '"' || char.IsControl(c)) || profile.Any(c => c == '"' || char.IsControl(c)))
            throw new TrackerException("当前程序位置无法注册登录启动。");
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile));
        // A root directory retains its final backslash; escape it before the closing Windows command-line quote.
        if (normalized.EndsWith('\\')) normalized += "\\";
        var command = $"\"{executable}\" --profile \"{normalized}\" --minimized";
        if (command.Length > 260) throw new TrackerException("程序与配置目录路径过长，无法注册登录启动；请使用较短的安装位置。");
        return command;
    }
}

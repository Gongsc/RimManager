using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RimManager;

internal sealed record WorkshopUpdate(Mod Mod, long Installed, long Latest);
internal sealed record WorkshopPresentation(string Description, string Cover);
internal readonly record struct WorkshopCheckProgress(int Completed, int Total);

internal static class Workshop
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };

    private static async Task<JsonDocument> GetDetails(IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        var fields = new List<KeyValuePair<string, string>> { new("itemcount", ids.Count.ToString()) };
        fields.AddRange(ids.Select((id, index) => new KeyValuePair<string, string>($"publishedfileids[{index}]", id)));
        using var content = new FormUrlEncodedContent(fields);
        using var response = await Http.PostAsync("https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/", content, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    internal static async Task<WorkshopPresentation?> GetPresentation(string id, CancellationToken cancellationToken)
    {
        using var json = await GetDetails([id], cancellationToken);
        foreach (var item in json.RootElement.GetProperty("response").GetProperty("publishedfiledetails").EnumerateArray())
        {
            if (item.GetProperty("result").GetInt32() != 1 ||
                item.GetProperty("consumer_app_id").GetInt32() != int.Parse(ModLibrary.AppId)) continue;
            string Read(string key) => item.TryGetProperty(key, out var value) ? value.GetString() ?? "" : "";
            return new(Read("description"), Read("preview_url"));
        }
        return null;
    }

    internal static async Task<(List<WorkshopUpdate> Updates, int Unknown)> Check(
        IEnumerable<Mod> mods, IReadOnlyDictionary<string, long> revisions, IProgress<WorkshopCheckProgress> progress)
    {
        var items = mods.Where(m => m.Manifest is not null && m.WorkshopId is not null).ToList();
        var manifests = new Dictionary<string, string>(ModLibrary.Ids);
        var updates = new List<WorkshopUpdate>();
        var unknown = 0;
        var completed = 0;
        progress.Report(new(completed, items.Count));
        foreach (var batch in items.Chunk(50))
        {
            using var json = await GetDetails(batch.Select(m => m.WorkshopId!).ToArray());
            var remote = json.RootElement.GetProperty("response").GetProperty("publishedfiledetails")
                .EnumerateArray().Where(x => x.GetProperty("result").GetInt32() == 1)
                .ToDictionary(x => x.GetProperty("publishedfileid").GetString()!, x => x.GetProperty("time_updated").GetInt64());
            foreach (var mod in batch)
            {
                if (!manifests.TryGetValue(mod.Manifest!, out var manifest))
                    manifests[mod.Manifest!] = manifest = File.Exists(mod.Manifest) ? File.ReadAllText(mod.Manifest) : "";
                var installed = Math.Max(InstalledTime(manifest, mod.WorkshopId!), revisions.GetValueOrDefault(mod.WorkshopId!));
                if (!remote.TryGetValue(mod.WorkshopId!, out var latest) || installed == 0) { unknown++; continue; }
                if (latest > installed) updates.Add(new(mod, installed, latest));
            }
            completed += batch.Length;
            progress.Report(new(completed, items.Count));
        }
        return (updates, unknown);
    }

    private static long InstalledTime(string manifest, string id)
    {
        var start = manifest.IndexOf("\"WorkshopItemsInstalled\"", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return 0;
        var text = manifest[start..];
        var end = text.IndexOf("\"WorkshopItemDetails\"", StringComparison.OrdinalIgnoreCase);
        if (end >= 0) text = text[..end];
        var match = Regex.Match(text, $"\"{id}\"\\s*\\{{[^}}]*?\"timeupdated\"\\s+\"(\\d+)\"", RegexOptions.IgnoreCase);
        return long.TryParse(match.Groups[1].Value, out var timestamp) ? timestamp : 0;
    }

    internal static async Task Download(WorkshopUpdate update, string steamCmd, string gameVersion)
    {
        if (ModLibrary.GameRunning()) throw new InvalidOperationException("请退出 RimWorld 后再更新。");
        var id = update.Mod.WorkshopId!;
        var working = Path.GetDirectoryName(Path.GetFullPath(steamCmd))!;
        var destination = update.Mod.Path;
        var downloaded = Path.Combine(working, "steamapps", "workshop", "content", ModLibrary.AppId, id);
        if (ModLibrary.Ids.Equals(Path.GetFullPath(downloaded), Path.GetFullPath(destination)))
            throw new IOException("请选择独立于 Steam 游戏库的 SteamCMD 安装。");
        var installedManifest = update.Mod.Manifest!;
        var originalRevision = File.Exists(installedManifest) ? InstalledTime(File.ReadAllText(installedManifest), id) : 0;
        if (originalRevision > update.Installed) throw new IOException("Steam 已改动此条目，请重新检查更新。");
        var start = new ProcessStartInfo(steamCmd) { WorkingDirectory = working, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "+login", "anonymous", "+workshop_download_item", ModLibrary.AppId, id, "validate", "+quit" })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("无法启动 SteamCMD。");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new IOException("SteamCMD 下载超时。");
        }
        var log = (await output) + "\n" + (await error);
        if (process.ExitCode != 0 || !log.Contains($"Success. Downloaded item {id}", StringComparison.OrdinalIgnoreCase))
            throw new IOException($"SteamCMD 未能下载 {id}。{string.Join(" ", log.Split('\n').Where(l => l.Contains("ERROR", StringComparison.OrdinalIgnoreCase)).TakeLast(2)).Trim()}");
        var source = ModLibrary.ReadMod(downloaded, "Steam 创意工坊", update.Mod.Manifest, gameVersion);
        if (source is null || !ModLibrary.Ids.Equals(source.Id, update.Mod.Id))
            throw new InvalidDataException("下载包身份与本地模组不一致。");
        var backupRoot = Path.Combine(Directory.GetParent(destination)!.Parent!.FullName, "RimManagerBackups", id);
        Directory.CreateDirectory(backupRoot);
        var token = Guid.NewGuid().ToString("N");
        var staged = Path.Combine(backupRoot, token + ".stage");
        var backup = Path.Combine(backupRoot, token);
        try
        {
            CopyTree(downloaded, staged);
            if (ModLibrary.GameRunning()) throw new InvalidOperationException("RimWorld 已启动，请退出后重试。");
            var currentRevision = File.Exists(installedManifest) ? InstalledTime(File.ReadAllText(installedManifest), id) : 0;
            if (currentRevision != originalRevision)
                throw new IOException("下载期间 Steam 改动了此条目，请重新检查更新。");
            Directory.Move(destination, backup);
            try { Directory.Move(staged, destination); }
            catch { Directory.Move(backup, destination); throw; }
        }
        finally { if (Directory.Exists(staged)) Directory.Delete(staged, true); }
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var entry in new DirectoryInfo(from).EnumerateFileSystemInfos())
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("下载包包含重解析点。");
            var target = Path.Combine(to, entry.Name);
            if (entry is DirectoryInfo) CopyTree(entry.FullName, target);
            else File.Copy(entry.FullName, target);
        }
    }
}

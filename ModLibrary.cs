using System.IO;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;

namespace RimManager;

internal sealed record Mod(string Id, string Name, string Path, string Source, string Description,
    string Version, string Kind, string? WorkshopId, string? Manifest, string[] Supported,
    string[][] Dependencies, string[] Before, string[] After, string[] ForceBefore,
    string[] ForceAfter, string[] Conflicts);

internal sealed class ModLibrary(string game, string steam, string config)
{
    internal const string AppId = "294100";
    internal static readonly StringComparer Ids = StringComparer.OrdinalIgnoreCase;
    internal static string DefaultConfig => Path.GetFullPath(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        @"..\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Config\ModsConfig.xml"));
    internal string Game { get; } = game;
    internal string Steam { get; } = steam;
    internal string Config { get; } = config;
    internal string Version { get; } = GameVersion(game);
    internal List<string> Messages { get; } = [];
    private byte[]? lastConfig;

    internal static string FindSteam() =>
        (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamExe", null) as string)?.Replace('/', '\\')
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steam.exe");

    internal static IEnumerable<string> Libraries(string steam)
    {
        var root = Path.GetDirectoryName(steam);
        if (string.IsNullOrEmpty(root)) yield break;
        yield return root;
        var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;
        foreach (Match match in Regex.Matches(File.ReadAllText(vdf), """ "path"\s+"([^"]+)" """.Trim()))
            yield return match.Groups[1].Value.Replace(@"\\", @"\");
    }

    internal static string? FindGame(string steam)
    {
        foreach (var root in Libraries(steam))
        {
            var apps = Path.Combine(root, "steamapps");
            var manifest = Path.Combine(apps, $"appmanifest_{AppId}.acf");
            if (!File.Exists(manifest)) continue;
            var match = Regex.Match(File.ReadAllText(manifest), """ "installdir"\s+"([^"]+)" """.Trim());
            if (!match.Success) continue;
            var path = Path.Combine(apps, "common", match.Groups[1].Value);
            if (IsGame(path)) return path;
        }
        return null;
    }

    internal static bool IsGame(string path) => File.Exists(Path.Combine(path, "RimWorldWin64.exe"));
    internal static bool GameRunning() => Process.GetProcessesByName("RimWorldWin64").Select(p => { p.Dispose(); return true; }).Any();
    private static string GameVersion(string path)
    {
        var file = Path.Combine(path, "Version.txt");
        return File.Exists(file) ? Regex.Match(File.ReadAllText(file), @"\d+\.\d+").Value : "";
    }

    internal List<Mod> Scan()
    {
        Messages.Clear();
        var mods = new List<Mod>();
        var seen = new HashSet<string>(Ids);
        void ReadFolder(string folder, string source, string? manifest = null)
        {
            if (!Directory.Exists(folder)) return;
            foreach (var path in Directory.EnumerateDirectories(folder))
            {
                try
                {
                    var mod = ReadMod(path, source, manifest, Version);
                    if (mod is null) continue;
                    if (seen.Add(mod.Id)) mods.Add(mod);
                    else Messages.Add($"重复包标识 {mod.Id}，跳过 {path}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
                {
                    Messages.Add($"{path} · {ex.Message}");
                }
            }
        }
        ReadFolder(Path.Combine(Game, "Data"), "官方");
        ReadFolder(Path.Combine(Game, "Mods"), "本地");
        var libraries = Libraries(Steam).ToList();
        var gameLibrary = Directory.GetParent(Game)?.Parent?.Parent?.FullName;
        if (gameLibrary is not null) libraries.Add(gameLibrary);
        foreach (var root in libraries.Distinct(Ids))
        {
            var workshop = Path.Combine(root, "steamapps", "workshop");
            ReadFolder(Path.Combine(workshop, "content", AppId), "Steam 创意工坊", Path.Combine(workshop, $"appworkshop_{AppId}.acf"));
        }
        return mods;
    }

    internal static Mod? ReadMod(string path, string source, string? manifest, string gameVersion)
    {
        var about = Path.Combine(path, "About", "About.xml");
        if (!File.Exists(about)) return null;
        var xml = XDocument.Load(about).Root!;
        string Get(string name) => xml.Element(name)?.Value.Trim() ?? "";
        IEnumerable<XElement> Entries(string name) =>
            (xml.Element(name + "ByVersion")?.Element("v" + gameVersion) ?? xml.Element(name))?.Elements() ?? [];
        static string[] Values(IEnumerable<XElement> entries) => entries.Select(e => e.Value.Trim()).Where(v => v.Length > 0).ToArray();
        var id = Get("packageId");
        if (id.Length == 0) return null;
        var name = Get("name");
        if (name.Length == 0) name = Path.GetFileName(path);
        var workshopId = manifest is null ? null : Path.GetFileName(path);
        var published = Path.Combine(path, "About", "PublishedFileId.txt");
        if (workshopId is null && source == "本地" && File.Exists(published)) workshopId = File.ReadAllText(published).Trim();
        if (!ulong.TryParse(workshopId, out var numericId) || numericId == 0) workshopId = null;
        var kind = source == "官方" ? (Ids.Equals(id, "ludeon.rimworld") ? "本体" : "DLC")
            : Regex.IsMatch(name, "汉化|中文|简中|繁中|translation", RegexOptions.IgnoreCase) ? "汉化"
            : Regex.IsMatch(name, "compatibility|兼容补丁", RegexOptions.IgnoreCase) ? "兼容补丁"
            : Regex.IsMatch(name, "library|framework|harmony|框架", RegexOptions.IgnoreCase) ? "框架"
            : Directory.Exists(Path.Combine(path, "Defs")) ? "内容" : "未分类";
        return new(id, name, path, source, Get("description"), Get("modVersion"), kind, workshopId, manifest,
            Values(Entries("supportedVersions")), Entries("modDependencies").Select(e =>
                Values(e.Elements("packageId").Concat(e.Element("alternativePackageIds")?.Elements() ?? [])))
                .Where(items => items.Length > 0).ToArray(),
            Values(Entries("loadBefore")), Values(Entries("loadAfter")), Values(Entries("forceLoadBefore")),
            Values(Entries("forceLoadAfter")), Values(Entries("incompatibleWith")));
    }

    internal List<string> ReadOrder()
    {
        lastConfig = File.Exists(Config) ? File.ReadAllBytes(Config) : null;
        if (lastConfig is null) return ["ludeon.rimworld"];
        using var stream = new MemoryStream(lastConfig);
        return ParseOrder(XDocument.Load(stream));
    }

    internal List<string> ReadBackup() => ParseOrder(XDocument.Load(Config + ".rimmanager.bak"));

    private static List<string> ParseOrder(XDocument document) =>
        document.Root?.Element("activeMods")?.Elements("li").Select(e => e.Value.Trim())
            .Where(id => id.Length > 0).Distinct(Ids).ToList()
            ?? throw new InvalidDataException("游戏配置缺少 activeMods。");

    internal void SaveOrder(IEnumerable<string> order)
    {
        if (GameRunning()) throw new InvalidOperationException("请退出 RimWorld 后再应用配置。");
        var current = File.Exists(Config) ? File.ReadAllBytes(Config) : null;
        if (current is null ? lastConfig is not null : lastConfig is null || !current.SequenceEqual(lastConfig))
            throw new IOException("游戏配置已在外部修改。请重新扫描后再应用。");
        var document = current is null
            ? new XDocument(new XElement("ModsConfigData"))
            : XDocument.Load(new MemoryStream(current));
        var root = document.Root!;
        var active = new XElement("activeMods", order.Select(id => new XElement("li", id)));
        if (root.Element("activeMods") is XElement previous) previous.ReplaceWith(active);
        else root.Add(active);
        Directory.CreateDirectory(Path.GetDirectoryName(Config)!);
        var temporary = Config + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            document.Save(temporary);
            if (current is null) File.Move(temporary, Config);
            else File.Replace(temporary, Config, Config + ".rimmanager.bak");
            lastConfig = File.ReadAllBytes(Config);
        }
        finally { File.Delete(temporary); }
    }
}

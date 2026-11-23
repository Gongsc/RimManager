using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RimManager;

public partial class MainWindow : Window
{
    private static readonly StringComparer Ids = ModLibrary.Ids;
    private static readonly string SettingsFile = Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "RimManager", "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private Settings settings = new();
    private ModLibrary? library;
    private List<Mod> mods = [];
    private List<string> order = [];
    private ModOrdering ordering = new([], "");
    private bool dirty, rendering;
    private ListBoxItem? Selection => (Active.SelectedItem ?? Inactive.SelectedItem) as ListBoxItem;
    private Mod? SelectedMod => Selection?.Tag is string id ? ordering.Find(id) : null;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Guard(() =>
        {
            if (File.Exists(SettingsFile)) settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsFile), Json) ?? new();
            Arguments.Text = settings.Arguments;
            Reload();
        });
        Active.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => QueueWires()));
        Active.SizeChanged += (_, _) => QueueWires();
        Closing += (_, e) =>
        {
            if (!Shell.IsEnabled || !CanDiscard()) e.Cancel = true;
            else Guard(SaveSettings);
        };
    }

    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "RimManager", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private bool CanDiscard() => !dirty || MessageBox.Show(this, "放弃尚未应用的加载顺序？", "RimManager",
        MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private void SaveSettings()
    {
        settings.Arguments = Arguments.Text;
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        var temp = SettingsFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json));
            File.Move(temp, SettingsFile, overwrite: true);
        }
        finally { File.Delete(temp); }
    }

    private void Reload()
    {
        var steam = string.IsNullOrWhiteSpace(settings.Steam) ? ModLibrary.FindSteam() : settings.Steam;
        var game = string.IsNullOrWhiteSpace(settings.Game) ? ModLibrary.FindGame(steam) : settings.Game;
        if (game is null || !ModLibrary.IsGame(game))
        {
            GameLabel.Text = "尚未检测到 RimWorld";
            Status.Text = "请在设置中选择 RimWorld 安装目录。";
            return;
        }
        var nextLibrary = new ModLibrary(game, steam,
            string.IsNullOrWhiteSpace(settings.Config) ? ModLibrary.DefaultConfig : settings.Config);
        var nextMods = nextLibrary.Scan();
        var nextOrder = nextLibrary.ReadOrder();
        library = nextLibrary;
        mods = ApplyKinds(nextMods);
        ordering = new(mods, library.Version);
        order = nextOrder;
        dirty = false;
        GameLabel.Text = library.Version.Length == 0 ? "RimWorld" : $"RimWorld {library.Version}";
        UpdateKinds();
        Refresh(order.FirstOrDefault());
        if (library.Messages.Count > 0)
            MessageBox.Show(this, string.Join("\n", library.Messages.Take(20)), "扫描提示");
    }

    private void UpdateKinds()
    {
        var previousKind = Kinds.SelectedItem as string;
        rendering = true;
        Kinds.ItemsSource = new[] { "全部类型" }.Concat(mods.Select(m => m.Kind).Distinct().Order()).ToList();
        Kinds.SelectedItem = previousKind;
        if (Kinds.SelectedIndex < 0) Kinds.SelectedIndex = 0;
        rendering = false;
    }

    private void Refresh(string? selected = null)
    {
        selected ??= Selection?.Tag as string;
        rendering = true;
        Active.Items.Clear();
        Inactive.Items.Clear();
        var active = order.Select(ordering.Find).OfType<Mod>().ToHashSet();
        var inactive = mods.Where(m => !active.Contains(m)).ToList();
        var activeIssues = ordering.AllIssues(order);
        for (var i = 0; i < order.Count; i++)
        {
            var id = order[i];
            activeIssues.TryGetValue(id, out var issues);
            Active.Items.Add(Row(id, ordering.Find(id), i + 1, issues));
        }
        foreach (var mod in inactive.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (Kinds.SelectedItem is string kind && kind != "全部类型" && kind != mod.Kind) continue;
            var search = Search.Text.Trim();
            if (!mod.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) && !mod.Id.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
            Inactive.Items.Add(Row(mod.Id, mod, null, null));
        }
        var item = Active.Items.Cast<ListBoxItem>().Concat(Inactive.Items.Cast<ListBoxItem>())
            .FirstOrDefault(row => Ids.Equals((string)row.Tag, selected));
        if (item is not null) item.IsSelected = true;
        rendering = false;
        ActiveTitle.Text = $"已启用模组 · {order.Count}";
        InactiveTitle.Text = $"未启用模组 · {inactive.Count}";
        LaunchButton.Content = dirty ? "应用并启动" : "启动游戏";
        Status.Text = dirty ? "加载顺序有待应用的更改。" : $"已启用 {order.Count} 项 · 未启用 {inactive.Count} 项";
        ShowDetails();
        QueueWires();
    }

    private ListBoxItem Row(string id, Mod? mod, int? index, List<string>? issues)
    {
        var grid = new Grid { MinHeight = 32 };
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(index.HasValue ? 52 : 0) });
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = index.HasValue ? $"⠿ {index:00}" : "", VerticalAlignment = VerticalAlignment.Center });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var incompatible = mod is not null && ordering.Incompatible(mod);
        var warning = mod is null ? "模组未安装" : issues is { Count: > 0 } ? issues[0] : null;
        text.Children.Add(new TextBlock { Text = mod?.Name ?? id, TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = warning is not null ? Brushes.Firebrick : incompatible ? Brushes.DarkGoldenrod : Foreground });
        if (warning is not null || incompatible)
            text.Children.Add(new TextBlock { Text = string.Join(" · ", new[] { warning, incompatible ? "版本不兼容" : null }.OfType<string>()),
                Foreground = incompatible ? Brushes.DarkGoldenrod : Brushes.Firebrick, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
        var kindLabel = new TextBlock { Text = mod?.Kind ?? "", Margin = new Thickness(7, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(kindLabel, 2);
        grid.Children.Add(kindLabel);
        return new() { Tag = id, Content = grid, ToolTip = string.Join("\n", new[] { mod?.Name ?? id, warning, incompatible ? "版本不兼容" : null }.OfType<string>()) };
    }

    private void ShowSelection(object sender, SelectionChangedEventArgs e)
    {
        if (rendering || e.AddedItems.Count == 0) return;
        (sender == Active ? Inactive : Active).UnselectAll();
        ShowDetails();
        QueueWires();
    }

    private void ShowDetails()
    {
        var mod = SelectedMod;
        ModName.Text = mod?.Name ?? (Selection?.Tag as string ?? "请选择模组");
        ModMeta.Text = mod is null ? "" : $"{mod.Id}\n{mod.Source} · {mod.Kind}" +
            (mod.Version.Length == 0 ? "" : $" · v{mod.Version}");
        var description = mod is null ? "" : mod.Description.Length > 0 ? mod.Description : "作者未提供简介。";
        ModSummary.Text = description.Length <= 220 ? description : description[..220] + "…";
        FullSummary.Text = description;
        More.Visibility = description.Length > 220 ? Visibility.Visible : Visibility.Collapsed;
        More.IsExpanded = false;
        WorkshopButton.Visibility = mod?.WorkshopId is null ? Visibility.Collapsed : Visibility.Visible;
        PinButton.Visibility = mod is null ? Visibility.Collapsed : Visibility.Visible;
        KindButton.Visibility = PinButton.Visibility;
        PinButton.Content = mod is not null && Locked(mod) ? "🔒 解除锁定" : "锁定版本";
        PinButton.ToolTip = mod is not null && settings.Locks.TryGetValue(mod.Id.ToLowerInvariant(), out var pinnedVersion) && pinnedVersion.Length > 0
            ? $"锁定时版本 v{pinnedVersion}" : null;
        if (mod is null) { Relations.Text = ""; return; }
        string Names(IEnumerable<string> ids) => string.Join("、", ids.Select(id => ordering.Find(id)?.Name ?? id));
        var issues = ordering.AllIssues(order);
        var relations = new List<string>(issues.GetValueOrDefault((string)Selection!.Tag) ?? []);
        if (ordering.Incompatible(mod)) relations.Add($"版本不兼容 · 支持 {string.Join("、", mod.Supported)}");
        if (mod.Dependencies.Length > 0) relations.Add("必需前置 · " + string.Join("、", mod.Dependencies.Select(Names)));
        var dependents = mods.Where(m => m.Dependencies.Any(d => d.Contains(mod.Id, Ids))).Select(m => m.Id).ToList();
        if (dependents.Count > 0) relations.Add("依赖此模组 · " + Names(dependents));
        if (mod.Before.Length + mod.ForceBefore.Length > 0) relations.Add("先于 · " + Names(mod.Before.Concat(mod.ForceBefore).Distinct(Ids)));
        if (mod.After.Length + mod.ForceAfter.Length > 0) relations.Add("晚于 · " + Names(mod.After.Concat(mod.ForceAfter).Distinct(Ids)));
        if (mod.Conflicts.Length > 0) relations.Add("不兼容 · " + Names(mod.Conflicts));
        Relations.Text = string.Join("\n", relations);
    }

    private List<Mod> ApplyKinds(IEnumerable<Mod> source) => source.Select(mod =>
        settings.Kinds.TryGetValue(mod.Id.ToLowerInvariant(), out var kind) ? mod with { Kind = kind } : mod).ToList();

    private void ChangeKind(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (SelectedMod is not Mod mod || library is null) return;
        var dialog = Dialog("修改模组类型", 320);
        var panel = DialogPanel(dialog, out var actions);
        string[] kinds = ["本体", "DLC", "框架", "内容", "功能", "汉化", "兼容补丁", "未分类"];
        var choice = new System.Windows.Controls.ComboBox { ItemsSource = kinds, SelectedItem = mod.Kind };
        panel.Children.Add(choice);
        AddAction(actions, "取消", () => dialog.DialogResult = false).IsCancel = true;
        AddAction(actions, "保存", () => dialog.DialogResult = true).IsDefault = true;
        if (dialog.ShowDialog() != true || choice.SelectedItem is not string kind) return;
        settings.Kinds[mod.Id.ToLowerInvariant()] = kind;
        SaveSettings();
        mods = ApplyKinds(mods);
        ordering = new(mods, library.Version);
        UpdateKinds();
        Refresh();
    });

    private bool Locked(Mod mod) => settings.Locks.ContainsKey(mod.Id.ToLowerInvariant());
    private void ToggleLock(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (SelectedMod is not Mod mod) return;
        var key = mod.Id.ToLowerInvariant();
        if (!settings.Locks.Remove(key)) settings.Locks[key] = mod.Version;
        SaveSettings();
        ShowDetails();
    });

    private void OpenWorkshop(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (SelectedMod?.WorkshopId is string id)
            Process.Start(new ProcessStartInfo($"https://steamcommunity.com/sharedfiles/filedetails/?id={id}") { UseShellExecute = true })?.Dispose();
    });

    private void FilterInactive(object sender, RoutedEventArgs e)
    {
        if (IsLoaded && library is not null && !rendering) Refresh();
    }
    private void EnableMod(object sender, RoutedEventArgs e)
    {
        if (Inactive.SelectedItem is not ListBoxItem row) return;
        var id = (string)row.Tag;
        order.Add(id);
        MarkChanged(id);
    }
    private void DisableMod(object sender, RoutedEventArgs e)
    {
        if (Active.SelectedItem is not ListBoxItem row) return;
        var id = (string)row.Tag;
        if (Ids.Equals(ordering.Find(id)?.Id, "ludeon.rimworld")) { Status.Text = "Core 是游戏本体。"; return; }
        var affected = ordering.Connections(order).Where(x => Ids.Equals(x.From.Id, ordering.Find(id)?.Id))
            .Select(x => x.To.Name).Distinct().ToList();
        if (affected.Count > 0 && MessageBox.Show(this, "以下已启用模组依赖此项。\n" + string.Join("、", affected) +
            "\n继续停用？", "停用模组", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        order.Remove(id);
        MarkChanged(id);
    }
    private void MarkChanged(string? id = null) { dirty = true; Refresh(id); }
    private void ScanAgain(object sender, RoutedEventArgs e) => Guard(() => { if (CanDiscard()) Reload(); });
    private void RestoreBackup(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (library is null) return;
        if (!File.Exists(library.Config + ".rimmanager.bak")) { Status.Text = "尚无加载顺序备份。"; return; }
        var backup = library.ReadBackup();
        if (!ConfirmText("恢复备份", $"将载入备份中的 {backup.Count} 项模组。点击「应用配置」后写入游戏设置。",
            "载入备份", true)) return;
        order = backup;
        MarkChanged();
    });
    private void ApplyClick(object sender, RoutedEventArgs e) => Guard(() => { ApplyOrder(); SaveSettings(); });
    private void ApplyOrder()
    {
        if (library is null) throw new InvalidOperationException("请先设置 RimWorld 安装目录。");
        if (!dirty) return;
        library.SaveOrder(order);
        dirty = false;
        Refresh();
    }
    private void Launch(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (library is null || !File.Exists(library.Steam)) throw new IOException("请在设置中选择 Steam.exe。");
        if (ModLibrary.GameRunning()) { Status.Text = "RimWorld 已在运行。"; return; }
        ApplyOrder();
        SaveSettings();
        using var process = Process.Start(new ProcessStartInfo(library.Steam,
            ("-applaunch " + ModLibrary.AppId + " " + Arguments.Text).TrimEnd()) { UseShellExecute = false });
        Status.Text = "已请求 Steam 启动 RimWorld。";
        _ = WatchLaunch();
    });
    private async Task WatchLaunch()
    {
        for (var i = 0; i < 20 && IsLoaded; i++)
        {
            await Task.Delay(1500);
            if (ModLibrary.GameRunning()) { Status.Text = "已检测到 RimWorld 正在运行。"; return; }
        }
        if (IsLoaded && Status.Text == "已请求 Steam 启动 RimWorld。")
            Status.Text = "尚未检测到 RimWorld，请查看 Steam 客户端。";
    }
}

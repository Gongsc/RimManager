using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RimManager;

public partial class MainWindow : Window
{
    private static readonly StringComparer Ids = ModLibrary.Ids;
    private static readonly string SettingsFile = Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "RimManager", "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private Settings settings = new();
    private ModLibrary? library;
    private readonly Dictionary<string, Task<BitmapSource?>> listIcons = new(Ids);
    private List<Mod> mods = [];
    private List<string> order = [];
    private ModOrdering ordering = new([], "");
    private bool dirty, rendering;
    private ListBoxItem? Selection => (Active.SelectedItem ?? Inactive.SelectedItem) as ListBoxItem;
    private Mod? SelectedMod => Selection?.Tag is string id ? ordering.Find(id) : null;

    public MainWindow()
    {
        InitializeComponent();
        Title = $"RimManager v{typeof(MainWindow).Assembly.GetName().Version!.ToString(3)}";
        SourceInitialized += (_, _) => App.ApplyCaptionTheme(this);
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
        Closed += (_, _) => descriptionRequest?.Cancel();
    }

    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "RimManager", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void MinimizeWindow(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void ToggleMaximize(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void CloseWindow(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

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
        listIcons.Clear();
        mods = ApplyKinds(nextMods);
        ordering = new(mods, library.Version);
        order = nextOrder;
        dirty = false;
        GameLabel.Text = library.Version.Length == 0 ? "RimWorld" : $"RimWorld {library.Version}";
        UpdateKinds();
        Refresh(order.Take(1), Active);
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

    private static string[] SelectedIds(ListBox list) => list.Items.Cast<ListBoxItem>()
        .Where(row => row.IsSelected && row.Visibility == Visibility.Visible).Select(row => (string)row.Tag).ToArray();

    private void Refresh(IEnumerable<string>? selected = null, ListBox? destination = null)
    {
        destination ??= Active.SelectedItems.Count > 0 ? Active : Inactive;
        var selectedIds = (selected ?? SelectedIds(destination)).ToHashSet(Ids);
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
            if (!MatchesSearch(mod.Id, mod, search)) continue;
            Inactive.Items.Add(Row(mod.Id, mod, null, null));
        }
        ApplyActiveSearch();
        foreach (ListBoxItem row in destination.Items)
            if (row.Visibility == Visibility.Visible && selectedIds.Contains((string)row.Tag)) row.IsSelected = true;
        rendering = false;
        InactiveTitle.Text = $"未启用模组 · {inactive.Count}";
        LaunchButton.Content = dirty ? "应用并启动" : "启动游戏";
        Status.Text = dirty ? "加载顺序有待应用的更改。" : $"已启用 {order.Count} 项 · 未启用 {inactive.Count} 项";
        ShowDetails();
        QueueWires();
    }

    private void ClearSearch(object sender, RoutedEventArgs e)
    {
        var search = (TextBox)((Button)sender).DataContext;
        search.Clear();
        search.Focus();
    }

    private static string DisplayKind(string kind) => kind == "未分类" ? "-" : kind;

    private ListBoxItem Row(string id, Mod? mod, int? index, List<string>? issues)
    {
        var grid = new Grid { MinHeight = 32 };
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(index.HasValue ? 52 : 0) });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var number = new TextBlock { Text = index.HasValue ? $"⠿ {index:00}" : "", VerticalAlignment = VerticalAlignment.Center };
        number.SetResourceReference(StyleProperty, "Secondary");
        grid.Children.Add(number);
        if (mod is not null)
        {
            var icon = new Image { Width = 24, Height = 24, Margin = new Thickness(0, 0, 6, 0), Stretch = Stretch.Uniform };
            icon.Loaded += async (_, _) =>
            {
                if (!listIcons.TryGetValue(mod.Path, out var pending))
                    listIcons[mod.Path] = pending = ModImages.LoadIcon(mod);
                icon.Source = await pending;
                if (icon.Source is null) icon.Visibility = Visibility.Collapsed;
            };
            Grid.SetColumn(icon, 1);
            grid.Children.Add(icon);
        }
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);
        var incompatible = mod is not null && ordering.Incompatible(mod);
        var warning = mod is null ? "模组未安装" : issues is { Count: > 0 } ? issues[0] : null;
        var label = new TextBlock { Text = mod?.Name ?? id, TextTrimming = TextTrimming.CharacterEllipsis };
        label.SetResourceReference(TextBlock.ForegroundProperty, warning is not null ? "ErrorText" : incompatible ? "WarningText" : "Text");
        text.Children.Add(label);
        if (warning is not null || incompatible)
        {
            var message = new TextBlock { Text = string.Join(" · ", new[] { warning, incompatible ? "版本不兼容" : null }.OfType<string>()),
                FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
            message.SetResourceReference(TextBlock.ForegroundProperty, incompatible ? "WarningText" : "ErrorText");
            text.Children.Add(message);
        }
        var kindLabel = new TextBlock { Text = mod is null ? "" : DisplayKind(mod.Kind), Margin = new Thickness(7, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
        kindLabel.SetResourceReference(StyleProperty, "Secondary");
        Grid.SetColumn(kindLabel, 3);
        grid.Children.Add(kindLabel);
        return new() { Tag = id, Content = grid, ToolTip = string.Join("\n", new[] { mod?.Name ?? id, warning, incompatible ? "版本不兼容" : null }.OfType<string>()) };
    }

    private void ShowSelection(object sender, SelectionChangedEventArgs e)
    {
        if (rendering) return;
        if (e.AddedItems.Count > 0) (sender == Active ? Inactive : Active).UnselectAll();
        ShowDetails();
        QueueWires();
    }

    private void ShowDetails()
    {
        var mod = SelectedMod;
        CloseWorkshopMenu(restoreFocus: false);
        ModName.Text = mod?.Name ?? (Selection?.Tag as string ?? "请选择模组");
        ModTile.Visibility = mod is null ? Visibility.Collapsed : Visibility.Visible;
        ModMeta.Text = mod is null ? "" : $"{mod.Id}\n{mod.Source} · {DisplayKind(mod.Kind)}" +
            (mod.Version.Length == 0 ? "" : $" · v{mod.Version}");
        ShowModPresentation(mod);
        WorkshopButton.Visibility = mod?.WorkshopId is null ? Visibility.Collapsed : Visibility.Visible;
        PinButton.Visibility = mod is null ? Visibility.Collapsed : Visibility.Visible;
        KindButton.Visibility = PinButton.Visibility;
        var locked = mod is not null && Locked(mod);
        var pinAction = locked ? "解除锁定" : "锁定版本";
        PinButton.Content = locked ? "\uE72E" : "\uE785";
        PinButton.SetResourceReference(BackgroundProperty, locked ? "SelectedSurface" : "ControlSurface");
        PinButton.SetResourceReference(ForegroundProperty, locked ? "Accent" : "Text");
        AutomationProperties.SetName(PinButton, pinAction);
        PinButton.ToolTip = locked && settings.Locks.TryGetValue(mod!.Id.ToLowerInvariant(), out var pinnedVersion) && pinnedVersion.Length > 0
            ? $"{pinAction} · 锁定时版本 v{pinnedVersion}" : pinAction;
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
        var choice = new System.Windows.Controls.ComboBox { ItemsSource = kinds, SelectedItem = mod.Kind,
            ItemTemplate = (DataTemplate)FindResource("ModKindLabel") };
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

    private void ShowWorkshopMenu(object sender, RoutedEventArgs e)
    {
        WorkshopOverlay.Visibility = Visibility.Visible;
        WorkshopOverlay.UpdateLayout();
        PositionWorkshopMenu();
        WorkshopBrowserOption.Focus();
    }

    private void PositionWorkshopMenu()
    {
        if (WorkshopOverlay.Visibility != Visibility.Visible || !WorkshopButton.IsVisible) return;
        const double inset = 6;
        const double gap = 4;
        var anchor = WorkshopButton.TranslatePoint(new Point(), WorkshopOverlay);
        var menu = WorkshopFlyout.DesiredSize;
        var x = anchor.X + WorkshopButton.ActualWidth - menu.Width;
        var y = anchor.Y + WorkshopButton.ActualHeight + gap;
        var maxY = Math.Max(inset, WorkshopOverlay.ActualHeight - menu.Height - inset);
        if (y > maxY) y = anchor.Y - menu.Height - gap;
        Canvas.SetLeft(WorkshopFlyout, Math.Clamp(x, inset, Math.Max(inset, WorkshopOverlay.ActualWidth - menu.Width - inset)));
        Canvas.SetTop(WorkshopFlyout, Math.Clamp(y, inset, maxY));
    }

    private void WorkshopOverlaySizeChanged(object sender, SizeChangedEventArgs e) => PositionWorkshopMenu();

    private void WorkshopOverlayMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == WorkshopOverlay) CloseWorkshopMenu();
    }

    private void WorkshopOverlayKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        CloseWorkshopMenu();
        e.Handled = true;
    }

    private void CloseWorkshopMenu(bool restoreFocus = true)
    {
        if (WorkshopOverlay.Visibility != Visibility.Visible) return;
        WorkshopOverlay.Visibility = Visibility.Collapsed;
        if (restoreFocus) WorkshopButton.Focus();
    }

    private void OpenWorkshopInBrowser(object sender, RoutedEventArgs e) => OpenWorkshop(inSteam: false);

    private void OpenWorkshopInSteam(object sender, RoutedEventArgs e) => OpenWorkshop(inSteam: true);

    private void OpenWorkshop(bool inSteam) => Guard(() =>
    {
        CloseWorkshopMenu();
        if (SelectedMod?.WorkshopId is not string id) return;
        var url = inSteam ? $"steam://url/CommunityFilePage/{id}" : $"https://steamcommunity.com/sharedfiles/filedetails/?id={id}";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
    });

    private void FilterInactive(object sender, RoutedEventArgs e)
    {
        if (IsLoaded && library is not null && !rendering) Refresh();
    }

    private static bool MatchesSearch(string id, Mod? mod, string query) =>
        id.Contains(query, StringComparison.OrdinalIgnoreCase)
        || mod is not null && (mod.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
            || mod.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase));

    private void ApplyActiveSearch()
    {
        var query = ActiveSearch.Text.Trim();
        var matched = 0;
        foreach (ListBoxItem row in Active.Items)
        {
            var id = (string)row.Tag;
            var visible = MatchesSearch(id, ordering.Find(id), query);
            row.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (visible) matched++;
        }
        ActiveTitle.Text = query.Length == 0 ? $"已启用模组 · {order.Count}" : $"已启用模组 · {matched}/{order.Count}";
    }

    private void FilterActive(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || library is null || rendering) return;
        ApplyActiveSearch();
        foreach (var row in Active.SelectedItems.Cast<ListBoxItem>().Where(row => row.Visibility == Visibility.Collapsed).ToArray())
            row.IsSelected = false;
        QueueWires();
    }
    private void EnableMod(object sender, RoutedEventArgs e) => PlaceInOrder(SelectedIds(Inactive), order.Count);
    private void DisableMod(object sender, RoutedEventArgs e) => DisableMods(SelectedIds(Active));

    private void PlaceInOrder(string[] ids, int insertion)
    {
        var moving = ids.ToHashSet(Ids);
        var next = order.Where(id => !moving.Contains(id)).ToList();
        next.InsertRange(order.Take(insertion).Count(id => !moving.Contains(id)), ids);
        if (order.SequenceEqual(next, Ids)) return;
        order = next;
        MarkChanged(ids, Active);
    }

    private void DisableMods(string[] ids)
    {
        var removing = ids.ToHashSet(Ids);
        var skippedCore = removing.RemoveWhere(id => Ids.Equals(ordering.Find(id)?.Id ?? id, "ludeon.rimworld")) > 0;
        if (removing.Count == 0)
        {
            if (skippedCore) Status.Text = "Core 是游戏本体。";
            return;
        }
        var remaining = order.Where(id => !removing.Contains(id)).ToList();
        var activeIds = order.Select(ordering.Find).OfType<Mod>().Select(m => m.Id).ToHashSet(Ids);
        var remainingMods = remaining.Select(ordering.Find).OfType<Mod>().ToList();
        var remainingIds = remainingMods.Select(m => m.Id).ToHashSet(Ids);
        var affected = remainingMods.Where(m => m.Dependencies.Any(options =>
            options.Any(activeIds.Contains) && !options.Any(remainingIds.Contains))).Select(m => m.Name).Distinct().ToList();
        if (affected.Count > 0 && MessageBox.Show(this, "停用后，以下已启用模组将缺少前置。\n" + string.Join("、", affected) +
            "\n继续停用？", "停用模组", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        order = remaining;
        MarkChanged(removing.Select(id => ordering.Find(id)?.Id ?? id), Inactive);
        if (skippedCore) Status.Text = "已停用所选模组，Core 保持启用。";
    }
    private void MarkChanged(IEnumerable<string>? ids = null, ListBox? destination = null) { dirty = true; Refresh(ids, destination); }
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

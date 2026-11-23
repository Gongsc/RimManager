using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace RimManager;

public partial class MainWindow
{
    private sealed class Settings
    {
        public string Game { get; set; } = "";
        public string Steam { get; set; } = "";
        public string Config { get; set; } = "";
        public string SteamCmd { get; set; } = "";
        public string Arguments { get; set; } = "";
        public Dictionary<string, string> Locks { get; set; } = new();
        public Dictionary<string, long> Revisions { get; set; } = new();
        public Dictionary<string, string> Kinds { get; set; } = new();
    }

    private Window Dialog(string title, double width = 640) => new()
    {
        Title = title, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Width = width, SizeToContent = SizeToContent.Height, MaxHeight = 560, FontFamily = FontFamily,
        Background = Background, Foreground = Foreground
    };

    private static DockPanel DialogPanel(Window dialog, out WrapPanel actions)
    {
        var panel = new DockPanel { Margin = new Thickness(16) };
        actions = new WrapPanel { Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(actions, Dock.Bottom);
        panel.Children.Add(actions);
        dialog.Content = panel;
        return panel;
    }

    private static Button AddAction(Panel actions, string label, Action run)
    {
        var button = new Button { Content = label, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(6, 0, 0, 0) };
        button.Click += (_, _) => run();
        actions.Children.Add(button);
        return button;
    }

    private bool ConfirmText(string title, string text, string action, bool allowed)
    {
        var dialog = Dialog(title);
        var panel = DialogPanel(dialog, out var buttons);
        AddAction(buttons, "取消", () => dialog.DialogResult = false).IsCancel = true;
        AddAction(buttons, action, () => dialog.DialogResult = true).IsEnabled = allowed;
        panel.Children.Add(new ScrollViewer { MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap } });
        return dialog.ShowDialog() == true;
    }

    private void Configure(object sender, RoutedEventArgs e) => Guard(() =>
    {
        var dialog = Dialog("路径设置");
        var panel = DialogPanel(dialog, out var buttons);
        var form = new StackPanel();
        panel.Children.Add(form);
        TextBox AddField(string title, string current, string filter = "")
        {
            form.Children.Add(new TextBlock { Text = title, Margin = new Thickness(0, 7, 0, 3) });
            var line = new DockPanel();
            var input = new TextBox { Text = current, Padding = new Thickness(4) };
            var browse = new Button { Content = "浏览", Margin = new Thickness(6, 0, 0, 0) };
            DockPanel.SetDock(browse, Dock.Right);
            line.Children.Add(browse);
            line.Children.Add(input);
            form.Children.Add(line);
            browse.Click += (_, _) =>
            {
                if (filter.Length == 0)
                {
                    var picker = new OpenFolderDialog();
                    if (picker.ShowDialog(dialog) == true) input.Text = picker.FolderName;
                }
                else
                {
                    var picker = new OpenFileDialog { Filter = filter };
                    if (picker.ShowDialog(dialog) == true) input.Text = picker.FileName;
                }
            };
            return input;
        }
        var game = AddField("RimWorld 安装目录（留空自动检测）", settings.Game);
        var steam = AddField("Steam.exe（留空自动检测）", settings.Steam, "Steam.exe|steam.exe");
        var config = AddField("ModsConfig.xml（留空使用默认位置）", settings.Config, "XML 文件|*.xml");
        var cmd = AddField("SteamCMD.exe（用于下载工坊更新）", settings.SteamCmd, "SteamCMD.exe|steamcmd.exe");
        AddAction(buttons, "取消", () => dialog.DialogResult = false).IsCancel = true;
        AddAction(buttons, "保存", () =>
        {
            if (game.Text.Trim().Length > 0 && !ModLibrary.IsGame(game.Text.Trim()))
            {
                MessageBox.Show(dialog, "所选目录中找不到 RimWorldWin64.exe。");
                return;
            }
            if (!CanDiscard()) return;
            settings.Game = game.Text.Trim();
            settings.Steam = steam.Text.Trim();
            settings.Config = config.Text.Trim();
            settings.SteamCmd = cmd.Text.Trim();
            dialog.DialogResult = true;
        }).IsDefault = true;
        if (dialog.ShowDialog() != true) return;
        SaveSettings();
        Reload();
    });

    private List<WorkshopUpdate> ChooseUpdates(List<WorkshopUpdate> updates, int unknown)
    {
        var chosen = new List<WorkshopUpdate>();
        var dialog = Dialog("可更新模组", 690);
        var panel = DialogPanel(dialog, out var buttons);
        var rows = new StackPanel();
        var locked = updates.Count(u => Locked(u.Mod));
        rows.Children.Add(new TextBlock { Text = $"查到 {updates.Count} 项新版 · 已锁定 {locked} 项 · 状态未知 {unknown} 项",
            Margin = new Thickness(0, 0, 0, 12) });
        if (updates.Count == 0) rows.Children.Add(new TextBlock { Text = "未查到可更新的工坊模组。" });
        var boxes = new List<(WorkshopUpdate Update, CheckBox Box)>();
        AddAction(buttons, "关闭", () => dialog.DialogResult = false).IsCancel = true;
        void Complete(IEnumerable<WorkshopUpdate> selection)
        {
            chosen = selection.Where(u => !Locked(u.Mod)).ToList();
            dialog.DialogResult = true;
        }
        var selected = AddAction(buttons, "更新所选", () => Complete(boxes.Where(x => x.Box.IsChecked == true).Select(x => x.Update)));
        selected.IsEnabled = false;
        AddAction(buttons, "全部更新", () => Complete(updates)).IsEnabled = updates.Count > locked;
        foreach (var update in updates)
        {
            var mod = update.Mod;
            var pinned = Locked(mod);
            string Date(long time) => DateTimeOffset.FromUnixTimeSeconds(time).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            var box = new CheckBox
            {
                IsEnabled = !pinned,
                Margin = new Thickness(0, 5, 0, 5),
                Content = new TextBlock { TextWrapping = TextWrapping.Wrap,
                    Text = $"{(pinned ? "🔒 已锁定 · " : "")}{mod.Name}\n{(order.Any(id => ordering.Find(id) == mod) ? "已启用" : "未启用")} · 工坊版本 {Date(update.Installed)} → {Date(update.Latest)}" }
            };
            boxes.Add((update, box));
            box.Checked += (_, _) => selected.IsEnabled = true;
            box.Unchecked += (_, _) => selected.IsEnabled = boxes.Any(x => x.Box.IsChecked == true);
            rows.Children.Add(box);
        }
        panel.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 345, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        dialog.ShowDialog();
        return chosen;
    }

    private async void CheckUpdates(object sender, RoutedEventArgs e)
    {
        if (library is null) return;
        Shell.IsEnabled = false;
        try
        {
            Status.Text = "正在查询 Steam 创意工坊…";
            var (updates, unknown) = await Workshop.Check(mods, settings.Revisions);
            var chosen = ChooseUpdates(updates, unknown);
            if (chosen.Count == 0) { Status.Text = "更新检查完成。"; return; }
            var cmd = settings.SteamCmd;
            if (!File.Exists(cmd))
            {
                var picker = new OpenFileDialog { Title = "请选择独立安装的 SteamCMD.exe", Filter = "SteamCMD.exe|steamcmd.exe" };
                if (picker.ShowDialog(this) != true) { Status.Text = "更新已取消。"; return; }
                settings.SteamCmd = cmd = picker.FileName;
                SaveSettings();
            }
            if (ModLibrary.GameRunning()) throw new InvalidOperationException("请退出 RimWorld 后再更新。");
            var failures = new List<string>();
            var succeeded = 0;
            foreach (var update in chosen.Where(u => !Locked(u.Mod)))
            {
                Status.Text = $"正在更新 {update.Mod.Name}…";
                try
                {
                    await Workshop.Download(update, cmd, library.Version);
                    settings.Revisions[update.Mod.WorkshopId!] = update.Latest;
                    succeeded++;
                    SaveSettings();
                }
                catch (Exception ex) { failures.Add($"{update.Mod.Name} · {ex.Message}"); }
            }
            mods = ApplyKinds(library.Scan());
            ordering = new(mods, library.Version);
            UpdateKinds();
            Refresh();
            Status.Text = $"已更新 {succeeded} 项 · 失败 {failures.Count} 项";
            if (failures.Count > 0) ConfirmText("更新结果", string.Join("\n", failures), "完成", true);
        }
        catch (Exception ex)
        {
            Status.Text = "更新未完成。";
            MessageBox.Show(this, ex.Message, "模组更新", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { Shell.IsEnabled = true; }
    }
}

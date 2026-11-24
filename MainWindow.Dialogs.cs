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

    private Window Dialog(string title, double width = 640, Window? owner = null)
    {
        var dialog = new Window
        {
            Title = title, Owner = owner ?? this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = width, SizeToContent = SizeToContent.Height, MaxHeight = 560, FontFamily = FontFamily,
            Background = Background, Foreground = Foreground, FontSize = FontSize, ShowInTaskbar = false,
            Icon = Icon, UseLayoutRounding = true
        };
        dialog.SourceInitialized += (_, _) => App.ApplyCaptionTheme(dialog);
        return dialog;
    }

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

    private void UsePrimaryStyle(Button button) => button.Style = (Style)FindResource("PrimaryButton");

    private bool CanDiscard(Window? owner = null)
    {
        if (!dirty) return true;
        var dialog = Dialog("加载顺序", 360, owner);
        dialog.ResizeMode = ResizeMode.NoResize;
        var panel = DialogPanel(dialog, out var buttons);
        var question = new TextBlock { Text = "放弃尚未应用的加载顺序？", TextWrapping = TextWrapping.Wrap };
        question.SetResourceReference(StyleProperty, "PanelHeading");
        panel.Children.Add(question);
        AddAction(buttons, "放弃更改", () => dialog.DialogResult = true);
        var keep = AddAction(buttons, "继续编辑", () => dialog.DialogResult = false);
        keep.IsDefault = true;
        keep.IsCancel = true;
        UsePrimaryStyle(keep);
        dialog.Loaded += (_, _) => keep.Focus();
        return dialog.ShowDialog() == true;
    }

    private bool ConfirmText(string title, string text, string action, bool allowed)
    {
        var dialog = Dialog(title);
        var panel = DialogPanel(dialog, out var buttons);
        AddAction(buttons, "取消", () => dialog.DialogResult = false).IsCancel = true;
        var confirm = AddAction(buttons, action, () => dialog.DialogResult = true);
        confirm.IsEnabled = allowed;
        UsePrimaryStyle(confirm);
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
        var save = AddAction(buttons, "保存", () =>
        {
            if (game.Text.Trim().Length > 0 && !ModLibrary.IsGame(game.Text.Trim()))
            {
                MessageBox.Show(dialog, "所选目录中找不到 RimWorldWin64.exe。");
                return;
            }
            if (!CanDiscard(dialog)) return;
            settings.Game = game.Text.Trim();
            settings.Steam = steam.Text.Trim();
            settings.Config = config.Text.Trim();
            settings.SteamCmd = cmd.Text.Trim();
            dialog.DialogResult = true;
        });
        save.IsDefault = true;
        UsePrimaryStyle(save);
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
        var heading = new TextBlock { Text = "可更新模组", Margin = new Thickness(0, 0, 0, 5) };
        heading.SetResourceReference(StyleProperty, "PanelHeading");
        DockPanel.SetDock(heading, Dock.Top);
        panel.Children.Add(heading);
        var summary = new TextBlock { Text = $"查到 {updates.Count} 项新版 · 已锁定 {locked} 项 · 状态未知 {unknown} 项",
            Margin = new Thickness(0, 0, 0, 12) };
        summary.SetResourceReference(StyleProperty, "Secondary");
        DockPanel.SetDock(summary, Dock.Top);
        panel.Children.Add(summary);
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
        var all = AddAction(buttons, "全部更新", () => Complete(updates));
        all.IsEnabled = updates.Count > locked;
        UsePrimaryStyle(all);
        foreach (var update in updates)
        {
            var mod = update.Mod;
            var pinned = Locked(mod);
            string Date(long time) => DateTimeOffset.FromUnixTimeSeconds(time).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            var description = new StackPanel();
            var title = new TextBlock { Text = pinned ? $"🔒 {mod.Name} · 已锁定" : mod.Name,
                FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            title.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            var version = new TextBlock { Text = $"{(order.Any(id => ordering.Find(id) == mod) ? "已启用" : "未启用")} · 工坊版本 {Date(update.Installed)} → {Date(update.Latest)}",
                TextWrapping = TextWrapping.Wrap };
            version.SetResourceReference(StyleProperty, "Secondary");
            description.Children.Add(title);
            description.Children.Add(version);
            var box = new CheckBox { IsEnabled = !pinned, Content = description, Padding = new Thickness(7, 0, 0, 0) };
            boxes.Add((update, box));
            box.Checked += (_, _) => selected.IsEnabled = true;
            box.Unchecked += (_, _) => selected.IsEnabled = boxes.Any(x => x.Box.IsChecked == true);
            var row = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 9, 0, 9), Child = box };
            row.SetResourceReference(Border.BorderBrushProperty, "Outline");
            rows.Children.Add(row);
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
            var (updates, unknown) = await CheckWorkshopWithProgress();
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

    private async Task<(List<WorkshopUpdate> Updates, int Unknown)> CheckWorkshopWithProgress()
    {
        Status.Visibility = Visibility.Collapsed;
        WorkshopCheckLabel.Text = "正在准备检查模组更新…";
        WorkshopCheckBar.Value = 0;
        WorkshopCheckBar.IsIndeterminate = true;
        WorkshopCheckIndicator.Visibility = Visibility.Visible;
        var progress = new Progress<WorkshopCheckProgress>(value =>
        {
            if (WorkshopCheckIndicator.Visibility != Visibility.Visible) return;
            WorkshopCheckBar.Maximum = Math.Max(1, value.Total);
            WorkshopCheckBar.Value = value.Completed;
            WorkshopCheckBar.IsIndeterminate = value.Completed == 0 && value.Total > 0;
            WorkshopCheckLabel.Text = value.Total == 0 ? "没有可检查的工坊模组。"
                : $"检查模组更新 · {value.Completed} / {value.Total}";
        });
        try
        {
            return await Task.Run(() => Workshop.Check(mods, settings.Revisions, progress));
        }
        finally
        {
            WorkshopCheckIndicator.Visibility = Visibility.Collapsed;
            WorkshopCheckBar.IsIndeterminate = false;
            Status.Visibility = Visibility.Visible;
        }
    }
}

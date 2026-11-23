using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Shape = System.Windows.Shapes.Path;

namespace RimManager;

public partial class MainWindow
{
    private static readonly Brush[] WireColors = [Brushes.Teal, Brushes.Peru, Brushes.MediumSlateBlue, Brushes.SeaGreen];
    private bool wiresQueued;
    private Point dragOrigin;
    private string? dragId;
    private ListBoxItem? dropRow;
    private ScrollViewer? activeScroll;
    private long lastDragScroll;

    private void QueueWires()
    {
        if (wiresQueued) return;
        wiresQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            wiresQueued = false;
            DrawWires();
        });
    }

    private void DrawWires()
    {
        Wires.Children.Clear();
        var positions = new Dictionary<Mod, double>();
        foreach (ListBoxItem row in Active.Items)
            if (row.IsArrangeValid && ordering.Find((string)row.Tag) is Mod mod)
                positions[mod] = row.TranslatePoint(new Point(0, row.ActualHeight / 2), Wires).Y;
        var connections = ordering.Connections(order).Distinct().ToList();
        var groups = new Dictionary<Mod, int>();
        var groupIndex = 0;
        foreach (var (from, to) in connections)
        {
            var found = groups.TryGetValue(from, out var first);
            if (!found && !groups.TryGetValue(to, out first)) first = groupIndex++;
            if (groups.TryGetValue(to, out var second) && second != first)
                foreach (var key in groups.Where(kv => kv.Value == second).Select(kv => kv.Key).ToArray())
                    groups[key] = first;
            groups[from] = groups[to] = first;
        }
        var visibleIndex = 0;
        foreach (var (from, to) in connections)
        {
            if (!positions.TryGetValue(from, out var y1) || !positions.TryGetValue(to, out var y2)) continue;
            var lane = 10 + (visibleIndex++ % 7) * 5;
            if (Math.Max(y1, y2) < 0 || Math.Min(y1, y2) > Wires.ActualHeight) continue;
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(52, y1), false, false);
                ctx.PolyLineTo([new(lane, y1), new(lane, y2), new(52, y2)], true, false);
                ctx.BeginFigure(new Point(46, y2 - 3), false, false);
                ctx.PolyLineTo([new(52, y2), new(46, y2 + 3)], true, false);
            }
            Wires.Children.Add(new Shape { Data = geometry, Stroke = WireColors[groups[from] % WireColors.Length], StrokeThickness = 1.7 });
        }
    }

    private ListBoxItem? RowAt(object source) => source is DependencyObject obj
        ? ItemsControl.ContainerFromElement(Active, obj) as ListBoxItem : null;
    private void DragStart(object sender, MouseButtonEventArgs e)
    {
        dragOrigin = e.GetPosition(Active);
        dragId = RowAt(e.OriginalSource)?.Tag as string;
    }
    private void DragMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || dragId is not string id) return;
        var delta = e.GetPosition(Active) - dragOrigin;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        try { DragDrop.DoDragDrop(Active, new DataObject("RimManager.OrderRow", id), DragDropEffects.Move); }
        finally { dragId = null; ClearDrop(); }
    }

    private void ClearDrop()
    {
        if (dropRow is null) return;
        dropRow.ClearValue(BorderThicknessProperty);
        dropRow.ClearValue(BorderBrushProperty);
        dropRow = null;
    }

    private void DragOverRow(object sender, DragEventArgs e)
    {
        if (dragId is null || !e.Data.GetDataPresent("RimManager.OrderRow")) return;
        ClearDrop();
        e.Handled = true;
        ScrollDuringDrag(e.GetPosition(Active).Y);
        dropRow = RowAt(e.OriginalSource);
        e.Effects = dropRow is null ? DragDropEffects.None : DragDropEffects.Move;
        if (dropRow is null) return;
        var after = e.GetPosition(dropRow).Y >= dropRow.ActualHeight / 2;
        dropRow.BorderBrush = Brushes.SeaGreen;
        dropRow.BorderThickness = after ? new Thickness(0, 0, 0, 2) : new Thickness(0, 2, 0, 0);
    }

    private void LeaveRow(object sender, DragEventArgs e) => ClearDrop();

    private void ScrollDuringDrag(double y)
    {
        var now = Environment.TickCount64;
        if (now - lastDragScroll < 100 || (y > 24 && y < Active.ActualHeight - 24)) return;
        activeScroll ??= Active.Template.FindName("PART_ScrollViewer", Active) as ScrollViewer
            ?? FindScrollViewer(Active);
        if (activeScroll is null) return;
        lastDragScroll = now;
        activeScroll.ScrollToVerticalOffset(activeScroll.VerticalOffset + (y <= 24 ? -36 : 36));
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer viewer) return viewer;
            if (FindScrollViewer(child) is { } nested) return nested;
        }
        return null;
    }
    private void DropRow(object sender, DragEventArgs e)
    {
        if (dragId is not string id || !e.Data.GetDataPresent("RimManager.OrderRow") ||
            RowAt(e.OriginalSource) is not ListBoxItem row || Ids.Equals(id, (string)row.Tag)) return;
        e.Handled = true;
        var next = order.Where(item => !Ids.Equals(item, id)).ToList();
        var index = next.FindIndex(item => Ids.Equals(item, (string)row.Tag));
        next.Insert(index + (e.GetPosition(row).Y >= row.ActualHeight / 2 ? 1 : 0), id);
        ClearDrop();
        if (order.SequenceEqual(next, Ids)) return;
        order = next;
        MarkChanged(id);
        Active.ScrollIntoView(Active.SelectedItem);
    }

    private void AutoSort(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (library is null) return;
        var (next, skipped) = ordering.Sort(order);
        var original = order.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i, Ids);
        var changes = next.Select((id, i) => (id, i)).Where(x => original[x.id] != x.i)
            .Select(x => $"{original[x.id] + 1,4} → {x.i + 1,-4} {ordering.Find(x.id)?.Name ?? x.id}").ToList();
        if (skipped.Count > 0) changes.AddRange(skipped.Select(s => "未采纳建议 · " + s));
        if (changes.Count == 0) { Status.Text = "当前顺序已满足排序规则。"; return; }
        if (!ConfirmText("自动排序预览", string.Join("\n", changes), "采用排序", changes.Count > skipped.Count)) return;
        order = next;
        MarkChanged();
    });

    internal sealed record OrderFile(int Version, string[] Mods);
    private void ExportOrder(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (library is null) return;
        var dialog = new SaveFileDialog { Filter = "排序清单 (*.json)|*.json", FileName = "rimworld-order.json" };
        if (dialog.ShowDialog(this) != true) return;
        File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(new OrderFile(1, order.ToArray()), Json));
        Status.Text = "已导出排序清单。";
    });

    private void ImportOrder(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (library is null) return;
        var dialog = new OpenFileDialog { Filter = "排序清单 (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        var data = JsonSerializer.Deserialize<OrderFile>(File.ReadAllText(dialog.FileName), Json);
        if (data is not { Version: 1, Mods: not null } || data.Mods.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("排序清单格式无效。");
        var ids = data.Mods.Select(x => x.Trim()).ToList();
        if (ids.Distinct(Ids).Count() != ids.Count) throw new InvalidDataException("清单含重复包标识。");
        var missing = ids.Where(id => ordering.Find(id) is null).ToList();
        var available = ids.Except(missing, Ids).ToList();
        if (available.Select(ordering.Find).Distinct().Count() != available.Count)
            throw new InvalidDataException("清单中多个包标识对应同一安装项。");
        var info = $"将启用 {available.Count} 项并替换当前清单。";
        if (missing.Count > 0) info += "\n\n缺失模组\n" + string.Join("\n", missing);
        if (available.Count == 0) info += "\n没有可导入的模组。";
        if (!ConfirmText("导入排序", info, missing.Count == 0 ? "导入" : "忽略缺失并导入", available.Count > 0)) return;
        if (order.SequenceEqual(available, Ids)) return;
        order = available;
        MarkChanged();
    });
}

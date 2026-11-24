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
    private bool wiresQueued;
    private Point dragOrigin;
    private ListBoxItem? pressedRow;
    private bool deferredClick;
    private sealed record ModDrag(ListBox Source, string[] Ids);
    private const string ModDragFormat = "RimManager.ModSelection";
    private ModDrag? currentDrag;
    private ListBoxItem? dropRow;
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
        if (Active.SelectedItem is not ListBoxItem selectedRow || SelectedMod is not Mod selected) return;

        var connections = ordering.Connections(order)
            .Where(edge => edge.From == selected || edge.To == selected).Distinct().ToArray();
        if (connections.Length == 0) return;
        var related = connections.Select(edge => edge.From == selected ? edge.To : edge.From).ToHashSet();
        var positions = new Dictionary<Mod, double>();
        foreach (ListBoxItem row in Active.Items)
            if (row.Visibility == Visibility.Visible && row.IsArrangeValid &&
                ordering.Find((string)row.Tag) is Mod mod && related.Contains(mod))
                positions[mod] = row.TranslatePoint(new Point(0, row.ActualHeight / 2), Wires).Y;

        var center = selectedRow.TranslatePoint(new Point(0, selectedRow.ActualHeight / 2), Wires).Y;
        var prerequisites = connections.Where(edge => edge.To == selected).Select(edge => edge.From)
            .Where(positions.ContainsKey).Select(mod => positions[mod]).Distinct().ToArray();
        var dependents = connections.Where(edge => edge.From == selected).Select(edge => edge.To)
            .Where(positions.ContainsKey).Select(mod => positions[mod]).Distinct().ToArray();
        var twoDirections = prerequisites.Length > 0 && dependents.Length > 0;
        DrawBranch(prerequisites, center - (twoDirections ? 5 : 0), 14, "Accent", arrowsAtLeaves: false);
        DrawBranch(dependents, center + (twoDirections ? 5 : 0), twoDirections ? 30 : 14, "WarningText", arrowsAtLeaves: true);
    }

    private void DrawBranch(double[] leaves, double center, double lane, string brushKey, bool arrowsAtLeaves)
    {
        if (leaves.Length == 0) return;
        var top = Math.Min(center, leaves.Min());
        var bottom = Math.Max(center, leaves.Max());
        if (bottom < 0 || top > Wires.ActualHeight) return;

        const double end = 52;
        var path = new StreamGeometry();
        using (var ctx = path.Open())
        {
            void Segment(double x1, double y1, double x2, double y2)
            {
                ctx.BeginFigure(new Point(x1, y1), false, false);
                ctx.LineTo(new Point(x2, y2), true, false);
            }
            void Arrow(double y)
            {
                ctx.BeginFigure(new Point(end - 5, y - 3), false, false);
                ctx.PolyLineTo([new(end, y), new(end - 5, y + 3)], true, false);
            }
            bool Visible(double y) => y >= 0 && y <= Wires.ActualHeight;

            Segment(lane, Math.Max(0, top), lane, Math.Min(Wires.ActualHeight, bottom));
            if (Visible(center))
            {
                Segment(lane, center, end, center);
                if (!arrowsAtLeaves) Arrow(center);
            }
            foreach (var leaf in leaves.Where(Visible))
            {
                Segment(lane, leaf, end, leaf);
                if (arrowsAtLeaves) Arrow(leaf);
            }
        }
        path.Freeze();
        var wire = new Shape
        {
            Data = path, StrokeThickness = 1.8, StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
        };
        wire.SetResourceReference(Shape.StrokeProperty, brushKey);
        Wires.Children.Add(wire);
    }

    private static ListBoxItem? RowAt(ListBox list, object source) => source is DependencyObject obj
        ? ItemsControl.ContainerFromElement(list, obj) as ListBoxItem : null;
    private void DragStart(object sender, MouseButtonEventArgs e)
    {
        var list = (ListBox)sender;
        dragOrigin = e.GetPosition(list);
        pressedRow = RowAt(list, e.OriginalSource);
        deferredClick = false;
        if (pressedRow is null || Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.ClickCount == 2)
        {
            var id = (string)pressedRow.Tag;
            pressedRow = null;
            e.Handled = true;
            if (list == Active) DisableMods([id]);
            else PlaceInOrder([id], order.Count);
        }
        else if (pressedRow.IsSelected && list.SelectedItems.Count > 1)
        {
            // Keep the group selected until the gesture resolves to a click or drag.
            deferredClick = true;
            list.CaptureMouse();
            e.Handled = true;
        }
    }

    private void FinishClick(object sender, MouseButtonEventArgs e)
    {
        var list = (ListBox)sender;
        if (deferredClick && pressedRow is not null)
        {
            list.ReleaseMouseCapture();
            list.UnselectAll();
            list.SelectedItem = pressedRow;
            pressedRow.Focus();
            e.Handled = true;
        }
        deferredClick = false;
        pressedRow = null;
    }
    private void DragMove(object sender, MouseEventArgs e)
    {
        var list = (ListBox)sender;
        if (e.LeftButton != MouseButtonState.Pressed || pressedRow is not { IsSelected: true } row ||
            ItemsControl.ItemsControlFromItemContainer(row) != list) return;
        var delta = e.GetPosition(list) - dragOrigin;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        pressedRow = null;
        deferredClick = false;
        if (list.IsMouseCaptured) list.ReleaseMouseCapture();
        currentDrag = new(list, SelectedIds(list));
        try { DragDrop.DoDragDrop(list, new DataObject(ModDragFormat, true), DragDropEffects.Move); }
        finally { currentDrag = null; ClearDrop(); }
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
        var list = (ListBox)sender;
        ClearDrop();
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (!CanDrop(list, e)) return;
        e.Effects = DragDropEffects.Move;
        ScrollDuringDrag(list, e.GetPosition(list).Y);
        if (list != Active) return;
        dropRow = RowAt(list, e.OriginalSource);
        if (dropRow is null) return;
        var after = e.GetPosition(dropRow).Y >= dropRow.ActualHeight / 2;
        dropRow.SetResourceReference(BorderBrushProperty, "Accent");
        dropRow.BorderThickness = after ? new Thickness(0, 0, 0, 2) : new Thickness(0, 2, 0, 0);
    }

    private void LeaveRow(object sender, DragEventArgs e) => ClearDrop();

    private bool CanDrop(ListBox list, DragEventArgs e) => currentDrag is not null &&
        (list == Active || currentDrag.Source == Active) && e.Data.GetDataPresent(ModDragFormat);

    private void ScrollDuringDrag(ListBox list, double y)
    {
        var now = Environment.TickCount64;
        if (now - lastDragScroll < 100 || (y > 24 && y < list.ActualHeight - 24)) return;
        if (list.Template.FindName("PART_ScrollViewer", list) is not ScrollViewer scroll) return;
        lastDragScroll = now;
        if (y <= 24) scroll.LineUp(); else scroll.LineDown();
    }

    private void DropRow(object sender, DragEventArgs e)
    {
        var list = (ListBox)sender;
        e.Handled = true;
        ClearDrop();
        e.Effects = DragDropEffects.None;
        if (!CanDrop(list, e) || currentDrag is not { } drag) return;
        e.Effects = DragDropEffects.Move;
        if (list == Inactive) DisableMods(drag.Ids);
        else
        {
            var insertion = order.Count;
            if (RowAt(list, e.OriginalSource) is { } row)
            {
                if (drag.Source == list && row.IsSelected) return;
                insertion = list.Items.IndexOf(row) + (e.GetPosition(row).Y >= row.ActualHeight / 2 ? 1 : 0);
            }
            PlaceInOrder(drag.Ids, insertion);
        }
        if (list.SelectedItem is { } selected) list.ScrollIntoView(selected);
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

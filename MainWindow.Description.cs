using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace RimManager;

public partial class MainWindow
{
    private Mod? descriptionMod;
    private CancellationTokenSource? descriptionRequest;
    private string? displayedCover;

    private void ShowModPresentation(Mod? mod)
    {
        if (descriptionMod == mod && (mod is not null || ModSummary.Inlines.Count == 0)) return;
        descriptionMod = mod;
        descriptionRequest?.Cancel();
        descriptionRequest = null;
        displayedCover = null;
        ModDetailsScroll.ScrollToTop();
        CoverFrame.Visibility = Visibility.Collapsed;
        ModCover.Source = null;
        DescriptionImages.Children.Clear();
        DescriptionImageSection.Visibility = Visibility.Collapsed;
        DescriptionSection.Visibility = mod is null ? Visibility.Collapsed : Visibility.Visible;
        ModSummary.Inlines.Clear();
        FullSummary.Inlines.Clear();
        More.Visibility = Visibility.Collapsed;
        More.IsExpanded = false;
        if (mod is null) return;

        var description = ModDescription.Parse(mod.Description);
        RenderDescription(description);
        var request = descriptionRequest = new CancellationTokenSource();
        _ = LoadPresentation(mod, description, request);
    }

    private async Task LoadPresentation(Mod mod, ModDescription local, CancellationTokenSource request)
    {
        var token = request.Token;
        try
        {
            foreach (var filename in new[] { "About/Preview.png", "About/Preview.jpg", "About/Preview.jpeg", "About/Preview.gif" })
                if (await ShowCover(mod, filename, token)) break;
            token.ThrowIfCancellationRequested();

            var loaded = new HashSet<string>(StringComparer.Ordinal);
            var localImages = ShowDescriptionImages(mod, local.Images, loaded, token);
            if (mod.WorkshopId is string id)
            {
                try
                {
                    var workshop = await Workshop.GetPresentation(id, token);
                    token.ThrowIfCancellationRequested();
                    if (workshop is not null)
                    {
                        if (displayedCover is null && workshop.Cover.Length > 0)
                            await ShowCover(mod, workshop.Cover, token);
                        var online = ModDescription.Parse(workshop.Description);
                        if (!HasText(local) && HasText(online)) RenderDescription(online);
                        await localImages;
                        await ShowDescriptionImages(mod, online.Images, loaded, token);
                    }
                }
                catch (Exception error) when (error is HttpRequestException or IOException or JsonException or
                    InvalidOperationException or KeyNotFoundException or FormatException or OperationCanceledException)
                {
                    // Local media and descriptions remain available when Workshop metadata fails.
                }
            }
            await localImages;
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(descriptionRequest, request)) descriptionRequest = null;
            request.Dispose();
        }
    }

    private async Task<bool> ShowCover(Mod mod, string reference, CancellationToken token)
    {
        var image = await ModImages.Load(mod, reference, token);
        token.ThrowIfCancellationRequested();
        if (image is null) return false;
        displayedCover = reference;
        ModCover.Source = image;
        CoverFrame.Visibility = Visibility.Visible;
        return true;
    }

    private async Task ShowDescriptionImages(Mod mod, IReadOnlyList<string> references, HashSet<string> loaded, CancellationToken token)
    {
        foreach (var path in references.Take(20))
        {
            token.ThrowIfCancellationRequested();
            if (path == displayedCover || !loaded.Add(path)) continue;
            var source = await ModImages.Load(mod, path, token);
            token.ThrowIfCancellationRequested();
            if (source is null) continue;
            var image = new Image { Source = source, Stretch = Stretch.Uniform, MaxHeight = 320, Margin = new Thickness(0, 3, 0, 5) };
            System.Windows.Automation.AutomationProperties.SetName(image, "简介图片");
            DescriptionImages.Children.Add(image);
            DescriptionImageSection.Visibility = Visibility.Visible;
        }
    }

    private static bool HasText(ModDescription description) => description.Runs.Any(run => !string.IsNullOrWhiteSpace(run.Text));

    private void RenderDescription(ModDescription description)
    {
        var longDescription = description.Runs.Sum(run => (long)run.Text.Length) > 220;
        More.Visibility = longDescription ? Visibility.Visible : Visibility.Collapsed;
        WriteDescription(ModSummary, description.Runs, longDescription ? 220 : int.MaxValue);
        FullSummary.Inlines.Clear();
        if (longDescription) WriteDescription(FullSummary, description.Runs, int.MaxValue);
        if (!HasText(description))
            ModSummary.Inlines.Add(new Run(description.Images.Count == 0 ? "作者未提供简介。" : "简介图片"));
    }

    private void WriteDescription(TextBlock target, IReadOnlyList<DescriptionRun> description, int limit)
    {
        target.Inlines.Clear();
        var remaining = limit;
        foreach (var part in description)
        {
            if (remaining == 0) break;
            var count = Math.Min(remaining, part.Text.Length);
            var length = count < part.Text.Length && char.IsHighSurrogate(part.Text[count - 1]) ? count - 1 : count;
            remaining -= count;
            var run = new Run(part.Text[..length]);
            if (part.Style.Bold) run.FontWeight = FontWeights.Bold;
            if (part.Style.Italic) run.FontStyle = FontStyles.Italic;
            if (part.Style.Size is double size) run.FontSize = size;
            if (part.Style.Color is Color color && !SystemParameters.HighContrast &&
                TryFindResource("DescriptionSurface") is SolidColorBrush background && HasReadableContrast(color, background.Color))
                run.Foreground = new SolidColorBrush(color);
            if (part.Style.Underline || part.Style.Strike)
            {
                var decorations = new TextDecorationCollection();
                if (part.Style.Underline) decorations.Add(TextDecorations.Underline[0]);
                if (part.Style.Strike) decorations.Add(TextDecorations.Strikethrough[0]);
                run.TextDecorations = decorations;
            }
            if (part.Style.Link is Uri url)
            {
                var link = new Hyperlink(run) { NavigateUri = url };
                link.SetResourceReference(TextElement.ForegroundProperty, "Accent");
                link.RequestNavigate += (_, e) =>
                {
                    e.Handled = true;
                    Guard(() => Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true })?.Dispose());
                };
                target.Inlines.Add(link);
            }
            else target.Inlines.Add(run);
        }
        if (remaining == 0 && limit != int.MaxValue) target.Inlines.Add(new Run("…"));
    }

    private static bool HasReadableContrast(Color text, Color background)
    {
        static double Luminance(Color color) => .2126 * color.ScR + .7152 * color.ScG + .0722 * color.ScB;
        var surface = Luminance(background);
        var foreground = text.ScA * Luminance(text) + (1 - text.ScA) * surface;
        return (Math.Max(foreground, surface) + .05) / (Math.Min(foreground, surface) + .05) >= 4.5;
    }
}

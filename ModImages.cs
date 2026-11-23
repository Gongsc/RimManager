using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;

namespace RimManager;

internal static class ModImages
{
    private const int MaxBytes = 12 * 1024 * 1024;
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly string[] ImageHosts = [
        "steamusercontent.com", "steamstatic.com", "steamuserimages-a.akamaihd.net", "i.imgur.com",
        "raw.githubusercontent.com", "user-images.githubusercontent.com"
    ];

    internal static async Task<BitmapSource?> Load(Mod mod, string reference, CancellationToken cancellationToken)
    {
        try
        {
            if (Uri.TryCreate(reference, UriKind.Absolute, out var uri))
            {
                if (!IsImageHost(uri)) return null;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                for (var redirects = 0; redirects <= 3; redirects++)
                {
                    using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                    if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is Uri location)
                    {
                        uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                        if (!IsImageHost(uri)) return null;
                        continue;
                    }
                    if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBytes) return null;
                    await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
                    using var bytes = new MemoryStream();
                    var buffer = new byte[81920];
                    int received;
                    while ((received = await input.ReadAsync(buffer.AsMemory(), timeout.Token)) > 0)
                    {
                        if (bytes.Length + received > MaxBytes) return null;
                        bytes.Write(buffer, 0, received);
                    }
                    bytes.Position = 0;
                    return Decode(bytes);
                }
                return null;
            }
            return await Task.Run(() =>
            {
                var relative = Uri.UnescapeDataString(reference);
                if (Path.IsPathRooted(relative) || relative.Contains(':')) return null;
                foreach (var folder in new[] { mod.Path, Path.Combine(mod.Path, "About") })
                {
                    var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
                    var file = Path.GetFullPath(relative, root);
                    if (!file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                        !File.Exists(file) || new FileInfo(file).Length > MaxBytes) continue;
                    if (HasReparsePoint(file, root)) continue;
                    using var stream = File.OpenRead(file);
                    return Decode(stream);
                }
                return null;
            }, cancellationToken);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or HttpRequestException
            or FormatException or ArgumentException or NotSupportedException or InvalidOperationException or OperationCanceledException)
        {
            return null;
        }
    }

    private static bool IsImageHost(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && ImageHosts.Any(host => uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase));

    private static bool HasReparsePoint(string file, string root)
    {
        for (string? path = file; path is not null && path.Length >= root.Length; path = Path.GetDirectoryName(path))
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) return true;
        return false;
    }

    private static BitmapSource Decode(Stream stream)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = 960;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}

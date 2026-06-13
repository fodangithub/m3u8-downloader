using System.Text.RegularExpressions;

namespace M3U8Downloader.Helpers;

public static partial class FileNameHelper
{
    public static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "download";

        // Remove Windows reserved characters
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());

        // Also replace these problematic chars (valid in NTFS but cause issues with tools/APIs)
        sanitized = sanitized
            .Replace('[', '_').Replace(']', '_')
            .Replace('{', '_').Replace('}', '_')
            .Replace('#', '_').Replace('&', '_')
            .Replace('%', '_').Replace('~', '_');

        // Replace spaces with underscores
        sanitized = sanitized.Replace(' ', '_');

        // Collapse multiple underscores
        sanitized = Regex.Replace(sanitized, "_+", "_");

        // Remove leading/trailing dots, underscores and spaces
        sanitized = sanitized.Trim('.', '_', ' ');

        // If empty after sanitization, use default
        if (string.IsNullOrWhiteSpace(sanitized))
            return "download";

        // Truncate to reasonable length (avoid MAX_PATH issues)
        if (sanitized.Length > 150)
            sanitized = sanitized[..150];

        return sanitized;
    }

    public static string EnsureUnique(string filePath, string extension = ".mp4")
    {
        var dir = Path.GetDirectoryName(filePath) ?? "";
        var name = Path.GetFileNameWithoutExtension(filePath);
        var ext = Path.GetExtension(filePath);

        if (string.IsNullOrEmpty(ext))
            ext = extension;

        var fullPath = filePath;
        var counter = 1;

        while (File.Exists(fullPath))
        {
            fullPath = Path.Combine(dir, $"{name}_{counter}{ext}");
            counter++;
        }

        return fullPath;
    }

    public static string InferFileName(string url)
    {
        try
        {
            var uri = new Uri(url);
            var segments = uri.Segments;

            // Try to find a meaningful name from URL
            for (int i = segments.Length - 1; i >= 0; i--)
            {
                var segment = segments[i].Trim('/');
                if (string.IsNullOrEmpty(segment))
                    continue;

                // Remove common extensions
                var name = segment;
                if (name.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
                    name = name[..^5];
                else if (name.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
                    name = name[..^3];
                else if (name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                    name = name[..^4];

                // If it's not just an index or query string, use it
                if (!string.IsNullOrEmpty(name) && !Regex.IsMatch(name, @"^\d+$"))
                    return SanitizeFileName(name) + TimestampSuffix();
            }

            // Fallback: use host + path hash
            var host = uri.Host.Replace(".", "_");
            var hash = uri.AbsolutePath.GetHashCode().ToString("X8");
            return $"{host}_{hash}{TimestampSuffix()}";
        }
        catch
        {
            return "download_" + DateTime.Now.ToString("yyyyMMddHHmmssfff");
        }
    }

    private static string TimestampSuffix()
    {
        return "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
    }
}

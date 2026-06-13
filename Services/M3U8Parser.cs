using System.Net.Http;
using System.Text.RegularExpressions;
using M3U8Downloader.Models;
using Microsoft.Extensions.Logging;

namespace M3U8Downloader.Services;

public class M3U8Parser
{
    private readonly ILogger<M3U8Parser> _logger;

    public M3U8Parser(ILogger<M3U8Parser> logger)
    {
        _logger = logger;
    }

    public M3U8Playlist Parse(string content, string sourceUrl)
    {
        var playlist = new M3U8Playlist { SourceUrl = sourceUrl };
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                          .Select(l => l.Trim())
                          .Where(l => !string.IsNullOrEmpty(l))
                          .ToList();

        if (lines.Count == 0 || lines[0] != "#EXTM3U")
            throw new FormatException("Invalid M3U8 file: missing #EXTM3U header");

        bool isMaster = lines.Any(l => l.StartsWith("#EXT-X-STREAM-INF:"));
        playlist.IsMaster = isMaster;

        if (isMaster)
            ParseMasterPlaylist(playlist, lines, sourceUrl);
        else
            ParseMediaPlaylist(playlist, lines, sourceUrl);

        return playlist;
    }

    private void ParseMasterPlaylist(M3U8Playlist playlist, List<string> lines, string baseUrl)
    {
        for (int i = 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.StartsWith("#EXT-X-STREAM-INF:"))
            {
                var variant = new VariantStream();
                var attrs = ParseAttributes(line["#EXT-X-STREAM-INF:".Length..]);

                if (attrs.TryGetValue("BANDWIDTH", out var bw))
                    variant.Bandwidth = long.Parse(bw);
                if (attrs.TryGetValue("AVERAGE-BANDWIDTH", out var abw))
                    variant.AverageBandwidth = int.Parse(abw);
                if (attrs.TryGetValue("RESOLUTION", out var res))
                    variant.Resolution = res;
                if (attrs.TryGetValue("CODECS", out var codecs))
                    variant.Codecs = codecs.Trim('"');
                if (attrs.TryGetValue("AUDIO", out var audio))
                    variant.AudioGroupId = audio.Trim('"');
                if (attrs.TryGetValue("VIDEO", out var video))
                    variant.VideoGroupId = video.Trim('"');
                if (attrs.TryGetValue("NAME", out var name))
                    variant.Name = name.Trim('"');

                if (i + 1 < lines.Count && !lines[i + 1].StartsWith("#"))
                {
                    i++;
                    variant.Url = ResolveUrl(lines[i], baseUrl);
                }
                playlist.Variants.Add(variant);
            }
        }
        playlist.Variants.Sort((a, b) => b.Bandwidth.CompareTo(a.Bandwidth));
    }

    private void ParseMediaPlaylist(M3U8Playlist playlist, List<string> lines, string baseUrl)
    {
        int segmentIndex = 0;
        double currentDuration = 0;
        string currentTitle = "";
        EncryptionInfo? currentEncryption = null;
        long byteRangeOffset = 0;
        long byteRangeLength = 0;

        for (int i = 1; i < lines.Count; i++)
        {
            var line = lines[i];

            if (line.StartsWith("#EXT-X-VERSION:"))
                playlist.Version = int.Parse(line["#EXT-X-VERSION:".Length..]);
            else if (line.StartsWith("#EXT-X-TARGETDURATION:"))
                playlist.TargetDuration = int.Parse(line["#EXT-X-TARGETDURATION:".Length..]);
            else if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:"))
                playlist.MediaSequence = long.Parse(line["#EXT-X-MEDIA-SEQUENCE:".Length..]);
            else if (line.StartsWith("#EXT-X-ENDLIST"))
                playlist.IsEndList = true;
            else if (line.StartsWith("#EXT-X-KEY:"))
            {
                var attrs = ParseAttributes(line["#EXT-X-KEY:".Length..]);
                var method = attrs.GetValueOrDefault("METHOD", "NONE");
                if (method == "NONE")
                    currentEncryption = null;
                else
                {
                    currentEncryption = new EncryptionInfo
                    {
                        Method = method,
                        KeyUri = attrs.ContainsKey("URI")
                            ? ResolveUrl(attrs["URI"].Trim('"'), baseUrl)
                            : "",
                        KeyFormat = attrs.GetValueOrDefault("KEYFORMAT", "identity").Trim('"')
                    };
                    if (attrs.TryGetValue("IV", out var iv))
                    {
                        var ivHex = iv.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                            ? iv[2..] : iv;
                        currentEncryption.IV = HexToBytes(ivHex);
                    }
                }
            }
            else if (line.StartsWith("#EXTINF:"))
            {
                var parts = line["#EXTINF:".Length..].Split(',', 2);
                currentDuration = double.Parse(parts[0].TrimEnd('#'));
                currentTitle = parts.Length > 1 ? parts[1] : "";
            }
            else if (line.StartsWith("#EXT-X-BYTERANGE:"))
            {
                var range = line["#EXT-X-BYTERANGE:".Length..];
                var rangeParts = range.Split('@');
                byteRangeLength = long.Parse(rangeParts[0]);
                byteRangeOffset = rangeParts.Length > 1 ? long.Parse(rangeParts[1]) : byteRangeOffset;
            }
            else if (!line.StartsWith("#"))
            {
                var segment = new SegmentInfo
                {
                    Index = segmentIndex++,
                    Url = ResolveUrl(line, baseUrl),
                    Duration = currentDuration,
                    Title = currentTitle,
                    Encryption = currentEncryption,
                    ByteRangeOffset = byteRangeOffset,
                    ByteRangeLength = byteRangeLength
                };
                playlist.Segments.Add(segment);
                currentDuration = 0;
                currentTitle = "";
                byteRangeLength = 0;
            }
        }
    }

    private static Dictionary<string, string> ParseAttributes(string attrString)
    {
        var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pattern = @"([A-Z0-9_\-]+)=(?:""([^""]*)""|([^,]*))";
        var matches = Regex.Matches(attrString, pattern);
        foreach (Match match in matches)
        {
            var key = match.Groups[1].Value;
            var value = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
            attrs[key] = value;
        }
        return attrs;
    }

    private static string ResolveUrl(string url, string baseUrl)
    {
        if (string.IsNullOrEmpty(url)) return baseUrl;
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return url;
        if (url.StartsWith("/"))
        {
            var baseUri = new Uri(baseUrl);
            return $"{baseUri.Scheme}://{baseUri.Host}{url}";
        }
        var baseUriObj = new Uri(baseUrl);
        var basePath = baseUriObj.AbsolutePath;
        var dir = basePath.Contains('/') ? basePath[..basePath.LastIndexOf('/')] : "";
        return $"{baseUriObj.Scheme}://{baseUriObj.Host}{dir}/{url}";
    }

    private static byte[] HexToBytes(string hex)
    {
        if (hex.Length % 2 != 0) hex = "0" + hex;
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }

    public async Task<M3U8Playlist> ParseFromUrlAsync(
        string url, HttpClient httpClient, CancellationToken ct = default)
    {
        _logger.LogInformation("Parsing M3U8 from URL: {Url}", url);
        var response = await httpClient.GetStringAsync(url, ct);
        return Parse(response, url);
    }

    public async Task<M3U8Playlist> ResolveMasterPlaylistAsync(
        M3U8Playlist master, VariantStream variant,
        HttpClient httpClient, CancellationToken ct = default)
    {
        _logger.LogInformation("Resolving variant: {Variant} from {Url}",
            variant.DisplayName, variant.Url);
        var response = await httpClient.GetStringAsync(variant.Url, ct);
        var mediaPlaylist = Parse(response, variant.Url);
        mediaPlaylist.SourceUrl = variant.Url;
        return mediaPlaylist;
    }
}

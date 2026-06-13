namespace M3U8Downloader.Models;

public class M3U8Playlist
{
    public int Version { get; set; }
    public int TargetDuration { get; set; }
    public long MediaSequence { get; set; }
    public bool IsEndList { get; set; }
    public bool IsMaster { get; set; }
    public string SourceUrl { get; set; } = "";

    // Media Playlist fields
    public List<SegmentInfo> Segments { get; set; } = new();

    // Master Playlist fields (multi-quality)
    public List<VariantStream> Variants { get; set; } = new();

    // Total duration estimate
    public double TotalDuration => Segments.Sum(s => s.Duration);
}

public class SegmentInfo
{
    public int Index { get; set; }
    public string Url { get; set; } = "";
    public double Duration { get; set; }
    public string Title { get; set; } = "";
    public EncryptionInfo? Encryption { get; set; }
    public long ByteRangeOffset { get; set; }
    public long ByteRangeLength { get; set; }
}

public class EncryptionInfo
{
    public string Method { get; set; } = "";
    public string KeyUri { get; set; } = "";
    public byte[]? IV { get; set; }
    public string KeyFormat { get; set; } = "identity";

    public bool IsEncrypted => !string.IsNullOrEmpty(Method) && Method != "NONE";
}

public class VariantStream
{
    public string Url { get; set; } = "";
    public long Bandwidth { get; set; }
    public int AverageBandwidth { get; set; }
    public string Resolution { get; set; } = "";
    public string Codecs { get; set; } = "";
    public string AudioGroupId { get; set; } = "";
    public string VideoGroupId { get; set; } = "";
    public string Name { get; set; } = "";

    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrEmpty(Name))
                return Name;
            if (!string.IsNullOrEmpty(Resolution))
                return $"{Resolution} ({Bandwidth / 1000}kbps)";
            return $"{Bandwidth / 1000}kbps";
        }
    }
}

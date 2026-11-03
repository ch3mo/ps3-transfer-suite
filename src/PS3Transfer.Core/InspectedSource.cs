namespace PS3Transfer.Core;

public enum GroupKind
{
    Logical = 0,
    Tree = 1,
}

public sealed record TreeFile(string FullPath, string RelativePath, long Length);

public sealed class InspectedSource
{
    public required string Label { get; init; }
    public required string DisplayPath { get; init; }
    public required GroupKind Group { get; init; }
    public required ContentKind Kind { get; init; }
    public string? Title { get; init; }
    public string? TitleId { get; init; }
    public string? ContentId { get; init; }
    public string? RomFolder { get; init; }
    public string? Warning { get; init; }
    public bool Fatal { get; init; }
    public string OriginalName { get; init; } = "";
    public IReadOnlyList<string> Parts { get; init; } = [];
    public long Length { get; init; }
    public IReadOnlyList<TreeFile> Tree { get; init; } = [];
    public bool AllowMemberSplit { get; init; }
    public long SourceStamp { get; init; }
    public string? ExtractRoot { get; set; }
}

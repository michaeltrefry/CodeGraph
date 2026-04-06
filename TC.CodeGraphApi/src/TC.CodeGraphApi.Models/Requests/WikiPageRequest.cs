namespace TC.CodeGraphApi.Models.Requests;

public class WikiPageRequest
{
    public string? Slug { get; set; }
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public string? RawContent { get; set; }
    public string? Author { get; set; }
    public long? ParentId { get; set; }
    public int? SortOrder { get; set; }
}

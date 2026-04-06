namespace TC.CodeGraphApi.Models.Requests;

public class WikiSectionRequest
{
    public string Title { get; set; } = "";
    public string? Slug { get; set; }
    public string? Description { get; set; }
    public string? Icon { get; set; }
    public int? SortOrder { get; set; }
    public bool AllowUserPages { get; set; } = true;
    public bool HasRawContent { get; set; }
}

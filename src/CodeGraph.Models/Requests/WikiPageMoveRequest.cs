namespace CodeGraph.Models.Requests;

public class WikiPageMoveRequest
{
    public long? NewParentId { get; set; }
    public long? NewSectionId { get; set; }
    public int? SortOrder { get; set; }
}

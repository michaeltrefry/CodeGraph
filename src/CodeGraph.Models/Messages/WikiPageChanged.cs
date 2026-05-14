namespace CodeGraph.Models.Messages;

public class WikiPageChanged
{
    public long PageId { get; set; }
    public long SectionId { get; set; }
    public string SectionSlug { get; set; } = "";
    public string PageSlug { get; set; } = "";
    public int Revision { get; set; }
    public string ChangeType { get; set; } = "";
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
}

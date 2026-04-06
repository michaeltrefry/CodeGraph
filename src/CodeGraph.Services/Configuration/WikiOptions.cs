namespace CodeGraph.Services.Configuration;

public class WikiOptions
{
    public int MaxAttachmentSizeMb { get; set; } = 10;
    public string AttachmentStoragePath { get; set; } = "uploads/wiki";
}

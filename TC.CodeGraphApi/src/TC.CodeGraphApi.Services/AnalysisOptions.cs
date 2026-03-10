namespace TC.CodeGraphApi.Services;

public class AnalysisOptions
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "claude-sonnet-4-6";
    public int MaxTokensPerAnalysis { get; set; } = 8192;
    public int MaxTokensPerSynthesis { get; set; } = 4096;
    public int MaxFileSizeKb { get; set; } = 512;
}

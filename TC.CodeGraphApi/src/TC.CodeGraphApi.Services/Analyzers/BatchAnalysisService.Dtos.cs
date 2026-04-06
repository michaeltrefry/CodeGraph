namespace TC.CodeGraphApi.Services.Analyzers;

public partial class BatchAnalysisService
{
    // --- Private DTOs for Anthropic Batch API ---

    private sealed class BatchRequest
    {
        public string CustomId { get; set; } = "";
        public BatchRequestParams Params { get; set; } = new();
    }

    private sealed class BatchRequestParams
    {
        public string Model { get; set; } = "";
        public int MaxTokens { get; set; } = 8192;
        public List<BatchMessage> Messages { get; set; } = [];
    }

    private sealed class BatchMessage
    {
        public string Role { get; set; } = "";
        public string Content { get; set; } = "";
    }

    private sealed class BatchCreatedResponse
    {
        public string Id { get; set; } = "";
        public string ProcessingStatus { get; set; } = "";
    }

    private sealed class BatchStatusResponse
    {
        public string Id { get; set; } = "";
        public string ProcessingStatus { get; set; } = "";
    }

    private sealed class BatchResultLine
    {
        public string CustomId { get; set; } = "";
        public BatchResult? Result { get; set; }
    }

    private sealed class BatchResult
    {
        public string Type { get; set; } = "";
        public BatchResultMessage? Message { get; set; }
    }

    private sealed class BatchResultMessage
    {
        public string Model { get; set; } = "";
        public List<BatchContentBlock> Content { get; set; } = [];
    }

    private sealed class BatchContentBlock
    {
        public string Type { get; set; } = "";
        public string? Text { get; set; }
    }
}

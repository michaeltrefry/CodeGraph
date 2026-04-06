using TC.CodeGraphApi.Models.Memory;
using TC.Common.TcServiceStack.Queue.Abstractions;

namespace TC.CodeGraphApi.Models.Messages;

[TcServiceBusEvent(TcQueueHosts.Enterprise)]
public class StoreMemory
{
    public required string Username { get; set; }
    public required MemoryExtractionResult Extraction { get; set; }
    public string Source { get; set; } = "api";
}

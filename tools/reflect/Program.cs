using System.Reflection;

var asm = typeof(Anthropic.AnthropicClient).Assembly;

// Find RawContentBlockDelta
var deltaType = asm.GetTypes().FirstOrDefault(t => t.Name == "RawContentBlockDelta");
Console.WriteLine($"RawContentBlockDelta: {deltaType?.FullName ?? "NOT FOUND"}");
if (deltaType != null)
{
    Console.WriteLine("Methods:");
    foreach (var m in deltaType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        Console.WriteLine($"  {m.Name}");
    Console.WriteLine("Properties:");
    foreach (var p in deltaType.GetProperties())
        Console.WriteLine($"  {p.PropertyType.Name} {p.Name}");
}

Console.WriteLine();

// Find RawMessageStreamEvent
var eventType = asm.GetTypes().FirstOrDefault(t => t.Name == "RawMessageStreamEvent");
Console.WriteLine($"RawMessageStreamEvent: {eventType?.FullName ?? "NOT FOUND"}");
if (eventType != null)
{
    Console.WriteLine("Methods:");
    foreach (var m in eventType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        Console.WriteLine($"  {m.Name}");
}

Console.WriteLine();

// Find InputSchema
var schemaType = asm.GetTypes().FirstOrDefault(t => t.Name == "InputSchema" && t.FullName!.Contains("Messages") && !t.FullName.Contains("Beta"));
Console.WriteLine($"InputSchema: {schemaType?.FullName ?? "NOT FOUND"}");
if (schemaType != null)
{
    Console.WriteLine("Properties:");
    foreach (var p in schemaType.GetProperties())
        Console.WriteLine($"  {p.PropertyType.Name} {p.Name}");
}

Console.WriteLine();

// Find ToolUseBlockParam
var tubpType = asm.GetTypes().FirstOrDefault(t => t.Name == "ToolUseBlockParam" && !t.FullName!.Contains("Beta"));
Console.WriteLine($"ToolUseBlockParam: {tubpType?.FullName ?? "NOT FOUND"}");
if (tubpType != null)
{
    Console.WriteLine("Properties:");
    foreach (var p in tubpType.GetProperties())
        Console.WriteLine($"  {p.PropertyType.Name} {p.Name}");
}

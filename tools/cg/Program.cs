using System.Collections;
using CodeGraph.Cg;

var environment = Environment.GetEnvironmentVariables()
    .Cast<DictionaryEntry>()
    .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value, StringComparer.Ordinal);
return await new CgApp(environment, Console.Out, Console.Error).RunAsync(args);

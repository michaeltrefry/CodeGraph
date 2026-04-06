using TC.Jarvis.Auth.Token;

namespace TC.CodeGraphJobs;

public class Scopes : IScopes
{
    public const string ServiceBusRepublish = "servicebus:republish";
    
    private static readonly IEnumerable<Scope> _scopes = new List<Scope>
    {
        new Scope(ServiceBusRepublish, nameof(ServiceBusRepublish))
    };

    public IEnumerable<Scope> ApiScopes => _scopes;
}
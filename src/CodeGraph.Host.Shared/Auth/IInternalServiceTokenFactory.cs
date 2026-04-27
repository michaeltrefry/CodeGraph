namespace CodeGraph.Host.Shared.Auth;

public interface IInternalServiceTokenFactory
{
    string CreateToken(string username, string audience);
}

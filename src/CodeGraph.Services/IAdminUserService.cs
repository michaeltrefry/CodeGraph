namespace CodeGraph.Services;

public interface IAdminUserService
{
    Task<IReadOnlyList<string>> ListAsync();
    Task<bool> IsAdminAsync(string username);
    Task<bool> AddAsync(string username);
    Task<bool> RemoveAsync(string username);
}

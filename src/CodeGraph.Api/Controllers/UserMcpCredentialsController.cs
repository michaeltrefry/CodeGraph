using System.Security.Cryptography;
using System.Text;
using CodeGraph.Api.Auth;
using CodeGraph.Data;
using CodeGraph.Mcp.Hub;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CodeGraph.Api.Controllers;

/// <summary>
/// Per-user delegated provider credentials. Each user connects their own credential (e.g. a
/// Shortcut API token); provider tool calls then run as that user rather than a hub-shared
/// secret — see Shortcut sc-1052.
/// </summary>
[ApiController]
[Authorize(Policy = CodeGraphAuthenticationDefaults.UserPolicy)]
[Route("api/user/mcp-credentials")]
public sealed class UserMcpCredentialsController(
    IMcpProviderCredentialStore credentialStore,
    McpHubService hub) : ControllerBase
{
    // Providers that support per-user delegated credentials, mapped to their single credential key.
    private static readonly IReadOnlyDictionary<string, string> DelegatedProviders =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["shortcut"] = McpHubService.ShortcutCredentialKey,
        };

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<McpProviderCredentialResponse>>> List(CancellationToken ct)
    {
        var credentials = await credentialStore.ListForUserAsync(GetNormalizedUsername(), ct);
        return Ok(credentials.Select(Map).ToList());
    }

    [HttpPut("{providerKey}/{credentialKey}")]
    public async Task<ActionResult<McpProviderCredentialWriteResult>> Upsert(
        string providerKey,
        string credentialKey,
        [FromBody] McpProviderCredentialWriteRequest request,
        CancellationToken ct)
    {
        if (!DelegatedProviders.TryGetValue(providerKey, out var expectedKey))
            return BadRequest($"Provider '{providerKey}' does not support per-user delegated credentials.");
        if (!string.Equals(credentialKey, expectedKey, StringComparison.OrdinalIgnoreCase))
            return BadRequest($"Provider '{providerKey}' expects credential key '{expectedKey}'.");
        if (string.IsNullOrWhiteSpace(request.Value))
            return BadRequest("A credential value is required.");

        var now = DateTime.UtcNow;
        var validation = await ValidateAsync(providerKey, request.Value, ct);

        await credentialStore.UpsertAsync(
            new McpProviderCredentialEntity
            {
                ProviderKey = providerKey,
                Username = GetNormalizedUsername(),
                CredentialKey = expectedKey,
                TokenFingerprint = Fingerprint(request.Value),
                ProviderIdentity = validation.ProviderIdentity,
                ValidationState = validation.IsValid ? "valid" : "invalid",
                ValidationMessage = validation.Message,
                LastValidatedAtUtc = validation.IsValid ? now : null,
                LastAttemptAtUtc = now,
            },
            request.Value,
            ct);

        return Ok(new McpProviderCredentialWriteResult(
            Stored: true,
            ValidationState: validation.IsValid ? "valid" : "invalid",
            ProviderIdentity: validation.ProviderIdentity,
            Message: validation.Message));
    }

    [HttpDelete("{providerKey}/{credentialKey}")]
    public async Task<ActionResult> Revoke(string providerKey, string credentialKey, CancellationToken ct) =>
        await credentialStore.DeleteAsync(providerKey, GetNormalizedUsername(), credentialKey, ct)
            ? NoContent()
            : NotFound();

    private async Task<DelegatedCredentialValidationResult> ValidateAsync(
        string providerKey,
        string value,
        CancellationToken ct) =>
        string.Equals(providerKey, "shortcut", StringComparison.OrdinalIgnoreCase)
            ? await hub.ValidateShortcutCredentialAsync(value, ct)
            : new DelegatedCredentialValidationResult(false, null, "Validation is not supported for this provider.");

    private static McpProviderCredentialResponse Map(McpProviderCredentialEntity entity) =>
        new(
            entity.ProviderKey,
            entity.CredentialKey,
            !string.IsNullOrWhiteSpace(entity.EncryptedValue),
            entity.TokenFingerprint,
            entity.ProviderIdentity,
            entity.ValidationState,
            entity.ValidationMessage,
            entity.LastValidatedAtUtc,
            entity.LastAttemptAtUtc,
            entity.ExpiresAtUtc,
            entity.UpdatedAtUtc);

    // Non-reversible short fingerprint so the UI can show "which token is connected" without
    // ever exposing the secret.
    private static string Fingerprint(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    private string GetNormalizedUsername() =>
        (User.GetUsername() ?? Request.Headers["X-CodeGraph-User"].FirstOrDefault() ?? "anonymous")
        .Trim()
        .ToLowerInvariant();
}

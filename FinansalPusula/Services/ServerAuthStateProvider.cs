using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Components.Authorization;

namespace FinansalPusula.Services;

public sealed class ServerAuthStateProvider(HttpClient httpClient) : AuthenticationStateProvider
{
    private static readonly ClaimsPrincipal AnonymousUser = new(new ClaimsIdentity());

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var principal = await LoadPrincipalAsync();
        return new AuthenticationState(principal);
    }

    public void NotifyStateChanged()
    {
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }

    private async Task<ClaimsPrincipal> LoadPrincipalAsync()
    {
        HttpResponseMessage response;
        try
        {
            response = await httpClient.GetAsync("bff/user");
        }
        catch
        {
            return AnonymousUser;
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return AnonymousUser;
        }

        if (!response.IsSuccessStatusCode)
        {
            return AnonymousUser;
        }

        var payload = await response.Content.ReadFromJsonAsync<AuthUserResponse>();
        if (payload is null || payload.IsAuthenticated is not true || payload.Claims is null || payload.Claims.Count == 0)
        {
            return AnonymousUser;
        }

        var claims = payload.Claims
            .Where(c => !string.IsNullOrWhiteSpace(c.Type) && !string.IsNullOrWhiteSpace(c.Value))
            .Select(c => new Claim(c.Type, c.Value!));

        var identity = new ClaimsIdentity(claims, authenticationType: "ServerCookie");
        return new ClaimsPrincipal(identity);
    }

    private sealed class AuthUserResponse
    {
        [JsonPropertyName("isAuthenticated")]
        public bool IsAuthenticated { get; set; }

        [JsonPropertyName("claims")]
        public List<AuthClaim>? Claims { get; set; }
    }

    private sealed class AuthClaim
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("value")]
        public string? Value { get; set; }
    }
}

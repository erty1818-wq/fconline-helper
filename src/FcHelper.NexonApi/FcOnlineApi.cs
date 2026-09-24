using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using FcHelper.Core.Models;

namespace FcHelper.NexonApi;

public interface IFcOnlineApi
{
    /// <returns>The ouid, or null when no user has that nickname.</returns>
    Task<string?> GetOuidAsync(string nickname, CancellationToken ct = default);
    Task<UserBasic> GetUserBasicAsync(string ouid, CancellationToken ct = default);
    Task<IReadOnlyList<MaxDivision>> GetMaxDivisionAsync(string ouid, CancellationToken ct = default);
    /// <returns>Match IDs, newest first.</returns>
    Task<IReadOnlyList<string>> GetUserMatchIdsAsync(string ouid, int matchType, int offset, int limit, CancellationToken ct = default);
    /// <summary>Raw JSON is returned so it can be cached verbatim and re-analysed later without new calls.</summary>
    Task<string> GetMatchDetailJsonAsync(string matchId, CancellationToken ct = default);
    Task<string> GetMetadataJsonAsync(string name, CancellationToken ct = default);
}

public sealed class NexonApiException(HttpStatusCode status, string errorName, string message)
    : Exception($"NEXON Open API {(int)status} {errorName}: {message}")
{
    public HttpStatusCode Status { get; } = status;
    public string ErrorName { get; } = errorName;

    public bool IsRateLimited => Status == HttpStatusCode.TooManyRequests;
    public bool IsAuthError => Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
    public bool IsBadRequest => Status == HttpStatusCode.BadRequest;
}

/// <summary>Client for https://open.api.nexon.com/fconline/v1/. Every call goes through the shared <see cref="RateLimiter"/>.</summary>
public sealed class FcOnlineApi : IFcOnlineApi
{
    public static readonly Uri DefaultBaseAddress = new("https://open.api.nexon.com/");
    private const string ApiPrefix = "fconline/v1/";
    private const string MetaPrefix = "static/fconline/meta/";
    private const int MaxAttempts = 4;

    private static readonly HashSet<string> MetadataNames =
        ["matchtype", "spid", "seasonid", "spposition", "division", "division_volta"];

    private readonly HttpClient _http;
    private readonly RateLimiter _limiter;
    private readonly string _apiKey;
    private readonly TimeProvider _time;
    private readonly TimeSpan _retryBaseDelay;

    /// <param name="retryBaseDelay">First backoff after a 429/5xx; doubles on each retry. Defaults to 1 second.</param>
    public FcOnlineApi(HttpClient http, string apiKey, RateLimiter limiter, TimeProvider? time = null, TimeSpan? retryBaseDelay = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("API key is required.", nameof(apiKey));
        _http = http;
        _http.BaseAddress ??= DefaultBaseAddress;
        _apiKey = apiKey.Trim();
        _limiter = limiter;
        _time = time ?? TimeProvider.System;
        _retryBaseDelay = retryBaseDelay ?? TimeSpan.FromSeconds(1);
    }

    public async Task<string?> GetOuidAsync(string nickname, CancellationToken ct = default)
    {
        try
        {
            var res = await GetAsync($"{ApiPrefix}id?nickname={Uri.EscapeDataString(nickname.Trim())}", FcJsonContext.Default.OuidResponse, ct);
            return string.IsNullOrEmpty(res.Ouid) ? null : res.Ouid;
        }
        catch (NexonApiException e) when (e.IsBadRequest || e.Status == HttpStatusCode.NotFound)
        {
            // The API answers an unknown nickname with a 400-class error rather than an empty body.
            return null;
        }
    }

    public Task<UserBasic> GetUserBasicAsync(string ouid, CancellationToken ct = default) =>
        GetAsync($"{ApiPrefix}user/basic?ouid={Uri.EscapeDataString(ouid)}", FcJsonContext.Default.UserBasic, ct);

    public async Task<IReadOnlyList<MaxDivision>> GetMaxDivisionAsync(string ouid, CancellationToken ct = default) =>
        await GetAsync($"{ApiPrefix}user/maxdivision?ouid={Uri.EscapeDataString(ouid)}", FcJsonContext.Default.ListMaxDivision, ct);

    public async Task<IReadOnlyList<string>> GetUserMatchIdsAsync(string ouid, int matchType, int offset, int limit, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 100);
        return await GetAsync(
            $"{ApiPrefix}user/match?ouid={Uri.EscapeDataString(ouid)}&matchtype={matchType}&offset={offset}&limit={limit}",
            FcJsonContext.Default.ListString, ct);
    }

    public Task<string> GetMatchDetailJsonAsync(string matchId, CancellationToken ct = default) =>
        GetStringAsync($"{ApiPrefix}match-detail?matchid={Uri.EscapeDataString(matchId)}", authenticated: true, ct);

    public Task<string> GetMetadataJsonAsync(string name, CancellationToken ct = default)
    {
        if (!MetadataNames.Contains(name)) throw new ArgumentException($"Unknown metadata '{name}'.", nameof(name));
        return GetStringAsync($"{MetaPrefix}{name}.json", authenticated: false, ct);
    }

    private async Task<T> GetAsync<T>(string path, JsonTypeInfo<T> type, CancellationToken ct)
    {
        var json = await GetStringAsync(path, authenticated: true, ct);
        return JsonSerializer.Deserialize(json, type)
            ?? throw new NexonApiException(HttpStatusCode.OK, "EMPTY", $"Empty response from {path}");
    }

    private async Task<string> GetStringAsync(string path, bool authenticated, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            if (authenticated) await _limiter.WaitAsync(ct);

            using var req = new HttpRequestMessage(HttpMethod.Get, path);
            if (authenticated) req.Headers.Add("x-nxopen-api-key", _apiKey);

            using var res = await _http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (res.IsSuccessStatusCode) return body;

            var error = ParseError(res.StatusCode, body);
            var retryable = res.StatusCode == HttpStatusCode.TooManyRequests || (int)res.StatusCode >= 500;
            if (!retryable || attempt >= MaxAttempts) throw error;

            // Exponential backoff: 1s, 2s, 4s by default.
            await Task.Delay(_retryBaseDelay * Math.Pow(2, attempt - 1), _time, ct);
        }
    }

    private static NexonApiException ParseError(HttpStatusCode status, string body)
    {
        try
        {
            var err = JsonSerializer.Deserialize(body, FcJsonContext.Default.NexonErrorResponse)?.Error;
            if (err is not null) return new NexonApiException(status, err.Name, err.Message);
        }
        catch (JsonException)
        {
        }
        return new NexonApiException(status, "HTTP", body.Length > 200 ? body[..200] : body);
    }
}

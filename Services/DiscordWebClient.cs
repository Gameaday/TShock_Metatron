using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace Metatron;

/// <summary>
/// A lightweight, rate-limit-aware wrapper for the Discord REST API.
/// </summary>
public class DiscordWebClient
{
    private readonly HttpClient _http;
    public readonly string ApiBase = "https://discord.com/api/v10";
    
    // The Rate Limiter Lock
    private readonly SemaphoreSlim _rateLimitLock = new(1, 1);
    private DateTime _globalPauseUntil = DateTime.MinValue;

    public DiscordWebClient(string botToken)
    {
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", botToken);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DiscordBot (Metatron, v3.0)");
    }

    public async Task<JsonElement?> GetAsync(string endpoint) => await SendWithRetryAsync(HttpMethod.Get, endpoint);
    
    public async Task<JsonElement?> PostAsync(string endpoint, object payload) => await SendWithRetryAsync(HttpMethod.Post, endpoint, payload);
    
    public async Task<JsonElement?> PatchAsync(string endpoint, object payload) => await SendWithRetryAsync(HttpMethod.Patch, endpoint, payload);
    
    public async Task DeleteAsync(string endpoint) => await SendWithRetryAsync(HttpMethod.Delete, endpoint);

    /// <summary>
    /// The Core Engine: Handles queuing, exponential backoff, and 429 interception.
    /// </summary>
    private async Task<JsonElement?> SendWithRetryAsync(HttpMethod method, string endpoint, object? payload = null)
    {
        await _rateLimitLock.WaitAsync();
        try
        {
            // 1. Respect active rate limits
            if (DateTime.UtcNow < _globalPauseUntil)
            {
                await Task.Delay(_globalPauseUntil - DateTime.UtcNow);
            }

            int retries = 0;
            while (retries < 3)
            {
                using var request = new HttpRequestMessage(method, $"{ApiBase}{endpoint}");
                if (payload != null)
                {
                    request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                }

                var response = await _http.SendAsync(request);

                // 2. Handle '429 Too Many Requests' natively
                if ((int)response.StatusCode == 429)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(content);
                    if (doc.RootElement.TryGetProperty("retry_after", out var retryAfterProp))
                    {
                        // Discord tells us exactly how many seconds to wait
                        double waitSeconds = retryAfterProp.GetDouble();
                        _globalPauseUntil = DateTime.UtcNow.AddSeconds(waitSeconds + 0.5); // Add 500ms buffer
                        await Task.Delay(TimeSpan.FromSeconds(waitSeconds + 0.5));
                        retries++;
                        continue; 
                    }
                }

                // 3. Prevent rapid-fire burst limits (Standard 50ms global delay)
                await Task.Delay(50);

                if (!response.IsSuccessStatusCode) return null;
                
                if (method == HttpMethod.Delete) return null; // Deletes don't return JSON

                var successContent = await response.Content.ReadAsStringAsync();
                using var successDoc = JsonDocument.Parse(successContent);
                return successDoc.RootElement.Clone();
            }
            return null;
        }
        finally { _rateLimitLock.Release(); }
    }
}

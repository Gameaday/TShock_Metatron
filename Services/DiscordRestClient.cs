using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

// We use a custom namespace so it mimics Discord.Net perfectly
namespace Metatron.DiscordStub;

public enum TokenType { Bot }
public enum Direction { Before, After, Around }

/// <summary>
/// A lightweight, drop-in replacement for DiscordRestClient.
/// </summary>
public class DiscordRestClient
{
    internal readonly HttpClient Http;
    internal readonly string ApiBase = "https://discord.com/api/v10";
    private readonly SemaphoreSlim _rateLimitLock = new(1, 1);
    private DateTime _globalPauseUntil = DateTime.MinValue;

    public RestUser CurrentUser { get; private set; } = null!;

    public DiscordRestClient()
    {
        Http = new HttpClient();
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("DiscordBot (Metatron, v3.0)");
    }

    public async Task LoginAsync(TokenType type, string token)
    {
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(type.ToString(), token);
        var meJson = await GetJsonAsync("/users/@me");
        if (meJson != null) CurrentUser = new RestUser(this, meJson.Value);
    }

    public async Task<RestMessageChannel?> GetChannelAsync(ulong id)
    {
        var json = await GetJsonAsync($"/channels/{id}");
        return json != null ? new RestMessageChannel(this, id) : null;
    }

    public async Task<RestGuild?> GetGuildAsync(ulong id)
    {
        var json = await GetJsonAsync($"/guilds/{id}");
        return json != null ? new RestGuild(this, id) : null;
    }

    public async Task<RestUser?> GetUserAsync(ulong id)
    {
        var json = await GetJsonAsync($"/users/{id}");
        return json != null ? new RestUser(this, json.Value) : null;
    }

    // --- INTERNAL RATE-LIMITED ENGINE ---
    internal async Task<JsonElement?> GetJsonAsync(string endpoint, bool throwOnFailure = false, bool returnNullOnNotFound = false)
        => await SendAsync(HttpMethod.Get, endpoint, null, throwOnFailure, returnNullOnNotFound);
    internal async Task<JsonElement?> PostJsonAsync(string endpoint, object payload) => await SendAsync(HttpMethod.Post, endpoint, payload);
    internal async Task<JsonElement?> PatchJsonAsync(string endpoint, object payload) => await SendAsync(HttpMethod.Patch, endpoint, payload);
    internal async Task DeleteAsync(string endpoint) => await SendAsync(HttpMethod.Delete, endpoint);

    private async Task<JsonElement?> SendAsync(
        HttpMethod method,
        string endpoint,
        object? payload = null,
        bool throwOnFailure = false,
        bool returnNullOnNotFound = false)
    {
        await _rateLimitLock.WaitAsync();
        try
        {
            if (DateTime.UtcNow < _globalPauseUntil) await Task.Delay(_globalPauseUntil - DateTime.UtcNow);

            int retries = 0;
            while (retries < 3)
            {
                using var request = new HttpRequestMessage(method, $"{ApiBase}{endpoint}");
                if (payload != null) request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                var response = await Http.SendAsync(request);

                if ((int)response.StatusCode == 429)
                {
                    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    if (doc.RootElement.TryGetProperty("retry_after", out var retryAfter))
                    {
                        double wait = retryAfter.GetDouble();
                        _globalPauseUntil = DateTime.UtcNow.AddSeconds(wait + 0.5);
                        await Task.Delay(TimeSpan.FromSeconds(wait + 0.5));
                        retries++;
                        continue; 
                    }
                }

                await Task.Delay(50); // Burst limit protection
                if (!response.IsSuccessStatusCode || method == HttpMethod.Delete)
                {
                    if (returnNullOnNotFound && response.StatusCode == HttpStatusCode.NotFound)
                        return null;

                    if (throwOnFailure)
                        throw new HttpRequestException(
                            $"Discord API request failed for {endpoint} with status {(int)response.StatusCode}.",
                            null,
                            response.StatusCode);

                    return null;
                }
                
                using var successDoc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                return successDoc.RootElement.Clone();
            }
            if (throwOnFailure)
                throw new HttpRequestException($"Discord API request failed after retries for {endpoint}.");
            return null;
        }
        finally { _rateLimitLock.Release(); }
    }
}

// --- FACADE OBJECTS ---
public class RestMessageChannel
{
    private readonly DiscordRestClient _client;
    public ulong Id { get; }

    internal RestMessageChannel(DiscordRestClient client, ulong id) { _client = client; Id = id; }

    public async Task<RestMessage?> SendMessageAsync(string text, object? allowedMentions = null)
    {
        object payload = allowedMentions != null
            ? new { content = text, allowed_mentions = allowedMentions }
            : new { content = text };
        var json = await _client.PostJsonAsync($"/channels/{Id}/messages", payload);
        return json != null ? new RestMessage(_client, json.Value) : null;
    }

    public async Task<List<RestMessage>> GetMessagesAsync(int limit)
    {
        var json = await _client.GetJsonAsync($"/channels/{Id}/messages?limit={limit}");
        return ParseMessageArray(json);
    }

    public async Task<List<RestMessage>> GetMessagesAsync(ulong fromMessageId, Direction dir, int limit)
    {
        string dirStr = dir.ToString().ToLower();
        var json = await _client.GetJsonAsync($"/channels/{Id}/messages?{dirStr}={fromMessageId}&limit={limit}");
        return ParseMessageArray(json);
    }

    public async Task<List<RestMessage>> GetPinnedMessagesAsync()
    {
        var json = await _client.GetJsonAsync($"/channels/{Id}/pins");
        return ParseMessageArray(json);
    }

    private List<RestMessage> ParseMessageArray(JsonElement? json)
    {
        var list = new List<RestMessage>();
        if (json != null && json.Value.ValueKind == JsonValueKind.Array)
            foreach (var element in json.Value.EnumerateArray()) list.Add(new RestMessage(_client, element));
        return list;
    }
}

public class RestMessage
{
    private readonly DiscordRestClient _client;
    public ulong Id { get; }
    public ulong ChannelId { get; }
    public string Content { get; }
    public RestUser Author { get; }
    public DateTimeOffset Timestamp { get; }

    internal RestMessage(DiscordRestClient client, JsonElement element)
    {
        _client = client;
        Id = ulong.Parse(element.GetProperty("id").GetString()!);
        ChannelId = ulong.Parse(element.GetProperty("channel_id").GetString()!);
        Content = element.GetProperty("content").GetString() ?? "";
        Author = new RestUser(client, element.GetProperty("author"));
        
        if (element.TryGetProperty("timestamp", out var ts)) Timestamp = DateTimeOffset.Parse(ts.GetString()!);
    }

    public async Task DeleteAsync() => await _client.DeleteAsync($"/channels/{ChannelId}/messages/{Id}");
    
    public async Task ModifyAsync(string newContent) => await _client.PatchJsonAsync($"/channels/{ChannelId}/messages/{Id}", new { content = newContent });

    public async Task PinAsync() => await _client.Http.PutAsync($"{_client.ApiBase}/channels/{ChannelId}/pins/{Id}", null);
}

public class RestUser
{
    private readonly DiscordRestClient _client;
    public ulong Id { get; }
    public string Username { get; }
    public string GlobalName { get; }
    public bool IsBot { get; }

    internal RestUser(DiscordRestClient client, JsonElement element)
    {
        _client = client;
        Id = ulong.Parse(element.GetProperty("id").GetString()!);
        Username = element.GetProperty("username").GetString() ?? "Unknown";
        GlobalName = element.TryGetProperty("global_name", out var gn) && gn.ValueKind == JsonValueKind.String ? gn.GetString()! : Username;
        IsBot = element.TryGetProperty("bot", out var bot) && bot.GetBoolean();
    }

    public async Task<RestMessageChannel?> CreateDMChannelAsync()
    {
        var json = await _client.PostJsonAsync("/users/@me/channels", new { recipient_id = Id.ToString() });
        return json != null ? new RestMessageChannel(_client, ulong.Parse(json.Value.GetProperty("id").GetString()!)) : null;
    }
}

public class RestGuild
{
    private readonly DiscordRestClient _client;
    public ulong Id { get; }

    internal RestGuild(DiscordRestClient client, ulong id) { _client = client; Id = id; }

    public async Task<RestGuildUser?> GetUserAsync(ulong userId)
    {
        var json = await _client.GetJsonAsync($"/guilds/{Id}/members/{userId}", throwOnFailure: true, returnNullOnNotFound: true);
        if (json == null) return null;
        
        var userElement = json.Value.GetProperty("user");
        var roleIds = json.Value.GetProperty("roles").EnumerateArray().Select(r => ulong.Parse(r.GetString()!)).ToList();
        return new RestGuildUser(_client, userElement, roleIds);
    }
}

public class RestGuildUser : RestUser
{
    public List<ulong> RoleIds { get; }

    internal RestGuildUser(DiscordRestClient client, JsonElement element, List<ulong> roleIds) : base(client, element)
    {
        RoleIds = roleIds;
    }
}

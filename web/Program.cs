using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(new HttpClient());

var app = builder.Build();

var appPin = GetRequiredEnv("APP_PIN");
var appName = GetRequiredEnv("APP_NAME");
var freeDailyLimit = GetRequiredIntEnv("FREE_DAILY_LIMIT");
var cooldownSeconds = GetRequiredIntEnv("COOLDOWN_SECONDS");
var maxInputChars = GetRequiredIntEnv("MAX_INPUT_CHARS");
var maxContextMessages = GetRequiredIntEnv("MAX_CONTEXT_MESSAGES");
var llmAdapterUrl = GetRequiredEnv("LLM_ADAPTER_URL");
var sessionTtlHours = GetRequiredIntEnv("SESSION_TTL_HOURS");
var systemPromptTemplate = Environment.GetEnvironmentVariable("SYSTEM_PROMPT");
var tzName = Environment.GetEnvironmentVariable("TZ") ?? "UTC";

var sessionTtl = TimeSpan.FromHours(sessionTtlHours);
var cooldown = TimeSpan.FromSeconds(cooldownSeconds);
var usageStore = new UsageStore(Path.Combine(app.Environment.ContentRootPath, "data", "usage.json"));

var sessions = new ConcurrentDictionary<string, SessionInfo>();

app.UseStaticFiles();

app.MapGet("/", () => Results.Redirect("/login"));

app.MapGet("/login", () =>
    Results.Text(RenderHtmlTemplate(app.Environment.WebRootPath, "login.html", appName), "text/html"));

app.MapPost("/login", async (HttpRequest request, HttpResponse response) =>
{
    var form = await request.ReadFormAsync();
    var pin = form["pin"].ToString();
    var now = DateTime.UtcNow;

    var authState = await usageStore.GetAuthStateAsync();
    if (authState.LockedUntilUtc.HasValue && authState.LockedUntilUtc.Value > now)
    {
        response.StatusCode = StatusCodes.Status403Forbidden;
        await response.WriteAsync("ロック中です。しばらく待ってください。");
        return;
    }

    if (!string.Equals(pin, appPin, StringComparison.Ordinal))
    {
        await usageStore.RecordFailedAuthAsync(now);
        response.StatusCode = StatusCodes.Status401Unauthorized;
        await response.WriteAsync("PINが違います。");
        return;
    }

    await usageStore.ResetAuthFailuresAsync();
    var sid = GenerateSessionId();
    sessions[sid] = new SessionInfo(now);

    response.Cookies.Append("sid", sid, new CookieOptions
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Secure = request.IsHttps,
        Expires = now.Add(sessionTtl)
    });

    response.Redirect("/chat");
});

app.MapGet("/chat", (HttpRequest request) =>
{
    if (!TryGetSession(request, sessions, sessionTtl, out _))
    {
        return Results.Redirect("/login");
    }

    return Results.Text(RenderHtmlTemplate(app.Environment.WebRootPath, "chat.html", appName), "text/html");
});

app.MapPost("/api/chat", async (HttpRequest request, HttpResponse response, HttpClient httpClient) =>
{
    if (!TryGetSession(request, sessions, sessionTtl, out var session))
    {
        response.StatusCode = StatusCodes.Status403Forbidden;
        await response.WriteAsJsonAsync(new { error = "unauthorized" });
        return;
    }

    var payload = await JsonSerializer.DeserializeAsync<ChatRequest>(request.Body);
    if (payload is null || string.IsNullOrWhiteSpace(payload.Input))
    {
        response.StatusCode = StatusCodes.Status400BadRequest;
        await response.WriteAsJsonAsync(new { error = "empty" });
        return;
    }

    if (payload.Input.Length > maxInputChars)
    {
        response.StatusCode = StatusCodes.Status400BadRequest;
        await response.WriteAsJsonAsync(new { error = "too_long" });
        return;
    }

    var now = DateTime.UtcNow;
    if (session.LastSentUtc.HasValue && now - session.LastSentUtc.Value < cooldown)
    {
        response.StatusCode = StatusCodes.Status429TooManyRequests;
        await response.WriteAsJsonAsync(new { error = "cooldown" });
        return;
    }

    var dailyCount = await usageStore.GetDailyCountAsync(now);
    if (dailyCount >= freeDailyLimit)
    {
        response.StatusCode = StatusCodes.Status429TooManyRequests;
        await response.WriteAsJsonAsync(new { error = "daily_limit" });
        return;
    }

    session.LastSentUtc = now;

    List<ChatMessage> contextMessages;
    lock (session.SyncRoot)
    {
        session.Messages.Add(new ChatMessage("user", payload.Input));
        TrimMessages(session.Messages, maxContextMessages);
        contextMessages = session.Messages.ToList();
        session.LastActiveUtc = now;
    }

    var systemPrompt = BuildSystemPrompt(systemPromptTemplate, appName, tzName, DateTime.Now);
    var llmMessages = BuildLlmMessages(contextMessages, systemPrompt);
    var llmRequest = new LlmRequest
    {
        Messages = llmMessages,
        MaxTokens = 600
    };

    HttpResponseMessage llmResponse;
    try
    {
        llmResponse = await httpClient.PostAsJsonAsync(llmAdapterUrl, llmRequest);
    }
    catch
    {
        response.StatusCode = StatusCodes.Status502BadGateway;
        await response.WriteAsJsonAsync(new { error = "llm_unreachable" });
        return;
    }

    if (!llmResponse.IsSuccessStatusCode)
    {
        response.StatusCode = StatusCodes.Status502BadGateway;
        await response.WriteAsJsonAsync(new { error = "llm_error" });
        return;
    }

    var llmResult = await llmResponse.Content.ReadFromJsonAsync<LlmResponse>();
    if (llmResult is null || (!string.IsNullOrWhiteSpace(llmResult.ErrorType) && string.IsNullOrWhiteSpace(llmResult.Text)))
    {
        response.StatusCode = StatusCodes.Status502BadGateway;
        await response.WriteAsJsonAsync(new { error = llmResult?.ErrorType ?? "llm_failed" });
        return;
    }

    if (string.IsNullOrWhiteSpace(llmResult.Text))
    {
        response.StatusCode = StatusCodes.Status502BadGateway;
        await response.WriteAsJsonAsync(new { error = "llm_invalid" });
        return;
    }

    lock (session.SyncRoot)
    {
        session.Messages.Add(new ChatMessage("assistant", llmResult.Text));
        TrimMessages(session.Messages, maxContextMessages);
        session.LastActiveUtc = now;
    }

    await usageStore.IncrementDailyCountAsync(now);

    await response.WriteAsJsonAsync(new { text = llmResult.Text, model_used = llmResult.ModelUsed ?? string.Empty });
});

app.MapPost("/api/lock", (HttpRequest request, HttpResponse response) =>
{
    if (request.Cookies.TryGetValue("sid", out var sid))
    {
        sessions.TryRemove(sid, out _);
    }

    response.Cookies.Delete("sid");
    response.Redirect("/login");
});

app.Run();

static void TrimMessages(List<ChatMessage> messages, int maxCount)
{
    while (messages.Count > maxCount)
    {
        messages.RemoveAt(0);
    }
}

static string GenerateSessionId()
{
    var bytes = RandomNumberGenerator.GetBytes(32);
    return Convert.ToHexString(bytes);
}

static bool TryGetSession(HttpRequest request, ConcurrentDictionary<string, SessionInfo> sessions, TimeSpan ttl, out SessionInfo session)
{
    session = null!;
    if (!request.Cookies.TryGetValue("sid", out var sid))
    {
        return false;
    }

    CleanupSessions(sessions, ttl);

    if (!sessions.TryGetValue(sid, out session))
    {
        return false;
    }

    if (DateTime.UtcNow - session.LastActiveUtc > ttl)
    {
        sessions.TryRemove(sid, out _);
        return false;
    }

    session.LastActiveUtc = DateTime.UtcNow;
    return true;
}

static void CleanupSessions(ConcurrentDictionary<string, SessionInfo> sessions, TimeSpan ttl)
{
    var now = DateTime.UtcNow;
    foreach (var item in sessions)
    {
        if (now - item.Value.LastActiveUtc > ttl)
        {
            sessions.TryRemove(item.Key, out _);
        }
    }
}

static string GetRequiredEnv(string name)
{
    var value = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException($"Missing env: {name}");
    }

    return value;
}

static int GetRequiredIntEnv(string name)
{
    var value = GetRequiredEnv(name);
    if (!int.TryParse(value, out var parsed))
    {
        throw new InvalidOperationException($"Invalid int env: {name}");
    }

    return parsed;
}

static string RenderHtmlTemplate(string webRootPath, string fileName, string appName)
{
    var path = Path.Combine(webRootPath, fileName);
    var template = File.ReadAllText(path);
    var safeAppName = WebUtility.HtmlEncode(appName);
    return template.Replace("{{APP_NAME}}", safeAppName, StringComparison.Ordinal);
}

static string? BuildSystemPrompt(string? template, string appName, string tzName, DateTime nowLocal)
{
    if (string.IsNullOrWhiteSpace(template))
    {
        return null;
    }

    var resolvedAppName = string.IsNullOrWhiteSpace(appName) ? "talk-with-pucha" : appName;
    var resolvedTz = string.IsNullOrWhiteSpace(tzName) ? "UTC" : tzName;
    var date = nowLocal.ToString("yyyy-MM-dd");

    return template
        .Replace("{APP_NAME}", resolvedAppName, StringComparison.Ordinal)
        .Replace("{DATE}", date, StringComparison.Ordinal)
        .Replace("{TZ}", resolvedTz, StringComparison.Ordinal);
}

static List<ChatMessage> BuildLlmMessages(List<ChatMessage> contextMessages, string? systemPrompt)
{
    if (string.IsNullOrWhiteSpace(systemPrompt))
    {
        return contextMessages;
    }

    var hasSystem = contextMessages.Any(message =>
        string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase));
    if (hasSystem)
    {
        return contextMessages;
    }

    var messages = new List<ChatMessage>(contextMessages.Count + 1)
    {
        new("system", systemPrompt)
    };
    messages.AddRange(contextMessages);
    return messages;
}

record ChatRequest([property: JsonPropertyName("input")] string Input);

record ChatMessage([property: JsonPropertyName("role")] string Role, [property: JsonPropertyName("content")] string Content);

class LlmRequest
{
    [JsonPropertyName("messages")]
    public List<ChatMessage> Messages { get; init; } = new();

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; init; }
}

class LlmResponse
{
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("model_used")]
    public string? ModelUsed { get; init; }

    [JsonPropertyName("error_type")]
    public string? ErrorType { get; init; }
}

class SessionInfo
{
    public SessionInfo(DateTime nowUtc)
    {
        LastActiveUtc = nowUtc;
    }

    public DateTime LastActiveUtc { get; set; }

    public DateTime? LastSentUtc { get; set; }

    public List<ChatMessage> Messages { get; } = new();

    public object SyncRoot { get; } = new();
}

class UsageStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public UsageStore(string path)
    {
        _path = path;
    }

    public async Task<AuthState> GetAuthStateAsync()
    {
        var data = await LoadAsync();
        return new AuthState
        {
            Failed = data.Auth.Failed,
            LockedUntilUtc = ParseLockedUntil(data.Auth.LockedUntil)
        };
    }

    public async Task RecordFailedAuthAsync(DateTime nowUtc)
    {
        await _lock.WaitAsync();
        try
        {
            var data = await LoadAsyncInternal();
            data.Auth.Failed += 1;
            if (data.Auth.Failed >= 5)
            {
                data.Auth.LockedUntil = nowUtc.AddMinutes(10).ToString("O");
            }

            await SaveAsyncInternal(data);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ResetAuthFailuresAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var data = await LoadAsyncInternal();
            data.Auth.Failed = 0;
            data.Auth.LockedUntil = null;
            await SaveAsyncInternal(data);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<int> GetDailyCountAsync(DateTime nowUtc)
    {
        var data = await LoadAsync();
        var key = nowUtc.ToString("yyyy-MM-dd");
        if (data.Daily.TryGetValue(key, out var count))
        {
            return count;
        }

        return 0;
    }

    public async Task IncrementDailyCountAsync(DateTime nowUtc)
    {
        await _lock.WaitAsync();
        try
        {
            var data = await LoadAsyncInternal();
            var key = nowUtc.ToString("yyyy-MM-dd");
            data.Daily[key] = data.Daily.TryGetValue(key, out var current) ? current + 1 : 1;
            await SaveAsyncInternal(data);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<UsageData> LoadAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return await LoadAsyncInternal();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<UsageData> LoadAsyncInternal()
    {
        try
        {
            if (!File.Exists(_path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var fresh = new UsageData();
                await SaveAsyncInternal(fresh);
                return fresh;
            }

            var json = await File.ReadAllTextAsync(_path);
            if (string.IsNullOrWhiteSpace(json))
            {
                var fresh = new UsageData();
                await SaveAsyncInternal(fresh);
                return fresh;
            }

            var data = JsonSerializer.Deserialize<UsageData>(json, _jsonOptions);
            if (data is null)
            {
                var fresh = new UsageData();
                await SaveAsyncInternal(fresh);
                return fresh;
            }

            return data;
        }
        catch
        {
            var fresh = new UsageData();
            await SaveAsyncInternal(fresh);
            return fresh;
        }
    }

    private async Task SaveAsyncInternal(UsageData data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var json = JsonSerializer.Serialize(data, _jsonOptions);
        await File.WriteAllTextAsync(_path, json);
    }

    private static DateTime? ParseLockedUntil(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return parsed;
        }

        return null;
    }
}

class UsageData
{
    [JsonPropertyName("daily")]
    public Dictionary<string, int> Daily { get; set; } = new();

    [JsonPropertyName("auth")]
    public AuthData Auth { get; set; } = new();
}

class AuthData
{
    [JsonPropertyName("failed")]
    public int Failed { get; set; }

    [JsonPropertyName("locked_until")]
    public string? LockedUntil { get; set; }
}

class AuthState
{
    public int Failed { get; set; }
    public DateTime? LockedUntilUtc { get; set; }
}

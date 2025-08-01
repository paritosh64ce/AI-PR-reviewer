using System.Net.Http.Headers;
using System.Text.Json;
using System.Text;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net;

public class GitHubWebhookFunction
{
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
    private readonly string _githubToken;
    private readonly string _openAiEndpoint;
    private readonly string _openAiKey;

    public GitHubWebhookFunction(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<GitHubWebhookFunction>();
        _httpClient = new HttpClient();
        _githubToken = Environment.GetEnvironmentVariable("GitHubToken") ?? "";
        _openAiEndpoint = Environment.GetEnvironmentVariable("OpenAIEndpoint") ?? "";
        _openAiKey = Environment.GetEnvironmentVariable("OpenAIKey") ?? "";
    }

    [Function("GitHubWebhook")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequestData req)
    {
        try
        {
            _logger.LogWarning("Step 1: Start function");
            var requestBody = await ReadRequestBodyAsync(req);
            if (!IsValidPayload(requestBody))
            {
                _logger.LogWarning("Invalid request body: '{body}'", requestBody);
                return await CreateResponseAsync(req, HttpStatusCode.BadRequest, "Request body is not valid JSON.");
            }

            var root = JsonDocument.Parse(requestBody).RootElement;
            if (!IsSupportedAction(root))
            {
                return await CreateResponseAsync(req, HttpStatusCode.OK, "Ignored event");
            }

            var prNumber = root.GetProperty("pull_request").GetProperty("number").GetInt32();
            var repoName = root.GetProperty("repository").GetProperty("name").GetString() ?? string.Empty;
            var owner = root.GetProperty("repository").GetProperty("owner").GetProperty("login").GetString() ?? string.Empty;
            _logger.LogInformation("Processing PR #{prNumber} in {repoName}/{owner}", prNumber, repoName, owner);

            var filesUrl = $"https://api.github.com/repos/{owner}/{repoName}/pulls/{prNumber}/files";
            var pr = root.GetProperty("pull_request");
            var (codeDiff, fullFiles) = await FetchPrFilesAndContents(filesUrl, owner, repoName, pr);

            if (string.IsNullOrWhiteSpace(codeDiff) && string.IsNullOrWhiteSpace(fullFiles))
            {
                _logger.LogInformation("No code changes found in PR #{prNumber}", prNumber);
                return await CreateResponseAsync(req, HttpStatusCode.OK, "No code changes found.");
            }

            var feedback = await AnalyzeCodeWithOpenAI(codeDiff, fullFiles);
            var commentsUrl = $"https://api.github.com/repos/{owner}/{repoName}/issues/{prNumber}/comments";
            await PostComment(commentsUrl, feedback);

            return await CreateResponseAsync(req, HttpStatusCode.OK, "Reviewed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in GitHubWebhookFunction");
            return await CreateResponseAsync(req, HttpStatusCode.InternalServerError, $"Internal server error: {ex.Message}");
        }
    }

    private async Task<string> ReadRequestBodyAsync(HttpRequestData req)
    {
        using var reader = new StreamReader(req.Body);
        return await reader.ReadToEndAsync();
    }

    private bool IsValidPayload(string requestBody)
    {
        if (string.IsNullOrWhiteSpace(requestBody) || !requestBody.TrimStart().StartsWith("{"))
            return false;
        try
        {
            using var doc = JsonDocument.Parse(requestBody);
            var root = doc.RootElement;
            return root.TryGetProperty("action", out _) &&
                   root.TryGetProperty("pull_request", out _) &&
                   root.TryGetProperty("repository", out _);
        }
        catch
        {
            return false;
        }
    }

    private bool IsSupportedAction(JsonElement root)
    {
        var action = root.GetProperty("action").GetString();
        return action == "opened" || action == "synchronize" || action == "edited";
    }

    private async Task<HttpResponseData> CreateResponseAsync(HttpRequestData req, HttpStatusCode status, string message)
    {
        var resp = req.CreateResponse(status);
        await resp.WriteStringAsync(message);
        return resp;
    }

    private async Task<(string codeDiff, string fullFiles)> FetchPrFilesAndContents(string url, string owner, string repoName, JsonElement pr)
    {
        var files = await GetPrFilesAsync(url);
        var sbDiff = new StringBuilder();
        var sbFull = new StringBuilder();
        var headSha = pr.GetProperty("head").GetProperty("sha").GetString();

        foreach (var file in files)
        {
            var filename = file.GetProperty("filename").GetString() ?? string.Empty;
            if (file.TryGetProperty("patch", out var patch))
            {
                sbDiff.AppendLine($"File: {filename}\n{patch.GetString()}\n");
            }
            var fileContent = await FetchFileContent(owner, repoName, filename, headSha);
            if (!string.IsNullOrEmpty(fileContent))
            {
                sbFull.AppendLine($"File: {filename}\n{fileContent}\n");
            }
        }
        return (sbDiff.ToString(), sbFull.ToString());
    }

    private async Task<JsonElement[]> GetPrFilesAsync(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("PRReviewerBot", "1.0"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _githubToken);
        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        var files = JsonDocument.Parse(content).RootElement.EnumerateArray();
        return files.ToArray();
    }

    private async Task<string?> FetchFileContent(string owner, string repo, string path, string sha)
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}/contents/{Uri.EscapeDataString(path)}?ref={sha}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("PRReviewerBot", "1.0"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _githubToken);
        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            return null;
        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        if (doc.RootElement.TryGetProperty("content", out var encodedContent) &&
            doc.RootElement.TryGetProperty("encoding", out var encoding) &&
            encoding.GetString() == "base64")
        {
            var base64 = encodedContent.GetString();
            if (!string.IsNullOrEmpty(base64))
            {
                var bytes = Convert.FromBase64String(base64);
                return Encoding.UTF8.GetString(bytes);
            }
        }
        return null;
    }

    private async Task<string> AnalyzeCodeWithOpenAI(string codeDiff, string fullFiles)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("Review the following code changes for naming, structure, and readability. Give concise feedback.");
        prompt.AppendLine("## Code Changes (Diffs):");
        prompt.AppendLine(codeDiff);
        prompt.AppendLine("## Full Content of Updated Files:");
        prompt.AppendLine(fullFiles);

        var payload = new
        {
            messages = new[] {
                new { role = "system", content = "You are a code reviewer bot." },
                new { role = "user", content = prompt.ToString() }
            },
            max_completion_tokens = 512
        };
        var request = new HttpRequestMessage(HttpMethod.Post, _openAiEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _openAiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError("OpenAI API error: {StatusCode}, Response: {Response}", response.StatusCode, errorContent);
            throw new Exception($"OpenAI API error: {response.StatusCode} - {errorContent}");
        }
        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        var feedback = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        return string.IsNullOrWhiteSpace(feedback) ? "No feedback generated." : feedback;
    }

    private async Task PostComment(string url, string feedback)
    {
        var payload = new { body = $"😎 PR Reviewer Bot 😎 commented: {feedback}" };
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("PRReviewerBot", "1.0"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _githubToken);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var response = await _httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError("Failed to post comment. Status: {StatusCode}, Response: {Response}", response.StatusCode, errorContent);
            throw new Exception($"GitHub API error: {response.StatusCode} - {errorContent}");
        }
    }
}

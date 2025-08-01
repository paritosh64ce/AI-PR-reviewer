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
            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            _logger.LogWarning("Step 2: Read body: '{body}'", requestBody);

            if (string.IsNullOrWhiteSpace(requestBody))
            {
                _logger.LogWarning("Request body is empty.");
                var resp = req.CreateResponse(HttpStatusCode.BadRequest);
                await resp.WriteStringAsync("Request body is empty.");
                return resp;
            }
            if (!requestBody.TrimStart().StartsWith("{"))
            {
                _logger.LogWarning("Request body is not valid JSON: {body}", requestBody);
                var resp = req.CreateResponse(HttpStatusCode.BadRequest);
                await resp.WriteStringAsync("Request body is not valid JSON.");
                return resp;
            }

            using var doc = JsonDocument.Parse(requestBody);
            var root = doc.RootElement;
            var action = root.GetProperty("action").GetString();
            if (action != "opened" && action != "synchronize" && action != "edited")
            {
                var resp = req.CreateResponse(HttpStatusCode.OK);
                await resp.WriteStringAsync("Ignored event");
                return resp;
            }
            var pr = root.GetProperty("pull_request");
            var prNumber = pr.GetProperty("number").GetInt32();
            var repo = root.GetProperty("repository");
            var repoName = repo.GetProperty("name").GetString();
            var owner = repo.GetProperty("owner").GetProperty("login").GetString() ?? string.Empty;
            _logger.LogInformation("Step 1: Processing PR #{prNumber} in {repoName}/{owner}", prNumber, repoName, owner);

            var filesUrl = $"https://api.github.com/repos/{owner}/{repoName}/pulls/{prNumber}/files";

            var (codeDiff, fullFiles) = await FetchPrFilesAndContents(filesUrl, owner, repoName, pr);
            _logger.LogInformation("Step 2: Fetched code diff and full file contents for PR #{prNumber}", prNumber);
            
            _logger.LogInformation("Step 3: Analyzing code with OpenAI");
            var feedback = await AnalyzeCodeWithOpenAI(codeDiff, fullFiles);

            _logger.LogInformation("Step 4: Posting feedback to GitHub");
            var commentsUrl = $"https://api.github.com/repos/{owner}/{repoName}/issues/{prNumber}/comments";
            await PostComment(commentsUrl, feedback);
            var response = req.CreateResponse(HttpStatusCode.OK);
            
            await response.WriteStringAsync("Reviewed");
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in GitHubWebhookFunction");
            var errorResp = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResp.WriteStringAsync($"Internal server error: {ex.Message}");
            return errorResp;
        }
    }

    // Fetches both code diffs and full file contents for updated files
    private async Task<(string codeDiff, string fullFiles)> FetchPrFilesAndContents(string url, string owner, string repoName, JsonElement pr)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("PRReviewerBot", "1.0"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _githubToken);
        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        var files = JsonDocument.Parse(content).RootElement.EnumerateArray();
        var sbDiff = new StringBuilder();
        var sbFull = new StringBuilder();

        // Get the head SHA for the PR to fetch the latest file content
        var headSha = pr.GetProperty("head").GetProperty("sha").GetString();

        foreach (var file in files)
        {
            var filename = file.GetProperty("filename").GetString() ?? string.Empty;
            if (file.TryGetProperty("patch", out var patch))
            {
                sbDiff.AppendLine($"File: {filename}\n{patch.GetString()}\n");
            }
            // Fetch the full file content at the PR's head commit
            var fileContent = await FetchFileContent(owner, repoName, filename, headSha);
            if (!string.IsNullOrEmpty(fileContent))
            {
                sbFull.AppendLine($"File: {filename}\n{fileContent}\n");
            }
        }
        return (sbDiff.ToString(), sbFull.ToString());
    }

    // Fetches the raw content of a file at a specific commit SHA
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

    // Now includes both codeDiff and fullFiles in the prompt
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
            max_tokens = 512
        };
        var request = new HttpRequestMessage(HttpMethod.Post, _openAiEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _openAiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        var feedback = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        return feedback ?? "No feedback generated.";
    }

    private async Task PostComment(string url, string feedback)
    {
        var payload = new { body = feedback };
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

using System.Net.Http.Headers;
using System.Text.Json;
using System.Text;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

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
        [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req)
    {
        try
        {
            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            using var doc = JsonDocument.Parse(requestBody);
            var root = doc.RootElement;
            var action = root.GetProperty("action").GetString();
            if (action != "opened" && action != "synchronize" && action != "edited")
            {
                var resp = req.CreateResponse(System.Net.HttpStatusCode.OK);
                await resp.WriteStringAsync("Ignored event");
                return resp;
            }
            var pr = root.GetProperty("pull_request");
            var prNumber = pr.GetProperty("number").GetInt32();
            var repo = root.GetProperty("repository");
            var repoName = repo.GetProperty("name").GetString();
            var owner = repo.GetProperty("owner").GetProperty("login").GetString();
            // Fetch PR files
            var filesUrl = $"https://api.github.com/repos/{owner}/{repoName}/pulls/{prNumber}/files";
            var codeDiff = await FetchPrFiles(filesUrl);
            // Analyze code with OpenAI
            var feedback = await AnalyzeCodeWithOpenAI(codeDiff);
            // Post feedback as comment
            var commentsUrl = $"https://api.github.com/repos/{owner}/{repoName}/issues/{prNumber}/comments";
            await PostComment(commentsUrl, feedback);
            var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
            await response.WriteStringAsync("Reviewed");
            return response;
        }
        catch (Exception ex)
        {
            var errorResp = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
            await errorResp.WriteStringAsync($"Internal server error: {ex.Message}");
            return errorResp;
        }
    }

    private async Task<string> FetchPrFiles(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("PRReviewerBot", "1.0"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _githubToken);
        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        // Extract code changes (patches)
        var files = JsonDocument.Parse(content).RootElement.EnumerateArray();
        var sb = new StringBuilder();
        foreach (var file in files)
        {
            var filename = file.GetProperty("filename").GetString() ?? string.Empty;
            if (file.TryGetProperty("patch", out var patch))
            {
                sb.AppendLine($"File: {file.GetProperty("filename").GetString()}\n{patch.GetString()}\n");
            }
        }
        return sb.ToString();
    }

    private async Task<string> AnalyzeCodeWithOpenAI(string codeDiff)
    {
        var prompt = $"Review the following code changes for naming, structure, and readability. Give concise feedback.\n{codeDiff}";
        var payload = new
        {
            messages = new[] {
                new { role = "system", content = "You are a code reviewer bot." },
                new { role = "user", content = prompt }
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
        response.EnsureSuccessStatusCode();
    }
}

# PRReviewerBot

## Overview
PRReviewerBot is an Azure Functions-based bot that automatically reviews GitHub pull requests using Azure OpenAI (GPT). When a pull request is opened or updated, the bot analyzes both the code changes (diffs) and the full content of all updated files for naming, structure, and readability, then posts feedback as a comment on the PR.

## Features
- Monitors pull requests via GitHub webhook.
- Uses GPT to provide feedback on code quality.
- Reviews both code diffs and the entire content of updated files.
- Posts feedback directly as a comment in the pull request.

## Tech Stack
- .NET 8 (C# 12)
- Azure Functions
- GitHub Webhooks
- Azure OpenAI

## Getting Started

### Prerequisites
- .NET 8 SDK
- Azure Functions Core Tools
- GitHub account and repository
- Azure OpenAI resource and API key

### Local Development

1. **Clone the repository:**
    ```bash
    git clone <your-repo-url>
    cd <repo-folder>
    ```

2. **Configure secrets:**
    Edit `PrReviewerBot/local.settings.json` and add:
    ```json
    {
      "IsEncrypted": false,
      "Values": {
        "GitHubToken": "<your-github-token>",
        "OpenAIEndpoint": "<your-openai-endpoint>",
        "OpenAIKey": "<your-openai-key>"
      }
    }
    ```

    > #### How to get GitHubToken
    > 1. Go to [GitHub Settings > Developer settings > Personal access tokens](https://github.com/settings/tokens).
    > 2. Click **Generate new token**.
    > 3. Select **repo** scope (for PRs and comments).
    > 4. Copy the generated token and use it as the value for `GitHubToken`.
    > 
    > #### How to get OpenAIEndpoint
    > 1. Go to the [Azure Portal](https://portal.azure.com).
    > 2. Navigate to your Azure OpenAI resource.
    > 3. Under **Resource Management > Keys and Endpoint**, copy the **Endpoint** value.
    >     - It will look like:  
    >       `https://<your-resource-name>.openai.azure.com/openai/deployments/<deployment-name>/chat/completions?api-version=2024-02-15-preview`
    > > - Ensure you have a deployment created in your Azure OpenAI resource using [https://oai.azure.com/](https://oai.azure.com/).
    > 
    > #### How to get OpenAIKey
    > 1. In the same **Keys and Endpoint** section of your Azure OpenAI resource, copy one of the **Key** values.
    > 2. Use this as the value for `OpenAIKey`.

3. **Run the function locally:**
    ```bash
    func start
    ```
    Or use Visual Studio's __Start Debugging__.

4. **Set up GitHub webhook:**
    - Go to your repository settings > **Webhooks**.
    - Add a webhook pointing to your local/hosted function endpoint.
    - Select **Pull requests** events.

### Deployment

- Deploy to Azure using Visual Studio or Azure CLI.
- Set the required secrets in the Azure Function App's __Configuration__.

## Contributing

- Fork the repository and create a feature branch.
- Follow C# 12 and .NET 8 coding standards.
- Submit pull requests with clear descriptions.
- Ensure new features are covered by tests if applicable.

## Troubleshooting

- Ensure all secrets are set before running.
- Check Azure Function logs for errors.
- For API issues, verify your GitHub token and OpenAI credentials.

- Webhook not adding any comments:
  - Ensure the webhook is correctly configured in GitHub.
  - Check if the Azure Function is receiving the webhook events (check logs).
  - Verify that the OpenAI API is reachable and configured correctly.
  - Check GitHub Webhook Delivery:
    - Go to your repository → Settings → Webhooks → Click your webhook → Recent Deliveries.
    - If you don’t see a delivery for your PR event, the webhook is not firing.
    - If you see a red X, click it to see the error details.
  - Authentication/Authorization Issues
    - If your function is set to require a key or authentication, GitHub’s webhook will not be able to call it unless you provide the key in the URL.
    - For public webhooks, use AuthorizationLevel.Function or Anonymous as needed.


## License

This project is licensed under the MIT License.

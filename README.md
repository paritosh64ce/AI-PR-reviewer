# PR-Reviewer Bot

## Overview
PRReviewerBot is an Azure Functions-based bot that automatically reviews GitHub pull requests using Azure OpenAI (GPT). When a pull request is opened or updated, the bot analyzes code changes for naming, structure, and readability, then posts feedback as a comment on the PR.

## Features
- Monitors pull requests via GitHub webhook.
- Uses GPT to provide feedback on code quality.
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
3. **Run the function locally:**
	```bash
	func start
	```
or use Visual Studio's __Start Debugging__.

4. **Set up GitHub webhook:**
- Go to your repository settings > Webhooks.
- Add a webhook pointing to your local/hosted function endpoint.
- Select "Pull requests" events.

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

## License

This project is licensed under the MIT License.

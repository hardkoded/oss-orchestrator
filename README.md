# oss-orchestrator

Orchestrates Claude Code sessions, one per GitHub issue, with a dashboard.

## Parts

- **Agent** (`src/OssOrchestrator.Agent`): runs locally. Polls GitHub, launches Claude sessions, watches PRs and CI, closes finished sessions, and reads commands from the queue. Claude sessions have to run on the machine that has the repos.
- **Web** (`src/OssOrchestrator.Web`): ASP.NET Core + Blazor dashboard. Shows sessions, questions and PR status, and sends commands (pause, answer, limit). It has no GitHub credentials.
- **Queue**: Azure Storage Queue (web to agent commands, agent to web state).
- **Database**: Azure Cosmos DB free tier.

## Hosting and access

- The web app runs on Azure App Service (F1) with App Service Authentication (Microsoft Entra ID). Only the owner's account is allowed. Every route requires login.
- The agent authenticates to the queue and the API with a dedicated identity whose secret lives only on the local machine.

## Status

Scaffold only. Targets .NET 10 until the .NET 11 SDK is available.

# oss-orchestrator

Orchestrates Claude Code sessions, one per GitHub issue, with a dashboard.

## Parts

- **Agent** (`src/OssOrchestrator.Agent`): runs locally. Wakes when GitHub reports a change (and polls every 10 minutes as a safety net), launches Claude sessions, watches PRs and CI, closes finished sessions, and reads commands from the queue. Claude sessions have to run on the machine that has the repos.
- **Web** (`src/OssOrchestrator.Web`): ASP.NET Core + Blazor dashboard. Shows sessions, questions and PR status, and sends commands (pause, answer, limit). It has no GitHub credentials.
- **Queue**: Azure Storage Queue (web to agent commands, agent to web state).
- **Database**: Azure Cosmos DB free tier. Containers: `config`, `commands`, `state`, `events`.
- **Webhook**: GitHub calls `POST /webhooks/github` on the web app. It checks the signature and records the event in `events`. The agent reads that container through the change feed and runs a pass right away. The pass reads the real state from GitHub, so the event only says "look now".

## Waiting on another issue or PR

A worker that cannot go on until other issues or PRs of the same repo are done posts one comment with a marker `<!-- orc:blocked-by #123 -->` for each one, then adds the `waiting-on` label. Only the newest such comment counts. Only comments by the worker's own login count. While the label is on, the session shows "waiting on a blocker" with the first open blocker in the "Blocked by" column, and it does not hold a slot. When every blocker is merged, completed or closed, the agent tells the worker to continue and removes the label.

## Hosting and access

- The web app runs on Azure App Service (F1) with App Service Authentication (Microsoft Entra ID). Only the owner's account is allowed. Every route requires login.
- The agent authenticates to the queue and the API with a dedicated identity whose secret lives only on the local machine.

## Webhook setup

1. Create the `events` container in database `orc`: partition key `/repo`, default TTL 7 days (`az cosmosdb sql container create -a <account> -g <group> -d orc -n events -p /repo --ttl 604800`). The web app identity needs the Data Contributor role on the account; if it has that at account scope, nothing more is needed.
2. Login is handled inside the app (App Service Authentication is off), and `/webhooks/github` is anonymous: it checks the HMAC signature itself. If you ever turn App Service Authentication on, exclude that path, because GitHub cannot sign in.
3. Set the app setting `GitHub__WebhookSecret` to a long random value.
4. Register the hook on each repo: `GITHUB_WEBHOOK_SECRET=... scripts/register-webhooks.sh https://<app>.azurewebsites.net owner/repo ...`

## Status

Scaffold only. Targets .NET 10 until the .NET 11 SDK is available.

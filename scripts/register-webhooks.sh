#!/usr/bin/env bash
# Registers the GitHub webhook on each repo, so the agent wakes within seconds of a change.
# Usage: GITHUB_WEBHOOK_SECRET=... scripts/register-webhooks.sh https://<app>.azurewebsites.net owner/repo [owner/repo ...]
# Safe to run again: a repo that already has the hook is skipped.
set -euo pipefail

base_url="${1:?web app base URL}"; shift
: "${GITHUB_WEBHOOK_SECRET:?set GITHUB_WEBHOOK_SECRET (the same value as the app setting GitHub__WebhookSecret)}"
url="${base_url%/}/webhooks/github"

for repo in "$@"; do
  # Read the list first: grep -q would close the pipe early and make gh fail under pipefail.
  existing="$(gh api --paginate "repos/$repo/hooks" --jq '.[].config.url')"
  if grep -qxF "$url" <<<"$existing"; then
    echo "$repo: already registered"
    continue
  fi
  gh api "repos/$repo/hooks" --method POST \
    -f name=web -F active=true \
    -f "config[url]=$url" -f "config[content_type]=json" -f "config[secret]=$GITHUB_WEBHOOK_SECRET" \
    -f "events[]=issues" -f "events[]=pull_request" -f "events[]=workflow_run" -f "events[]=check_suite" -f "events[]=push" \
    -f "events[]=pull_request_review" -f "events[]=pull_request_review_comment" -f "events[]=issue_comment" \
    --jq '"'"$repo"': registered hook \(.id)"'
done

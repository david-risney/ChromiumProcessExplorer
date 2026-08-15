# Copilot automation

## Issue assignment

Applying the `copilot-ready` label to an issue queues it and starts the
[Assign Copilot to ready issues](../.github/workflows/assign-copilot.yml)
workflow. Each run assigns the Copilot cloud agent to one eligible issue: the
lowest-numbered open `copilot-ready` issue that is not already assigned to
Copilot. It uses the repository's default branch as the base for the work.

## Required manual setup

Perform these steps after the workflow has been merged into the default branch:

1. Confirm that the token owner has a paid Copilot plan with cloud agent access.
   For an organization-owned repository, an organization owner must also enable
   the Copilot cloud agent policy. In the repository settings, make sure the
   repository has not opted out of cloud agent access. A quick verification is
   to open an issue's **Assignees** picker and confirm that **Copilot** appears.
2. Create a fine-grained personal access token while signed in as that user:
   - Open **Settings > Developer settings > Personal access tokens >
     Fine-grained tokens**, then select **Generate new token**.
   - Select the repository owner and limit repository access to
     `ChromiumProcessExplorer`.
   - Grant **Metadata: Read-only** and **Actions**, **Contents**, **Issues**, and
     **Pull requests: Read and write**. These are the permissions required by
     GitHub's Copilot issue-assignment API.
   - Choose the shortest practical expiration, generate the token, and complete
     organization approval if GitHub requests it.
3. Add the token to this repository:
   - Open **Settings > Secrets and variables > Actions > Secrets**.
   - Select **New repository secret**.
   - Enter `COPILOT_ASSIGNMENT_TOKEN` as the name, paste the token as the value,
     and select **Add secret**.
4. Confirm that the `copilot-ready` label exists. It already exists in this
   repository, so no label change is required.
5. Protect the default branch under **Settings > Rules > Rulesets** (or
   **Branches**, if using classic branch protection). Require the project's CI
   checks and human pull request approval, and do not give Copilot bypass
   permission.

The assignment endpoint requires a user token. The workflow's `GITHUB_TOKEN`
cannot start this task, so the workflow gives it no repository permissions and
exposes the user token only to the assignment step.

### Start the issues that were labeled before setup

Adding a workflow does not replay old `labeled` events. The existing
`copilot-ready` issues therefore require a manual first dispatch:

1. Open the repository's **Actions** tab.
2. Select **Assign Copilot to ready issues**.
3. Select **Run workflow**, choose the default branch, and confirm
   **Run workflow**.
4. Open the run and verify that **Assign next ready issue** succeeded. The first
   run selects issue `#1`, the current lowest-numbered eligible issue.
5. Wait for the run to finish before manually running it again. Each additional
   run selects the next-lowest eligible issue. Stop when the log says that no
   unassigned open issues have the label.

The same manual dispatch is useful after rotating the token or retrying a
failed assignment.

## Usage

Apply `copilot-ready` only after an issue has enough detail to implement and
its proposed changes are safe to run in CI. Labeling any issue starts one queue
run, but the newly labeled issue is not necessarily selected immediately:
lower-numbered eligible issues always take priority. Pull requests, closed
issues, and issues already assigned to Copilot are skipped. The workflow fails
with an explicit error if the token secret has not been configured.

Removing the label does not cancel work already started. Review Copilot's pull
request and wait for all required checks before merging.

## Review-fix dispatcher

### Optional manual setup

1. Create a second fine-grained personal access token for a write-capable
   Copilot user. Limit it to this repository and grant **Metadata: Read-only**
   and **Pull requests: Read and write**.
2. Add it under **Settings > Secrets and variables > Actions** as
   `COPILOT_REVIEW_DISPATCH_TOKEN`.
3. Create the `copilot-autofix` label if it does not exist.
4. After the workflow is on the default branch, open **Actions > Dispatch
   Copilot review fixes > Run workflow** to test it once. A scheduled run will
   also occur every 15 minutes.

Applying the `copilot-autofix` label to a same-repository pull request opts it
into the
[Dispatch Copilot review fixes](../.github/workflows/dispatch-copilot-review-fixes.yml)
workflow. Every 15 minutes, the workflow finds unresolved, non-outdated review
threads opened by Copilot code review and posts one explicit `@copilot` request
containing links to at most 20 comments. It dispatches at most one pull request
per run. The workflow can also be run manually.

The dispatcher does not copy review text into its request. Review comments are
untrusted input; only their GitHub URLs and opaque IDs are used. Hidden markers
record which comments were dispatched so repeated scheduled runs do not create
duplicate requests. To retry a comment, remove its marker from the dispatch
comment and run the workflow manually.

The dispatcher ignores fork pull requests, resolved or outdated threads,
comments from other reviewers, and pull requests without the opt-in label. It
does not resolve threads or merge changes. The token user is recorded as the
author of each dispatch request and must have repository write access and
permission to invoke Copilot.

Scheduled workflows run from the default branch, so this dispatcher begins
operating only after the workflow is merged. GitHub may delay scheduled runs
during periods of high Actions load.

For projects spanning multiple repositories, add this workflow and its secret
to each repository whose issues should be eligible.

See GitHub's documentation for
[using Copilot cloud agent via the API](https://docs.github.com/en/copilot/how-tos/use-copilot-agents/cloud-agent/use-cloud-agent-via-the-api)
and
[risks and mitigations for Copilot cloud agent](https://docs.github.com/en/copilot/concepts/agents/cloud-agent/risks-and-mitigations).

# Copilot automation

## Issue assignment

Applying the `copilot-ready` label to an issue starts the
[Assign Copilot to ready issues](../.github/workflows/assign-copilot.yml)
workflow. The workflow assigns the Copilot cloud agent to the issue and uses
the repository's default branch as the base for its work.

## One-time setup

1. Enable the Copilot cloud agent for the repository. Organization-owned
   repositories must also allow the agent in their Copilot policies.
2. Create a fine-grained personal access token for a user who can use Copilot
   in this repository. Grant read access to metadata and read/write access to
   Actions, contents, issues, and pull requests.
3. Store the token as an Actions repository secret named
   `COPILOT_ASSIGNMENT_TOKEN`.
4. Ensure the `copilot-ready` label exists.
5. If review-fix dispatching will be used, create a second fine-grained personal
   access token for a write-capable Copilot user. Grant read access to metadata
   and read/write access to pull requests, then store it as the Actions
   repository secret `COPILOT_REVIEW_DISPATCH_TOKEN`.
6. Ensure the `copilot-autofix` label exists.
7. Protect the default branch with the required CI checks and human pull
   request approvals. Do not allow the Copilot agent to bypass those rules.

The assignment API requires a user token; the workflow's `GITHUB_TOKEN` cannot
start this task. The workflow gives `GITHUB_TOKEN` no repository permissions
and exposes the user token only to the assignment step.

## Usage

Apply `copilot-ready` only after an issue has enough detail to implement and
its proposed changes are safe to run in CI. The workflow will fail with an
explicit error if the token secret has not been configured.

Removing the label does not cancel work already started. Review Copilot's pull
request and wait for all required checks before merging.

## Review-fix dispatcher

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

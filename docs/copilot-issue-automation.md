# Copilot issue automation

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
5. Protect the default branch with the required CI checks and human pull
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

For projects spanning multiple repositories, add this workflow and its secret
to each repository whose issues should be eligible.

See GitHub's documentation for
[using Copilot cloud agent via the API](https://docs.github.com/en/copilot/how-tos/use-copilot-agents/cloud-agent/use-cloud-agent-via-the-api)
and
[risks and mitigations for Copilot cloud agent](https://docs.github.com/en/copilot/concepts/agents/cloud-agent/risks-and-mitigations).

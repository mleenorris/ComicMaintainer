# Automatic Copilot Pull Request Reviews

GitHub Copilot can review every pull request automatically. This is set up with a repository ruleset in GitHub's settings.
No file in this repository is involved.

1. Go to **Settings > Rules > Rulesets > New ruleset > New branch ruleset**.
2. Target the default branch, `dotnet`, or target all branches to cover every pull request base.
3. Set **Enforcement status** to **Active**.
4. Under **Branch rules**, enable **Automatically request Copilot code review**.

Copilot is then added as a reviewer on every pull request targeting those branches, including Dependabot's. Draft pull
requests are reviewed once they are marked ready for review. Copilot code review must be enabled for the repository. The
same setting can be applied to a whole organization.

To check it, open a throwaway pull request against `dotnet` and confirm that **Copilot** appears under **Reviewers** and
posts a review.

Separately, `.github/workflows/claude-code-review.yml` runs a Claude code review on each pull request update. Both tools
follow the conventions in `.github/copilot-instructions.md`.

# GitHub Actions Setup for Test Coverage

## How it works

The test coverage workflow automatically:
1. Runs tests with coverage on every push to main and on PRs
2. Uploads coverage reports to Codecov
3. Makes coverage available via Codecov (the README badge is served by Codecov, not committed by this workflow)

## Setup Instructions

1. **Enable Codecov:**
   - Go to https://codecov.io
   - Sign in with GitHub
   - Add your repository
   - Codecov will automatically detect coverage reports from GitHub Actions

2. **Codecov renders the badge automatically** once the first workflow run completes (the badge is served live by Codecov, not written into README.md by this workflow)

That's it! No secrets or additional configuration needed. Codecov works automatically with public repositories.
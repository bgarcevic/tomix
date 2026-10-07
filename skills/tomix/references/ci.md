# tomix in CI pipelines

In pipelines, pass the model with `-m <path>` instead of `tx connect`, so every step
says exactly which model it uses.

Flags for pipelines:

- `--non-interactive`: fail with an error instead of waiting for input.
- `--quiet`: no spinners or hints.
- `--ci github` or `--ci vsts` (on `validate`, `bpa run`, `test`, `deploy`): CI log
  groups and annotations on stderr.
- `--trx <file>` (on `validate`, `bpa run`, `test`): a test-results file for the CI UI.
- `--yes`: apply `deploy` / `refresh` instead of previewing them. Use it only in a
  step the user asked to deploy.

## Service principal sign-in

Read the secret from stdin. `tx` rejects secrets passed on the command line.

```bash
printf '%s' "$CLIENT_SECRET" | tx auth login -u "$CLIENT_ID" -t "$TENANT_ID" --password -
```

After that, commands that reach a workspace take `--auth spn`.

## GitHub Actions: check every pull request

```yaml
name: semantic-model
on:
  pull_request:
    paths: ["**/*.SemanticModel/**"]

jobs:
  check:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - run: curl -LsSf https://raw.githubusercontent.com/bgarcevic/tomix-cli/main/install/install.sh | sh
      - run: echo "$HOME/.local/bin" >> "$GITHUB_PATH"
      - run: tx validate -m "./MyModel.SemanticModel" --ci github --non-interactive
      - run: tx bpa run -m "./MyModel.SemanticModel" --ci github --non-interactive
```

Both steps exit non-zero on errors. `bpa run --fail-on warning` also fails on
warnings.

## GitHub Actions: deploy after merge

```yaml
on:
  push:
    branches: [main]
    paths: ["**/*.SemanticModel/**"]

jobs:
  deploy:
    runs-on: ubuntu-latest
    environment: production        # require a reviewer in the repository settings
    steps:
      - uses: actions/checkout@v4
      - run: curl -LsSf https://raw.githubusercontent.com/bgarcevic/tomix-cli/main/install/install.sh | sh
      - run: echo "$HOME/.local/bin" >> "$GITHUB_PATH"
      - run: printf '%s' "$CLIENT_SECRET" | tx auth login -u "$CLIENT_ID" -t "$TENANT_ID" --password -
        env:
          CLIENT_ID: ${{ secrets.PBI_CLIENT_ID }}
          TENANT_ID: ${{ secrets.PBI_TENANT_ID }}
          CLIENT_SECRET: ${{ secrets.PBI_CLIENT_SECRET }}
      - run: >
          tx deploy -m "./MyModel.SemanticModel"
          --server "MyWorkspace" --database "MyModel"
          --auth spn --yes --non-interactive --ci github
```

By default, `deploy` keeps the target's connections, partitions, roles, and M parameter
values, and it blocks on BPA errors. Add a `--deploy-*` flag only for something the user
wants overwritten.

## DAX regression tests

`tx test` runs against a deployed model, so the order of steps is: deploy the
branch to a dev workspace, refresh it, then run `tx test`.

```bash
tx test ./tests -s "DevWorkspace" -d "MyModel" --auth spn --non-interactive --ci github --trx results.trx
```

To accept an intended change in results, run `tx test --update` locally and commit
the updated `.expected.json` files.

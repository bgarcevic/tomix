# Using tomix from your agent

`tx` works well with coding agents such as Claude Code, Codex, Copilot and Cursor.
Every command can print JSON, errors come back as one JSON object with a code and a hint,
and edits are only previews until you add `--save`. The agent can explore and propose
changes freely, and nothing is written until someone saves.

The tomix skill teaches an agent those conventions: how to connect, read before editing,
preview then save, validate afterwards, and ask before deploying.

## Install the skill

From the repository that holds your model:

```sh
tx skills install
```

This writes the skill to `.claude/skills/tomix/` (Claude Code) and
`.agents/skills/tomix/` (Codex and other harnesses that follow the open
[Agent Skills](https://agentskills.io) format). Without `--agent`, it installs only
for the agents the repository already uses, or for both if it can't tell. Commit the
folders so everyone working on the model gets them, then start a new agent session.

```sh
tx skills install --agent codex          # one agent
tx skills install --user                 # for every project, in your home folder
tx skills status                         # where it is installed, and whether it is current
```

The skill is built into `tx`, so it always matches the commands of the version
you have. After `tx update`, `tx skills status` shows older copies as `outdated`, and
`tx skills install` updates them. tx never overwrites a copy you edited unless you
pass `--force`. See [`skills`](../commands/manage.md#skills-agent-skill) for details.

If you manage all your skills with the [skills CLI](https://github.com/vercel-labs/skills),
that works too. It installs the latest version from GitHub, which may describe
commands newer than your `tx`:

```sh
npx skills add bgarcevic/tomix-cli --skill tomix
```

## Agents without skills

Paste this into the repository's `AGENTS.md` or `CLAUDE.md`, or your tool's rules
file:

```markdown
## Semantic model

The model is at `./MyModel.SemanticModel`. Use the tomix CLI (`tx`) for all model work.

- Read with `tx get`, `tx ls`, `tx find`, `tx deps`. Add `--non-interactive`, and `--output-format json` when you need exact values or parse the output.
- Edit with `tx add`, `tx set`, `tx mv`, `tx rm`, `tx replace`. They only preview; add `--save` to write.
- Rename and delete with `tx mv` and `tx rm`, not by editing TMDL files, so DAX references stay intact.
- After saving, run `tx validate` and `tx bpa run`, and fix new errors before reporting done.
- Ask before `tx deploy`, `tx refresh`, or any command with `--yes` or `--force`.
```

## Try it

Connect to a sample model, then give the agent a task:

```sh
tx connect "./samples/Revenue Opportunities.SemanticModel"
```

> Find measures without a format string and add a sensible one to each.
> Show me the changes before saving.

The agent should list measures with `tx get`, propose `tx set ... --set formatString=...`
commands, show you the previews, and only run them with `--save` once you agree.
Then it should run `tx validate`.

## Watch the agent work: `tx mcp` and `tx ui`

To see each edit as the agent makes it, and undo any of them, give the agent a live
session as MCP tools and open the same model in `tx ui`:

```sh
claude mcp add tomix -- tx mcp          # Claude Code; other harnesses run `tx mcp` from their MCP config
tx ui "./samples/Revenue Opportunities.SemanticModel" --open
```

The agent's edits appear in the page as undo steps. They reach the files when the agent
or you save. The skill tells the agent to save a TMDL folder or `.bim` file itself once
its checks pass, since git and undo can take it back, and to ask you before saving to a
server or Power BI Desktop. See [`mcp`](../commands/modify.md#mcp-give-ai-agents-a-session-as-tools)
for the tools and options.

## What the skill tells the agent

- Use `--output-format json` when it needs exact values or parses output, and plain text
  for overviews. Read the `hint` in error envelopes before retrying.
- Treat exit code `3` from `tx deploy` or `tx refresh` as a finished preview, not an error.
- Read the model through `tx` instead of grepping TMDL files.
- Preview, then `--save` or `--stage` + `tx stage commit`, then `tx validate`.
- Make renames and deletes with `tx mv` / `tx rm`, which keep DAX references working.
- Ask before using `--force`, `--allow-delete`, or `--no-fix-refs`, before ignoring BPA
  rules, and before `deploy` or `refresh` with `--yes`.
- Use `-m <path>` in scripts and CI instead of a saved connection.
- In a live session (`tx mcp` tools, or a model open in `tx ui`), edit through the session and
  never the files. Save file models when done, and ask before saving to a server.

The skill's [CI reference](https://github.com/bgarcevic/tomix-cli/blob/main/skills/tomix/references/ci.md)
has GitHub Actions workflows for pull-request checks and deploying after merge.

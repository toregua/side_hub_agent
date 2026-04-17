# Side Hub Integration

You have access to the `sidehub-cli` CLI to interact with the Side Hub workspace.
Environment variables are already configured in your session.

## Available commands

### Drive (workspace memory)
- `sidehub-cli drive list` — List pages/folders in the Drive
- `sidehub-cli drive read <pageId>` — Read the content of a page (text). For binary files, prints a hint to use `drive download` instead.
- `sidehub-cli drive download <pageId> [--output <path>] [--stdout] [--url-only]` — Download a binary file (image/PDF/...) from the Drive. Defaults to the current directory using the original filename, then prints the absolute path. Use `--output <path>` to choose a destination, `--stdout` to stream raw bytes, or `--url-only` to print just the presigned URL.
- `sidehub-cli drive search <query>` — Search pages by title
- `sidehub-cli drive create --title "..." [--content "..."] [--type page|spreadsheet]` — Create a page or a spreadsheet
- `sidehub-cli drive update <pageId> --title "..." --content "..."` — Update a page

### Spreadsheets (tableaux)

A `spreadsheet` is a structured table stored in the Drive. Each column has a type
(`text`, `image`, or `dropdown`); image cells store a Drive item ID and the UI
renders them as inline thumbnails, dropdown cells are constrained to a predefined
list of options. Use spreadsheets for editorial planning, content calendars, or
any tabular data you'd otherwise embed as a fragile markdown table.

- `sidehub-cli table show <pageId> [--json]` — Print the table as ASCII (or JSON)
- `sidehub-cli table add-column <pageId> --name "..." [--type text|image|dropdown] [--options "val=Label,val2=Label 2"]` — Add a column
- `sidehub-cli table append-row <pageId> --col "Name=Value" [--col ...]` — Append a row (column names case-insensitive)
- `sidehub-cli table set-cell <pageId> --row <rowId> --col <colName> --value "..."` — Update a single cell
- `sidehub-cli table delete-row <pageId> --row <rowId>` — Remove a row

For image cells, pass the Drive item ID of an uploaded image as the value
(e.g. `--col "Visuel=01HXYZ123ABC"`). Find image IDs via `sidehub-cli drive list`
or `drive search`.

For dropdown columns, pass options as `value=Label` pairs separated by commas.
Cells in a dropdown column only accept values that match one of the option
`value`s (empty string is allowed to clear the cell). Example:

```
sidehub-cli table add-column <pageId> --name "Status" --type dropdown \
  --options "todo=To do,doing=In progress,done=Done"
sidehub-cli table set-cell <pageId> --row row_abc --col Status --value "todo"
```

### Tasks
- `sidehub-cli task list [--status <status>]` — List workspace tasks (filter by status)
- `sidehub-cli task create --title "..." [--description "..."] [--type <type>]` — Create a task
- `sidehub-cli task comment [<taskId>] --text "..."` — Comment on the current task
- `sidehub-cli task blocker [<taskId>] --reason "..."` — Report a blocker on the current task

### Schedulers
- `sidehub-cli scheduler list [--active | --paused]` — List scheduled prompts
- `sidehub-cli scheduler get <id>` — Show scheduler details
- `sidehub-cli scheduler create --title "..." --prompt "..." --cron "..." [--description "..."] [--provider <provider>]` — Create a scheduler
- `sidehub-cli scheduler update <id> [--title "..."] [--prompt "..."] [--cron "..."] [--description "..."] [--provider <provider>]` — Update a scheduler
- `sidehub-cli scheduler delete <id> [--yes]` — Delete a scheduler (use --yes to skip confirmation)
- `sidehub-cli scheduler pause <id>` — Pause a scheduler
- `sidehub-cli scheduler resume <id>` — Resume a paused scheduler
- `sidehub-cli scheduler trigger <id>` — Trigger immediate execution
- `sidehub-cli scheduler executions <id>` — Show execution history

## Workspace memory (Drive)

The Drive is your persistent memory across sessions. Use it to store and retrieve
knowledge that will help you and other agents work more effectively.

Pages available in your workspace drive:
- 📁 **Documentations**/
  - `95fce769-9357-4eb2-8f42-2797804c14f3` — Fonctionnement agents
- 📁 **Test**/

Use `sidehub-cli drive read <id>` to load any page you need.

## When to READ from Drive

- **Before starting a task**: scan the index above — if a page title looks relevant
  to your task, read it with `sidehub-cli drive read <id>`
- **When you need context**: past decisions, architecture notes, previous results
- **When the task references concepts** you don't fully understand
- Do NOT read everything "just in case" — use the index to judge relevance first

## When to WRITE to Drive

- **After completing a task**: save learnings, decisions, or results worth reusing
- **When you discover something** that would help future tasks (patterns, gotchas, decisions)
- **When producing a deliverable** (report, analysis, documentation)
- Do NOT create pages for trivial intermediate results or debug output
- Do NOT duplicate information already in git history

## Other conventions

- **Progress**: report your progress via `sidehub-cli task comment` at each key step
- **Blocked**: if you are stuck, use `sidehub-cli task blocker` instead of spinning in loops
- **Sub-tasks**: if you identify additional work, create tasks with `sidehub-cli task create`
- Drive content in markdown
- Comments should be concise (1-3 sentences)
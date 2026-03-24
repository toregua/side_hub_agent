ub Integration

 access to the `sidehub-cli` CLI to interact with the Side Hub workspace.
ent variables are already configured in your session.

able commands

e (documentation, deliverables)
ub-cli drive list` — List pages/folders in the Drive
ub-cli drive read <pageId>` — Read the content of a page
ub-cli drive create --title "..." --content "..."` — Create a page
ub-cli drive update <pageId> --title "..." --content "..."` — Update a page

s
ub-cli task list` — List workspace tasks
ub-cli task create --title "..." --description "..."` — Create a task
ub-cli task comment --text "..."` — Comment on the current task
ub-cli task blocker --reason "..."` — Report a blocker on the current task

to use these commands

verables**: when you produce a significant result (report, analysis, documentation),
 a Drive page with `sidehub-cli drive create`
ress**: report your progress via `sidehub-cli task comment` at each key step
ked**: if you are stuck, use `sidehub-cli task blocker` instead of spinning in loops
tasks**: if you identify additional work, create tasks with `sidehub-cli task create`

ntions
content in markdown
ts should be concise (1-3 sentences)
 create Drive pages for trivial intermediate results
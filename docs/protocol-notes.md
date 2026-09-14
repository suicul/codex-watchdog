# Verified interface (2026-09-15)

Host: Linux; installed `codex-cli 0.154.0` (this environment's distribution).
Ran `codex --version`, `codex --help`, `codex exec --help`, `codex queue --help`,
`codex app-server --help`, `codex app-server proxy --help`,
`codex app-server generate-json-schema --out /tmp/watchdog-schema`.

Official reference fetched: https://developers.openai.com/codex/app-server
Local generated schema, rather than assumptions about another release, determines field names.
In this version, `exec` is noninteractive; writing more lines to its initial stdin is not
an interactive continuation API. `queue` exists, but queueing by itself does not prove idle.

Selected JSON-RPC transport: existing daemon via `codex app-server proxy`, or local WebSocket.
Methods: initialize, initialized, thread/loaded/list, thread/read (metadata),
thread/turns/list (limit=1, descending, full items), turn/start.
App-server interfaces are experimental; older Codex distributions may lack proxy or turn pagination.
Unsupported interfaces fail closed. Watchdog never starts another agent or resumes an unloaded thread.

`ThreadStatus`: notLoaded, idle, systemError, active; activeFlags may contain
waitingOnApproval / waitingOnUserInput. A completed turn is NOT a completed task.
History fetch uses the latest turn only; observations of builds/tests are not cached across turns.
Metadata is re-read after history retrieval, and the complete observation is re-read before sending.

`turn/start` has no expected-idle/compare-and-swap guard and can steer an already active turn.
The remaining read/send race requires exclusive user ownership of the session. A per-user thread
lock prevents two watchdog instances, not other app-server clients or the terminal user.
Do not use watchdog on a session with other writers. No claim of atomic attachment is made.

Continuation explicitly sets sandboxPolicy=workspaceWrite, networkAccess=false, no extra
writableRoots, approvalPolicy=untrusted, approvalsReviewer=user. These overrides persist
in Codex for subsequent turns. The watchdog never approves tools, requests or permissions.
Existing tools/rules/plugins and the OS determine actual sandbox enforcement. The watchdog
is not a command firewall: prompts and workspace sandboxing cannot prove that all in-project
file deletions or pre-authorized tool actions are impossible. Use a disposable worktree/VM,
review existing policies and do not start the original session with bypass flags.

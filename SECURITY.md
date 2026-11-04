# Security

Report vulnerabilities through the repository's **Security → Report a
vulnerability** page. Avoid putting credentials, real transcripts or exploit
details in public issues. The current release is the supported version.

Codex Monitor reads local session files and can attach to an existing Codex
app-server. Sending a message, resuming a session or interrupting a turn is an
explicit user action. The application does not separately read or retain login
tokens and does not execute JavaScript found in transcripts. Exports and
transcripts can contain sensitive workspace information; review them before
sharing.

Use local app-server endpoints you control. The monitor does not add an
authentication layer to an app-server endpoint or change its sandbox and
approval policy.

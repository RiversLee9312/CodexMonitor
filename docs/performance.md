# History loading

The conversation view starts at the tail. Local history uses byte-aligned JSONL pages of about 300 records, with a 2 MiB soft page budget and a 64 MiB single-record safety limit. Older pages are requested when scrolling upward near the loaded boundary. App-server uses `thread/items/list`, descending, 100 items at a time, so a single very long turn is not hydrated as a unit.

Message IDs use absolute byte offsets for local records and protocol item IDs for server records. Rows are reconciled by ID; tool groups retain their disclosure identity when a preceding page joins the same group. The outer list and nested group keep a visible item and its pixel position. Appends preserve history reading; “Jump to latest” resumes following.

The synthetic MSTest fixture has 100,000 records (17,439,066 bytes). One local run read 52,350 bytes for the initial page in 54.3 ms. This is a sample measurement on the development machine, not a performance guarantee. Tests assert the read window, stable IDs, cursor use, overlap handling, truncation, partial writes and visible anchors rather than fragile timing thresholds.

The local session index reads bounded head/tail windows for changed rollouts and caches unchanged metadata. If a lifecycle marker is outside those windows, status is unknown rather than guessed. Local updates reparse only the window the user has loaded, allowing paired calls/results and native mirrors across page boundaries to reconcile correctly.

Loaded history is virtualized; previously loaded pages remain available during the selection. Memory therefore grows as a user deliberately explores older content. Search and Markdown export cover that loaded window. Unknown extensions and very large individual records may still be more expensive than ordinary messages.

## Conversation list

The sidebar also uses incremental loading: 50 rows initially, then automatic prefetch near the end after a downward scroll gesture. There is no manual load-more control. The local metadata inventory is scanned in the background and cached under the application's LocalAppData directory; row models are only created for loaded matches. Server summaries use 50-item thread/list cursor pages, with separate active/archive cursors. Keyword and date filters can search beyond the loaded rows; stale and uncooperative requests cannot overwrite a newer query.

Update time is derived from semantic conversation records rather than file metadata alone. Attaching a runtime preserves the existing conversation time. Runtime status and historical page hydration do not create unread content. Rows are moved by identity when real content changes their order, preserving selection and the visible anchor. Anchor restoration cannot itself request page after page; downward input is tracked separately from programmatic scrolling. Touch inertia and later input cancel outdated anchor restoration.

A 10,000-conversation MSTest fixture verifies a 50-row start, distinct automatic pages during repeated fast scrolling, native dispatcher progress, retained anchors during server imports and no automatic history drain. Date ranges use local calendar days, including timezone boundaries, and combine with keywords. Indexing still needs a filesystem metadata inventory; a cold cache must inspect the files in the background. It does not read whole transcripts or construct all UI rows.

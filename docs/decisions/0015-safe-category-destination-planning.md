# ADR 0015: Explicit category destination planning

- Date: 2026-11-14
- Status: Accepted; Windows folder-dialog/reparse acceptance pending

Add an opt-in destination suggestion built from an explicitly chosen absolute base directory, one category leaf and one URL filename leaf. The suggestion is displayed in the editable destination field. It does not silently override a manual path or create directories while previewing. On Add, validate the whole queued request first; only a still-matching generated destination authorizes creation of its category directory. No data file is created by planning, and no existing destination is replaced.

Validate Windows leaf semantics on every platform: reject separators, traversal leaves, streams/colon, control/invalid characters, trailing dots/spaces and reserved device names (including COM/LPT superscript digits). Bound leaves to 200 UTF-16 characters, leaving room for checkpoint suffixes. Decode only the last URI path component; reject encoded path separators. Ignore query/fragment and do not use Content-Disposition. A URL ending with a slash suggests download.bin. Invalid input requires a manual destination or corrected category; do not silently rewrite a conflicting unsafe name.

Revalidate generated plan structure before creating the folder. Require an existing root and reject existing reparse/symlink ancestors before and after creation. This prevents ordinary linked-path surprises, but is not a hostile-local-process filesystem sandbox: managed check/create operations have TOCTOU limitations. Windows junction/long-path/access behavior still requires acceptance. Folder creation may leave an empty category directory if later queue persistence fails; downloaded/user files are not deleted to undo that.

The optional base folder is saved only through Save defaults, within the existing protected preferences envelope. The complete destination filename, URLs, connections and scheduling policies remain excluded from defaults. Loading existing preferences without a base folder remains compatible. Manual paths retain their existing behavior and do not receive automatic directory creation.

Tests cover Windows names, device names, decoded separator/traversal rejection, query exclusion, fallback names, category path composition, directory creation without a data file, forged-plan rejection and encrypted base-folder round-trip. Platform-neutral tests do not prove native WPF dialog or Windows reparse behavior.

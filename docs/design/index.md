# Design decisions

Architecture decision records (ADRs) capture decisions that shape several issues or projects at once. They record the context, the decision, the alternatives that were rejected, and the consequences.

| ADR | Title | Status |
|---|---|---|
| [0001](adr-0001-live-model-session.md) | Live model session: lifecycle, identity, threading, undo, handler sharing, save and events | Accepted; §3 and parts of §4 superseded by 0002 and 0003 |
| [0002](adr-0002-live-session-lease-gate-and-journal-first.md) | Live model session: lease gate instead of an actor, journal before the session | Accepted; rollback in §2 superseded by 0003 |
| [0003](adr-0003-live-session-checkpoint-rollback.md) | Live model session: checkpoint rollback instead of journal inversion | Proposed |

## Writing an ADR

- Name the file `adr-NNNN-short-title.md` and add it to the table above and to the `nav` in `zensical.toml`.
- Start with the status (`Proposed`, `Accepted`, `Superseded by NNNN`), date and issue.
- Once an ADR is accepted, do not rewrite its decisions. Supersede them with a new ADR and link the two.
- Keep the matching `CONTEXT.md` files in step with what the ADR says each project owns.

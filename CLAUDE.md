# Working in this repository

Second Key core. Read before changing anything:

1. [`docs/WORKPLAN.md`](docs/WORKPLAN.md) — every ticket, its "done when" line and status.
2. [`docs/analysis/phase-01.md`](docs/analysis/phase-01.md) — which files each ticket owns,
   and the decisions already taken. If your change disagrees with it, change the analysis
   first.
3. [`docs/reference-notes.md`](docs/reference-notes.md) — what was taken from the older
   repositories, so it is not re-derived.
4. The estate standards, declared in `.claude/settings.json`:
   `architecture-standards/docs/architecture/00-REFERENCE-ARCHITECTURE.md` and its guides.

Rules that are easy to break by accident:

- **The artifact formats are the product interface.** A change to `schemas/*.json` is a
  format change: bump the `v` field it governs, update `docs/formats/`, and keep the
  samples validating.
- **No model in the verdict path** (design rule 1). Nothing under `SecondKey.Contract` or
  `SecondKey.Compare` may call a language model.
- **Culture-explicit string handling everywhere** (CA1305/CA1307/CA1309/CA1310 are
  warnings, and warnings are errors): the product exists to catch culture bugs.
- Recordings from real systems never enter git; only synthetic samples and fixtures.

Build, test, mutate:

```sh
./scripts/setup.sh
dotnet build SecondKey.slnx && dotnet test SecondKey.slnx
./scripts/mutation.sh
```

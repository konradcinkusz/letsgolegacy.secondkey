# Contributing

1. Pick a ticket from [`docs/WORKPLAN.md`](docs/WORKPLAN.md); one ticket is one pull
   request (or one commit in a milestone pull request), and its "done when" line is the
   acceptance criterion — quoted verbatim in the pull request.
2. Run `./scripts/setup.sh` once; it installs the pre-commit secret scan.
3. Before pushing: `dotnet build SecondKey.slnx`, `dotnet test SecondKey.slnx`, and for
   changes in logic `./scripts/mutation.sh`.
4. Never commit a recording from a real system. Synthetic samples live in
   `schemas/samples/` and test fixtures in `tests/**/fixtures/`.

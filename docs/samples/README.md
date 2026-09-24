# API samples

- `synthetic-match-detail.json` is **hand-written**, but follows the field names and score semantics of real
  responses (verified 2026-09-25, docs/PLANNING.md 3.4): `offsideCount`, `ballPossesionSuccess`, `gamepad`,
  `division`, and an own goal that counts in the opponent's `goalTotalDisplay`.
- Real responses: `dotnet run --project src/FcHelper.Cli -- dump <nickname>` writes `*.local.json` files, which are
  git-ignored because they contain other players' nicknames (and the API terms require refreshing crawled data
  within 30 days). When they exist, `dotnet test` also checks every one of them against the verified semantics.

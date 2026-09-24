# API samples

- `synthetic-match-detail.json` is **hand-written** in the documented `match-detail` shape. It is used by the tests
  and does not prove the real API's field semantics.
- Real responses go here once an API key exists: `dotnet run --project src/FcHelper.Cli -- dump <nickname>` writes
  `*.local.json` files, which are git-ignored because they contain other players' nicknames.
  Check them against docs/PLANNING.md section 3.4 (coordinate direction, shot type codes, record delay).

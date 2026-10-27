# ONI Advisor — notes for agents working on this repo

Start by reading `docs/HANDOFF.md` (current state, what is untested, open issues, environment gotchas)
and `docs/STATUS.md` (test results). `README.md` has the design goals and the user's preferences.

Key facts:
- .NET SDK: `%USERPROFILE%\.dotnet\dotnet.exe` (not on PATH). Mod targets net48; tools target net8.
- Deploy the mod only with ONI closed: `dotnet build src/OniAdvisor/OniAdvisor.csproj -c Release -p:Deploy=true`.
- Restart the companion by stopping only processes whose command line contains `OniAdvisorCompanion.dll`,
  then `tools/run-companion.cmd`.
- `advisor-workspace/CLAUDE.md` is NOT for you: it is the in-game advisor's instructions, copied into
  `Documents/Klei/OxygenNotIncluded/advisor/` by the companion.
- Commits so far were authored as `Claude <noreply@anthropic.com>` (no git identity configured on this
  machine), with a `Co-Authored-By` trailer.

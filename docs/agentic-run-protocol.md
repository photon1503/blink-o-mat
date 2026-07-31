# Agentic Run Protocol

This protocol enforces docs/agentic-coding-spec.md during implementation runs.

## Pre-Run Checklist
1. Select one task from docs/agentic-migration-board.md.
2. Confirm task status is TODO or IN_PROGRESS.
3. Copy docs/agentic-task-report-template.md into a new report file:
   - docs/reports/<task-id>.md
4. Fill Task Contract and Baseline references before code changes.

## Execution Loop
1. Baseline extraction from WPF files.
2. Minimal parity implementation in Avalonia/Core.
3. Validation commands.
4. Screenshot capture if UI changed.
5. Fill Acceptance Matrix.

If any matrix row is No:
- Task remains IN_PROGRESS.
- Document exact remaining delta.

## Validation Commands
```bash
dotnet build src/Rejector.Avalonia/Rejector.Avalonia.csproj
dotnet build Rejector.CrossPlatform.slnx
dotnet test tests/Rejector.Core.Tests/Rejector.Core.Tests.csproj
```

## Screenshot Protocol
Use deterministic naming:
- src/shots/<task-id>-current.png
- src/shots/<task-id>-legacy-ref.png

Capture command example:
```bash
mkdir -p src/shots
screencapture -x src/shots/<task-id>-current.png
```

## Status Update Rules
After each task run:
1. Update task status in docs/agentic-migration-board.md.
2. Record Yes/No matrix values in the board task.
3. Link the report path in the task section.

## Completion Rules
A task can be marked DONE only when:
- Behavior matched = Yes
- Build passed = Yes
- Tests passed = Yes
- Screenshot captured = Yes for UI tasks
- Remaining delta listed = Yes

## Merge Gate Recommendation
Before merge, ensure:
1. Report file exists for each DONE task.
2. Board status and matrix values match report content.
3. No unresolved regression notes.

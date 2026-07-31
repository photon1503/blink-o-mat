# Agentic Task Report Template

Use this template for every parity task.

## 1. Objective
One sentence describing the exact parity behavior targeted.

## 2. Task Contract
- Task ID:
- Epic:
- User behavior to match:
- Baseline references:
  - 
- Target files expected:
  - 
- Acceptance criteria:
  - 

## 3. Baseline Extraction Notes
Summarize exact trigger path and state rules from WPF baseline.

## 4. Changes Made
- File: 
- What changed:

## 5. Validation
Commands run:
```bash
dotnet build src/Rejector.Avalonia/Rejector.Avalonia.csproj
dotnet build Rejector.CrossPlatform.slnx
dotnet test tests/Rejector.Core.Tests/Rejector.Core.Tests.csproj
```

Outcomes:
- Build Avalonia:
- Build Solution:
- Core Tests:

## 6. Visual Evidence
- New screenshot(s):
  - 
- Legacy reference screenshot(s):
  - 
- Comparison notes:
  - 

## 7. Acceptance Matrix
- Behavior matched: Yes/No
- Build passed: Yes/No
- Tests passed: Yes/No
- Screenshot captured: Yes/No
- Remaining delta listed: Yes/No

## 8. Remaining Deltas
List any unresolved parity differences explicitly.

## 9. Risks and Follow-Ups
- Risk:
- Follow-up task:

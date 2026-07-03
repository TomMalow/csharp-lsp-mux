# ISSUES

Load, parse, and understand the open issues.

You've been passed the latest few commits. Review these to understand what work has been done.

If all tasks are complete, output <promise>NO MORE TASKS</promise>
Otherwise, output <promise>TASK COMPLETE</promise>

If the implementation of an issue fails, output  <promise>TASK INCOMPLETE</promise>

# TASK SELECTION

Pick the next task. Prioritize in this order

1. Critical bug fixes
2. Development Infrastructure
  Getting development infrastructure like test or shared code is an important precursor to building features.

3. Tracer bullets for new features
  Tracer bullets are small slices of functionality that go through all layers of the system, allowing you to test and validate your approach early. This helps in identifying potential issues and ensures that the overall architecture is sound before investing significant time in development.
  TL;DR - build a tiny, end-to-end slice of the feature first, then expand it out.

4. Polish and quick wins
5. Refactors

# EXPLORATION

Explore the repo.

# IMPLEMENTATION

Keep changes small and focused:

- One logical change per commit
- If a task feels too large, break it into subtasks
- Prefer multiple small commits over one large commit
- Run feedback loops after each change, not at the end
Quality over speed. Small steps compound into big progress.

Use /tdd to complete the task.

Follow the guidelines defined in CLAUDE.md

This codebase will outlive you. Every shortcut you take becomes
someone else's burden. Every hack compounds into technical debt
that slows the whole team down.

You are not just writing code. You are shaping the future of this
project. The patterns you establish will be copied. The corners
you cut will be cut again.

Fight entropy. Leave the codebase better than you found it.

If implementation fails,

# FEEDBACK LOOPS

Before committing, run the feedback loops:

- 'dotnet build'
- 'dotnet test' - this include all tests
- review code with /review sub-agent and resolve issues. Do not use other review skills than this. Run build and test again after resolving feedback from review

# COMMITING

Make a git commit. The commit message must:

1. Include key decision made
2. Include files changed
3. Blockers or notes for next iteration

# FINISHING

## LOCAL

If the task is complete move the issue file to '/<feature-name>/done/'
If the task is not complete, add a note to the issue file with what was done.

## GITHUB

If the task is complete. Close the issue.
If the issue has an attached Parent issue, and all it's child issues are closed. Then close it too.
If the task is not complete, update the relevant task on the issue and write a comment with what was done. Update the issue tag to 'ready-for-human'

# FINAL RULES

ONLY WORK ON A SINGLE TASK.

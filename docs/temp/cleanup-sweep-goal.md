I want you to work on improving the code quality before we move on to the next features. 
- Find and fix Persistence and Core bugs, using red green TDD. After each bug, decide if a follow-up refactor is warranted, and do it if needed.
- Find and fix Performance issues which are likely under expected homeserver-level loads. After each performance issue, decide if a follow-up refactor is warranted, and do it if needed.
- Locate code which can be refactored to improve readability or maintainability, or prevent bugs and performance issues you have located in the future. Prefer refactors that delete complexity and reduce net production LOC. Do not compress, combine, obscure, or over-abstract code merely to reduce LOC. Large refactors are okay as long as care is taken not to regress.

Document your found bugs and architectural refactors in cleanup-sweep-findings.md. Where a genuine product decision is revealed where you are unsure of the answer and require my input, list those at the end of the file and make no changes.

I highly recommend keeping in mind https://github.com/users/fiso64/projects/1/views/8 and ./webui/DAEMON-AUDIT.md for context on what we will be working on next.

Time budget: approximately 2 hours of active investigation/implementation.

Do not optimize for finishing quickly. The numerical stop conditions are minimum
acceptance criteria, not a signal to stop.

Work  systematically through the remaining Persistence and Core code:
- inspect unreviewed modules
- look for additional genuine correctness bugs and performance problems
- remove accidental complexity and duplication
- simplify APIs and control flow where behavior is preserved

Continue until:
1. the repository areas in scope have received a reasonably comprehensive pass, AND
2. the approximate time budget has been reached.
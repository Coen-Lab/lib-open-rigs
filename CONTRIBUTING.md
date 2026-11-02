# Contributing

How to add docs pages and open a pull request is in [docs/articles/contributing.md](docs/articles/contributing.md). This page covers what we expect in issues, pull requests and commits. It applies to everyone, and in particular to anything written with an AI assistant.

## Issues

- One problem per issue. A bug report or request is about 150 words: what is wrong, how you hit it (versions, what you ran, what came out), and the cause if you know it.
- A fix goes in a pull request from your fork that closes the issue, not as a diff in the issue.
- Long logs or measurements go in a `<details>` block, cut to the lines that matter.
- If you need a decision from a maintainer, ask it as a direct question.
- Search open and closed issues and PRs first. If something was removed or decided in a review, say why that reason no longer holds before proposing it again.

## Pull requests

- From a fork, against `main`, with `Closes #N`.
- About 200 words: what changed, and why if the issue doesn't already say. One line on what was tested and on what (hardware, unit test, simulated device). Don't restate the diff.
- One concern per PR. Unrelated fixes, refactors and follow-ups go in their own PR or issue.
- The smallest change that fixes it, in the idiom of the surrounding code. Use an existing operator or package before writing a new one. No options, layers or abstractions nobody asked for.
- Remove rather than keep: no commented-out code, disabled nodes, unused files or leftover dependencies.
- Names spelled out in full and plural for collections; units go in descriptions, not names. A description says what this class does, not what is true of every class.

### Bonsai workflows

- Keep `WorkflowBuilder Version="2.9.0"`.
- Change only the nodes you mean to. If the editor re-serialises the file, revert the unrelated hunks so the diff can be reviewed.
- No absolute paths, machine names, or subject and session values in committed workflows, YAML or JSON.
- Don't commit `.bonsai/Settings/` or built `.nupkg` files.

## Commits

Imperative subject under 72 characters (`Fix timer to use harp time, not uptime`). A body only when the reason isn't obvious from the diff, and then a few lines.

## AI-assisted contributions

- Say so in one line at the top of the issue or PR. Leave the tool's `Co-Authored-By` trailer off commits: you are the author and answer for the change.
- You have read every line and can defend it in review. If you can't explain a change, take it out.
- Same length and format as everything else here: no confidence scores, badges or emoji headings, and no lists of possible causes or alternative designs. Say what was tested and what wasn't.

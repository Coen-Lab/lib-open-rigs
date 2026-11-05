# Decisions

Why the lab's infrastructure is shaped as it is, for audits. Each entry: what was decided, by whom and when, the reasons, and what was rejected and why. A later decision that reverses one adds an entry and marks the old one superseded; entries are never edited away.

## 2026-11-05: Lab infrastructure leaves Pip's computer (PIPOFFICERIG)

Decided by Pip with Claude, 5 Nov 2026. Built in four stages, in this order; each stage's commits name it.

**Principle.** No lab check, update, deploy gate or scheduled job may depend on Pip's own computer. Pip: "This is not really sustainable... I don't want things depending on my computer." The hourly task "lib-open-rigs update-workflows" ran only while Pip was signed in to PIPOFFICERIG, and its health files (`K:\config\<M>\<M>_WorkflowsUpdate.json`, `_WorkflowsFailed.txt`) existed only to notice when it stopped.

### Stage 1: descriptive script names (done 5 Nov 2026)

- `scripts\push.ps1` is `scripts\push-and-update-workflows.ps1`; `scripts\check-bonsai.ps1` is `scripts\check-workflow-builds.ps1`. Pip: "audit file names to be more descriptive. E.g. \"push\" is not clear enough". Old commit messages keep the old names; rig-setup tests 07 and 18 and ClaudioVisual's `test_rig_health.py` keep `check-bonsai` in fixture text, since those tests go in stage 4.

### Stage 2: the names live on K:, not in git

- `K:\config\device-dictionary.json` (device names as `Kind: [Instances]`, rig.yml settings keys) and `K:\config\data-dictionary.json` (log streams, experiment settings, trial columns with `type` and `required`), edited through rig-setup's Repos page behind the Lab PIN with a change log, as `rollouts.json` is.
- **One lab-wide superset per file, no per-rig lists.** The check only ever asks "is this name listed", and only lab-wide trial columns are required. Pip: "Since these are \"allowed\" names, should it not just be one superset every rig checks against?" Rejected: lists per kind of rig, and keyed by repo ("many kinds will be on multiple rigs").
- **On K:, not in a repo.** Once the meaning of the data (stage 3) moves to the workflows and the manifest, the names list no longer decides how data is read, so it needs no versioning with the code. Accepted cost: nothing off the lab network (CI, outside collaborators) checks names.
- **Checked where it runs, not where it is written.** At deploy on the rig by rig-setup (a failure keeps the previous deployed version and is flagged in the Machines window and ClaudioVisual) and by rig-control at load, which **blocks** the session on an unlisted name; with K: unreachable it uses the last good copy on the rig and says so. Pip: "Can't it just check when loading with rig control?" then "Block". Rejected: a CI names check and a pre-push hook (hooks are per clone and bypassable).

### Stage 3: meaning belongs to each workflow and each session

- Codes (`values`), `unit` and `formerly` leave the dictionary. Each workflow repo defines its own (av-dome owns its `trialType` codes) and each session saves them in its `manifest.json`; `open_rigs.trials` reads them from the session, falling back to the workflow repo at the session's commit for older sessions. Pip: "And why trial type couldn't be different? Aren't we just checking names?" Then `data_dictionary.yml` and `data_dictionary.py` leave this repo.

### Stage 4: one updater, run by whoever pushes; the hourly task retires

- `push-and-update-workflows.ps1` is the only updater, run by whoever pushes here from any machine with Bonsai, .NET, write access to ucl-open and, on a rig, the Lab PIN. It writes `K:\config\lab-package-versions.json`; rig-setup flags a workflow behind it, rig-control **warns** (never blocks) on behind.
- Package versions stay committed in each workflow repo, so each repo runs on its own. Pip, rejecting rigs that load lab packages from a shared path: "But doing this basically creates repos that don't run independently?"
- The repo list stays `K:\config\profile-repos.json`, not a repo. Pip: "people from any pc might change repo list at any time. How can it be a repo?"
- Rejected: a GitHub Actions or GitHub App updater (needs a bot login and the ucl-open owners' approval; too complicated), and a scheduled task on a rig.
- Then `scripts\hourly.ps1` and `scripts\schedule.ps1` go, the task is unregistered on PIPOFFICERIG, and the `*_Workflows*` files on K: are deleted.

### Stage 2, first: the share's config folder holds machines in one place (6 Nov 2026)

- `K:\config\` holds only lab-wide files (`profile-repos.json`, `data-dictionary.json`, `device-dictionary.json`, `rollouts.json`), `_backups\` and `machines\`. Every machine's files are under `config\machines\<MACHINE>\`, including rig-control's card (`rig-control.json`) and its session summary (`<MACHINE>_Sessions.json`, before this in `config\rig-control\sessions\<MACHINE>.json`). Pip, looking at `K:\config`: "This should be tidied up--not a mix of rig names and a bunch of other random folders & files".
- Lab-wide files stay at the top of `config\`, not in a `config\lab\` folder: four or five files never need a folder, and `profile-repos.json`, read by every rig every 30 minutes, keeps its path.
- `config\login-used\` (one stale Lab PIN stamp from 23 Oct; the stamps live in `K:\logins\login-used\`) was deleted, with a backup in `config\_backups\`. `config\RigTransfer\` (ASUS chipset and Serial IO drivers copied by hand on 20 Aug, used by no code) moved to `K:\setup\drivers\`.
- Order: the code in rig-setup, rig-control, ClaudioVisual and `scripts\hourly.ps1` first, then the folders moved; a rig that had not yet taken rig-setup's update wrote to its old folder for up to 30 minutes, merged in afterwards.

**Out of scope, noted.** Rigs restore ucl-open packages with a token on Pip's personal GitHub account (rig-setup-rules.json, until ucl-open publishes to nuget.org, ucl-open/ucl-open#12). pip-hub's Monday rota emails also run on PIPOFFICERIG.

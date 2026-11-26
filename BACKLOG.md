# Backlog

Changes considered and not made, with the reason, so they are not proposed again without a new one.

## Audit 24 Nov 2026 (not recommended)

- C5, "Off Go" after LaserOff: LaserOff does not clear the aim, so Go sends a second Zero and reports "Off Go" for a beam already off. Not changed: the extra Zero is harmless.
- The agent's Zapit backstop skips while another Bonsai runs: not changed, as a running Bonsai may own the NI lines and zeroing them under it would fight it.
- No backstop for a native workflow that dies before Ready (the agent marks it native only at Ready): not changed, as before Ready no command has driven the lines, and ZapitDaq zeroes them when it opens.

## Streamed early stop, 26 Nov 2026 (open)

- One hang after a streamed trial ran dry, cause unknown: on B2's USB-6453 (Dev1), a trial cut short ran out of samples at its ramp's end, but NI's count of samples generated stayed short of the samples written, so nothing ended the trial (no Stopped in 6 s). Patched, not explained: with all its samples written, ZapitDaq's feeder asks the card whether the task is done (`IsDone`, which reports the run-out), and `ACutTrialEndsThoughTheDaqsCountStaysShortOfItsEnd` holds that. Seen once in the first idle hardware run, not in the 80 trials after the patch or in the 42-trial Bonsai session. Keep the patch; find the cause if a Stopped is ever late again.

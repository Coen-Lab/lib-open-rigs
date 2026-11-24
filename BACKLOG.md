# Backlog

Changes considered and not made, with the reason, so they are not proposed again without a new one.

## Audit 24 Nov 2026 (not recommended)

- C5, "Off Go" after LaserOff: LaserOff does not clear the aim, so Go sends a second Zero and reports "Off Go" for a beam already off. Not changed: the extra Zero is harmless.
- The agent's Zapit backstop skips while another Bonsai runs: not changed, as a running Bonsai may own the NI lines and zeroing them under it would fight it.
- No backstop for a native workflow that dies before Ready (the agent marks it native only at Ready): not changed, as before Ready no command has driven the lines, and ZapitDaq zeroes them when it opens.

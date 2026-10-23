# Si_AntiCheat

Server-side plausibility checks for Silica dedicated servers, with admin-triggered audits and a written report.

## Why the server has to check

In Silica the **client computes hit damage** and sends one float per hit (`ObjectClientDamageHit`). The server's
handler (`DamageManager.OnReceiveClientDamageHitPacket`) only clamps that float to the victim's remaining health and
subtracts it. It never recomputes the damage from weapon data, never checks that the sender owns the shooting unit it
names, and accepts negative values (healing) from anyone. The vanilla connect-time checksum covers the content folder
on disk, not mods or memory. So a client mod that multiplies its projectile damage one-shots units and structures and
nothing in the game notices.

The server does hold the ground truth: the weapon data of every unit (with Si_UnitBalance overrides applied), unit
positions, top speeds and fire rates. This mod compares what clients claim against that.

## What it does

**Always on (cheap, one lookup and a float compare per hit):**

- **Damage cap.** For every client-reported hit, the largest hit the shooter's unit can deal is computed from the
  server's live data: max over its weapons of impact/ricochet, penetration or splash damage (each is its own packet),
  times the victim's largest weak-spot multiplier, times the game mode's damage scale, times `CapTolerance`.
  `CapMode` = `log` only counts and logs hits above the cap; `clamp` also replaces the reported damage by the cap.
  Hits above the cap are aggregated per player and reported once per `NotifyCooldownSeconds` to the log and to online
  admins ("`!audit <name>` for a report").
- **Sender check.** A Harmony prefix on `NetworkLayer.ProcessMessage` remembers who sent the packet; a damage packet
  whose instigator is not owned by the sender is logged (and counted in audits).

**Only while an admin audits someone (`!audit <player>`):**

- damage / cap ratio per hit, with a per-unit breakdown
- distance shooter to victim / (weapon max range + splash radius + `RangeMargin`)
- hits in the last second / the unit's maximum shots per second
- horizontal speed / the unit's `TopSpeed`, sampled every `SpeedSampleInterval` (boarded passengers skipped)
- healing packets beyond the unit's repair maximum, sender mismatches

Every other player on the server is collected as the **population baseline** over the same window, so lag spikes,
server load and map effects show up on both sides of the comparison. Several players can be audited in parallel
(`MaxParallelAudits`).

## Report

When the window elapses, the player leaves, or an admin runs `!audit stop`, a text report goes to
`UserData/cfg/AntiCheat/reports/<date>_<player>.txt` and a one-line summary to every online admin. The report shows,
per metric, n / p50 / p95 / max / flagged share for the audited player next to the same numbers for the population,
counts (hits above cap, mismatches, healing over max), a per-unit table and the individual events (over-cap hits,
out-of-reach hits, speed spikes).

Verdict rules (stated in the report): a metric stands out when its flagged share is at least 2 % and four times the
population's, or its p95 exceeds the flag threshold and 1.5x the population's p95. Damage or range standing out, three
or more sender mismatches, or two soft metrics (rate, speed, healing) give **SUSPICIOUS**; one soft metric gives
**WATCH**; fewer than 30 hits and 100 speed samples give **INCONCLUSIVE**. Note that vanilla clamps the reported
damage to the victim's health before this mod sees it, so a 9999 claim on a 1000-HP unit shows as ratio
1000 / cap, not 9999 / cap.

## Chat commands (admins with the Kick power)

| Command | Effect |
|---|---|
| `!audit <player> [minutes]` | Start an audit (name, part of a name, or Steam id; default `DefaultAuditMinutes`, max `MaxAuditMinutes`). Silent for everyone but admins. |
| `!audit stop <player>\|all` | End now, write the report, summary to admins. |
| `!audit report [player\|all]` | Write an interim report, keep auditing. |
| `!audit status` | Running audits with live counters, baseline size, cap mode. |
| `!audit cap [off\|log\|clamp]` | Show or set the always-on cap mode (saved to the config). |
| `!audit reload` | Re-read the config. |

## Config

`UserData/cfg/AntiCheat/Si_AntiCheat_Config.json` (written with defaults on first start).

| Key | Default | Meaning |
|---|---|---|
| `CapMode` | `log` | `off`, `log` (count + notify) or `clamp` (also cut the damage). Start with `log` for a few days, read the reports, then switch. |
| `CapTolerance` | 1.25 | Multiplier on the computed maximum before a hit counts as over the cap. |
| `CapNegativeDamage` | false | In `clamp` mode also clamp healing packets to the unit's repair maximum. |
| `NotifyAdmins`, `NotifyCooldownSeconds`, `LogClamps` | true, 60, true | Over-cap aggregation per player. |
| `SenderCheck` | true | Harmony prefix on `NetworkLayer.ProcessMessage`. |
| `DamageFlag`, `RangeFlag`, `RateFlag`, `SpeedFlag` | 1.0, 1.2, 1.5, 1.15 | Ratio above which a sample is "flagged" in the audit metrics. |
| `RangeMargin` | 30 | Metres added to the weapon reach (victim size, movement during flight). |
| `SpeedSampleInterval` | 0.25 | Seconds between position samples during audits. |
| `DefaultAuditMinutes`, `MaxAuditMinutes`, `MaxParallelAudits` | 10, 120, 8 | Audit windows. |
| `PopulationBaseline` | true | Collect the same metrics for everyone else while an audit runs. |
| `ReportOnRoundEnd` | true | Interim report for running audits when the round ends (audits continue into the next round). |

## Build

`Si_AntiCheat/Si_AntiCheat.csproj` (netstandard2.1) references `include/<branch>/*.dll`, copied from
`Projects\_silica_refs`:

```
dotnet build -c Release                           # test branch  -> bin/Release/netstandard2.1/Si_AntiCheat.dll
dotnet build -c Release -p:SilicaBranch=default   # default      -> bin/Release/default/Si_AntiCheat.dll
```

Requires databomb's Admin Mod: the hit hook is its `Event_Damage.OnPrePlayerDamageReceived` (fired inside the client
damage packet handler with a writable damage value), the command goes through `HelperMethods.RegisterAdminCommand`.
No other mod is touched; Si_FriendlyFireLimits and this mod both adjust the same event value in subscription order,
which is fine because the cap is an upper bound.

## Status

0.1.0 (2026-10-23): built for both branches, reference check OK against the live server and 0.9.47, **not yet run in
a live round**. First thing to check in the log after a round: `[AC]` lines with "no weapon profile" (units whose
weapons the profile walk misses) and over-cap lines for players who are obviously not cheating (then raise
`CapTolerance` or fix the profile) before switching `CapMode` to `clamp`.

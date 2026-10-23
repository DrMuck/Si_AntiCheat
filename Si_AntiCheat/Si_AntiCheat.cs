/*
 Si_AntiCheat - server-side plausibility checks for client-reported damage, with admin-triggered audits.

 Silica clients compute hit damage themselves and send one float per hit (ObjectClientDamageHit); the server only
 clamps it to the victim's health. This mod compares every such hit with the largest hit the shooter's unit can deal
 according to the server's own data (UnitBalance overrides included), optionally clamps it, and lets an admin audit
 selected players: damage ratio, distance vs weapon reach, hit rate, movement speed, and whether the packet's sender
 owns the unit it names. Everyone else is collected as a population baseline over the same window, so lag and map
 effects cancel out in the report.

   !audit <player> [minutes]   start an audit (default minutes from config)
   !audit stop <player>|all    end now and write the report
   !audit report [player|all]  write a report now, keep auditing
   !audit status               running audits, population size, cap mode
   !audit cap [off|log|clamp]  show or set the always-on damage cap mode (saved)
   !audit reload               re-read the config

 Requires the Admin Mod (Event_Damage.OnPrePlayerDamageReceived, Power.Kick for the command).
*/

using MelonLoader;
using SilicaAdminMod;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

[assembly: MelonInfo(typeof(Si_AntiCheat.AntiCheatMod), "Si_AntiCheat", Si_AntiCheat.AntiCheatMod.Version, "DrMuck")]
[assembly: MelonGame("Bohemia Interactive", "Silica")]

namespace Si_AntiCheat
{
    public class AntiCheatMod : MelonMod
    {
        public const string Version = "0.1.0";
        const string Tag = "<color=#FF9F5A>[AC]</color> ";
        internal static AntiCheatConfig Cfg = new AntiCheatConfig();

        static bool _registered;

        // hits in the last second, per shooter (only while that shooter is audited or part of the baseline)
        static readonly Dictionary<ulong, Queue<float>> _hitTimes = new Dictionary<ulong, Queue<float>>();

        // over-cap hits aggregated per shooter for one log line / admin chat line per cooldown
        class ClampAgg { public int Count; public float MaxRatio; public string Unit = ""; public float Removed; public float First; }
        static readonly Dictionary<ulong, ClampAgg> _clampAgg = new Dictionary<ulong, ClampAgg>();
        static readonly Dictionary<ulong, float> _mismatchLog = new Dictionary<ulong, float>();

        // movement sampling (audits only)
        class SpeedTrack { public Unit? Unit; public Vector3 Pos; public float T; }
        static readonly Dictionary<ulong, SpeedTrack> _speed = new Dictionary<ulong, SpeedTrack>();
        static float _nextSpeedSample;
        static float _lastError;

        // ------------------------------------------------------------------ lifecycle

        public override void OnLateInitializeMelon()
        {
            Cfg = AntiCheatConfig.Load();
            if (!RegisteredMelons.Any(m => m.Info.Name == "Admin Mod"))
            {
                MelonLogger.Warning("[AC] Admin Mod not found: Si_AntiCheat needs its damage event and command system, doing nothing");
                return;
            }
            try
            {
                HelperMethods.RegisterAdminCommand("audit", Command_Audit, Power.Kick,
                    "Audit a player for cheating. Usage: !audit <player> [minutes] | !audit stop|report <player>|all | !audit status | !audit cap [off|log|clamp]");
                Event_Damage.OnPrePlayerDamageReceived += OnPreDamage;
                if (Cfg.SenderCheck) SenderPatch.Apply(HarmonyInstance);
                _registered = true;
                MelonLogger.Msg("[AC] Si_AntiCheat v" + Version + " loaded, cap mode " + Cfg.CapMode + ", !audit registered");
            }
            catch (Exception ex) { MelonLogger.Error("[AC] init failed: " + ex); }
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            if (!_registered) return;
            GameEvents.OnGameStarted -= OnGameStarted; GameEvents.OnGameStarted += OnGameStarted;
            GameEvents.OnGameEnded -= OnGameEnded; GameEvents.OnGameEnded += OnGameEnded;
            GameEvents.OnPlayerLeft -= OnPlayerLeft; GameEvents.OnPlayerLeft += OnPlayerLeft;
        }

        static void OnGameStarted(GameMode mode)
        {
            Profiles.Clear();
            _hitTimes.Clear();
            _speed.Clear();
        }

        static void OnGameEnded(GameMode mode, Team winner)
        {
            try
            {
                if (Cfg.ReportOnRoundEnd)
                {
                    foreach (AuditSession s in Audit.Sessions.Values.ToList())
                    {
                        Audit.WriteReport(s, Cfg, "round ended, audit continues");
                        NotifyAdmins("interim: " + Audit.ChatSummary(s, Cfg));
                    }
                }
                if (Audit.Any) Audit.ResetPopulation();
                _hitTimes.Clear();
                _speed.Clear();
            }
            catch (Exception ex) { MelonLogger.Warning("[AC] round end: " + ex.Message); }
        }

        static void OnPlayerLeft(Player p)
        {
            try
            {
                if (p == null) return;
                ulong sid = p.PlayerID.m_ID;
                _hitTimes.Remove(sid);
                _speed.Remove(sid);
                _clampAgg.Remove(sid);
                if (Audit.Sessions.TryGetValue(sid, out AuditSession s))
                {
                    s.S.Note("player left the server");
                    Finish(s, "player left");
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[AC] player left: " + ex.Message); }
        }

        public override void OnUpdate()
        {
            if (!_registered || !Cfg.Enabled) return;
            try
            {
                float now = Time.realtimeSinceStartup;
                FlushAggregates(now);
                if (!Audit.Any) return;
                foreach (AuditSession s in Audit.Sessions.Values.ToList())
                    if (s.Expired) Finish(s, "time window elapsed");
                if (Audit.Any) SampleSpeeds(now);
            }
            catch (Exception ex) { ErrorThrottled("update", ex); }
        }

        // ------------------------------------------------------------------ the hit handler

        static void OnPreDamage(object? sender, OnPreDamageReceivedArgs args)
        {
            if (!Cfg.Enabled || args == null) return;
            try
            {
                DamageManager victim = args.DamageManager;
                float dmg = args.Damage;
                GameObject inst = args.Instigator;
                BaseGameObject? shooterObj = inst != null ? inst.GetComponent<BaseGameObject>() : null;
                Player? shooter = Attribute(shooterObj);
                ulong sid = shooter != null ? shooter.PlayerID.m_ID : 0;
                Stats? st = Audit.StatsFor(sid, Cfg);
                float now = Time.realtimeSinceStartup;

                // 1. sender must own the instigator it names (the sender is the suspect, not the named unit's owner)
                if (Cfg.SenderCheck && SenderPatch.HaveSender)
                {
                    ulong senderId = SenderPatch.Current.m_ID;
                    if (senderId != 0 && (shooter == null || sid != senderId))
                    {
                        Stats? ss = Audit.StatsFor(senderId, Cfg);
                        string senderName = NameOf(senderId);
                        string what = "sender " + senderName + " reported " + dmg.ToString("0", CultureInfo.InvariantCulture) + " damage to " + Profiles.DisplayName(victim.Owner) +
                                      " by " + (shooterObj != null ? Profiles.DisplayName(shooterObj) : "no instigator") + " owned by " + (shooter != null ? shooter.PlayerName : "nobody");
                        if (ss != null) { ss.SenderMismatch++; ss.Note("mismatch: " + what); }
                        if (!_mismatchLog.TryGetValue(senderId, out float last) || now - last > 10f)
                        {
                            _mismatchLog[senderId] = now;
                            MelonLogger.Warning("[AC] " + what);
                        }
                    }
                }

                if (shooterObj == null)
                {
                    if (st != null) st.Unknown++;
                    return;
                }
                UnitProfile? prof = Profiles.For(shooterObj);
                if (prof == null || prof.Entries == 0)
                {
                    if (st != null) { st.Unknown++; st.Hits++; }
                    if (Cfg.DebugLog) MelonLogger.Msg("[AC] no weapon profile for " + Profiles.DisplayName(shooterObj));
                    return;
                }
                if (st != null) st.Hits++;

                float mult = Profiles.VictimMultiplier(victim);
                float scale = ModeScale(dmg);
                if (dmg >= 0f)
                {
                    // 2. damage vs the largest hit this unit can deal (vanilla already clamped dmg to the victim's health)
                    float cap = prof.MaxHit * mult * scale * Cfg.CapTolerance;
                    float ratio = cap > 0f ? dmg / cap : (dmg > 0f ? 99f : 0f);
                    if (st != null)
                    {
                        st.Damage.Add(ratio, Cfg.DamageFlag);
                        st.NoteUnit(prof.Name, ratio, Cfg.DamageFlag);
                    }
                    if (dmg > cap && Cfg.CapActive)
                    {
                        float removed = dmg - cap;
                        if (Cfg.Clamp) args.Damage = cap;
                        if (st != null)
                        {
                            st.Clamps++;
                            st.ClampedDamage += removed;
                            if (st.Notes.Count < 150 && st != Audit.Population)
                                st.Note("over cap: " + dmg.ToString("0", CultureInfo.InvariantCulture) + " > " + cap.ToString("0", CultureInfo.InvariantCulture) +
                                        " (" + prof.Name + " on " + Profiles.DisplayName(victim.Owner) + ", x" + ratio.ToString("0.00", CultureInfo.InvariantCulture) + ")");
                        }
                        Aggregate(sid, ratio, prof.Name, removed, now);
                    }
                }
                else
                {
                    // 3. healing packets vs the unit's repair maximum
                    float repairCap = prof.MaxRepair * mult * scale * Cfg.CapTolerance;
                    float ratio = repairCap > 0f ? -dmg / repairCap : 99f;
                    if (-dmg > repairCap)
                    {
                        if (st != null)
                        {
                            st.NegativeOver++;
                            if (st != Audit.Population) st.Note("healing " + (-dmg).ToString("0", CultureInfo.InvariantCulture) + " > " + repairCap.ToString("0", CultureInfo.InvariantCulture) + " by " + prof.Name);
                        }
                        if (Cfg.Clamp && Cfg.CapNegativeDamage) args.Damage = -repairCap;
                        if (Cfg.DebugLog) MelonLogger.Msg("[AC] healing over max by " + prof.Name + ": " + (-dmg) + " > " + repairCap);
                    }
                }

                if (st == null) return; // nothing below runs unless an audit is active

                // 4. distance between shooter and victim vs weapon reach
                float dist = Vector3.Distance(shooterObj.transform.position, victim.transform.position);
                float reach = (prof.MaxRange > 0f ? prof.MaxRange + prof.MaxRadius : prof.MaxRadius * 2f) + Cfg.RangeMargin;
                float rr = dist / Mathf.Max(reach, 1f);
                st.Range.Add(rr, Cfg.RangeFlag);
                if (rr > Cfg.RangeFlag && st != Audit.Population && st.Notes.Count < 150)
                    st.Note("out of reach: " + dist.ToString("0", CultureInfo.InvariantCulture) + " m with " + prof.Name + " (reach " + reach.ToString("0", CultureInfo.InvariantCulture) + " m, x" + rr.ToString("0.00", CultureInfo.InvariantCulture) + ")");

                // 5. hits in the last second vs the unit's shot rate
                if (sid != 0)
                {
                    float hps = HitsLastSecond(sid, now);
                    st.Rate.Add(hps / Mathf.Max(prof.ShotsPerSec, 0.1f), Cfg.RateFlag);
                }
            }
            catch (Exception ex) { ErrorThrottled("damage handler", ex); }
        }

        static Player? Attribute(BaseGameObject? bgo)
        {
            if (bgo == null) return null;
            try
            {
                NetworkComponent nc = bgo.NetworkComponent;
                if (nc != null && nc.OwnerPlayer != null) return nc.OwnerPlayer;
            }
            catch { }
            try
            {
                Unit? u = bgo as Unit;
                if (u != null && u.ControlledBy != null) return u.ControlledBy;
            }
            catch { }
            return null;
        }

        static float ModeScale(float dmg)
        {
            try
            {
                GameMode gm = GameMode.CurrentGameMode;
                if (gm == null) return 1f;
                return Mathf.Max(1f, Mathf.Max(gm.GetDamageByPlayerScale(Mathf.Abs(dmg)), gm.GetDamageToPlayerScale()));
            }
            catch { return 1f; }
        }

        static float HitsLastSecond(ulong sid, float now)
        {
            if (!_hitTimes.TryGetValue(sid, out Queue<float> q)) { q = new Queue<float>(); _hitTimes[sid] = q; }
            q.Enqueue(now);
            while (q.Count > 0 && now - q.Peek() > 1f) q.Dequeue();
            return q.Count;
        }

        static void Aggregate(ulong sid, float ratio, string unit, float removed, float now)
        {
            if (!_clampAgg.TryGetValue(sid, out ClampAgg a)) { a = new ClampAgg { First = now }; _clampAgg[sid] = a; }
            a.Count++;
            a.Removed += removed;
            if (ratio > a.MaxRatio) { a.MaxRatio = ratio; a.Unit = unit; }
        }

        static void FlushAggregates(float now)
        {
            if (_clampAgg.Count == 0) return;
            foreach (var kv in _clampAgg.ToList())
            {
                ClampAgg a = kv.Value;
                if (now - a.First < Cfg.NotifyCooldownSeconds) continue;
                _clampAgg.Remove(kv.Key);
                string name = NameOf(kv.Key);
                string line = name + ": " + a.Count + " hits above the server maximum in " + Cfg.NotifyCooldownSeconds + " s (max x" +
                              a.MaxRatio.ToString("0.00", CultureInfo.InvariantCulture) + " with " + a.Unit + ", " +
                              (Cfg.Clamp ? "clamped " : "would clamp ") + a.Removed.ToString("0", CultureInfo.InvariantCulture) + " damage)";
                if (Cfg.LogClamps) MelonLogger.Warning("[AC] " + line);
                if (Cfg.NotifyAdmins && !Audit.Sessions.ContainsKey(kv.Key)) NotifyAdmins(line + " - !audit " + name + " for a report");
            }
        }

        // ------------------------------------------------------------------ movement sampling

        static void SampleSpeeds(float now)
        {
            if (now < _nextSpeedSample) return;
            _nextSpeedSample = now + Cfg.SpeedSampleInterval;
            List<Player> players = Player.Players;
            if (players == null) return;
            foreach (Player p in players)
            {
                if (p == null) continue;
                ulong sid = p.PlayerID.m_ID;
                Stats? st = Audit.StatsFor(sid, Cfg);
                if (st == null) continue;
                if (!_speed.TryGetValue(sid, out SpeedTrack tr)) { tr = new SpeedTrack(); _speed[sid] = tr; }
                Unit? u = p.ControlledUnit;
                if (u == null || u.InCompartment != null) { tr.Unit = null; continue; }
                Vector3 pos = u.transform.position;
                if (tr.Unit != u)
                {
                    tr.Unit = u; tr.Pos = pos; tr.T = now;
                    continue;
                }
                float dt = now - tr.T;
                if (dt > 0.05f && dt <= 1.5f)
                {
                    float top = u.TopSpeed;
                    if (top > 0.5f)
                    {
                        Vector3 d = pos - tr.Pos;
                        d.y = 0f;
                        float ratio = (d.magnitude / dt) / top;
                        st.Speed.Add(ratio, Cfg.SpeedFlag);
                        st.SpeedSamples++;
                        if (ratio > Cfg.SpeedFlag * 2f && st != Audit.Population && st.Notes.Count < 150)
                            st.Note("speed x" + ratio.ToString("0.00", CultureInfo.InvariantCulture) + " with " + Profiles.DisplayName(u) + " (" + (d.magnitude / dt).ToString("0.0", CultureInfo.InvariantCulture) + " m/s, top " + top.ToString("0.0", CultureInfo.InvariantCulture) + ")");
                    }
                }
                tr.Pos = pos; tr.T = now;
            }
        }

        // ------------------------------------------------------------------ sessions

        static void Finish(AuditSession s, string reason)
        {
            Audit.Sessions.Remove(s.SteamId);
            string? path = Audit.WriteReport(s, Cfg, reason);
            string summary = Audit.ChatSummary(s, Cfg);
            MelonLogger.Msg("[AC] audit finished (" + reason + "): " + summary);
            NotifyAdmins("audit finished (" + reason + "): " + summary);
            if (path != null) NotifyAdmins("report: " + System.IO.Path.GetFileName(path));
            if (!Audit.Any) { _hitTimes.Clear(); _speed.Clear(); }
        }

        static void NotifyAdmins(string msg)
        {
            List<Player> players;
            try { players = Player.Players; } catch { return; }
            if (players == null) return;
            foreach (Player p in players)
            {
                if (p == null || !IsAdmin(p)) continue;
                try { HelperMethods.SendChatMessageToPlayer(p, Tag + msg); } catch { }
            }
        }

        static bool IsAdmin(Player p)
        {
            try { return AdminMethods.FindAdminFromSteamId((long)p.PlayerID.m_ID) != null; } catch { return false; }
        }

        static string NameOf(ulong sid)
        {
            try
            {
                Player? p = Player.Players?.FirstOrDefault(x => x != null && x.PlayerID.m_ID == sid);
                if (p != null && !string.IsNullOrEmpty(p.PlayerName)) return p.PlayerName;
            }
            catch { }
            return sid.ToString();
        }

        static void ErrorThrottled(string where, Exception ex)
        {
            float now = Time.realtimeSinceStartup;
            if (now - _lastError < 30f) return;
            _lastError = now;
            MelonLogger.Error("[AC] " + where + ": " + ex);
        }

        // ------------------------------------------------------------------ !audit

        public static void Command_Audit(Player? caller, string args)
        {
            try
            {
                List<string> tok = (args ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                if (tok.Count > 0 && (tok[0].StartsWith("!") || tok[0].StartsWith("/") || tok[0].Equals("audit", StringComparison.OrdinalIgnoreCase) || tok[0].Equals("sam_audit", StringComparison.OrdinalIgnoreCase)))
                    tok.RemoveAt(0);
                if (tok.Count == 0 || tok[0].Equals("help", StringComparison.OrdinalIgnoreCase)) { Help(caller); return; }
                string sub = tok[0].ToLowerInvariant();
                switch (sub)
                {
                    case "status": Status(caller); return;
                    case "stop": StopOrReport(caller, tok.Skip(1).ToList(), stop: true); return;
                    case "report": StopOrReport(caller, tok.Skip(1).ToList(), stop: false); return;
                    case "cap": CapMode(caller, tok.Count > 1 ? tok[1] : ""); return;
                    case "reload":
                        Cfg = AntiCheatConfig.Load();
                        Reply(caller, "config reloaded, cap mode " + Cfg.CapMode);
                        return;
                }
                // start: <player name words...> [minutes]
                int minutes = Cfg.DefaultAuditMinutes;
                if (tok.Count > 1 && int.TryParse(tok[tok.Count - 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int m))
                {
                    minutes = Math.Max(1, Math.Min(Cfg.MaxAuditMinutes, m));
                    tok.RemoveAt(tok.Count - 1);
                }
                string query = string.Join(" ", tok);
                Player? target = FindPlayer(query, out string why);
                if (target == null) { Reply(caller, why + ". Usage: !audit <player> [minutes]"); return; }
                ulong sid = target.PlayerID.m_ID;
                if (Audit.Sessions.TryGetValue(sid, out AuditSession running))
                {
                    Reply(caller, target.PlayerName + " is already audited, " + running.MinutesLeft.ToString("0.0", CultureInfo.InvariantCulture) + " min left (" + running.S.Hits + " hits). !audit stop / report " + target.PlayerName);
                    return;
                }
                if (Audit.Sessions.Count >= Cfg.MaxParallelAudits) { Reply(caller, "already " + Audit.Sessions.Count + " audits running (MaxParallelAudits)"); return; }
                AuditSession s = Audit.Start(sid, target.PlayerName, minutes, caller);
                MelonLogger.Msg("[AC] audit started on " + target.PlayerName + " (" + sid + ") for " + minutes + " min by " + s.StartedByName);
                Reply(caller, "auditing " + target.PlayerName + " for " + minutes + " min (cap mode " + Cfg.CapMode + ", baseline " + (Cfg.PopulationBaseline ? "on" : "off") + "). Report to admins + file when done.");
            }
            catch (Exception ex)
            {
                MelonLogger.Error("[AC] command: " + ex);
                Reply(caller, "error: " + ex.Message);
            }
        }

        static void Help(Player? caller)
        {
            Reply(caller, "!audit <player> [minutes] | stop <player>|all | report [player|all] | status | cap [off|log|clamp] | reload");
        }

        static void Status(Player? caller)
        {
            Reply(caller, "cap mode " + Cfg.CapMode + " (tol " + Cfg.CapTolerance.ToString("0.00", CultureInfo.InvariantCulture) + "), sender check " + (SenderPatch.Applied ? "on" : "off") +
                          ", profiles cached " + Profiles.CacheCount + ", audits " + Audit.Sessions.Count +
                          (Audit.Any ? ", baseline " + Audit.PopulationPlayers.Count + " players / " + Audit.Population.Hits + " hits" : ""));
            foreach (AuditSession s in Audit.Sessions.Values)
                Reply(caller, "  " + s.Name + ": " + s.MinutesLeft.ToString("0.0", CultureInfo.InvariantCulture) + " min left, hits " + s.S.Hits + ", above cap " + s.S.Clamps +
                              ", dmg max x" + s.S.Damage.Max.ToString("0.00", CultureInfo.InvariantCulture) + ", range max x" + s.S.Range.Max.ToString("0.00", CultureInfo.InvariantCulture) +
                              ", speed max x" + s.S.Speed.Max.ToString("0.00", CultureInfo.InvariantCulture) + ", mismatches " + s.S.SenderMismatch);
        }

        static void StopOrReport(Player? caller, List<string> rest, bool stop)
        {
            if (Audit.Sessions.Count == 0) { Reply(caller, "no audit running"); return; }
            string query = string.Join(" ", rest);
            List<AuditSession> targets;
            if (query.Length == 0 || query.Equals("all", StringComparison.OrdinalIgnoreCase)) targets = Audit.Sessions.Values.ToList();
            else
            {
                targets = Audit.Sessions.Values.Where(s => s.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (targets.Count == 0) { Reply(caller, "no audit matches '" + query + "'"); return; }
            }
            foreach (AuditSession s in targets)
            {
                if (stop) Finish(s, "stopped by " + (caller != null ? caller.PlayerName : "console"));
                else
                {
                    string? path = Audit.WriteReport(s, Cfg, "interim report requested by " + (caller != null ? caller.PlayerName : "console"));
                    Reply(caller, Audit.ChatSummary(s, Cfg) + (path != null ? " | " + System.IO.Path.GetFileName(path) : ""));
                }
            }
        }

        static void CapMode(Player? caller, string mode)
        {
            mode = (mode ?? "").Trim().ToLowerInvariant();
            if (mode.Length == 0) { Reply(caller, "cap mode " + Cfg.CapMode + " (off = nothing, log = count hits above the maximum, clamp = also cut them to the maximum)"); return; }
            if (mode != "off" && mode != "log" && mode != "clamp") { Reply(caller, "cap mode must be off, log or clamp"); return; }
            Cfg.CapMode = mode;
            Cfg.Save();
            MelonLogger.Msg("[AC] cap mode set to " + mode + " by " + (caller != null ? caller.PlayerName : "console"));
            Reply(caller, "cap mode " + mode + " (saved)");
        }

        internal static Player? FindPlayer(string query, out string why)
        {
            why = "";
            List<Player> players;
            try { players = Player.Players; } catch { why = "player list unavailable"; return null; }
            if (players == null) { why = "player list unavailable"; return null; }
            string q = query.Trim();
            if (q.Length == 0) { why = "no player given"; return null; }
            var exact = players.Where(p => p != null && string.Equals(p.PlayerName, q, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1) return exact[0];
            if (ulong.TryParse(q, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong id))
            {
                Player? byId = players.FirstOrDefault(p => p != null && p.PlayerID.m_ID == id);
                if (byId != null) return byId;
            }
            var partial = players.Where(p => p != null && (p.PlayerName ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (partial.Count == 1) return partial[0];
            why = partial.Count == 0 ? "no player matches '" + q + "'" : "'" + q + "' matches " + partial.Count + " players: " + string.Join(", ", partial.Take(5).Select(p => p.PlayerName));
            return null;
        }

        static void Reply(Player? p, string msg)
        {
            if (p == null) { MelonLogger.Msg("[AC] " + msg); return; }
            HelperMethods.SendChatMessageToPlayer(p, Tag + msg);
        }
    }
}

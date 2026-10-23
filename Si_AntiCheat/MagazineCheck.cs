using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace Si_AntiCheat
{
    /// <summary>
    /// Magazine / fire-rate check. The server never sees ammo, but every client sends a FIRE RPC (bool) when the trigger
    /// state changes and a SYNCHRELOAD RPC at reload start and end, for soldiers (HumanHandsAnimator) and vehicle
    /// turrets (VehicleTurret, primary and secondary). Firing time between FIRE on and off, divided by the weapon's fire
    /// delay, gives the shots fired; summed since the last reload and divided by the magazine size (live data, so
    /// UnitBalance overrides count) it must stay near or below 1.0. A bigger magazine, a faster fire rate or a removed
    /// reload all push it above. For soldiers the damage packets add two more counters: hits per magazine beyond what
    /// the magazine can hold, and hits that arrive while the weapon has not fired for longer than a projectile lives.
    /// The booleans are peeked from the packet in a prefix (position restored) because the server copy's own state can
    /// diverge (StartReload on a remote copy may refuse). Everything is gated by AntiCheatMod.Armed.
    /// </summary>
    public static class MagazineCheck
    {
        public static bool Applied;

        class WeaponState
        {
            public float FireStart = -1f;   // realtime when the current burst began, -1 when not firing
            public float LastFireOff = -1f; // realtime of the last FIRE off
            public float Created;
            public float Shots;             // shots since the last reload (integrated)
            public int Hits;                // damage packets since the last reload (soldiers only)
            public bool OverMagazine;       // already counted for this cycle
            public bool HitsOver;
            public UnityEngine.Object? Bullet;
        }

        static readonly Dictionary<HumanHandsAnimator, WeaponState> _soldiers = new Dictionary<HumanHandsAnimator, WeaponState>();
        static readonly Dictionary<VehicleTurret, WeaponState[]> _turrets = new Dictionary<VehicleTurret, WeaponState[]>();
        static FieldInfo? _streamField;
        static float _lastError;

        public static void Clear() { _soldiers.Clear(); _turrets.Clear(); }
        public static int Tracked => _soldiers.Count + _turrets.Count;

        public static void Apply(HarmonyLib.Harmony harmony)
        {
            try
            {
                _streamField = AccessTools.Field(typeof(GameByteStreamReader), "m_MemoryStream");
                if (_streamField == null) { MelonLogger.Warning("[AC] GameByteStreamReader.m_MemoryStream not found: magazine check disabled"); return; }
                MethodInfo? a = AccessTools.Method(typeof(HumanHandsAnimator), "OnNetRPC");
                MethodInfo? t = AccessTools.Method(typeof(VehicleTurret), "OnNetRPC");
                if (a == null || t == null) { MelonLogger.Warning("[AC] OnNetRPC not found (animator " + (a != null) + ", turret " + (t != null) + "): magazine check disabled"); return; }
                harmony.Patch(a, prefix: new HarmonyMethod(typeof(MagazineCheck), nameof(AnimatorPrefix)));
                harmony.Patch(t, prefix: new HarmonyMethod(typeof(MagazineCheck), nameof(TurretPrefix)));
                Applied = true;
                MelonLogger.Msg("[AC] magazine check active (fire/reload RPC prefixes)");
            }
            catch (Exception ex) { MelonLogger.Warning("[AC] magazine check patch failed: " + ex.Message); }
        }

        // ------------------------------------------------------------------ packet peek

        /// <summary>Reads the RPC payload without consuming it: [packed uint32 fired rounds if shotByShot] bool.</summary>
        static bool Peek(GameByteStreamReader reader, bool shotByShot, out bool value, out int firedRounds)
        {
            value = false; firedRounds = -1;
            if (_streamField == null) return false;
            MemoryStream? ms = _streamField.GetValue(reader) as MemoryStream;
            if (ms == null) return false;
            long pos = ms.Position;
            try
            {
                if (shotByShot) firedRounds = (int)reader.ReadPackedUInt32();
                value = reader.ReadBool();
                return true;
            }
            catch { return false; }
            finally { ms.Position = pos; }
        }

        // ------------------------------------------------------------------ soldiers

        static void AnimatorPrefix(HumanHandsAnimator __instance, byte rpcIndex, GameByteStreamReader packetReader)
        {
            if (!AntiCheatMod.Armed || __instance == null) return;
            try
            {
                Soldier sol = __instance.CharacterController;
                if (sol == null) return;
                ulong sid = AntiCheatMod.OwnerId(sol);
                if (sid == 0) return;                       // server-owned (AI) unit: no client packets to judge
                Stats? st = Audit.StatsFor(sid, AntiCheatMod.Cfg);
                if (st == null) return;
                WeaponState w = SoldierState(__instance);
                int magazine = __instance.MagazineSize;
                float delay = Mathf.Max(__instance.FireDelay, 0.01f);
                float now = Time.realtimeSinceStartup;
                string unit = Profiles.DisplayName(sol);
                if (rpcIndex == 0)
                {
                    if (!Peek(packetReader, false, out bool firing, out _)) return;
                    OnFire(w, firing, now, delay, 1, magazine, st, unit);
                }
                else if (rpcIndex == 1)
                {
                    if (!Peek(packetReader, __instance.ReloadShotByShot, out bool start, out int fired)) return;
                    OnReload(w, start, now, delay, 1, magazine, st, unit, fired);
                }
            }
            catch (Exception ex) { Error("animator rpc", ex); }
        }

        static WeaponState SoldierState(HumanHandsAnimator a)
        {
            if (!_soldiers.TryGetValue(a, out WeaponState w)) { w = new WeaponState { Created = Time.realtimeSinceStartup }; _soldiers[a] = w; }
            if (!ReferenceEquals(w.Bullet, a.BulletType))
            {
                // weapon changed (pickup): a new magazine, counts start over
                w.Bullet = a.BulletType; w.Shots = 0f; w.Hits = 0; w.OverMagazine = false; w.HitsOver = false; w.FireStart = -1f;
            }
            return w;
        }

        /// <summary>Called from the damage handler for every rated hit of a soldier instigator.</summary>
        public static void OnSoldierHit(Soldier sol, Stats st, float now)
        {
            try
            {
                HumanHandsAnimator a = sol.HumanHandsAnimator;
                if (a == null || a.MagazineSize <= 0) return;
                ProjectileData b = a.BulletType;
                if (b == null || b.m_bSplash) return;       // splash hits several victims per shot: no per-shot bookkeeping
                WeaponState w = SoldierState(a);
                w.Hits++;
                int perShot = b.m_bPenetrating ? 2 : 1;     // penetration is a second packet for the same bullet
                int allowed = a.MagazineSize * perShot + 2;
                if (w.Hits > allowed && !w.HitsOver)
                {
                    w.HitsOver = true;
                    st.HitsOverMagazine++;
                    if (st != Audit.Population) st.Note("hits over magazine: " + w.Hits + " hit packets since the last reload, magazine " + a.MagazineSize + " (" + Profiles.DisplayName(sol) + ")");
                }
                // not firing: no burst running and the last one ended longer ago than a bullet lives (plus slack)
                float age = now - w.Created;
                float lifetime = b.m_fLifeTime + 1f;
                bool idle = w.FireStart < 0f && (w.LastFireOff >= 0f ? now - w.LastFireOff > lifetime : age > lifetime);
                if (idle)
                {
                    st.HitsNotFiring++;
                    if (st != Audit.Population && st.Notes.Count < 150 && st.HitsNotFiring <= 5)
                        st.Note("hit while not firing: " + Profiles.DisplayName(sol) + ", last FIRE off " + (w.LastFireOff >= 0f ? (now - w.LastFireOff).ToString("0.0", CultureInfo.InvariantCulture) + " s ago" : "never"));
                }
            }
            catch (Exception ex) { Error("soldier hit", ex); }
        }

        // ------------------------------------------------------------------ vehicle turrets

        static void TurretPrefix(VehicleTurret __instance, byte rpcIndex, GameByteStreamReader packetReader)
        {
            if (!AntiCheatMod.Armed || __instance == null) return;
            try
            {
                Vehicle veh = __instance.Vehicle;
                if (veh == null) return;
                ulong sid = AntiCheatMod.OwnerId(veh);
                if (sid == 0) return;
                Stats? st = Audit.StatsFor(sid, AntiCheatMod.Cfg);
                if (st == null) return;
                if (!_turrets.TryGetValue(__instance, out WeaponState[] ws))
                {
                    float c = Time.realtimeSinceStartup;
                    ws = new[] { new WeaponState { Created = c }, new WeaponState { Created = c } };
                    _turrets[__instance] = ws;
                }
                bool primary = rpcIndex == 0 || rpcIndex == 2;
                WeaponState w = ws[primary ? 0 : 1];
                int magazine = primary ? __instance.PrimaryMagazineSize : __instance.SecondaryMagazineSize;
                float delay = Mathf.Max(primary ? __instance.PrimaryFireInterval : __instance.SecondaryFireInterval, 0.01f);
                int perShot = Math.Max(1, primary ? __instance.PrimaryShotCount : __instance.SecondaryShotCount);
                float now = Time.realtimeSinceStartup;
                string unit = Profiles.DisplayName(veh) + (primary ? " primary" : " secondary");
                if (rpcIndex == 0 || rpcIndex == 1)
                {
                    if (!Peek(packetReader, false, out bool firing, out _)) return;
                    OnFire(w, firing, now, delay, perShot, magazine, st, unit);
                }
                else if (rpcIndex == 2 || rpcIndex == 3)
                {
                    bool shotByShot = primary ? __instance.PrimaryReloadShotByShot : __instance.SecondaryReloadShotByShot;
                    if (!Peek(packetReader, shotByShot, out bool start, out int fired)) return;
                    OnReload(w, start, now, delay, perShot, magazine, st, unit, fired);
                }
            }
            catch (Exception ex) { Error("turret rpc", ex); }
        }

        // ------------------------------------------------------------------ shared bookkeeping

        static void OnFire(WeaponState w, bool firing, float now, float delay, int perShot, int magazine, Stats st, string unit)
        {
            if (firing)
            {
                // a FIRE on while a burst is open means the off was lost: drop the stale burst rather than inflate it
                w.FireStart = now;
                return;
            }
            if (w.FireStart < 0f) return;
            float burst = Mathf.Max(1f, (now - w.FireStart) / delay) * perShot;
            if (magazine > 0) burst = Mathf.Min(burst, magazine * 1.5f);   // one late off packet cannot fake a whole magazine
            w.Shots += burst;
            w.FireStart = -1f;
            w.LastFireOff = now;
            Sample(w, magazine, st, unit, now);
        }

        static void OnReload(WeaponState w, bool start, float now, float delay, int perShot, int magazine, Stats st, string unit, int firedClaimed)
        {
            if (start)
            {
                if (w.FireStart >= 0f)
                {
                    w.Shots += Mathf.Max(1f, (now - w.FireStart) / delay) * perShot;
                    w.FireStart = -1f;
                    w.LastFireOff = now;
                }
                Sample(w, magazine, st, unit, now);
                if (firedClaimed >= 0 && magazine > 0 && firedClaimed > magazine + 1 && st != Audit.Population)
                    st.Note("reload claims " + firedClaimed + " fired rounds, magazine " + magazine + " (" + unit + ")");
                w.Shots = 0f; w.Hits = 0; w.OverMagazine = false; w.HitsOver = false;
            }
            // reload end: nothing to do, the counters were reset at the start
        }

        static void Sample(WeaponState w, int magazine, Stats st, string unit, float now)
        {
            if (magazine <= 0 || w.Shots <= 0f) return;
            float ratio = w.Shots / magazine;
            st.Magazine.Add(ratio, AntiCheatMod.Cfg.MagazineFlag);
            if (ratio > AntiCheatMod.Cfg.MagazineFlag && !w.OverMagazine)
            {
                w.OverMagazine = true;
                st.MagazineOver++;
                if (st != Audit.Population && st.Notes.Count < 150)
                    st.Note("over magazine: ~" + w.Shots.ToString("0", CultureInfo.InvariantCulture) + " shots since the last reload, magazine " + magazine + " (" + unit + ", x" + ratio.ToString("0.00", CultureInfo.InvariantCulture) + ")");
            }
        }

        static void Error(string where, Exception ex)
        {
            float now = Time.realtimeSinceStartup;
            if (now - _lastError < 30f) return;
            _lastError = now;
            MelonLogger.Error("[AC] magazine " + where + ": " + ex);
        }
    }
}

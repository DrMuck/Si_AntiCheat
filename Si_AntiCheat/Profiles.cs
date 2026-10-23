using System;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;

namespace Si_AntiCheat
{
    /// <summary>
    /// What a unit can do at most, from the server's own live data (so Si_UnitBalance overrides are included):
    /// the biggest single hit any of its weapons can deal, its repair maximum, its reach and its shot rate.
    /// Same walk as the game's DamageCalculatorEntry (VehicleTurret, TurretWeapon, CreatureDecapod attacks,
    /// Soldier hands animator), plus Weapon items under a soldier.
    /// </summary>
    public class UnitProfile
    {
        public string Name = "?";
        public float MaxHit;        // largest positive damage one packet can legitimately carry (impact/ricochet, penetration or splash, each is its own packet)
        public float MaxRepair;     // largest negative damage (healing) one packet can carry
        public float MaxRange;      // metres: max over weapons of speed × lifetime (0 for melee)
        public float MaxRadius;     // metres: largest splash radius or melee reach
        public float ShotsPerSec;   // sum over weapons of shots per second (upper bound for hits per second)
        public int Entries;

        public override string ToString() =>
            Name + " hit<=" + MaxHit.ToString("0") + (MaxRepair > 0 ? " repair<=" + MaxRepair.ToString("0") : "") +
            " range<=" + MaxRange.ToString("0") + "+" + MaxRadius.ToString("0") + "m rate<=" + ShotsPerSec.ToString("0.0") + "/s (" + Entries + " entries)";
    }

    public static class Profiles
    {
        static readonly Dictionary<ObjectInfo, UnitProfile> _cache = new Dictionary<ObjectInfo, UnitProfile>();
        static readonly Dictionary<ObjectInfo, float> _victimMult = new Dictionary<ObjectInfo, float>();

        public static void Clear() { _cache.Clear(); _victimMult.Clear(); }
        public static int CacheCount => _cache.Count;

        /// <summary>Profile of the instigator. Soldiers are never cached: their hands animator follows the weapon they hold.</summary>
        public static UnitProfile? For(BaseGameObject bgo)
        {
            if (bgo == null) return null;
            Soldier? soldier = bgo as Soldier;
            if (soldier != null) return Build(bgo);
            ObjectInfo info = bgo.ObjectInfo;
            if (info == null) return Build(bgo);
            if (_cache.TryGetValue(info, out UnitProfile p)) return p;
            p = Build(bgo);
            _cache[info] = p;
            return p;
        }

        static UnitProfile Build(BaseGameObject bgo)
        {
            UnitProfile p = new UnitProfile { Name = DisplayName(bgo) };
            try
            {
                foreach (VehicleTurret t in bgo.GetComponentsInChildren<VehicleTurret>(true))
                {
                    if (t.PrimaryProjectileData != null) AddProjectile(p, t.PrimaryProjectileData, Math.Max(1, t.PrimaryShotCount), t.PrimaryFireInterval);
                    if (t.SecondaryProjectileData != null) AddProjectile(p, t.SecondaryProjectileData, Math.Max(1, t.SecondaryShotCount), t.SecondaryFireInterval);
                }
                foreach (TurretWeapon t in bgo.GetComponentsInChildren<TurretWeapon>(true))
                {
                    if (t.ProjectileData != null) AddProjectile(p, t.ProjectileData, 1, t.FireInterval);
                }
                CreatureDecapod? creature = bgo as CreatureDecapod;
                if (creature != null)
                {
                    if (creature.AttackPrimary != null) AddCreatureAttack(p, creature.AttackPrimary);
                    if (creature.AttackSecondary != null) AddCreatureAttack(p, creature.AttackSecondary);
                }
                Soldier? soldier = bgo as Soldier;
                if (soldier != null)
                {
                    HumanHandsAnimator hands = soldier.HumanHandsAnimator;
                    if (hands != null && hands.BulletType != null) AddProjectile(p, hands.BulletType, 1, hands.FireDelay);
                    foreach (Weapon w in bgo.GetComponentsInChildren<Weapon>(true))
                    {
                        if (w.BulletType != null) AddProjectile(p, w.BulletType, 1, w.FireDelay);
                        if (w.BulletTypeTracer != null) AddProjectile(p, w.BulletTypeTracer, 1, w.FireDelay, countRate: false);
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[AC] profile build failed for " + p.Name + ": " + ex.Message);
            }
            return p;
        }

        static void AddProjectile(UnitProfile p, ProjectileData d, int perShot, float interval, bool countRate = true)
        {
            float direct = d.m_fImpactDamage;
            if (d.m_bRicochet && d.m_fRicochetDamage > direct) direct = d.m_fRicochetDamage;
            Consider(p, direct);
            if (d.m_bPenetrating) Consider(p, d.m_fPenetratingDamage);
            if (d.m_bSplash)
            {
                Consider(p, d.m_fSplashDamageMax);
                if (d.m_fSplashDamageRadiusMax > p.MaxRadius) p.MaxRadius = d.m_fSplashDamageRadiusMax;
            }
            float speedMax = d.m_fBaseSpeed * Mathf.Max(1f, d.m_fBaseSpeedRandomMax);
            float range = speedMax * d.m_fLifeTime;
            if (range > p.MaxRange) p.MaxRange = range;
            if (countRate)
            {
                float rate = (interval > 0f ? 1f / interval : 1f) * perShot;
                p.ShotsPerSec += rate;
            }
            p.Entries++;
        }

        static void AddCreatureAttack(UnitProfile p, CreatureAttack a)
        {
            if (a.IsRanged && a.AttackProjectileData != null)
            {
                AddProjectile(p, a.AttackProjectileData, 1, a.Time);
                return;
            }
            Consider(p, a.Damage);
            if (a.Radius > p.MaxRadius) p.MaxRadius = a.Radius;
            p.ShotsPerSec += a.Time > 0f ? 1f / a.Time : 1f;
            p.Entries++;
        }

        static void Consider(UnitProfile p, float dmg)
        {
            if (dmg > p.MaxHit) p.MaxHit = dmg;
            if (dmg < 0f && -dmg > p.MaxRepair) p.MaxRepair = -dmg;
        }

        /// <summary>
        /// Upper bound of what the victim's weak-spot modifiers can multiply a hit by (product of all multipliers above 1,
        /// damage type ignored). Reductions only lower damage and are left out, so this is always >= 1.
        /// </summary>
        public static float VictimMultiplier(DamageManager dm)
        {
            if (dm == null) return 1f;
            ObjectInfo? info = dm.Owner != null ? dm.Owner.ObjectInfo : null;
            if (info != null && _victimMult.TryGetValue(info, out float cached)) return cached;
            float best = 1f;
            try
            {
                List<DamageModifier> mods = dm.DamageModifiers;
                if (mods != null)
                {
                    float scale = dm.GetSizeMultiplier(1f);
                    foreach (DamageModifier m in mods)
                    {
                        if (m == null) continue;
                        float a = Mathf.Abs(m.DamageMultiplier);
                        float mult = m.MultiplyByScale ? ((a >= 1f) ? (a - 1f) * scale + 1f : a * scale) : a;
                        if (mult > 1f) best *= mult;
                    }
                }
            }
            catch { }
            if (info != null) _victimMult[info] = best;
            return best;
        }

        public static string DisplayName(BaseGameObject? bgo)
        {
            if (bgo == null) return "?";
            try
            {
                if (bgo.ObjectInfo != null && !string.IsNullOrEmpty(bgo.ObjectInfo.DisplayName)) return bgo.ObjectInfo.DisplayName;
                return bgo.gameObject.name.Replace("(Clone)", "");
            }
            catch { return "?"; }
        }
    }
}

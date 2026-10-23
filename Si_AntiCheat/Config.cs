using System;
using System.IO;
using MelonLoader;
using MelonLoader.Utils;
using Newtonsoft.Json;

namespace Si_AntiCheat
{
    /// <summary>
    /// UserData/cfg/AntiCheat/Si_AntiCheat_Config.json. Reports go to UserData/cfg/AntiCheat/reports/.
    /// </summary>
    public class AntiCheatConfig
    {
        public bool Enabled = true;

        // false (default): the mod is idle until an admin starts an audit (one boolean check per hit, nothing else).
        // true: the damage cap and sender check below run on every hit all the time.
        public bool AlwaysOn = false;

        // ---- damage cap (every client-reported hit while armed: AlwaysOn or an audit running) ----
        // "off"   = do nothing
        // "log"   = compute the cap, log hits above it, never change the damage   (start here, measure for a few days)
        // "clamp" = also replace the reported damage by the cap
        public string CapMode = "log";
        public float CapTolerance = 1.25f;      // cap = max possible hit × victim weak-spot multiplier × game-mode scale × this
        public bool CapNegativeDamage = false;  // in "clamp" mode also clamp healing packets (repair) to the unit's repair maximum
        public bool NotifyAdmins = true;        // one chat line to online admins per player and cooldown when hits exceed the cap
        public int NotifyCooldownSeconds = 60;
        public bool LogClamps = true;           // MelonLoader log line per cooldown window (aggregated per player)

        // ---- sender check: the client that sends a damage packet must own the instigator it names ----
        public bool SenderCheck = true;         // Harmony prefix on NetworkLayer.ProcessMessage (stores the sender for the handler)

        // ---- audit metrics (per hit / per sample; ratio 1.0 = exactly at the server-side maximum) ----
        public float DamageFlag = 1.0f;         // damage / cap            (cap already contains CapTolerance)
        public float RangeMargin = 30f;         // metres added to the weapon's max range before the ratio is formed
        public float RangeFlag = 1.2f;          // distance / (max range + splash radius + margin)
        public float RateFlag = 1.5f;           // hits in the last second / max shots per second of the unit
        public float SpeedFlag = 1.15f;         // horizontal speed / TopSpeed of the unit
        public float SpeedSampleInterval = 0.25f;

        // ---- audit sessions ----
        public int DefaultAuditMinutes = 10;
        public int MaxAuditMinutes = 120;
        public int MaxParallelAudits = 8;
        public bool PopulationBaseline = true;  // while any audit runs, collect the same metrics for every other player (differential compare)
        public bool ReportOnRoundEnd = true;    // write an interim report for running audits when the round ends
        public bool DebugLog = false;

        public static string Dir => System.IO.Path.Combine(MelonEnvironment.UserDataDirectory, "cfg", "AntiCheat");
        public static string Path => System.IO.Path.Combine(Dir, "Si_AntiCheat_Config.json");
        public static string ReportDir => System.IO.Path.Combine(Dir, "reports");

        public static AntiCheatConfig Load()
        {
            AntiCheatConfig cfg = new AntiCheatConfig();
            try
            {
                if (File.Exists(Path))
                {
                    cfg = JsonConvert.DeserializeObject<AntiCheatConfig>(File.ReadAllText(Path)) ?? new AntiCheatConfig();
                    MelonLogger.Msg("[AC] Config loaded from " + Path);
                }
                else
                {
                    cfg.Save();
                    MelonLogger.Msg("[AC] Default config written to " + Path);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[AC] Config load failed (" + ex.Message + "), using defaults");
            }
            cfg.Sanitize();
            return cfg;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(Path, JsonConvert.SerializeObject(this, Formatting.Indented));
            }
            catch (Exception ex) { MelonLogger.Warning("[AC] Config save failed: " + ex.Message); }
        }

        public bool Clamp => CapMode == "clamp";
        public bool CapActive => CapMode == "clamp" || CapMode == "log";

        void Sanitize()
        {
            CapMode = (CapMode ?? "log").Trim().ToLowerInvariant();
            if (CapMode != "off" && CapMode != "log" && CapMode != "clamp") CapMode = "log";
            if (CapTolerance < 1f) CapTolerance = 1f;
            if (CapTolerance > 10f) CapTolerance = 10f;
            if (NotifyCooldownSeconds < 5) NotifyCooldownSeconds = 5;
            if (DamageFlag < 0.5f) DamageFlag = 0.5f;
            if (RangeMargin < 0f) RangeMargin = 0f;
            if (RangeFlag < 1f) RangeFlag = 1f;
            if (RateFlag < 1f) RateFlag = 1f;
            if (SpeedFlag < 1f) SpeedFlag = 1f;
            if (SpeedSampleInterval < 0.1f) SpeedSampleInterval = 0.1f;
            if (SpeedSampleInterval > 2f) SpeedSampleInterval = 2f;
            if (DefaultAuditMinutes < 1) DefaultAuditMinutes = 1;
            if (MaxAuditMinutes < DefaultAuditMinutes) MaxAuditMinutes = DefaultAuditMinutes;
            if (MaxParallelAudits < 1) MaxParallelAudits = 1;
            if (MaxParallelAudits > 64) MaxParallelAudits = 64;
        }
    }
}

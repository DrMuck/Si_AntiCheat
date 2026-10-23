using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using MelonLoader;

namespace Si_AntiCheat
{
    /// <summary>One ratio metric: exact count/mean/max/flag count plus a 4096-sample reservoir for percentiles.</summary>
    public class Metric
    {
        public int N;
        public int Flagged;
        public float Max;
        public double Sum;
        readonly float[] _res = new float[4096];
        int _resN;
        static readonly Random _rng = new Random();

        public void Add(float v, float flagAt)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return;
            N++;
            Sum += v;
            if (v > Max) Max = v;
            if (v > flagAt) Flagged++;
            if (_resN < _res.Length) _res[_resN++] = v;
            else
            {
                int j = _rng.Next(N);
                if (j < _res.Length) _res[j] = v;
            }
        }

        public float Mean => N > 0 ? (float)(Sum / N) : 0f;
        public float FlaggedShare => N > 0 ? (float)Flagged / N : 0f;

        public float Percentile(float p)
        {
            if (_resN == 0) return 0f;
            float[] a = new float[_resN];
            Array.Copy(_res, a, _resN);
            Array.Sort(a);
            int idx = (int)Math.Round(p * (a.Length - 1));
            if (idx < 0) idx = 0;
            if (idx >= a.Length) idx = a.Length - 1;
            return a[idx];
        }

        static string F(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);

        public string Line()
        {
            if (N == 0) return "n=0";
            return "n=" + N + " p50=" + F(Percentile(0.5f)) + " p95=" + F(Percentile(0.95f)) + " max=" + F(Max) +
                   " flagged=" + Flagged + " (" + (FlaggedShare * 100f).ToString("0.0", CultureInfo.InvariantCulture) + "%)";
        }

        /// <summary>Compact table cell: p50 / p95 / max (flagged %, n).</summary>
        public string Cell()
        {
            if (N == 0) return "-";
            return F(Percentile(0.5f)) + " / " + F(Percentile(0.95f)) + " / " + F(Max) + " (" + (FlaggedShare * 100f).ToString("0.0", CultureInfo.InvariantCulture) + "%, n=" + N + ")";
        }
    }

    public class UnitRow
    {
        public int Hits;
        public float MaxRatio;
        public int Over;
    }

    public class Stats
    {
        public readonly Metric Damage = new Metric();
        public readonly Metric Range = new Metric();
        public readonly Metric Rate = new Metric();
        public readonly Metric Speed = new Metric();
        public int Hits;
        public int Clamps;              // hits above the cap (clamped in "clamp" mode, only counted in "log" mode)
        public float ClampedDamage;     // damage removed by the cap (clamp mode) or that would have been removed (log mode)
        public int SenderMismatch;
        public int NegativeOver;        // healing packets beyond the unit's repair maximum
        public int Unknown;             // hits whose instigator had no weapon profile
        public int Self;                // self-damage packets (suicide, fall, own splash): not capped, not rated
        public int SpeedSamples;
        public readonly Dictionary<string, UnitRow> ByUnit = new Dictionary<string, UnitRow>();
        public readonly List<string> Notes = new List<string>();

        public void NoteUnit(string unit, float ratio, float flagAt)
        {
            if (!ByUnit.TryGetValue(unit, out UnitRow r)) { r = new UnitRow(); ByUnit[unit] = r; }
            r.Hits++;
            if (ratio > r.MaxRatio) r.MaxRatio = ratio;
            if (ratio > flagAt) r.Over++;
        }

        public void Note(string s)
        {
            if (Notes.Count < 200) Notes.Add(DateTime.Now.ToString("HH:mm:ss") + " " + s);
        }
    }

    public class AuditSession
    {
        public ulong SteamId;
        public string Name = "?";
        public bool IsReference;        // cross-check player chosen by the admin: own column in every report, no verdict
        public ulong StartedBy;
        public string StartedByName = "console";
        public DateTime Start;
        public DateTime End;
        public DateTime? FinishedAt;
        public string FinishReason = "";
        public string FinalVerdict = "";
        public Stats S = new Stats();
        public int Reports;
        public string? LastReport;

        public double MinutesLeft => (End - DateTime.Now).TotalMinutes;
        public bool Expired => DateTime.Now >= End;
        public string Role => IsReference ? "reference" : "suspect";
    }

    /// <summary>
    /// Audit sessions keyed by Steam id, plus the population baseline (everyone who is not audited) collected over the
    /// same window so lag, server load and map effects cancel out in the comparison. Reference sessions are honest
    /// players the admin picked for a direct cross-check; they appear as their own columns.
    /// </summary>
    public static class Audit
    {
        public static readonly Dictionary<ulong, AuditSession> Sessions = new Dictionary<ulong, AuditSession>();
        public static readonly List<AuditSession> Recent = new List<AuditSession>();   // finished, newest first, max 10
        public static Stats Population = new Stats();
        public static DateTime PopulationStart = DateTime.Now;
        public static readonly HashSet<ulong> PopulationPlayers = new HashSet<ulong>();

        public static bool Any => Sessions.Count > 0;
        public static IEnumerable<AuditSession> Suspects => Sessions.Values.Where(s => !s.IsReference);
        public static IEnumerable<AuditSession> References => Sessions.Values.Where(s => s.IsReference);

        public static AuditSession Start(ulong steamId, string name, int minutes, Player? admin, bool reference)
        {
            if (!Any) ResetPopulation();
            AuditSession s = new AuditSession
            {
                SteamId = steamId,
                Name = name,
                IsReference = reference,
                Start = DateTime.Now,
                End = DateTime.Now.AddMinutes(minutes),
                StartedBy = admin != null ? admin.PlayerID.m_ID : 0,
                StartedByName = admin != null ? admin.PlayerName : "console"
            };
            Sessions[steamId] = s;
            PopulationPlayers.Remove(steamId);
            return s;
        }

        public static void Archive(AuditSession s, string reason, AntiCheatConfig cfg)
        {
            s.FinishedAt = DateTime.Now;
            s.FinishReason = reason;
            s.FinalVerdict = Verdict(s, cfg, out _);
            Recent.Insert(0, s);
            while (Recent.Count > 10) Recent.RemoveAt(Recent.Count - 1);
        }

        public static void ResetPopulation()
        {
            Population = new Stats();
            PopulationStart = DateTime.Now;
            PopulationPlayers.Clear();
        }

        public static Stats? StatsFor(ulong steamId, AntiCheatConfig cfg)
        {
            if (Sessions.TryGetValue(steamId, out AuditSession s)) return s.S;
            if (cfg.PopulationBaseline && Any && steamId != 0)
            {
                PopulationPlayers.Add(steamId);
                return Population;
            }
            return null;
        }

        /// <summary>Reference columns for a report: running references plus references finished in the last hour.</summary>
        public static List<AuditSession> ReferenceColumns(AuditSession forSession)
        {
            var list = References.Where(r => r.SteamId != forSession.SteamId).ToList();
            foreach (AuditSession r in Recent)
                if (r.IsReference && r.SteamId != forSession.SteamId && r.FinishedAt.HasValue && (DateTime.Now - r.FinishedAt.Value).TotalMinutes < 60 && list.All(x => x.SteamId != r.SteamId))
                    list.Add(r);
            return list.Take(4).ToList();
        }

        // ------------------------------------------------------------------ verdict + report

        static bool MetricFlagged(Metric a, Metric p, float flagAt, out string why)
        {
            why = "";
            if (a.N < 30) { why = "n<30"; return false; }
            float popShare = Math.Max(p.N >= 30 ? p.FlaggedShare : 0f, 0.002f);
            float a95 = a.Percentile(0.95f);
            float p95 = Math.Max(p.N >= 30 ? p.Percentile(0.95f) : 0f, 0.2f);
            if (a.FlaggedShare >= 0.02f && a.FlaggedShare >= 4f * popShare)
            {
                why = "flagged share " + (a.FlaggedShare * 100f).ToString("0.0", CultureInfo.InvariantCulture) + "% vs population " + (popShare * 100f).ToString("0.0", CultureInfo.InvariantCulture) + "%";
                return true;
            }
            if (a95 > flagAt && a95 >= 1.5f * p95)
            {
                why = "p95 " + a95.ToString("0.00", CultureInfo.InvariantCulture) + " vs population " + p95.ToString("0.00", CultureInfo.InvariantCulture);
                return true;
            }
            return false;
        }

        public static string Verdict(AuditSession s, AntiCheatConfig cfg, out List<string> reasons)
        {
            reasons = new List<string>();
            if (s.IsReference) return "REFERENCE";
            Stats a = s.S, p = Population;
            int hard = 0, soft = 0;
            if (MetricFlagged(a.Damage, p.Damage, cfg.DamageFlag, out string w)) { hard++; reasons.Add("damage: " + w); }
            if (MetricFlagged(a.Range, p.Range, cfg.RangeFlag, out w)) { hard++; reasons.Add("range: " + w); }
            if (MetricFlagged(a.Rate, p.Rate, cfg.RateFlag, out w)) { soft++; reasons.Add("hit rate: " + w); }
            if (MetricFlagged(a.Speed, p.Speed, cfg.SpeedFlag, out w)) { soft++; reasons.Add("speed: " + w); }
            // one or two can be an ownership hand-over in flight (vehicle enter/exit); three or more is not
            if (a.SenderMismatch >= 3) { hard++; reasons.Add("sender mismatch: " + a.SenderMismatch + " packets named a unit the sender does not own"); }
            else if (a.SenderMismatch > 0) { soft++; reasons.Add("sender mismatch: " + a.SenderMismatch + " packet(s), could be an ownership hand-over"); }
            if (a.NegativeOver > 0) { soft++; reasons.Add("healing beyond repair maximum: " + a.NegativeOver + " packets"); }
            if (a.Hits < 30 && a.SpeedSamples < 100) return "INCONCLUSIVE (too little data: " + a.Hits + " hits, " + a.SpeedSamples + " speed samples)";
            if (hard > 0 || soft >= 2) return "SUSPICIOUS";
            if (soft == 1) return "WATCH";
            return "CLEAN";
        }

        public static string BuildReport(AuditSession s, AntiCheatConfig cfg, string reason)
        {
            StringBuilder b = new StringBuilder();
            string verdict = s.FinishedAt.HasValue ? s.FinalVerdict : Verdict(s, cfg, out _);
            Verdict(s, cfg, out List<string> reasons);
            List<AuditSession> refs = ReferenceColumns(s);
            b.AppendLine("Si_AntiCheat audit report  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  (" + reason + ")");
            b.AppendLine("Player: " + s.Name + "  steam " + s.SteamId + "  role " + s.Role + "  started by " + s.StartedByName + " at " + s.Start.ToString("HH:mm:ss") +
                         "  window " + ((s.FinishedAt ?? DateTime.Now) - s.Start).TotalMinutes.ToString("0.0", CultureInfo.InvariantCulture) + " min");
            b.AppendLine("Cap mode: " + cfg.CapMode + "  tolerance " + cfg.CapTolerance.ToString("0.00", CultureInfo.InvariantCulture) +
                         "  flags: damage>" + cfg.DamageFlag + " range>" + cfg.RangeFlag + " rate>" + cfg.RateFlag + " speed>" + cfg.SpeedFlag);
            b.AppendLine();
            b.AppendLine("VERDICT: " + verdict);
            foreach (string r in reasons) b.AppendLine("  - " + r);
            if (reasons.Count == 0 && !s.IsReference) b.AppendLine("  - no metric stands out against the population");
            b.AppendLine();
            Stats a = s.S, p = Population;
            b.AppendLine("Metric (ratio, 1.0 = server-side maximum)   " + s.Name.PadRight(40) + " | population (" + PopulationPlayers.Count + " other players since " + PopulationStart.ToString("HH:mm:ss") + ")");
            b.AppendLine("  damage / cap        " + Pad(a.Damage.Line()) + " | " + p.Damage.Line());
            b.AppendLine("  distance / range    " + Pad(a.Range.Line()) + " | " + p.Range.Line());
            b.AppendLine("  hits/s / max rate   " + Pad(a.Rate.Line()) + " | " + p.Rate.Line());
            b.AppendLine("  speed / top speed   " + Pad(a.Speed.Line()) + " | " + p.Speed.Line());
            b.AppendLine();
            b.AppendLine("Counts: hits " + a.Hits + ", above cap " + a.Clamps + " (" + (cfg.Clamp ? "clamped" : "logged only") + ", " + a.ClampedDamage.ToString("0", CultureInfo.InvariantCulture) + " damage), " +
                         "sender mismatches " + a.SenderMismatch + ", healing over max " + a.NegativeOver + ", unknown instigator " + a.Unknown + ", self-damage " + a.Self + ", speed samples " + a.SpeedSamples);
            b.AppendLine("        population: hits " + p.Hits + ", above cap " + p.Clamps + ", sender mismatches " + p.SenderMismatch + ", healing over max " + p.NegativeOver + ", unknown " + p.Unknown + ", speed samples " + p.SpeedSamples);
            if (refs.Count > 0)
            {
                b.AppendLine();
                b.AppendLine("Reference players (chosen by the admin for a direct cross-check, same window):");
                foreach (AuditSession r in refs)
                {
                    b.AppendLine("  " + r.Name + (r.FinishedAt.HasValue ? " (finished " + r.FinishedAt.Value.ToString("HH:mm:ss") + ")" : " (running)") + ": hits " + r.S.Hits + ", above cap " + r.S.Clamps + ", mismatches " + r.S.SenderMismatch);
                    b.AppendLine("    damage / cap        " + r.S.Damage.Line());
                    b.AppendLine("    distance / range    " + r.S.Range.Line());
                    b.AppendLine("    hits/s / max rate   " + r.S.Rate.Line());
                    b.AppendLine("    speed / top speed   " + r.S.Speed.Line());
                }
            }
            b.AppendLine();
            b.AppendLine("By unit (" + s.Name + "): hits / max damage ratio / hits above cap");
            foreach (var kv in a.ByUnit.OrderByDescending(k => k.Value.Hits))
                b.AppendLine("  " + kv.Key.PadRight(24) + kv.Value.Hits.ToString().PadLeft(6) + "  " + kv.Value.MaxRatio.ToString("0.00", CultureInfo.InvariantCulture).PadLeft(6) + "  " + kv.Value.Over.ToString().PadLeft(6));
            if (a.ByUnit.Count == 0) b.AppendLine("  (none)");
            if (a.Notes.Count > 0)
            {
                b.AppendLine();
                b.AppendLine("Events:");
                foreach (string n in a.Notes) b.AppendLine("  " + n);
            }
            b.AppendLine();
            b.AppendLine("How to read: every client-reported hit is compared with the largest hit the shooter's unit can deal on the server (weak spots and");
            b.AppendLine("game-mode scaling included). Honest players cluster below 1.0 with a few lag outliers; the population and reference columns show");
            b.AppendLine("that cluster for the same window and map. A cheater shows a second cluster above 1.0, hits from beyond the weapon's reach, or");
            b.AppendLine("packets for units they do not own. Vanilla clamps a reported hit to the victim's health before this mod sees it.");
            return b.ToString();
        }

        static string Pad(string s) => s.PadRight(72);

        /// <summary>Report as a grid for the ModUI admin page: one column for the player, the population and each reference.</summary>
        public static (string title, string[] headers, string[][] rows) ReportTable(AuditSession s, AntiCheatConfig cfg)
        {
            string verdict = s.FinishedAt.HasValue ? s.FinalVerdict : Verdict(s, cfg, out _);
            Verdict(s, cfg, out List<string> reasons);
            List<AuditSession> refs = ReferenceColumns(s);
            string when = s.FinishedAt.HasValue ? "finished " + s.FinishedAt.Value.ToString("HH:mm") + ", " + s.FinishReason : "live, " + s.MinutesLeft.ToString("0.0", CultureInfo.InvariantCulture) + " min left";
            string title = (s.IsReference ? "Reference " : "Report ") + s.Name + ": " + verdict + " (" + when + ")";
            var headers = new List<string> { "p50 / p95 / max (flag%, n)", s.Name, "population" };
            foreach (AuditSession r in refs) headers.Add("ref " + r.Name);
            var rows = new List<string[]>();
            rows.Add(Row("damage / cap", s, refs, st => st.Damage.Cell()));
            rows.Add(Row("distance / range", s, refs, st => st.Range.Cell()));
            rows.Add(Row("hits/s / max rate", s, refs, st => st.Rate.Cell()));
            rows.Add(Row("speed / top speed", s, refs, st => st.Speed.Cell()));
            rows.Add(Row("hits / above cap", s, refs, st => st.Hits + " / " + st.Clamps));
            rows.Add(Row("mismatch / heal>max", s, refs, st => st.SenderMismatch + " / " + st.NegativeOver));
            rows.Add(Row("self-dmg / speed n", s, refs, st => st.Self + " / " + st.SpeedSamples));
            int i = 0;
            foreach (string reason in reasons.Take(3))
            {
                var r = new string[headers.Count];
                r[0] = i == 0 ? "why" : "";
                r[1] = reason;
                for (int k = 2; k < r.Length; k++) r[k] = "";
                rows.Add(r);
                i++;
            }
            return (title, headers.ToArray(), rows.ToArray());
        }

        static string[] Row(string label, AuditSession s, List<AuditSession> refs, Func<Stats, string> cell)
        {
            var r = new List<string> { label, cell(s.S), cell(Population) };
            foreach (AuditSession x in refs) r.Add(cell(x.S));
            return r.ToArray();
        }

        public static string ChatSummary(AuditSession s, AntiCheatConfig cfg)
        {
            string verdict = s.FinishedAt.HasValue ? s.FinalVerdict : Verdict(s, cfg, out _);
            Verdict(s, cfg, out List<string> reasons);
            Stats a = s.S;
            string line = s.Name + " (" + s.Role + "): " + verdict + " | hits " + a.Hits + ", above cap " + a.Clamps +
                          ", dmg p95 " + a.Damage.Percentile(0.95f).ToString("0.00", CultureInfo.InvariantCulture) + " max " + a.Damage.Max.ToString("0.00", CultureInfo.InvariantCulture) +
                          ", range max " + a.Range.Max.ToString("0.00", CultureInfo.InvariantCulture) +
                          ", speed max " + a.Speed.Max.ToString("0.00", CultureInfo.InvariantCulture) +
                          ", mismatches " + a.SenderMismatch;
            if (reasons.Count > 0) line += " | " + string.Join("; ", reasons.Take(2));
            return line;
        }

        public static string? WriteReport(AuditSession s, AntiCheatConfig cfg, string reason)
        {
            try
            {
                Directory.CreateDirectory(AntiCheatConfig.ReportDir);
                string safe = new string(s.Name.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-').ToArray());
                if (safe.Length == 0) safe = s.SteamId.ToString();
                string path = Path.Combine(AntiCheatConfig.ReportDir, DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + safe + (s.IsReference ? "_ref" : "") + ".txt");
                File.WriteAllText(path, BuildReport(s, cfg, reason));
                s.Reports++;
                s.LastReport = path;
                MelonLogger.Msg("[AC] report written: " + path);
                return path;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[AC] report write failed: " + ex.Message);
                return null;
            }
        }
    }
}

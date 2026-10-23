using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MelonLoader;
using SilicaAdminMod;

namespace Si_AntiCheat
{
    // Si_ModUI export: the !audit sub-commands as pickers / switches, live audit lines as status, and the reports as
    // per-player tables (only admins get them: ModUIPlayerData checks the Steam id against the Admin Mod).
    // Chat stays fully usable without the UI.
    public partial class AntiCheatMod
    {
        static string? _uiCache;
        static string _uiKey = "";

        static string F(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);

        public static string? ModUIData()
        {
            try
            {
                // Runs every few seconds; the server only pushes a diff when the document changes, so coarse counters.
                var key = new System.Text.StringBuilder();
                key.Append(Cfg.Enabled).Append('|').Append(Cfg.AlwaysOn).Append('|').Append(Cfg.CapMode).Append('|').Append(Cfg.DefaultAuditMinutes).Append('|').Append(Armed);
                foreach (AuditSession s in Audit.Sessions.Values)
                    key.Append('|').Append(s.SteamId).Append(s.IsReference).Append((int)Math.Ceiling(s.MinutesLeft)).Append(':').Append(s.S.Hits / 10).Append(':').Append(s.S.Clamps).Append(':').Append(s.S.SenderMismatch)
                       .Append(':').Append(F(s.S.Damage.Max)).Append(':').Append(F(s.S.Range.Max)).Append(':').Append(F(s.S.Speed.Max));
                if (Audit.Any) key.Append("|pop").Append(Audit.PopulationPlayers.Count).Append(':').Append(Audit.Population.Hits / 50);
                string k = key.ToString();
                if (_uiCache != null && k == _uiKey) return _uiCache;

                int suspects = Audit.Suspects.Count(), refs = Audit.References.Count();
                var x = ModUI.ModUIExport.New()
                    .Enabled(Cfg.Enabled)
                    .Info(Armed
                        ? "ARMED: " + suspects + " suspect" + (suspects == 1 ? "" : "s") + ", " + refs + " reference" + (refs == 1 ? "" : "s") + ", baseline " + Audit.PopulationPlayers.Count + " players / " + Audit.Population.Hits + " hits"
                        : "Idle: nothing runs until you start an audit")
                    .Info("Hits above cap: " + Cfg.CapMode.ToUpperInvariant() + " | checks without audit: " + (Cfg.AlwaysOn ? "ON" : "OFF") + " | default " + Cfg.DefaultAuditMinutes + " min | reports: UserData/cfg/AntiCheat/reports");
                foreach (AuditSession s in Audit.Sessions.Values.OrderBy(a => a.IsReference).ThenBy(a => a.Name))
                    x.Info(s.Name + " (" + s.Role + "): " + s.MinutesLeft.ToString("0", CultureInfo.InvariantCulture) + " min left, hits " + s.S.Hits + ", above cap " + s.S.Clamps +
                           ", dmg max x" + F(s.S.Damage.Max) + ", range max x" + F(s.S.Range.Max) + ", speed max x" + F(s.S.Speed.Max) + ", mismatches " + s.S.SenderMismatch);
                x.Picker("!audit", "Audit a player", "Pick the suspect, then type the minutes or take DEFAULT (" + Cfg.DefaultAuditMinutes + " min). Every other player on the server is collected as the baseline over the same window.", admin: true, arg: "player,text?")
                 .Picker("!audit compare", "Add reference player", "Audits a player you trust in parallel: their numbers become an extra column in every report for a direct cross-check (same window, same lag).", admin: true, arg: "player,text?")
                 .Value("!audit minutes", "Default minutes", "Audit length used when no minutes are typed (saved).", admin: true, current: Cfg.DefaultAuditMinutes)
                 .Action("!audit status", "Refresh tables", "Refreshes the report tables on this page and prints the running audits.", admin: true);
                if (Audit.Any)
                {
                    var choices = new List<(string, string)> { ("all", "ALL") };
                    foreach (AuditSession s in Audit.Sessions.Values.OrderBy(a => a.IsReference).ThenBy(a => a.Name))
                        choices.Add((s.SteamId.ToString(), s.Name + (s.IsReference ? " (ref)" : "")));
                    x.Picker("!audit report", "Report now", "Writes the report file for a running audit and refreshes its table; the audit continues.", admin: true, arg: "choice", choices.ToArray())
                     .Picker("!audit stop", "Stop + report", "Ends the audit now, writes the report and tells the admins the verdict.", admin: true, arg: "choice", choices.ToArray());
                }
                x.Switch("!audit cap", "Hits above the cap", "OFF ignore them, LOG count them and tell admins, CLAMP also cut the damage down to the server-side maximum.", admin: true, current: Cfg.CapMode,
                         ("off", "OFF"), ("log", "LOG"), ("clamp", "CLAMP"))
                 .OnOff("!audit always", "Checks without an audit", "ON runs the damage cap and sender check on every hit all the time. OFF (default) keeps the mod idle until an audit starts.", admin: true, on: Cfg.AlwaysOn)
                 .Action("!audit reload", "Reload config", "Re-read Si_AntiCheat_Config.json.", admin: true);
                _uiCache = x.Build();
                _uiKey = k;
                return _uiCache;
            }
            catch (Exception ex) { MelonLogger.Warning("[AC] ModUIData: " + ex.Message); return null; }
        }

        // Admins only: running audits grid, a report grid per running suspect (live) and the last finished reports.
        // Refreshed by the ModUI server at most once a minute or right after a command of that admin ("Refresh tables").
        public static string? ModUIPlayerData(ulong steamId)
        {
            try
            {
                if (!IsAdminId(steamId)) return null;
                var pe = ModUI.ModUIExport.ForPlayer();
                if (Audit.Any)
                {
                    var rows = new List<string[]>();
                    foreach (AuditSession s in Audit.Sessions.Values.OrderBy(a => a.IsReference).ThenBy(a => a.Name))
                        rows.Add(new[]
                        {
                            s.Name, s.Role, s.MinutesLeft.ToString("0.0", CultureInfo.InvariantCulture), s.S.Hits.ToString(), s.S.Clamps.ToString(),
                            F(s.S.Damage.Percentile(0.95f)) + " / " + F(s.S.Damage.Max), F(s.S.Range.Max), F(s.S.Rate.Max), F(s.S.Speed.Max), s.S.SenderMismatch.ToString()
                        });
                    rows.Add(new[]
                    {
                        "population", Audit.PopulationPlayers.Count + " players", "-", Audit.Population.Hits.ToString(), Audit.Population.Clamps.ToString(),
                        F(Audit.Population.Damage.Percentile(0.95f)) + " / " + F(Audit.Population.Damage.Max), F(Audit.Population.Range.Max), F(Audit.Population.Rate.Max), F(Audit.Population.Speed.Max), Audit.Population.SenderMismatch.ToString()
                    });
                    pe.Table("Running audits", new[] { "Player", "Role", "Min left", "Hits", "Above cap", "Dmg p95 / max", "Range max", "Rate max", "Speed max", "Mismatch" }, rows.ToArray());
                    foreach (AuditSession s in Audit.Suspects.OrderBy(a => a.Name))
                    {
                        var t = Audit.ReportTable(s, Cfg);
                        pe.Table(t.title, t.headers, t.rows);
                    }
                }
                int shown = 0;
                foreach (AuditSession s in Audit.Recent)
                {
                    if (s.IsReference) continue;
                    var t = Audit.ReportTable(s, Cfg);
                    pe.Table(t.title, t.headers, t.rows);
                    if (++shown >= 3) break;
                }
                return pe.Build();
            }
            catch (Exception ex) { MelonLogger.Warning("[AC] ModUIPlayerData: " + ex.Message); return null; }
        }

        static bool IsAdminId(ulong steamId)
        {
            try { return AdminMethods.FindAdminFromSteamId((long)steamId) != null; } catch { return false; }
        }
    }
}

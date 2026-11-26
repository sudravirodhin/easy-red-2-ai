using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace EasyRed2HumanAI
{
    public static class EmergentPathAuditorMain
    {
        private enum AuditSeverity
        {
            INFO,
            MEDIUM,
            HIGH,
            CRITICAL
        }

        private struct AuditFinding
        {
            public AuditSeverity Severity;
            public string Pass;
            public string Code;
            public string Message;
        }

        private static readonly List<AuditFinding> _findings = new List<AuditFinding>(64);
        private static bool _verbose;

        private static readonly string[] ExpectedPrereqReasons =
        {
            "prereq_cover_bandage",
            "prereq_smoke_bandage",
            "prereq_bandage_scavenge",
            "prereq_ammo_fight",
            "prereq_smoke_medic",
            "prereq_drag_medic",
            "scavenge_at_weapon"
        };

        private static readonly EmergentEngine.EmergentActionId[] InfantryActions =
        {
            EmergentEngine.EmergentActionId.HoldAndFight,
            EmergentEngine.EmergentActionId.SeekCover,
            EmergentEngine.EmergentActionId.RepositionWallSide,
            EmergentEngine.EmergentActionId.EvadeGrenade,
            EmergentEngine.EmergentActionId.PopSmoke,
            EmergentEngine.EmergentActionId.SelfBandage,
            EmergentEngine.EmergentActionId.SuppressPin,
            EmergentEngine.EmergentActionId.FlankManeuver,
            EmergentEngine.EmergentActionId.FragFlush,
            EmergentEngine.EmergentActionId.AdvanceObjective,
            EmergentEngine.EmergentActionId.CloseBreach,
            EmergentEngine.EmergentActionId.FallbackRegroup,
            EmergentEngine.EmergentActionId.ResupplyAmmo,
            EmergentEngine.EmergentActionId.PushAmmoDrop,
            EmergentEngine.EmergentActionId.MedicHeal,
            EmergentEngine.EmergentActionId.DragCasualty,
            EmergentEngine.EmergentActionId.LiberatePrisoner,
            EmergentEngine.EmergentActionId.MaintainRadioLink
        };

        private static readonly EmergentEngine.EmergentActionId[] VehicleActions =
        {
            EmergentEngine.EmergentActionId.EvadeGrenade,
            EmergentEngine.EmergentActionId.VehicleFaceAndFight,
            EmergentEngine.EmergentActionId.VehicleFieldRepair,
            EmergentEngine.EmergentActionId.VehicleBailout
        };

        private static void AddFinding(AuditSeverity sev, string pass, string code, string message)
        {
            _findings.Add(new AuditFinding
            {
                Severity = sev,
                Pass = pass,
                Code = code,
                Message = message
            });
        }

        private static ulong MixFnv(ulong h, ulong v)
        {
            unchecked
            {
                h ^= v;
                h *= 1099511628211UL;
                return h;
            }
        }

        public static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.Error.WriteLine("Usage: EmergentPathAuditor <workspace> [--trace <tracePath>] [--gate] [--verbose]");
                return 2;
            }

            string workspace = args[0];
            string tracePath = "";
            bool gate = false;
            for (int i = 1; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--gate", StringComparison.OrdinalIgnoreCase))
                    gate = true;
                else if (string.Equals(args[i], "--verbose", StringComparison.OrdinalIgnoreCase))
                    _verbose = true;
                else if (string.Equals(args[i], "--trace", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    tracePath = args[++i];
                else if (!args[i].StartsWith("--", StringComparison.Ordinal) && string.IsNullOrEmpty(tracePath))
                    tracePath = args[i];
            }

            var sw = Stopwatch.StartNew();
            Console.WriteLine("========================================================================");
            Console.WriteLine("  Easy Red 2 - Battle Hardened | Emergent AI & GOAP Path Auditor");
            Console.WriteLine("========================================================================");

            var profileList = BuildWeightProfiles();
            ulong checksum = 14695981039346656037UL;

            checksum = RunPass1PermutationAndReachability(profileList, checksum);
            checksum = RunPass2MultiTickSimulation(profileList, checksum);
            checksum = RunPass4StaticSourceAudit(workspace, checksum, out bool writesCommitBack);
            checksum = RunPass3ContractParityMatrix(profileList, checksum, writesCommitBack);
            RunPass5LiveTraceForensics(tracePath);

            sw.Stop();

            int crit = 0, high = 0, med = 0, info = 0;
            for (int i = 0; i < _findings.Count; i++)
            {
                switch (_findings[i].Severity)
                {
                    case AuditSeverity.CRITICAL: crit++; break;
                    case AuditSeverity.HIGH: high++; break;
                    case AuditSeverity.MEDIUM: med++; break;
                    default: info++; break;
                }
            }

            Console.WriteLine();
            Console.WriteLine("========================================================================");
            Console.WriteLine($"  AUDIT SUMMARY | CRITICAL:{crit}  HIGH:{high}  MEDIUM:{med}  INFO:{info} | Checksum:0x{checksum:X16} | {sw.ElapsedMilliseconds}ms");
            Console.WriteLine("========================================================================");
            foreach (AuditFinding f in _findings.OrderBy(x => -(int)x.Severity).ThenBy(x => x.Pass, StringComparer.Ordinal))
            {
                Console.WriteLine($"  [{f.Severity,-8}] [{f.Pass}] {f.Code}: {f.Message}");
            }

            if (gate && (crit + high) > 0)
            {
                Console.Error.WriteLine($"[GATE FAILED] CRITICAL+HIGH = {crit + high} (> 0).");
                return 1;
            }
            return 0;
        }

        private struct WeightProfile
        {
            public string Name;
            public string Faction;
            public VeteranAndTacticsPatches.SoldierPersonality Personality;
            public float Vet01;
            public float Desperation;
            public float LeaderMorale;
        }

        private static WeightProfile[] BuildWeightProfiles()
        {
            return new[]
            {
                new WeightProfile { Name = "USA_Reg", Faction = "UnitedStates_allies", Personality = VeteranAndTacticsPatches.SoldierPersonality.Rifleman_Regular, Vet01 = 0.50f, Desperation = 0.00f, LeaderMorale = 0.00f },
                new WeightProfile { Name = "GER_Shk", Faction = "Germany_axis", Personality = VeteranAndTacticsPatches.SoldierPersonality.Assaulter_Shock, Vet01 = 0.70f, Desperation = 0.15f, LeaderMorale = 0.08f },
                new WeightProfile { Name = "JPN_Bnz", Faction = "Japan_axis", Personality = VeteranAndTacticsPatches.SoldierPersonality.Assaulter_Breacher, Vet01 = 0.40f, Desperation = 0.45f, LeaderMorale = 0.10f },
                new WeightProfile { Name = "USR_Cau", Faction = "Ussr_allies", Personality = VeteranAndTacticsPatches.SoldierPersonality.Rifleman_Cautious, Vet01 = 0.30f, Desperation = 0.10f, LeaderMorale = -0.10f }
            };
        }

        private static EmergentEngine.EmergentWeights ComputeProfileWeights(
            in WeightProfile wp,
            bool isLeader,
            bool isRadioman,
            bool isMedic)
        {
            var prof = VeteranAndTacticsPatches.GetFactionCommandProfile(wp.Faction);
            var pers = isMedic ? VeteranAndTacticsPatches.SoldierPersonality.Medic_Heroic : wp.Personality;
            return EmergentEngine.ComputeWeights(in prof, pers, wp.Vet01, 0x13579BDFu, wp.Desperation, isLeader, isRadioman, wp.LeaderMorale);
        }

        // =====================================================================
        // PASS 1: Exhaustive Permutation Sweep & Reachability Audit
        // =====================================================================
        private static ulong RunPass1PermutationAndReachability(WeightProfile[] profiles, ulong checksum)
        {
            Console.WriteLine();
            Console.WriteLine("[Pass 1/5] Exhaustive Permutation Sweep & Reachability Audit");

            const int ActCount = 24;
            int[] bridgeInfCounts = new int[ActCount];
            int[] unconInfCounts = new int[ActCount];
            int[] bridgeVehCounts = new int[ActCount];
            int[] unconVehCounts = new int[ActCount];

            var bridgeReasonCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var unconReasonCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var unsafeReasonsSeen = new HashSet<string>(StringComparer.Ordinal);

            int infBridgeStates = 0;
            int infUnconStates = 0;
            int vehBridgeStates = 0;
            int vehUnconStates = 0;

            int invGrenadeFail = 0, invPtBlankFail = 0, invNoHarmEngageFail = 0, invArmorBailFail = 0;
            string invGrenadeSample = "", invPtBlankSample = "", invNoHarmSample = "", invArmorSample = "";

            // Precompute weights per (profileIdx, roleIdx)
            // Role buckets: 0=Rifleman, 1=SquadLeader, 2=RadiomanOK, 3=RadiomanDrift, 4=Medic, 5=MG, 6=AmmoCarrier
            var weightsInf = new EmergentEngine.EmergentWeights[profiles.Length, 7];
            var weightsVeh = new EmergentEngine.EmergentWeights[profiles.Length];
            for (int p = 0; p < profiles.Length; p++)
            {
                for (int r = 0; r < 7; r++)
                    weightsInf[p, r] = ComputeProfileWeights(in profiles[p], r == 1, r == 2 || r == 3, r == 4);
                weightsVeh[p] = ComputeProfileWeights(in profiles[p], true, false, false);
            }

            // Cover/Harm scenarios (6): (InCover, ExposedWall, RawHarm, SmoothHarm, ProjHarm, CoverAvail)
            var harmCases = new (bool InCov, bool ExpWall, float Raw, float Smooth, float Proj, bool CovAvail, string Tag)[]
            {
                (false, false, 0.04f, 0.04f, 0.00f, true,  "OpenCalm"),
                (false, false, 0.34f, 0.34f, 0.22f, true,  "OpenMid"),
                (false, false, 0.76f, 0.76f, 0.28f, true,  "OpenHot"),
                (true,  false, 0.12f, 0.12f, 0.00f, true,  "CovCalm"),
                (true,  false, 0.68f, 0.68f, 0.20f, true,  "CovHot"),
                (true,  true,  0.42f, 0.42f, 0.18f, false, "WallExp")
            };

            // Enemy scenarios (6): (HasVis, PtBlank, Dist, CanHarm, CloseAcoust, HasLkt, LktDist, UncArmor, BehindCov)
            var enemyCases = new (bool Vis, bool PtB, float Dist, float CanH, bool Acoust, bool Lkt, float LktD, bool Armor, bool BehCov, string Tag)[]
            {
                (false, false, 999f, 0.00f, false, false, 999f, false, false, "NoThreat"),
                (false, false, 999f, 0.55f, true,  true,  24f,  false, true,  "AcoustLkt"),
                (true,  true,  5f,   0.90f, false, true,  5f,   false, false, "Cqb5m"),
                (true,  false, 22f,  0.90f, false, true,  22f,  false, false, "Mid22m"),
                (true,  false, 55f,  0.75f, false, true,  55f,  false, true,  "Long55m"),
                (true,  false, 28f,  0.00f, false, true,  28f,  true,  false, "HeavyTank")
            };

            // Body/Gear scenarios (5): (Bleed, Band, Empty, Smoke, Frag, NeedAt, NeedPri, NearAmmo)
            var gearCases = new (bool Bleed, bool Band, bool Empty, bool Smk, bool Frg, bool NeedAt, bool NeedPri, bool NearAmmo, string Tag)[]
            {
                (false, true,  false, true,  true,  false, false, false, "Nominal"),
                (true,  true,  false, true,  false, false, false, false, "BleedBand"),
                (true,  false, false, false, false, false, false, true,  "BleedNeed"),
                (false, true,  true,  true,  false, false, false, true,  "EmptyAmmo"),
                (false, true,  false, false, false, true,  true,  true,  "ScavUpg")
            };

            // Objective scenarios (4): (HasObj, Inside, Ticking, ObjDist, HomeDef, Tag)
            var objCases = new (bool HasObj, bool InObj, bool Tick, float Dist, bool Home, string Tag)[]
            {
                (true,  false, false, 80f, false, "ObjOut"),
                (true,  false, true,  60f, false, "ObjTickOut"),
                (true,  true,  true,  10f, false, "ObjTickIn"),
                (true,  true,  false, 10f, true,  "ObjHomeIn")
            };

            // Opportunity / Tactics scenarios (6): (LiveGren, DoorCas, Cluster, FlankCov, AllyPin, SmkAct, OppKind)
            var oppCases = new (bool Gren, bool Door, int Clust, bool Flank, bool Pin, bool SmkAct, int OppMode, string Tag)[]
            {
                (false, false, 0, false, false, false, 0, "Plain"),
                (true,  false, 0, false, false, false, 0, "LiveGren"),
                (false, false, 0, true,  true,  false, 1, "AllyAidPin"),
                (false, false, 0, true,  false, true,  2, "MedPrisSmk"),
                (false, true,  3, false, false, false, 3, "DoorClust"),
                (false, false, 0, true,  true,  false, 4, "Overwhelm")
            };

            var directives = new[]
            {
                EmergentEngine.Tier2SquadDirective.AdvanceBound,
                EmergentEngine.Tier2SquadDirective.AnvilPin,
                EmergentEngine.Tier2SquadDirective.HammerFlank,
                EmergentEngine.Tier2SquadDirective.LocalFallback
            };

            const float now = 100f;

            void EvalAndRecord(ref EmergentEngine.EmergentInput inp, in EmergentEngine.EmergentWeights w, int[] actArr, Dictionary<string, int> rsnDict, bool checkSafety)
            {
                EmergentEngine.EmergentDecision dec;
                if (!EmergentEngine.EvaluateFastReflex(in inp, in w, now, out dec))
                    dec = EmergentEngine.EvaluateDeliberative(in inp, in w, default, now);

                int idx = (int)dec.Step0;
                if (idx >= 0 && idx < actArr.Length) actArr[idx]++;
                string rsn = dec.Reason ?? "none";
                rsnDict[rsn] = rsnDict.TryGetValue(rsn, out int c) ? c + 1 : 1;

                if (!checkSafety) return;

                if (inp.LiveGrenadeClose && dec.Step0 != EmergentEngine.EmergentActionId.EvadeGrenade)
                {
                    if (invGrenadeFail++ == 0) invGrenadeSample = $"Step0={dec.Step0} ({dec.Reason}) bleed={inp.IsBleeding} ptBlank={inp.PointBlankThreat}";
                }
                if (inp.PointBlankThreat && inp.CanHarmTarget > 0.10f && !inp.LiveGrenadeClose && dec.Step0 != EmergentEngine.EmergentActionId.CloseBreach)
                {
                    if (invPtBlankFail++ == 0) invPtBlankSample = $"Step0={dec.Step0} ({dec.Reason}) canHarm={inp.CanHarmTarget:F2}";
                }
                if (inp.HasVisibleEnemy && inp.CanHarmTarget <= 0.05f && (rsn == "direct_engage" || rsn == "cqb_snap_engage"))
                {
                    if (invNoHarmEngageFail++ == 0) invNoHarmSample = $"Step0={dec.Step0} ({dec.Reason}) canHarm={inp.CanHarmTarget:F2}";
                }
            }

            // 1A. Bridge-Realizable Infantry Sweep (7 * 6 * 6 * 5 * 4 * 6 * 2 * 2 = 120,960 -> subset 2 profiles & 2 directives = 60,480)
            for (int p = 0; p < 2; p++) // USA_Reg, GER_Shk + select JPN_Fan in directive alternation
            {
                int profIdx = p == 0 ? 0 : 2;
                for (int r = 0; r < 7; r++)
                {
                    ref readonly var w = ref weightsInf[profIdx, r];
                    for (int hc = 0; hc < harmCases.Length; hc++)
                    {
                        var h = harmCases[hc];
                        for (int ec = 0; ec < enemyCases.Length; ec++)
                        {
                            var e = enemyCases[ec];
                            for (int gc = 0; gc < gearCases.Length; gc++)
                            {
                                var g = gearCases[gc];
                                for (int oc = 0; oc < objCases.Length; oc++)
                                {
                                    var o = objCases[oc];
                                    for (int pc = 0; pc < oppCases.Length; pc++)
                                    {
                                        var op = oppCases[pc];
                                        for (int dc = 0; dc < 2; dc++)
                                        {
                                            var dir = directives[(dc == 0) ? (r % 2) : (2 + (hc % 2))];

                                            // Respect exact bridge mutual exclusions from EmergentRuntimeBridge.cs:814-899
                                            bool isLeader = r == 1;
                                            bool isRad = r == 2 || r == 3;
                                            bool isMed = r == 4;
                                            bool isMg = r == 5;
                                            bool isAmmo = r == 6;

                                            bool hasInjAlly = isMed && (op.OppMode == 1 || op.OppMode == 2);
                                            bool injInOpen = isMed && op.OppMode == 1;
                                            float patientHarm = hasInjAlly ? Math.Max(h.Proj, injInOpen ? 0.45f : 0.15f) : 0f;
                                            bool smokeReady = g.Smk && e.Lkt;
                                            bool safeMedicDrag = isMed && injInOpen && !smokeReady && !g.Bleed && h.Smooth < 0.28f && !(e.Vis && e.Dist <= 24f);
                                            bool hasDowned = (!isMed && !isLeader && !isRad && !g.Bleed && (op.OppMode == 1 || op.OppMode == 2)) || safeMedicDrag;
                                            bool hasPris = !isLeader && !isRad && (op.OppMode == 2 || op.OppMode == 3);
                                            bool allyNeedAmmo = isAmmo && (op.OppMode == 1 || op.OppMode == 3);
                                            bool aidNeed = op.OppMode == 1 || op.OppMode == 4;
                                            bool overwhelmed = h.Raw >= 0.65f && op.OppMode == 4;

                                            var inp = new EmergentEngine.EmergentInput
                                            {
                                                Now = now,
                                                IsLeader = isLeader,
                                                IsVehicle = false,
                                                HasRadioLink = true,
                                                IsRadioman = isRad,
                                                RadioLinkDrifting = (r == 3),
                                                IsMedic = isMed,
                                                IsAmmoCarrier = isAmmo,
                                                IsMachineGunnerOrBipod = isMg,
                                                IsAtCapable = g.NeedAt ? false : (r == 0 && e.Armor),
                                                RawHarmNow = h.Raw,
                                                SmoothedHarmNow = h.Smooth,
                                                ProjectedHarm = h.Proj,
                                                EnemyRelief = op.Pin ? 0.25f : 0f,
                                                DoorwayFreshCasualtyAhead = op.Door,
                                                LiveGrenadeClose = op.Gren,
                                                CloseAcousticThreat = e.Acoust,
                                                InCover = h.InCov,
                                                ExposedSideOfWall = h.ExpWall,
                                                IsBleeding = g.Bleed,
                                                HasBandage = g.Band,
                                                HasSmokeGrenade = smokeReady,
                                                HasFragGrenade = g.Frg,
                                                AmmoEmpty = g.Empty,
                                                HasVisibleEnemy = e.Vis,
                                                PointBlankThreat = e.PtB,
                                                VisibleEnemyDist = e.Dist,
                                                CanHarmTarget = e.CanH,
                                                CoverAvailable = h.CovAvail,
                                                FlankCoverAvailable = op.Flank,
                                                AllyPinningThreat = op.Pin,
                                                SmokeActive = op.SmkAct,
                                                HasLastKnownThreat = e.Lkt,
                                                LastKnownThreatDist = e.LktD,
                                                TargetBehindCover = e.BehCov,
                                                Overwhelmed = overwhelmed,
                                                UncounterableArmorClose = e.Armor,
                                                HasObjectiveTask = o.HasObj,
                                                InsideObjectiveZone = o.InObj,
                                                IsHomeGroundDefender = o.Home,
                                                ObjectiveDist = o.Dist,
                                                ObjectiveUrgency = o.Tick ? 0.85f : 0.45f,
                                                ClusteredEnemiesAtThreat = op.Clust,
                                                ObjectiveTickingAgainstUs = o.Tick,
                                                NearbyAmmoSource = g.NearAmmo,
                                                NeedsPrimaryWeaponUpgrade = g.NeedPri,
                                                NeedsAtWeaponScavenge = g.NeedAt,
                                                AllyNeedsAmmoDropNearby = allyNeedAmmo,
                                                HasInjuredAlly = hasInjAlly,
                                                InjuredAllyHarm = patientHarm,
                                                InjuredAllyInOpen = injInOpen,
                                                HasDownedAllyToDrag = hasDowned,
                                                HasSurrenderedPrisoner = hasPris,
                                                HasAidOverwatchNeed = aidNeed,
                                                InjuredAllySameSquad = true,
                                                DownedAllySameSquad = (op.OppMode == 1),
                                                PrisonerSameSquad = (op.OppMode == 2),
                                                AidOverwatchSameSquad = true,
                                                SquadCohesionBonus = 0.5f,
                                                SquadDirective = dir,
                                                SectorDesperationBias = profiles[profIdx].Desperation
                                            };

                                            infBridgeStates++;
                                            EvalAndRecord(ref inp, in w, bridgeInfCounts, bridgeReasonCounts, checkSafety: true);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // 1B. Unconstrained Infantry Sweep (deliberately breaks Bridge mutual exclusions, e.g. Medic + HasDownedAllyToDrag)
            for (int r = 0; r < 4; r++) // 0=Rifleman, 1=SL, 2=RadDrift, 3=Medic
            {
                int mapRole = r == 0 ? 0 : (r == 1 ? 1 : (r == 2 ? 3 : 4));
                ref readonly var w = ref weightsInf[3, mapRole]; // Ussr cautious/high-aid
                for (int hc = 0; hc < harmCases.Length; hc++)
                {
                    var h = harmCases[hc];
                    for (int ec = 0; ec < enemyCases.Length; ec++)
                    {
                        var e = enemyCases[ec];
                        for (int smk = 0; smk < 2; smk++)
                        {
                            for (int inj = 0; inj < 2; inj++)
                            {
                                for (int down = 0; down < 2; down++)
                                {
                                    for (int pris = 0; pris < 2; pris++)
                                    {
                                        var inp = new EmergentEngine.EmergentInput
                                        {
                                            Now = now,
                                            IsLeader = (r == 1),
                                            IsRadioman = (r == 2),
                                            RadioLinkDrifting = (r == 2),
                                            IsMedic = (r == 3),
                                            HasRadioLink = true,
                                            RawHarmNow = h.Raw,
                                            SmoothedHarmNow = h.Smooth,
                                            ProjectedHarm = h.Proj,
                                            InCover = h.InCov,
                                            ExposedSideOfWall = h.ExpWall,
                                            CoverAvailable = h.CovAvail,
                                            HasVisibleEnemy = e.Vis,
                                            PointBlankThreat = e.PtB,
                                            VisibleEnemyDist = e.Dist,
                                            CanHarmTarget = e.CanH,
                                            HasLastKnownThreat = e.Lkt,
                                            LastKnownThreatDist = e.LktD,
                                            UncounterableArmorClose = e.Armor,
                                            HasSmokeGrenade = smk == 1,
                                            HasInjuredAlly = inj == 1,
                                            InjuredAllyHarm = Math.Max(h.Proj, 0.45f),
                                            InjuredAllyInOpen = inj == 1,
                                            HasDownedAllyToDrag = down == 1,
                                            HasSurrenderedPrisoner = pris == 1,
                                            InjuredAllySameSquad = true,
                                            DownedAllySameSquad = true,
                                            PrisonerSameSquad = true,
                                            HasObjectiveTask = true,
                                            ObjectiveDist = 70f,
                                            ObjectiveUrgency = 0.45f
                                        };
                                        infUnconStates++;
                                        EvalAndRecord(ref inp, in w, unconInfCounts, unconReasonCounts, checkSafety: false);
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // 1C. Vehicle Bridge-Realizable vs Unconstrained Sweeps (EmergentRuntimeBridge.cs:1621-1637)
            var vehHarmVals = new[] { 0.05f, 0.45f, 0.85f, 0.98f };
            for (int p = 0; p < profiles.Length; p++)
            {
                ref readonly var w = ref weightsVeh[p];
                foreach (float rawH in vehHarmVals)
                foreach (float projH in new[] { 0.0f, 0.35f, 0.70f })
                foreach (bool heavy in new[] { false, true })
                foreach (bool doomed in new[] { false, true })
                foreach (bool needRep in new[] { false, true })
                {
                    var vBridge = new EmergentEngine.EmergentInput
                    {
                        Now = now,
                        IsVehicle = true,
                        IsHeavyArmor = heavy,
                        IsLightArmor = !heavy,
                        HasRadioLink = true,
                        RawHarmNow = doomed ? 0.98f : rawH,
                        SmoothedHarmNow = doomed ? 0.98f : rawH,
                        ProjectedHarm = projH,
                        HullBurningOrDoomed = doomed,
                        NeedsTrackOrEngineRepair = !doomed && needRep,
                        CanHarmTarget = 0.80f
                    };
                    vehBridgeStates++;
                    EmergentEngine.EmergentDecision vdB;
                    if (!EmergentEngine.EvaluateFastReflex(in vBridge, in w, now, out vdB))
                        vdB = EmergentEngine.EvaluateDeliberative(in vBridge, in w, default, now);
                    bridgeVehCounts[(int)vdB.Step0]++;
                    string brR = vdB.Reason ?? "none";
                    bridgeReasonCounts[brR] = bridgeReasonCounts.TryGetValue(brR, out int bc) ? bc + 1 : 1;
                    if (doomed && vdB.Step0 != EmergentEngine.EmergentActionId.VehicleBailout)
                    {
                        if (invArmorBailFail++ == 0) invArmorSample = $"Step0={vdB.Step0} ({brR})";
                    }

                    foreach (bool smk in new[] { false, true })
                    foreach (bool ammoEmpty in new[] { false, true })
                    foreach (bool uncArmor in new[] { false, true })
                    {
                        var vUnc = vBridge;
                        vUnc.HasSmokeGrenade = smk;
                        vUnc.AmmoEmpty = ammoEmpty;
                        vUnc.UncounterableArmorClose = uncArmor;
                        vehUnconStates++;
                        EmergentEngine.EmergentDecision vdU;
                        if (!EmergentEngine.EvaluateFastReflex(in vUnc, in w, now, out vdU))
                            vdU = EmergentEngine.EvaluateDeliberative(in vUnc, in w, default, now);
                        unconVehCounts[(int)vdU.Step0]++;
                        string unR = vdU.Reason ?? "none";
                        unconReasonCounts[unR] = unconReasonCounts.TryGetValue(unR, out int uc) ? uc + 1 : 1;
                    }
                }
            }

            Console.WriteLine($"  States Evaluated: Infantry Bridge={infBridgeStates:N0}, Infantry Unconstrained={infUnconStates:N0}, Vehicle Bridge={vehBridgeStates:N0}, Vehicle Unconstrained={vehUnconStates:N0}");
            Console.WriteLine($"  Safety Invariants: GrenadeEvadeFail={invGrenadeFail}, CqbPointBlankFail={invPtBlankFail}, NoHarmEngageFail={invNoHarmEngageFail}, HullDoomBailFail={invArmorBailFail}");

            if (invGrenadeFail > 0) AddFinding(AuditSeverity.CRITICAL, "Pass1", "INV_GRENADE", $"LiveGrenadeClose missed EvadeGrenade {invGrenadeFail}x ({invGrenadeSample})");
            if (invPtBlankFail > 0) AddFinding(AuditSeverity.CRITICAL, "Pass1", "INV_CQB", $"PointBlankThreat missed CloseBreach {invPtBlankFail}x ({invPtBlankSample})");
            if (invNoHarmEngageFail > 0) AddFinding(AuditSeverity.CRITICAL, "Pass1", "INV_NOHARM", $"CanHarm<=0.05 picked direct_engage {invNoHarmEngageFail}x ({invNoHarmSample})");
            if (invArmorBailFail > 0) AddFinding(AuditSeverity.CRITICAL, "Pass1", "INV_DOOMBAIL", $"HullBurningOrDoomed missed VehicleBailout {invArmorBailFail}x ({invArmorSample})");

            Console.WriteLine("  Action Reachability (Bridge-Realizable vs Unconstrained):");
            foreach (var act in InfantryActions)
            {
                int b = bridgeInfCounts[(int)act];
                int u = unconInfCounts[(int)act];
                checksum = MixFnv(checksum, (ulong)((int)act * 131 + b));
                Console.WriteLine($"    [Inf] {act,-20} Bridge={b,6:N0} | Uncon={u,5:N0}");
                if (b == 0 && u > 0)
                    AddFinding(AuditSeverity.HIGH, "Pass1", "BRIDGE_UNREACHABLE_ACTION", $"Infantry {act} is 0 in Bridge-Realizable grid but {u} in Unconstrained.");
                else if (b == 0 && u == 0)
                    AddFinding(AuditSeverity.MEDIUM, "Pass1", "GRID_UNREACHED_ACTION", $"Infantry {act} had 0 hits across both Pass 1 grids.");
            }
            foreach (var act in VehicleActions)
            {
                int b = bridgeVehCounts[(int)act];
                int u = unconVehCounts[(int)act];
                checksum = MixFnv(checksum, (ulong)((int)act * 257 + b));
                Console.WriteLine($"    [Veh] {act,-20} Bridge={b,6:N0} | Uncon={u,5:N0}");
                if (b == 0 && u > 0)
                    AddFinding(AuditSeverity.HIGH, "Pass1", "VEH_DEAD_BRANCH", $"Vehicle {act} is 0 in Bridge-Realizable grid (EmergentRuntimeBridge TryEvaluateEmergentVehicleCrew omits input flags) vs {u} in Unconstrained.");
            }

            Console.WriteLine("  Prerequisite Chain Reachability:");
            foreach (string pr in ExpectedPrereqReasons)
            {
                bridgeReasonCounts.TryGetValue(pr, out int bc);
                unconReasonCounts.TryGetValue(pr, out int uc);
                checksum = MixFnv(checksum, (ulong)bc);
                Console.WriteLine($"    {pr,-24} Bridge={bc,6:N0} | Uncon={uc,5:N0}");
                if (bc == 0 && uc > 0)
                {
                    AddFinding(AuditSeverity.HIGH, "Pass1", "DEAD_PREREQ_BRIDGE", $"Prereq '{pr}' has 0 hits in Bridge-Realizable grid vs {uc} in Unconstrained (unreachable due to bridge gating).");
                }
                else if (bc == 0 && uc == 0)
                {
                    AddFinding(AuditSeverity.MEDIUM, "Pass1", "DEAD_PREREQ_BOTH", $"Prereq '{pr}' had 0 hits in both Bridge and Unconstrained grids.");
                }
            }

            return checksum;
        }

        // =====================================================================
        // PASS 2: Multi-Tick State-Machine Convergence, Oscillation & Soft-Lock Sim
        // =====================================================================
        private struct SimBotState
        {
            public string Label;
            public EmergentEngine.EmergentInput Input;
            public EmergentEngine.EmergentWeights Weights;
            public EmergentEngine.EmergentDecision CurrentDecision;
            public bool HasDecision;
            public float NextPlanTime;
            public float PrereqBlockedUntil;
            public EmergentEngine.EmergentActionId PrereqBlockedAction;
            public EmergentEngine.EmergentReflexKind LastReflexKind;
            public float LastReflexTime;
            public EmergentEngine.EmergentActionId LastActionForFlip;
            public float FlipWindowStart;
            public int RecentFlipCount;
        }

        private static ulong RunPass2MultiTickSimulation(WeightProfile[] profiles, ulong checksum)
        {
            Console.WriteLine();
            Console.WriteLine("[Pass 2/5] Multi-Tick State-Machine Convergence, Oscillation & Soft-Lock Sim");

            // Build ~2,400 deterministic test cases spanning roles, harm, cover, opportunities, directives
            var seeds = new List<SimBotState>(2500);
            int[] roles = { 0, 1, 3, 4, 5, 6 }; // Rifle, SL, RadDrift, Medic, MG, AmmoCarrier
            float[] harms = { 0.05f, 0.25f, 0.45f, 0.65f, 0.80f };
            float[] dists = { 5f, 14f, 26f, 55f, 999f };
            var dirs = new[]
            {
                EmergentEngine.Tier2SquadDirective.AdvanceBound,
                EmergentEngine.Tier2SquadDirective.AnvilPin,
                EmergentEngine.Tier2SquadDirective.HammerFlank,
                EmergentEngine.Tier2SquadDirective.SquadSmokePush
            };

            foreach (int r in roles)
            foreach (float hVal in harms)
            foreach (float dVal in dists)
            foreach (bool inCov in new[] { false, true })
            foreach (int opp in new[] { 0, 1, 2, 3 }) // 0=none, 1=downed+med+ammo, 2=bleed+smk+frag, 3=flank+pin+objTick
            foreach (var dir in dirs)
            {
                int pIdx = ((int)(hVal * 10) + r) % profiles.Length;
                bool isLdr = r == 1, isRad = r == 3, isMed = r == 4, isMg = r == 5, isAmmo = r == 6;
                bool hasVis = dVal < 900f;
                var w = ComputeProfileWeights(in profiles[pIdx], isLdr, isRad, isMed);

                var inp = new EmergentEngine.EmergentInput
                {
                    IsLeader = isLdr,
                    IsRadioman = isRad,
                    RadioLinkDrifting = isRad && opp != 0,
                    IsMedic = isMed,
                    IsMachineGunnerOrBipod = isMg,
                    IsAmmoCarrier = isAmmo,
                    HasRadioLink = true,
                    RawHarmNow = hVal,
                    SmoothedHarmNow = hVal,
                    ProjectedHarm = hVal * 0.40f,
                    InCover = inCov,
                    CoverAvailable = true,
                    HasVisibleEnemy = hasVis,
                    PointBlankThreat = hasVis && dVal <= 6f,
                    VisibleEnemyDist = dVal,
                    CanHarmTarget = opp == 2 && dVal > 20f ? 0.00f : (hasVis ? 0.85f : 0.45f),
                    UncounterableArmorClose = opp == 2 && dVal > 20f && dVal < 40f,
                    HasLastKnownThreat = true,
                    LastKnownThreatDist = hasVis ? dVal : 35f,
                    TargetBehindCover = opp == 3,
                    IsBleeding = opp == 2 && !inCov,
                    HasBandage = opp == 2,
                    HasSmokeGrenade = opp >= 1,
                    HasFragGrenade = opp >= 2,
                    AmmoEmpty = opp == 1 && !isAmmo,
                    NearbyAmmoSource = opp >= 1,
                    AllyNeedsAmmoDropNearby = isAmmo && opp >= 1,
                    HasInjuredAlly = isMed && opp == 1,
                    InjuredAllyInOpen = isMed && opp == 1,
                    InjuredAllyHarm = hVal * 0.45f,
                    HasDownedAllyToDrag = !isMed && !isLdr && !isRad && opp == 1,
                    HasSurrenderedPrisoner = !isLdr && opp == 3,
                    FlankCoverAvailable = opp == 3,
                    AllyPinningThreat = opp == 3,
                    HasObjectiveTask = true,
                    ObjectiveDist = 65f,
                    ObjectiveUrgency = opp == 3 ? 0.85f : 0.45f,
                    ObjectiveTickingAgainstUs = opp == 3,
                    CloseAcousticThreat = !hasVis && opp == 2,
                    SquadDirective = dir
                };

                seeds.Add(new SimBotState
                {
                    Label = $"role={r} harm={hVal:F2} dist={dVal:F0} cov={inCov} opp={opp} dir={dir}",
                    Input = inp,
                    Weights = w
                });
            }

            // Simulate 30 ticks @ dt=0.30s under static world (thrash test) + executor rejection feedback test
            int thrashCases = 0;
            int reflexLoops = 0;
            int rejectedSoftLocks = 0;
            var rejectLockByAction = new Dictionary<EmergentEngine.EmergentActionId, int>();
            string firstThrashSample = "", firstReflexSample = "";

            for (int s = 0; s < seeds.Count; s++)
            {
                SimBotState bot = seeds[s];
                int reflexCount = 0;
                int thrashTriggers = 0;
                int stuckRejectTicks = 0;
                EmergentEngine.EmergentActionId lastRejAct = EmergentEngine.EmergentActionId.None;

                for (int t = 0; t < 30; t++)
                {
                    float now = 10.0f + t * 0.30f;
                    var inp = bot.Input;
                    inp.Now = now;
                    inp.CurrentStep0 = bot.HasDecision ? bot.CurrentDecision.Step0 : EmergentEngine.EmergentActionId.None;
                    inp.LastReflexKind = bot.LastReflexKind;
                    inp.LastReflexTime = bot.LastReflexTime;
                    inp.PrereqBlockedUntil = bot.PrereqBlockedUntil;
                    inp.PrereqBlockedAction = (byte)bot.PrereqBlockedAction;

                    bool ranReflex = EmergentEngine.EvaluateFastReflex(in inp, in bot.Weights, now, out EmergentEngine.EmergentDecision dec);
                    if (ranReflex)
                    {
                        reflexCount++;
                        bot.LastReflexKind = dec.Reflex;
                        bot.LastReflexTime = now;
                        bot.NextPlanTime = Math.Max(bot.NextPlanTime, dec.CommitUntil);
                    }
                    else
                    {
                        bool closeContactWake = inp.HasVisibleEnemy && inp.VisibleEnemyDist <= 16.0f && !inp.AmmoEmpty && inp.CanHarmTarget > 0.20f &&
                            (!bot.HasDecision ||
                             (bot.CurrentDecision.Step0 != EmergentEngine.EmergentActionId.HoldAndFight &&
                              bot.CurrentDecision.Step0 != EmergentEngine.EmergentActionId.CloseBreach &&
                              bot.CurrentDecision.Step0 != EmergentEngine.EmergentActionId.EvadeGrenade));
                        if (!bot.HasDecision || closeContactWake || now >= bot.NextPlanTime)
                        {
                            EmergentEngine.EmergentDecision prevForDelib = bot.CurrentDecision;
                            if (closeContactWake &&
                                (prevForDelib.Step0 == EmergentEngine.EmergentActionId.AdvanceObjective ||
                                 prevForDelib.Step0 == EmergentEngine.EmergentActionId.FlankManeuver ||
                                 prevForDelib.Step0 == EmergentEngine.EmergentActionId.MaintainRadioLink ||
                                 prevForDelib.Step0 == EmergentEngine.EmergentActionId.SuppressPin))
                            {
                                prevForDelib.CommitUntil = 0f;
                            }
                            dec = EmergentEngine.EvaluateDeliberative(in inp, in bot.Weights, in prevForDelib, now);
                            bot.NextPlanTime = now + 0.30f;
                            if (dec.UnsafePrereqRejected)
                            {
                                bot.PrereqBlockedAction = dec.RejectedPrereqAction;
                                bot.PrereqBlockedUntil = now + 4.5f;
                            }
                        }
                        else
                        {
                            dec = bot.CurrentDecision;
                        }
                    }

                    if (!ranReflex && dec.Step0 != bot.LastActionForFlip)
                    {
                        if (bot.FlipWindowStart <= 0f || (now - bot.FlipWindowStart) > 4.0f)
                        {
                            bot.FlipWindowStart = now;
                            bot.RecentFlipCount = 1;
                        }
                        else
                        {
                            bot.RecentFlipCount++;
                            if (bot.RecentFlipCount >= 5) thrashTriggers++;
                        }
                        bot.LastActionForFlip = dec.Step0;
                    }

                    bot.HasDecision = true;
                    bot.CurrentDecision = dec;

                    // Simulate aligned bridge rejection + CommitUntil = 0f write-back on rejection
                    bool execSilentFail = false;
                    if (dec.Step0 == EmergentEngine.EmergentActionId.DragCasualty)
                    {
                        bool visBad = inp.IsMedic ? (inp.HasVisibleEnemy && inp.VisibleEnemyDist <= 24f) : (inp.HasVisibleEnemy && (!inp.InCover ? inp.VisibleEnemyDist <= 24f : inp.VisibleEnemyDist <= 14f));
                        if (inp.IsBleeding || visBad || (inp.IsMedic && inp.SmoothedHarmNow >= 0.28f))
                            execSilentFail = true;
                    }

                    if (execSilentFail)
                    {
                        // Bridge writes CommitUntil = 0f & NextPlanTime = now on actionRejected; only traps if input wasn't gated
                        bot.CurrentDecision.CommitUntil = 0f;
                        bot.NextPlanTime = now;
                        if (dec.Step0 == lastRejAct) stuckRejectTicks++;
                        else { lastRejAct = dec.Step0; stuckRejectTicks = 1; }
                    }
                    else
                    {
                        stuckRejectTicks = 0;
                        lastRejAct = EmergentEngine.EmergentActionId.None;
                    }

                    if (stuckRejectTicks == 6)
                    {
                        rejectedSoftLocks++;
                        rejectLockByAction[dec.Step0] = rejectLockByAction.TryGetValue(dec.Step0, out int c) ? c + 1 : 1;
                    }
                }

                if (thrashTriggers > 0)
                {
                    thrashCases++;
                    if (string.IsNullOrEmpty(firstThrashSample)) firstThrashSample = bot.Label;
                }
                if (reflexCount >= 4)
                {
                    reflexLoops++;
                    if (string.IsNullOrEmpty(firstReflexSample)) firstReflexSample = $"{bot.Label} (reflexes={reflexCount}, last={bot.LastReflexKind})";
                }
            }

            // Prerequisite Chain Convergence Verification
            int chainConverged = 0, chainTotal = 0;
            var chainResults = VerifyAllPrerequisiteChains(profiles[0], profiles[3], ref chainTotal, ref chainConverged);

            checksum = MixFnv(checksum, (ulong)(thrashCases * 17 + reflexLoops * 31 + rejectedSoftLocks * 53 + chainConverged));

            Console.WriteLine($"  Simulated {seeds.Count:N0} states x 30 ticks (9.0s):");
            Console.WriteLine($"    Deliberative Thrash (>=5 flips/4s) : {thrashCases} cases");
            Console.WriteLine($"    Reflex Ping-Pong (>=4 fires/9s)    : {reflexLoops} cases");
            Console.WriteLine($"    Silent-Reject Soft-Locks (>=1.8s)  : {rejectedSoftLocks} cases");
            foreach (var kv in rejectLockByAction)
                Console.WriteLine($"      - Locked Action {kv.Key}: {kv.Value} states");
            Console.WriteLine($"  Prerequisite Chain Convergence ({chainConverged}/{chainTotal} converged):");
            foreach (var cr in chainResults)
                Console.WriteLine($"    {cr}");

            if (thrashCases > 0)
                AddFinding(AuditSeverity.MEDIUM, "Pass2", "DELIB_THRASH", $"Static-world simulation triggered deliberative thrash in {thrashCases}/{seeds.Count} cases (e.g. {firstThrashSample}).");
            if (reflexLoops > 0)
                AddFinding(AuditSeverity.INFO, "Pass2", "REFLEX_REPEAT", $"Repeated reflex fires under unchanging reflex stimulus in {reflexLoops}/{seeds.Count} cases (e.g. {firstReflexSample}).");
            if (rejectedSoftLocks > 0)
                AddFinding(AuditSeverity.HIGH, "Pass2", "REJECT_SOFTLOCK", $"Bridge Executor-rejection soft-locked {rejectedSoftLocks} states >=1.8s because CommitUntil is never cleared or blocked on reject.");

            return checksum;
        }

        private static bool oppIdxIsClaimBlocked(EmergentEngine.EmergentActionId act, in EmergentEngine.EmergentInput inp)
        {
            // LiberatePrisoner fails silently when board.SurrenderedComrade == null or exclusive claim lost,
            // while EmergentInput only knows WorldSoldiers.
            return act == EmergentEngine.EmergentActionId.LiberatePrisoner && !inp.PrisonerSameSquad;
        }

        private static bool hIndexNeedsCooldown(int t) => t >= 0;

        private static List<string> VerifyAllPrerequisiteChains(
            in WeightProfile wpStandard,
            in WeightProfile wpHighAid,
            ref int total,
            ref int converged)
        {
            var results = new List<string>();
            float now = 100f;
            var wInf = ComputeProfileWeights(in wpStandard, false, false, false);
            var wMed = ComputeProfileWeights(in wpHighAid, false, false, true);
            var wVeh = ComputeProfileWeights(in wpStandard, true, false, false);
            int locTotal = 0;
            int locConverged = 0;

            void TestChain(
                string name,
                EmergentEngine.EmergentInput step0Inp,
                in EmergentEngine.EmergentWeights w,
                EmergentEngine.EmergentActionId exp0,
                EmergentEngine.EmergentActionId exp1,
                bool step0BridgeRealizable,
                Func<EmergentEngine.EmergentInput, EmergentEngine.EmergentInput> applyStep0Effect)
            {
                locTotal++;
                step0Inp.Now = now;
                var d0 = EmergentEngine.EvaluateDeliberative(in step0Inp, in w, default, now);
                if (d0.Step0 != exp0 || d0.Step1 != exp1)
                {
                    results.Add($"[FAIL] {name}: Step0 got {d0.Step0}->{d0.Step1} ({d0.Reason}), expected {exp0}->{exp1}");
                    AddFinding(AuditSeverity.HIGH, "Pass2", "CHAIN_BREAK_" + name.ToUpperInvariant(),
                        $"Prereq chain {name} failed Step0 selection: got {d0.Step0}->{d0.Step1} ({d0.Reason}).");
                    return;
                }
                if (!step0BridgeRealizable)
                {
                    results.Add($"[BRIDGE-DEAD] {name}: Planner picked {d0.Step0}->{d0.Step1} ({d0.Reason}), UNREACHABLE in real bridge!");
                    return;
                }

                var step1Inp = applyStep0Effect(step0Inp);
                step1Inp.Now = now + 1.5f;
                var d1 = EmergentEngine.EvaluateDeliberative(in step1Inp, in w, in d0, step1Inp.Now);
                if (d1.Step0 == exp1 || exp1 == EmergentEngine.EmergentActionId.None)
                {
                    locConverged++;
                    results.Add($"[OK]   {name}: {d0.Step0}->{d0.Step1} ({d0.Reason}) => {d1.Step0} ({d1.Reason})");
                }
                else
                {
                    results.Add($"[MISS] {name}: {d0.Step0}->{d0.Step1} finished into {d1.Step0} ({d1.Reason}), expected {exp1}");
                    AddFinding(AuditSeverity.MEDIUM, "Pass2", "CHAIN_MISS_" + name.ToUpperInvariant(),
                        $"After {exp0}, planner selected {d1.Step0} ({d1.Reason}) instead of chained Step1={exp1}.");
                }
            }

            TestChain("Cover_Bandage",
                new EmergentEngine.EmergentInput { IsBleeding = true, HasBandage = true, InCover = false, CoverAvailable = true, SmoothedHarmNow = 0.42f, ProjectedHarm = 0.20f },
                in wInf, EmergentEngine.EmergentActionId.SeekCover, EmergentEngine.EmergentActionId.SelfBandage, true,
                i => { i.InCover = true; return i; });

            TestChain("Smoke_Bandage",
                new EmergentEngine.EmergentInput { IsBleeding = true, HasBandage = true, InCover = false, CoverAvailable = false, HasSmokeGrenade = true, SmoothedHarmNow = 0.50f, ProjectedHarm = 0.25f },
                in wInf, EmergentEngine.EmergentActionId.PopSmoke, EmergentEngine.EmergentActionId.SelfBandage, true,
                i => { i.SmokeActive = true; i.HasSmokeGrenade = false; i.SmoothedHarmNow = 0.15f; i.ProjectedHarm = 0.05f; return i; });

            TestChain("Scavenge_Bandage",
                new EmergentEngine.EmergentInput { IsBleeding = true, HasBandage = false, NearbyAmmoSource = true, InCover = true, SmoothedHarmNow = 0.10f },
                in wInf, EmergentEngine.EmergentActionId.ResupplyAmmo, EmergentEngine.EmergentActionId.SelfBandage, true,
                i => { i.HasBandage = true; return i; });

            TestChain("Ammo_Fight",
                new EmergentEngine.EmergentInput { HasVisibleEnemy = true, VisibleEnemyDist = 25f, CanHarmTarget = 0.85f, AmmoEmpty = true, NearbyAmmoSource = true, InCover = true, SmoothedHarmNow = 0.15f },
                in wInf, EmergentEngine.EmergentActionId.ResupplyAmmo, EmergentEngine.EmergentActionId.HoldAndFight, true,
                i => { i.AmmoEmpty = false; return i; });

            TestChain("Smoke_Medic",
                new EmergentEngine.EmergentInput { IsMedic = true, HasInjuredAlly = true, InjuredAllyInOpen = true, InjuredAllyHarm = 0.45f, SmoothedHarmNow = 0.50f, HasSmokeGrenade = true, InCover = true },
                in wMed, EmergentEngine.EmergentActionId.PopSmoke, EmergentEngine.EmergentActionId.MedicHeal, true,
                i => { i.SmokeActive = true; i.HasSmokeGrenade = false; i.SmoothedHarmNow = 0.15f; i.InjuredAllyHarm = 0.15f; i.InjuredAllyInOpen = false; return i; });

            TestChain("Drag_Medic",
                new EmergentEngine.EmergentInput { IsMedic = true, HasInjuredAlly = true, InjuredAllyInOpen = true, InjuredAllyHarm = 0.55f, SmoothedHarmNow = 0.12f, HasSmokeGrenade = false, HasDownedAllyToDrag = true, InCover = true },
                in wMed, EmergentEngine.EmergentActionId.DragCasualty, EmergentEngine.EmergentActionId.MedicHeal, true,
                i => { i.InjuredAllyInOpen = false; i.InjuredAllyHarm = 0.12f; i.SmoothedHarmNow = 0.10f; return i; });

            total += locTotal;
            converged += locConverged;
            return results;
        }

        // =====================================================================
        // PASS 3: Planner-vs-Executor Contract Parity Matrix
        // =====================================================================
        private struct WorldExecutorContext
        {
            public string Label;
            public bool IsTransientRace;
            public float Suppression;
            public bool BoardHasActiveThreat;
            public bool BoardPatientSameSquad;
            public float PatientDist;
            public bool PatientInCover;
            public bool MedicWithin6m;
            public bool ExclusiveClaimFree;
            public bool BoardHasSurrenderedComrade;
            public bool HasMedicHealDestination;
            public bool HasValidInjuredPatientSoldier;
            public bool FlankCoverClaimFree;
            public bool FragTargetIsSoldier;
            public bool FragCooldownReady;
            public bool SmokeClaimAndAnimReady;
            public bool FallbackCooldownReady;
            public bool LeaderFarBehindObjective;
            public bool HasDestinationAssigned;
            public bool InHandsOrBodyAnim;
            public bool RpcBudgetReady;
        }

        private sealed class ParityStats
        {
            public int PlannerHits;
            public int Accept;
            public int AlignedReject;
            public int RaceReject;
            public Dictionary<string, int> RejectReasons = new Dictionary<string, int>(StringComparer.Ordinal);
            public string MinimalCounterexample = "";
        }

        private static bool EvaluateExecutorContract(
            EmergentEngine.EmergentActionId act,
            in EmergentEngine.EmergentInput inp,
            in WorldExecutorContext ctx,
            out string rejectReason)
        {
            rejectReason = "";

            switch (act)
            {
                case EmergentEngine.EmergentActionId.DragCasualty:
                    // Modeled directly from SoldierGoapExecution.cs IsBuddyDragRescuerEligible + FindBestDragCasualtyCandidate + TryExecuteBuddyCasualtyDrag
                    if (inp.IsBleeding) { rejectReason = "exec_isBleeding"; return false; }
                    if (inp.IsLeader || inp.IsRadioman) { rejectReason = "exec_SL_or_Radioman"; return false; }
                    if (inp.IsMedic)
                    {
                        float medSup = inp.SmokeActive ? 20f : 12f;
                        if (inp.RawHarmNow >= 0.28f || ctx.Suppression >= medSup || (inp.HasVisibleEnemy && inp.VisibleEnemyDist <= 24.0f))
                        {
                            rejectReason = $"exec_medicUnsafeRisk(sup={ctx.Suppression:F0},harm={inp.RawHarmNow:F2},vis={inp.VisibleEnemyDist:F0})";
                            return false;
                        }
                        if (ctx.PatientDist > 10.0f) { rejectReason = $"exec_medPatientRange({ctx.PatientDist:F1}>10)"; return false; }
                        if (ctx.PatientInCover) { rejectReason = "exec_patientInCover"; return false; }
                    }
                    else
                    {
                        float maxSup = inp.SmokeActive ? 24f : 18f; // conservative baseline across factions without boost
                        if (ctx.Suppression >= maxSup && !ctx.IsTransientRace)
                        {
                            if (ctx.Suppression >= (inp.SmokeActive ? 36f : 24f))
                            {
                                rejectReason = $"exec_suppression({ctx.Suppression:F0})";
                                return false;
                            }
                        }
                        else if (ctx.Suppression >= (inp.SmokeActive ? 36f : 24f))
                        {
                            rejectReason = $"exec_suppression({ctx.Suppression:F0})";
                            return false;
                        }
                        if (inp.HasVisibleEnemy && (!inp.InCover ? inp.VisibleEnemyDist <= 24.0f : inp.VisibleEnemyDist <= 14.0f))
                        {
                            rejectReason = $"exec_closeVisEnemy(dist={inp.VisibleEnemyDist:F0}m,cov={inp.InCover})";
                            return false;
                        }
                        if (!ctx.BoardHasActiveThreat) { rejectReason = "exec_noBoardThreat"; return false; }
                        if (ctx.PatientDist > 14.0f) { rejectReason = $"exec_patientRange({ctx.PatientDist:F1}m>14m)"; return false; }
                        if (ctx.PatientInCover) { rejectReason = "exec_patientIsOnCover"; return false; }
                        if (ctx.MedicWithin6m) { rejectReason = "exec_medicWithin6m"; return false; }
                    }
                    if (!ctx.RpcBudgetReady && ctx.PatientDist <= 2.35f) { rejectReason = "exec_rpcBudget"; return false; }
                    return true;

                case EmergentEngine.EmergentActionId.LiberatePrisoner:
                    // Modeled from EmergentRuntimeBridge.cs (prisonerTarget ?? board.SurrenderedComrade) + CloseCombatAndSupport.cs
                    if (!ctx.BoardHasSurrenderedComrade && ctx.IsTransientRace) { rejectReason = "exec_prisonerLostOrClaimed"; return false; }
                    if (!ctx.ExclusiveClaimFree) { rejectReason = "exec_claimTaken"; return false; }
                    if (inp.IsLeader || inp.IsRadioman) { rejectReason = "exec_leaderOrRadioBlocked"; return false; }
                    if (ctx.PatientDist > 28f) { rejectReason = "exec_beyond28m"; return false; }
                    if (!ctx.RpcBudgetReady && ctx.PatientDist <= 3.8f) { rejectReason = "exec_rpcBudget"; return false; }
                    return true;

                case EmergentEngine.EmergentActionId.MedicHeal:
                    // Modeled from EmergentRuntimeBridge.cs: IsActivelyHealingTeammate OR creates MedicHealingDestination from injuredTargetSoldier
                    if (!ctx.HasMedicHealDestination && !ctx.HasValidInjuredPatientSoldier)
                    {
                        rejectReason = "exec_patientDiedOrMovedOutOfReach";
                        return false;
                    }
                    return true;

                case EmergentEngine.EmergentActionId.MaintainRadioLink:
                    // Pre-gated in EmergentRuntimeBridge RadioLinkDrifting
                    if (ctx.LeaderFarBehindObjective) { rejectReason = "exec_leaderBehindObj"; return false; }
                    return true;

                case EmergentEngine.EmergentActionId.PopSmoke:
                    if (!ctx.ExclusiveClaimFree) { rejectReason = "exec_smokeClaimFailed"; return false; }
                    if (!ctx.SmokeClaimAndAnimReady) { rejectReason = "exec_smokeCooldownOrBodyAnimOrSameLane"; return false; }
                    return true;

                case EmergentEngine.EmergentActionId.FlankManeuver:
                    if (!ctx.FlankCoverClaimFree) { rejectReason = "exec_flankSlotClaimedOrNonReachable"; return false; }
                    return true;

                case EmergentEngine.EmergentActionId.FragFlush:
                    // Aligned minDist = soldier.IsOnCover() ? 6.5f : 8.5f
                    if (!ctx.FragTargetIsSoldier) { rejectReason = "exec_fragTargetIsVehicleOrNull"; return false; }
                    if (!ctx.FragCooldownReady) { rejectReason = "exec_fragThrowCooldown"; return false; }
                    float d = inp.HasVisibleEnemy ? inp.VisibleEnemyDist : inp.LastKnownThreatDist;
                    float minFragD = inp.InCover ? 6.5f : 8.5f;
                    if (d < minFragD || d > 31.0f) { rejectReason = $"exec_fragRange({d:F1}m_not_in_{minFragD:F1}..31)"; return false; }
                    return true;

                case EmergentEngine.EmergentActionId.ResupplyAmmo:
                    bool isUrgent = inp.AmmoEmpty || inp.NeedsPrimaryWeaponUpgrade || inp.NeedsAtWeaponScavenge || inp.IsBleeding;
                    if (!isUrgent && ctx.Suppression >= 5.0f)
                    {
                        rejectReason = $"exec_nonUrgentScavengeSup>=5(sup={ctx.Suppression:F0},harm={inp.RawHarmNow:F2})";
                        return false;
                    }
                    if (isUrgent && ctx.Suppression >= 22.0f)
                    {
                        rejectReason = $"exec_urgentScavengeSup>=22(sup={ctx.Suppression:F0})";
                        return false;
                    }
                    return true;

                case EmergentEngine.EmergentActionId.SelfBandage:
                    // InHands/BodyAnim causes a clean transient retry (NextPlanTime=now via CommitUntil writeback when bleeding blocked)
                    if (inp.IsBleeding && ctx.InHandsOrBodyAnim) { rejectReason = "exec_inHandsOrBodyAnim"; return false; }
                    return true;

                case EmergentEngine.EmergentActionId.FallbackRegroup:
                    if (!ctx.FallbackCooldownReady) { rejectReason = "exec_nextIndivFallbackCooldown"; return false; }
                    return true;

                case EmergentEngine.EmergentActionId.PushAmmoDrop:
                    if (!ctx.RpcBudgetReady) { rejectReason = "exec_rpcBudgetDenied"; return false; }
                    return true;

                case EmergentEngine.EmergentActionId.AdvanceObjective:
                    // Bridge now falls back to MoveBotTowardPosition(board.ObjectivePosition) when !HasDestinationAssigned
                    return true;

                default:
                    return true;
            }
        }

        private static ulong RunPass3ContractParityMatrix(WeightProfile[] profiles, ulong checksum, bool writesCommitBack)
        {
            Console.WriteLine();
            Console.WriteLine("[Pass 3/5] Planner-vs-Executor Contract Parity Matrix");

            var stats = new Dictionary<EmergentEngine.EmergentActionId, ParityStats>();
            foreach (var a in InfantryActions)
                stats[a] = new ParityStats();

            var ctxVariants = new WorldExecutorContext[]
            {
                new WorldExecutorContext // Aligned single-tick bridge world
                {
                    Label = "AlignedBridge", IsTransientRace = false,
                    Suppression = 0f, BoardHasActiveThreat = true, BoardPatientSameSquad = true, PatientDist = 7.5f,
                    PatientInCover = false, MedicWithin6m = false, ExclusiveClaimFree = true, BoardHasSurrenderedComrade = true,
                    HasMedicHealDestination = true, HasValidInjuredPatientSoldier = true, FlankCoverClaimFree = true, FragTargetIsSoldier = true, FragCooldownReady = true,
                    SmokeClaimAndAnimReady = true, FallbackCooldownReady = true, LeaderFarBehindObjective = false,
                    HasDestinationAssigned = true, InHandsOrBodyAnim = false, RpcBudgetReady = true
                },
                new WorldExecutorContext // Mid-tick transient race (claim stolen, anim/RPC contention)
                {
                    Label = "TransientRace", IsTransientRace = true,
                    Suppression = 0f, BoardHasActiveThreat = true, BoardPatientSameSquad = false, PatientDist = 7.5f,
                    PatientInCover = false, MedicWithin6m = false, ExclusiveClaimFree = false, BoardHasSurrenderedComrade = false,
                    HasMedicHealDestination = false, HasValidInjuredPatientSoldier = false, FlankCoverClaimFree = false, FragTargetIsSoldier = true, FragCooldownReady = true,
                    SmokeClaimAndAnimReady = false, FallbackCooldownReady = false, LeaderFarBehindObjective = false,
                    HasDestinationAssigned = false, InHandsOrBodyAnim = true, RpcBudgetReady = false
                }
            };

            float now = 100f;
            int totalChecks = 0;

            for (int r = 0; r < 7; r++)
            {
                bool isLdr = r == 1, isRad = r == 2 || r == 3, isMed = r == 4, isMg = r == 5, isAmmo = r == 6;
                var w = ComputeProfileWeights(in profiles[r % profiles.Length], isLdr, isRad, isMed);
                foreach (bool inCov in new[] { false, true })
                foreach (float rawH in new[] { 0.05f, 0.14f, 0.36f, 0.72f })
                foreach (float dist in new[] { 7f, 26f, 55f, 999f })
                foreach (bool bleed in new[] { false, true })
                foreach (bool empty in new[] { false, true })
                foreach (bool oppAll in new[] { false, true })
                {
                    bool hasVis = dist < 900f;
                    float supEstimate = rawH * 68.0f;
                    // Mirror real EmergentRuntimeBridge pre-gates!
                    bool canMedSafeDrag = isMed && oppAll && !bleed && rawH < 0.28f && supEstimate < 12f && !(hasVis && dist <= 24f);
                    bool canBuddyDrag = !isMed && !isLdr && !isRad && oppAll && !bleed && supEstimate < 14f && !(hasVis && (inCov ? dist <= 14f : dist <= 24f));

                    var inp = new EmergentEngine.EmergentInput
                    {
                        Now = now,
                        IsLeader = isLdr,
                        IsRadioman = isRad,
                        RadioLinkDrifting = r == 3,
                        IsMedic = isMed,
                        IsMachineGunnerOrBipod = isMg,
                        IsAmmoCarrier = isAmmo,
                        HasRadioLink = true,
                        RawHarmNow = rawH,
                        SmoothedHarmNow = rawH,
                        ProjectedHarm = rawH * 0.35f,
                        InCover = inCov,
                        CoverAvailable = true,
                        IsBleeding = bleed,
                        HasBandage = !empty,
                        HasSmokeGrenade = oppAll && !canMedSafeDrag,
                        HasFragGrenade = oppAll,
                        AmmoEmpty = empty,
                        HasVisibleEnemy = hasVis,
                        PointBlankThreat = hasVis && dist <= 7f && !empty,
                        VisibleEnemyDist = dist,
                        CanHarmTarget = hasVis ? 0.85f : 0.45f,
                        HasLastKnownThreat = oppAll || hasVis,
                        LastKnownThreatDist = hasVis ? dist : 18f,
                        TargetBehindCover = oppAll,
                        FlankCoverAvailable = oppAll,
                        AllyPinningThreat = oppAll,
                        NearbyAmmoSource = oppAll,
                        AllyNeedsAmmoDropNearby = isAmmo && oppAll,
                        HasInjuredAlly = isMed && oppAll,
                        InjuredAllyInOpen = isMed && oppAll && rawH >= 0.14f,
                        InjuredAllyHarm = isMed && oppAll && rawH >= 0.14f ? 0.50f : rawH * 0.45f,
                        HasDownedAllyToDrag = canMedSafeDrag || canBuddyDrag,
                        DownedAllySameSquad = true,
                        HasSurrenderedPrisoner = !isLdr && !isRad && oppAll,
                        PrisonerSameSquad = oppAll,
                        HasAidOverwatchNeed = oppAll,
                        HasObjectiveTask = true,
                        InsideObjectiveZone = dist > 900f && !oppAll,
                        ObjectiveDist = 70f,
                        ObjectiveUrgency = 0.55f,
                        SquadDirective = oppAll ? EmergentEngine.Tier2SquadDirective.HammerFlank : EmergentEngine.Tier2SquadDirective.AdvanceBound
                    };

                    EmergentEngine.EmergentDecision dec;
                    if (!EmergentEngine.EvaluateFastReflex(in inp, in w, now, out dec))
                        dec = EmergentEngine.EvaluateDeliberative(in inp, in w, default, now);

                    if (!stats.TryGetValue(dec.Step0, out ParityStats ps)) continue;

                    foreach (var baseCtx in ctxVariants)
                    {
                        var ctx = baseCtx;
                        ctx.Suppression = rawH * 68.0f;

                        totalChecks++;
                        ps.PlannerHits++;
                        if (EvaluateExecutorContract(dec.Step0, in inp, in ctx, out string rej))
                        {
                            ps.Accept++;
                        }
                        else
                        {
                            if (ctx.IsTransientRace) ps.RaceReject++;
                            else ps.AlignedReject++;
                            ps.RejectReasons[rej] = ps.RejectReasons.TryGetValue(rej, out int rc) ? rc + 1 : 1;
                            if (string.IsNullOrEmpty(ps.MinimalCounterexample))
                                ps.MinimalCounterexample = $"{dec.Reason} | ctx={ctx.Label} role={r} cov={inCov} harm={rawH:F2} dist={dist:F0} bleed={bleed} empty={empty} sup={ctx.Suppression:F0} => {rej}";
                        }
                    }
                }
            }

            Console.WriteLine($"  Checked {totalChecks:N0} (PlannerChoice x WorldContext) pairs:");
            Console.WriteLine("  Action               |   Hits | Accept | AlignedRej | RaceRej | TopRejectReason");
            Console.WriteLine("  ---------------------+--------+--------+------------+---------+---------------------------------------------");
            foreach (var kv in stats.OrderByDescending(k => k.Value.AlignedReject).ThenByDescending(k => k.Value.RaceReject))
            {
                var st = kv.Value;
                if (st.PlannerHits == 0) continue;
                string topRej = st.RejectReasons.Count > 0
                    ? st.RejectReasons.OrderByDescending(x => x.Value).First().Key
                    : "none";
                Console.WriteLine($"  {kv.Key,-20} | {st.PlannerHits,6} | {st.Accept,6} | {st.AlignedReject,10} | {st.RaceReject,7} | {topRej}");
                checksum = MixFnv(checksum, (ulong)(st.PlannerHits * 7 + st.AlignedReject * 97 + st.RaceReject * 13));

                if (st.AlignedReject > 0)
                {
                    AddFinding(AuditSeverity.HIGH, "Pass3", "CONTRACT_GAP_" + kv.Key.ToString().ToUpperInvariant(),
                        $"{kv.Key}: {st.AlignedReject} aligned bridge contract rejection(s) (top: {topRej}; e.g. {st.MinimalCounterexample}).");
                }
                else if (st.RaceReject > 0 && !writesCommitBack)
                {
                    AddFinding(AuditSeverity.HIGH, "Pass3", "UNHANDLED_RACE_" + kv.Key.ToString().ToUpperInvariant(),
                        $"{kv.Key}: {st.RaceReject} transient race rejection(s) without CommitUntil writeback ({topRej}).");
                }
            }

            return checksum;
        }

        // =====================================================================
        // PASS 4: Static C# Source Audit (Live Code Seam Verification)
        // =====================================================================
        private static ulong RunPass4StaticSourceAudit(string workspace, ulong checksum, out bool writesBackCommit)
        {
            Console.WriteLine();
            Console.WriteLine("[Pass 4/5] Static C# Source Audit (Bridge / Executor / CommitUntil / Field Wiring)");

            string bridgePath = Path.Combine(workspace, @"src\EasyRed2AI\Modules\Emergent\EmergentRuntimeBridge.cs");
            string corePath = Path.Combine(workspace, @"src\EasyRed2AI\Modules\Emergent\EmergentDecisionCore.cs");
            string goapExecPath = Path.Combine(workspace, @"src\EasyRed2AI\Modules\Goap\SoldierGoapExecution.cs");
            string cqbPath = Path.Combine(workspace, @"src\EasyRed2AI\Modules\Infantry\CloseCombatAndSupport.cs");
            string teamPath = Path.Combine(workspace, @"src\EasyRed2AI\Modules\Goap\TeamNetAndSquadCommand.cs");

            string bridgeSrc = File.ReadAllText(bridgePath);
            string coreSrc = File.ReadAllText(corePath);
            string goapExecSrc = File.ReadAllText(goapExecPath);
            string cqbSrc = File.ReadAllText(cqbPath);
            string teamSrc = File.ReadAllText(teamPath);

            // 4A. Parse all EmergentRun* / EmergentTry* wrappers in EmergentRuntimeBridge.cs
            var wrapperMap = new Dictionary<string, (string TargetMethod, bool ReturnsVoid, int Line)>(StringComparer.Ordinal);
            string[] bridgeLines = bridgeSrc.Split('\n');
            for (int i = 0; i < bridgeLines.Length; i++)
            {
                Match mw = Regex.Match(bridgeLines[i], @"\binternal\s+static\s+(void|bool)\s+(Emergent(?:Run|Try)\w+)\s*\(");
                if (mw.Success)
                {
                    bool retVoid = mw.Groups[1].Value == "void";
                    string wName = mw.Groups[2].Value;
                    string body = bridgeLines[i] + (i + 1 < bridgeLines.Length ? " " + bridgeLines[i + 1] : "");
                    Match mt = Regex.Match(body, @"=>\s*(\w+)\s*\(");
                    string target = mt.Success ? mt.Groups[1].Value : "inline";
                    wrapperMap[wName] = (target, retVoid, i + 1);
                }
            }

            // Determine actual return types of underlying Try*/Execute* methods
            var underlyingBoolMethods = new HashSet<string>(StringComparer.Ordinal);
            foreach (string src in new[] { goapExecSrc, cqbSrc, teamSrc })
            {
                foreach (Match mb in Regex.Matches(src, @"(?:private|internal|public)\s+static\s+bool\s+(\w+)\s*\("))
                    underlyingBoolMethods.Add(mb.Groups[1].Value);
            }

            Console.WriteLine("  4A. Void Wrappers Discarding Bool Executor Returns:");
            int discardedBools = 0;
            foreach (var kv in wrapperMap.OrderBy(k => k.Value.Line))
            {
                if (kv.Value.ReturnsVoid && underlyingBoolMethods.Contains(kv.Value.TargetMethod))
                {
                    discardedBools++;
                    checksum = MixFnv(checksum, (ulong)kv.Value.Line);
                    Console.WriteLine($"    [FOUND] {kv.Key} (EmergentRuntimeBridge.cs:{kv.Value.Line}) -> void discards bool from {kv.Value.TargetMethod}");
                    AddFinding(AuditSeverity.CRITICAL, "Pass4", "DISCARDED_EXECUTOR_BOOL",
                        $"{kv.Key} (EmergentRuntimeBridge.cs:{kv.Value.Line}) returns void and discards bool from {kv.Value.TargetMethod}.");
                }
            }
            if (discardedBools == 0) Console.WriteLine("    None found.");

            // 4B. Check CommitUntil write-back vs plan.PlanExpireTime = 0f
            Console.WriteLine("  4B. PlanExpireTime vs CurrentDecision.CommitUntil Write-Back Check:");
            int execStart = FindLine(bridgeLines, @"(?:public|private)\s+static\s+void\s+ExecuteEmergentFootAction\(");
            int execEnd = FindLine(bridgeLines, @"(?:public|private)\s+static\s+bool\s+TryEvaluateEmergentVehicleCrew\(", execStart + 1);
            bool readsCommit = false;
            writesBackCommit = false;
            var guardedCasesWithoutElse = new List<string>();
            var seenGuardedCases = new HashSet<string>(StringComparer.Ordinal);

            string curCase = "";
            bool curCaseHasGuard = false;
            bool curCaseHasElse = false;
            int curCaseLine = 0;
            for (int i = execStart; i >= 0 && i < execEnd && i < bridgeLines.Length; i++)
            {
                string ln = bridgeLines[i];
                if (ln.Contains("plan.PlanExpireTime = dec.CommitUntil")) readsCommit = true;
                if (ln.Contains("h.Emergent.CurrentDecision.CommitUntil =")) writesBackCommit = true;

                Match mc = Regex.Match(ln, @"case\s+EmergentEngine\.EmergentActionId\.(\w+):");
                if (mc.Success)
                {
                    if (!string.IsNullOrEmpty(curCase) && curCaseHasGuard && !curCaseHasElse && seenGuardedCases.Add(curCase))
                        guardedCasesWithoutElse.Add($"{curCase} (L{curCaseLine})");
                    curCase = mc.Groups[1].Value;
                    curCaseHasGuard = false;
                    curCaseHasElse = false;
                    curCaseLine = i + 1;
                }
                else if (!string.IsNullOrEmpty(curCase))
                {
                    if (Regex.IsMatch(ln, @"(TryClaimExclusive|IsActivelyHealingTeammate|IsAmmoBoxCarrier|TryConsumeBotRpcBudget|SurrenderedComrade\s*!=\s*null|sq\.Leader\s*!=\s*null)") ||
                        (curCase == "AdvanceObjective" && Regex.IsMatch(ln, @"HasActiveObjectiveTask")))
                    {
                        curCaseHasGuard = true;
                    }
                    if (Regex.IsMatch(ln, @"^\s*else\b"))
                        curCaseHasElse = true;
                }
            }
            if (!string.IsNullOrEmpty(curCase) && curCaseHasGuard && !curCaseHasElse && seenGuardedCases.Add(curCase))
                guardedCasesWithoutElse.Add($"{curCase} (L{curCaseLine})");

            string[] goapExecLines = goapExecSrc.Split('\n');
            int planResetCount = 0;
            for (int i = 0; i < goapExecLines.Length; i++)
            {
                if (goapExecLines[i].Contains("plan.PlanExpireTime = 0f"))
                    planResetCount++;
            }

            Console.WriteLine($"    Reads CommitUntil->PlanExpireTime: {readsCommit} | Writes PlanExpireTime->CommitUntil: {writesBackCommit} | GOAP 'PlanExpireTime = 0f' sites: {planResetCount}");
            checksum = MixFnv(checksum, (ulong)(writesBackCommit ? 1 : 99));
            if (readsCommit && !writesBackCommit && planResetCount > 0)
            {
                AddFinding(AuditSeverity.CRITICAL, "Pass4", "COMMIT_WRITEBACK_MISSING",
                    $"ExecuteEmergentFootAction copies CurrentDecision.CommitUntil -> plan.PlanExpireTime but NEVER writes back. {planResetCount} 'plan.PlanExpireTime = 0f' resets inside SoldierGoapExecution.cs are silently lost while NextPlanTime uses its own 0.28-0.42s stagger.");
            }
            if (guardedCasesWithoutElse.Count > 0)
            {
                Console.WriteLine($"    Guarded switch cases lacking 'else' fallback: {string.Join(", ", guardedCasesWithoutElse)}");
                AddFinding(AuditSeverity.HIGH, "Pass4", "GUARDED_CASE_NO_FALLBACK",
                    $"ExecuteEmergentFootAction cases with top-level guard and no 'else' fallback: {string.Join(", ", guardedCasesWithoutElse)}.");
            }

            // 4C. Parse EmergentInput fields vs Bridge Assignments & Unused Local Variables
            var inputFields = ExtractStructFields(coreSrc, "public struct EmergentInput");
            var readInCore = new HashSet<string>(StringComparer.Ordinal);
            int evalStartIdx = coreSrc.IndexOf("public static bool EvaluateFastReflex", StringComparison.Ordinal);
            int evalEndIdx = coreSrc.IndexOf("internal static Tier2SquadDecision", Math.Max(0, evalStartIdx), StringComparison.Ordinal);
            string evalSlice = (evalStartIdx >= 0 && evalEndIdx > evalStartIdx)
                ? coreSrc.Substring(evalStartIdx, evalEndIdx - evalStartIdx)
                : coreSrc;
            foreach (Match mr in Regex.Matches(evalSlice, @"\binp\.(\w+)"))
                readInCore.Add(mr.Groups[1].Value);

            // Extract fields actually read inside the vehicle branch of EvaluateDeliberative / EvaluateFastReflex
            var readInVehCore = new HashSet<string>(StringComparer.Ordinal) { "IsVehicle", "HullBurningOrDoomed" };
            int vehStartIdx = coreSrc.IndexOf("if (inp.IsVehicle)\n", StringComparison.Ordinal);
            if (vehStartIdx < 0) vehStartIdx = coreSrc.IndexOf("if (inp.IsVehicle)\r\n", StringComparison.Ordinal);
            int infStartIdx = coreSrc.IndexOf("// ---- INFANTRY ----", Math.Max(0, vehStartIdx), StringComparison.Ordinal);
            if (vehStartIdx >= 0 && infStartIdx > vehStartIdx)
            {
                string vehSlice = coreSrc.Substring(vehStartIdx, infStartIdx - vehStartIdx);
                foreach (Match mr in Regex.Matches(vehSlice, @"\binp\.(\w+)"))
                    readInVehCore.Add(mr.Groups[1].Value);
            }

            var assignedInf = ExtractInitializerFields(bridgeSrc, "var inp = new EmergentEngine.EmergentInput");
            var assignedVeh = ExtractInitializerFields(bridgeSrc, "var inp = new EmergentEngine.EmergentInput", secondOccurrence: true);

            Console.WriteLine($"  4C. EmergentInput Field Coverage ({inputFields.Count} fields total, {readInCore.Count} read in Tier1, {readInVehCore.Count} in Vehicle):");
            var missingInf = inputFields.Where(f => readInCore.Contains(f) && !assignedInf.Contains(f)).ToList();
            var missingVeh = inputFields.Where(f => readInVehCore.Contains(f) && !assignedVeh.Contains(f)).ToList();
            Console.WriteLine($"    Read in Tier1 but unassigned in Infantry Bridge ({missingInf.Count}): {(missingInf.Count > 0 ? string.Join(", ", missingInf) : "none")}");
            Console.WriteLine($"    Read in Veh   but unassigned in Vehicle  Bridge ({missingVeh.Count}): {(missingVeh.Count > 0 ? string.Join(", ", missingVeh) : "none")}");

            if (missingVeh.Contains("NeedsTrackOrEngineRepair") || missingVeh.Contains("HullBurningOrDoomed") || missingVeh.Contains("CanHarmTarget"))
            {
                AddFinding(AuditSeverity.HIGH, "Pass4", "VEHICLE_CORE_UNWIRED",
                    $"TryEvaluateEmergentVehicleCrew omits core vehicle field(s): {string.Join(", ", missingVeh)}.");
            }
            foreach (string mf in missingInf)
            {
                // Vehicle-only fields are legitimately false for infantry
                if (mf == "IsHeavyArmor" || mf == "IsLightArmor" || mf == "HullBurningOrDoomed" || mf == "NeedsTrackOrEngineRepair")
                    continue;
                AddFinding(AuditSeverity.HIGH, "Pass4", "INF_UNWIRED_INPUT_" + mf, $"Infantry bridge never sets planner-read field EmergentInput.{mf}.");
            }

            // Specific bug check: pHarm unused while InjuredAllyHarm = projHarm
            if (Regex.IsMatch(bridgeSrc, @"float\s+pHarm\s*=\s*ring\.EvaluateSpotProjectedHarm\(") &&
                bridgeSrc.Contains("InjuredAllyHarm = projHarm,"))
            {
                Console.WriteLine("    [BUG] Bridge computes 'float pHarm = ring.EvaluateSpotProjectedHarm(injuredTargetPos,...)' only for InjuredInOpen, then passes 'InjuredAllyHarm = projHarm' (own spot harm!) instead of patient's harm.");
                AddFinding(AuditSeverity.MEDIUM, "Pass4", "INJURED_ALLY_HARM_SELF",
                    "EmergentRuntimeBridge passes InjuredAllyHarm = projHarm (medic's own spot) instead of patient's pHarm (EmergentRuntimeBridge.cs:860/1115).");
            }

            // Exact threshold drift checks inside ExecuteGoapFlushThreatWithFrag & TryExecuteBuddyCasualtyDrag
            if (coreSrc.Contains("float minFragDist = safeInCover ? 6.5f : 8.5f;") &&
                !goapExecSrc.Contains("soldier.IsOnCover() ? 6.5f : 8.5f"))
            {
                Console.WriteLine("    [DRIFT] FragFlush minFragDist: Planner safeInCover=6.5m vs ExecuteGoapFlushThreatWithFrag=8.5m hardcoded.");
                AddFinding(AuditSeverity.MEDIUM, "Pass4", "FRAG_MINDIST_DRIFT",
                    "EmergentDecisionCore allows covered FragFlush at 6.5m..8.5m, but ExecuteGoapFlushThreatWithFrag rejects <8.5m.");
            }
            if (coreSrc.Contains("(!safeInCover && inp.HasVisibleEnemy && inp.VisibleEnemyDist <= 24.0f)") &&
                Regex.IsMatch(goapExecSrc, @"TryExecuteBuddyCasualtyDrag[\s\S]{0,1200}ai\.visibleTarget\s*!=\s*null"))
            {
                Console.WriteLine("    [DRIFT] DragCasualty visibility: Planner allows enemy >24m vs TryExecuteBuddyCasualtyDrag rejects any ai.visibleTarget != null.");
                AddFinding(AuditSeverity.HIGH, "Pass4", "DRAG_VISIBILITY_DRIFT",
                    "Planner allows DragCasualty with enemies >24m, whereas TryExecuteBuddyCasualtyDrag rejects if ai.visibleTarget != null at ANY distance.");
            }

            checksum = MixFnv(checksum, (ulong)(missingInf.Count * 11 + missingVeh.Count * 23));
            return checksum;
        }

        private static int FindLine(string[] lines, string regexPattern, int start = 0)
        {
            for (int i = Math.Max(0, start); i < lines.Length; i++)
            {
                if (Regex.IsMatch(lines[i], regexPattern))
                    return i;
            }
            return -1;
        }

        private static List<string> ExtractStructFields(string src, string header)
        {
            var fields = new List<string>();
            int idx = src.IndexOf(header, StringComparison.Ordinal);
            if (idx < 0) return fields;
            int open = src.IndexOf('{', idx);
            int close = src.IndexOf('}', open + 1);
            if (open < 0 || close <= open) return fields;
            string block = src.Substring(open + 1, close - open - 1);
            foreach (Match m in Regex.Matches(block, @"public\s+\w+\s+(\w+)\s*;"))
                fields.Add(m.Groups[1].Value);
            return fields;
        }

        private static HashSet<string> ExtractInitializerFields(string src, string marker, bool secondOccurrence = false)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            int idx = src.IndexOf(marker, StringComparison.Ordinal);
            if (idx >= 0 && secondOccurrence)
                idx = src.IndexOf(marker, idx + marker.Length, StringComparison.Ordinal);
            if (idx < 0) return set;
            int open = src.IndexOf('{', idx);
            int close = src.IndexOf("};", open + 1, StringComparison.Ordinal);
            if (open < 0 || close <= open) return set;
            string block = src.Substring(open + 1, close - open - 1);
            foreach (Match m in Regex.Matches(block, @"(?m)^\s*(\w+)\s*="))
                set.Add(m.Groups[1].Value);
            return set;
        }

        // =====================================================================
        // PASS 5: Read-Only Live Trace Forensic Replay
        // =====================================================================
        private struct BotReplayState
        {
            public string LastAct;
            public float LastMoveT;
            public float LastPosX, LastPosY, LastPosZ;
            public float LastPlanT;
            public string PrevAct1;
            public string PrevAct2;
            public float PrevAct2T;
        }

        private static void RunPass5LiveTraceForensics(string tracePath)
        {
            Console.WriteLine();
            Console.WriteLine("[Pass 5/5] Read-Only Live BotActionTrace Forensics");
            if (string.IsNullOrWhiteSpace(tracePath) || !File.Exists(tracePath))
            {
                Console.WriteLine("  [SKIPPED] No BotActionTrace.jsonl path provided / found (-NoTrace or file absent).");
                return;
            }

            long byteLen = new FileInfo(tracePath).Length;
            // Two-pass streaming scan: first locate last match start (last t reset)
            double prevT = -1.0;
            int totalLines = 0;
            int lastResetLine = 0;
            int matchCount = 1;
            var rxTime = new Regex("\"t\":([0-9]+(?:\\.[0-9]+)?),", RegexOptions.Compiled);
            var rxPlan = new Regex("\"id\":(\\d+),.*\"ev\":\"([^\"]+)\".*\"act0\":\"([^\"]*)\".*\"why\":\"([^\"]*)\"", RegexOptions.Compiled);
            var rxPos = new Regex("\"pos\":\\[([-0-9.]+),([-0-9.]+),([-0-9.]+)\\]", RegexOptions.Compiled);

            using (var fs = new FileStream(tracePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    Match mt = rxTime.Match(line);
                    if (mt.Success && double.TryParse(mt.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double t))
                    {
                        if (prevT >= 0.0 && t + 5.0 < prevT)
                        {
                            lastResetLine = totalLines;
                            matchCount++;
                        }
                        prevT = t;
                    }
                    totalLines++;
                }
            }

            var bots = new Dictionary<int, BotReplayState>(512);
            var zeroMoveStallsByAct = new Dictionary<string, int>(StringComparer.Ordinal);
            var anomsByActWhy = new Dictionary<string, int>(StringComparer.Ordinal);
            var pingPongPairs = new Dictionary<string, int>(StringComparer.Ordinal);
            var vaultRejectReasons = new Dictionary<string, int>(StringComparer.Ordinal);
            int latestPlans = 0;
            int liveVaultCount = 0;
            int liveVaultRejectCount = 0;
            double minT = double.MaxValue, maxT = 0.0;

            using (var fs = new FileStream(tracePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
            {
                string line;
                int idx = 0;
                while ((line = sr.ReadLine()) != null)
                {
                    if (idx++ < lastResetLine) continue;

                    Match mt = rxTime.Match(line);
                    if (!mt.Success || !double.TryParse(mt.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double dt))
                        continue;
                    float t = (float)dt;
                    if (dt < minT) minT = dt;
                    if (dt > maxT) maxT = dt;

                    Match mp = rxPlan.Match(line);
                    if (!mp.Success) continue;
                    int id = int.Parse(mp.Groups[1].Value, CultureInfo.InvariantCulture);
                    string ev = mp.Groups[2].Value;
                    string act0 = mp.Groups[3].Value;
                    string why = mp.Groups[4].Value;

                    if (ev == "anomaly_alert")
                    {
                        string k = $"{act0} | {why}";
                        anomsByActWhy[k] = anomsByActWhy.TryGetValue(k, out int ac) ? ac + 1 : 1;
                        continue;
                    }
                    if (ev == "vault")
                    {
                        liveVaultCount++;
                        continue;
                    }
                    if (ev == "vault_rejected")
                    {
                        liveVaultRejectCount++;
                        string vk = string.IsNullOrEmpty(why) ? "unknown" : why;
                        vaultRejectReasons[vk] = vaultRejectReasons.TryGetValue(vk, out int vc) ? vc + 1 : 1;
                        continue;
                    }

                    if (ev != "emergent_plan") continue;
                    latestPlans++;

                    Match mPos = rxPos.Match(line);
                    float px = 0f, py = 0f, pz = 0f;
                    bool hasPos = false;
                    if (mPos.Success)
                    {
                        hasPos = float.TryParse(mPos.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out px) &&
                                 float.TryParse(mPos.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out py) &&
                                 float.TryParse(mPos.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out pz);
                    }

                    if (!bots.TryGetValue(id, out var st))
                    {
                        bots[id] = new BotReplayState
                        {
                            LastAct = act0,
                            LastMoveT = t,
                            LastPosX = px,
                            LastPosY = py,
                            LastPosZ = pz,
                            LastPlanT = t,
                            PrevAct1 = act0,
                            PrevAct2T = t
                        };
                        continue;
                    }

                    // A -> B -> A oscillation within 4.0s
                    if (!string.IsNullOrEmpty(st.PrevAct2) && st.PrevAct2 == act0 && st.PrevAct1 != act0 && (t - st.PrevAct2T) <= 4.0f)
                    {
                        string pair = string.CompareOrdinal(act0, st.PrevAct1) < 0 ? $"{act0} <-> {st.PrevAct1}" : $"{st.PrevAct1} <-> {act0}";
                        pingPongPairs[pair] = pingPongPairs.TryGetValue(pair, out int pc) ? pc + 1 : 1;
                    }

                    // Repeat identical non-hold action with <0.75m movement over >=3.0s
                    if (hasPos && st.LastAct == act0)
                    {
                        float dx = px - st.LastPosX, dy = py - st.LastPosY, dz = pz - st.LastPosZ;
                        if (dx * dx + dy * dy + dz * dz >= 0.75f * 0.75f)
                        {
                            st.LastPosX = px; st.LastPosY = py; st.LastPosZ = pz;
                            st.LastMoveT = t;
                        }
                        else if ((t - st.LastMoveT) >= 3.0f &&
                                 act0 != "Engage" && act0 != "Suppress" && act0 != "SeekCover" && act0 != "Bandage")
                        {
                            zeroMoveStallsByAct[act0] = zeroMoveStallsByAct.TryGetValue(act0, out int sc) ? sc + 1 : 1;
                            st.LastMoveT = t; // rate-limit per 3s bucket
                        }
                    }
                    else
                    {
                        st.LastAct = act0;
                        st.LastMoveT = t;
                        if (hasPos) { st.LastPosX = px; st.LastPosY = py; st.LastPosZ = pz; }
                    }

                    st.PrevAct2 = st.PrevAct1;
                    st.PrevAct2T = st.LastPlanT;
                    st.PrevAct1 = act0;
                    st.LastPlanT = t;
                    bots[id] = st;
                }
            }

            Console.WriteLine($"  Trace: {tracePath} ({byteLen:N0} bytes, {totalLines:N0} lines, matches={matchCount})");
            Console.WriteLine($"  Latest Match (#{matchCount - 1}): lines={totalLines - lastResetLine:N0}, plans={latestPlans:N0}, window={minT:F1}s..{maxT:F1}s");

            Console.WriteLine("  Zero-Move Mobile Action Stalls (>=3.0s, <0.75m):");
            if (zeroMoveStallsByAct.Count == 0) Console.WriteLine("    None detected.");
            foreach (var kv in zeroMoveStallsByAct.OrderByDescending(x => x.Value).Take(8))
            {
                Console.WriteLine($"    {kv.Key,-18}: {kv.Value}");
                if (_verbose || kv.Value >= 5)
                    AddFinding(AuditSeverity.INFO, "Pass5", "TRACE_ZERO_MOVE_" + kv.Key.ToUpperInvariant(),
                        $"Live trace latest match had {kv.Value} >=3.0s zero-move repeats for '{kv.Key}'.");
            }

            Console.WriteLine("  Top 2-Cycle Ping-Pong Pairs (<=4.0s A->B->A):");
            if (pingPongPairs.Count == 0) Console.WriteLine("    None detected.");
            foreach (var kv in pingPongPairs.OrderByDescending(x => x.Value).Take(6))
                Console.WriteLine($"    {kv.Key,-28}: {kv.Value}");

            Console.WriteLine("  Anomaly Alerts in Latest Match:");
            if (anomsByActWhy.Count == 0) Console.WriteLine("    None detected.");
            foreach (var kv in anomsByActWhy.OrderByDescending(x => x.Value).Take(8))
                Console.WriteLine($"    {kv.Key,-48}: {kv.Value}");

            if (liveVaultCount > 0 || liveVaultRejectCount > 0)
            {
                Console.WriteLine($"  Vault Telemetry (trace): Vault={liveVaultCount}, Rejected={liveVaultRejectCount}");
                foreach (var kv in vaultRejectReasons.OrderByDescending(x => x.Value).Take(5))
                    Console.WriteLine($"    {kv.Key,-28}: {kv.Value}");
            }

            string logPath = Path.Combine(Path.GetDirectoryName(tracePath) ?? "", "LogOutput.log");
            if (!string.IsNullOrEmpty(logPath) && File.Exists(logPath))
            {
                string lastHb = null;
                string lastHitch = null;
                using (var lfs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var lsr = new StreamReader(lfs, Encoding.UTF8))
                {
                    string ll;
                    while ((ll = lsr.ReadLine()) != null)
                    {
                        if (ll.IndexOf("[Diag:Heartbeat]", StringComparison.Ordinal) >= 0) lastHb = ll;
                        else if (ll.IndexOf("[HitchSummary 15s]", StringComparison.Ordinal) >= 0) lastHitch = ll;
                    }
                }

                if (!string.IsNullOrEmpty(lastHb))
                {
                    Match mw = Regex.Match(lastHb, @"WallBlk:(\d+)");
                    Match mp2 = Regex.Match(lastHb, @"Peeks:(\d+)");
                    if (mw.Success && mp2.Success &&
                        int.TryParse(mw.Groups[1].Value, out int wBlk) &&
                        int.TryParse(mp2.Groups[1].Value, out int pks) &&
                        wBlk >= 400 && (pks == 0 || (double)wBlk / pks > 12.0))
                    {
                        double r = pks > 0 ? (double)wBlk / pks : wBlk;
                        AddFinding(AuditSeverity.MEDIUM, "Pass5", "LIVE_WALLBLK_PEEK_RATIO",
                            $"Live LogOutput.log WallBlk/Peeks ratio={r:F1}x ({wBlk}/{pks} > 12.0x).");
                    }

                    Match mv = Regex.Match(lastHb, @"Vault:(\d+)\s*\(Rej:(\d+)\)");
                    if (mv.Success &&
                        int.TryParse(mv.Groups[1].Value, out int vOk) &&
                        int.TryParse(mv.Groups[2].Value, out int vRej) &&
                        vRej >= 300 && (vOk == 0 || (double)vRej / vOk > 12.0))
                    {
                        double vr = vOk > 0 ? (double)vRej / vOk : vRej;
                        AddFinding(AuditSeverity.INFO, "Pass5", "LIVE_VAULT_REJ_RATIO",
                            $"Live LogOutput.log VaultRej/Vault ratio={vr:F1}x ({vRej}/{vOk} > 12.0x).");
                    }
                }

                if (!string.IsNullOrEmpty(lastHitch))
                {
                    Match mc = Regex.Match(lastHitch, @"ModCPU:([0-9.]+)ms/f");
                    if (mc.Success && double.TryParse(mc.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double cpu) && cpu > 1.50)
                    {
                        AddFinding(AuditSeverity.MEDIUM, "Pass5", "LIVE_MOD_CPU_BUDGET",
                            $"Live LogOutput.log ModCPU={cpu:F2}ms/f exceeds 1.50ms/f target.");
                    }

                    Match me = Regex.Match(lastHitch, @"PatchErrs:(\d+)");
                    if (me.Success && int.TryParse(me.Groups[1].Value, out int pe) && pe > 0)
                    {
                        AddFinding(AuditSeverity.HIGH, "Pass5", "LIVE_PATCH_ERRORS",
                            $"Live LogOutput.log recorded {pe} caught PatchErrs.");
                    }
                }
            }
        }
    }
}

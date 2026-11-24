using System;
using System.Collections.Generic;
using UnityEngine;

namespace EasyRed2HumanAI
{
    /// <summary>
    /// Live IL2CPP runtime bridge connecting Easy Red 2 entities, combat events, and native executors
    /// to <see cref="EmergentEngine"/> across all 3 tiers (Individual, Squad/Crew, Team Command Net).
    /// </summary>
    public static class EmergentRuntimeBridge
    {
        public struct SquadHeartbeatState
        {
            public float LastHeartbeatTime;
            public Vector3 LastKnownPos;
            public string Faction;
            public bool HadRadioLink;
            public bool MiaStamped;
            public bool BalkActive;
            public EmergentEngine.Tier2SquadDirective Directive;
            public EmergentEngine.Tier2BalkReason BalkReason;
            public float SectorDesperationBias;
            public EmergentEngine.Tier3NetLever LastT3Lever;
        }

        private static float ComputeWeaponEffectiveRangeWeight(
            Soldier soldier,
            AiCore.SoldierFastHandles h,
            GenericGun gun,
            bool isGunner,
            VeteranAndTacticsPatches.SoldierPersonality personality,
            float dist,
            float now)
        {
            if (gun == null || dist <= 1.0f)
                return 1.0f;

            float dmgRange = Mathf.Max(25f, gun.DamageRange());
            AmmoItem ammo = gun.GetAmmoItem() ?? gun.usedAmmoItem;
            BulletData bd = ammo != null ? ammo.GetBulletData() : null;

            float optimal;
            float taper;
            if (gun.isFlamethrower || (bd != null && bd.IsFire()))
            {
                optimal = 22f;
                taper = 42f;
            }
            else if (bd != null && bd.IsShotgun())
            {
                optimal = 24f;
                taper = 65f;
            }
            else if (gun.IsPistol())
            {
                optimal = 22f;
                taper = 65f;
            }
            else if (gun.IsAtWeapon())
            {
                if (dmgRange >= 150f && !gun.IsGrenadeLauncher())
                {
                    optimal = 110f;
                    taper = 210f;
                }
                else
                {
                    optimal = 50f;
                    taper = 105f;
                }
            }
            else if (h != null && AiCore.GetCachedOpticZoom(h, now) >= 2.2f)
            {
                optimal = 260f;
                taper = 420f;
            }
            else if (isGunner)
            {
                optimal = 210f;
                taper = 360f;
            }
            else if (dmgRange <= 135f ||
                     (personality >= VeteranAndTacticsPatches.SoldierPersonality.Assaulter_Shock &&
                      personality <= VeteranAndTacticsPatches.SoldierPersonality.Assaulter_Skirmisher &&
                      dmgRange <= 185f))
            {
                optimal = 55f;
                taper = 130f;
            }
            else
            {
                optimal = Mathf.Clamp(dmgRange * 0.55f, 115f, 185f);
                taper = Mathf.Max(optimal + 90f, dmgRange);
            }

            if (dist <= optimal)
                return 1.0f;
            if (dist <= taper)
                return Mathf.Lerp(1.0f, 0.35f, (dist - optimal) / Mathf.Max(1f, taper - optimal));

            return Mathf.Lerp(0.35f, 0.18f, Mathf.Clamp01((dist - taper) / Mathf.Max(30f, taper * 0.65f)));
        }

        private static float ComputeAtWeaponArmorWeight(
            Soldier soldier,
            GenericGun heldGun,
            float hullArmorMm,
            float dist)
        {
            GenericGun atGun = (heldGun != null && heldGun.IsAtWeapon()) ? heldGun : null;
            if (atGun == null && soldier != null)
            {
                for (int slot = 0; slot <= 2; slot++)
                {
                    GenericGun g = soldier.GetHeldGun(slot);
                    if (g != null && g.IsAtWeapon() && (g.GetCurrentAmmoCount() > 0 || !soldier.IsOutOfAmmosFor(g)))
                    {
                        atGun = g;
                        break;
                    }
                }
            }
            if (atGun == null)
                return 0.65f;

            float dmgRange = Mathf.Max(25f, atGun.DamageRange());
            float optimal = (dmgRange >= 150f && !atGun.IsGrenadeLauncher()) ? 110f : 50f;
            float taper = (dmgRange >= 150f && !atGun.IsGrenadeLauncher()) ? 210f : 105f;

            float rangeW = dist <= optimal
                ? 1.0f
                : (dist <= taper
                    ? Mathf.Lerp(1.0f, 0.35f, (dist - optimal) / Mathf.Max(1f, taper - optimal))
                    : Mathf.Lerp(0.35f, 0.18f, Mathf.Clamp01((dist - taper) / Mathf.Max(30f, taper * 0.65f))));

            AmmoItem ammo = atGun.GetAmmoItem() ?? atGun.usedAmmoItem;
            BulletData bd = ammo != null ? ammo.GetBulletData() : null;
            if (bd != null && hullArmorMm > 15f)
            {
                float pen = bd.CalculateMmPenetrationWithFallof(dist);
                if (pen > 0.1f && pen < hullArmorMm * 0.80f)
                    rangeW *= 0.45f;
            }

            return rangeW;
        }

        private static readonly EmergentEngine.DangerStampRing _side0Stamps = new EmergentEngine.DangerStampRing();
        private static readonly EmergentEngine.DangerStampRing _side1Stamps = new EmergentEngine.DangerStampRing();
        private static readonly EmergentEngine.IntentBoard _side0Intents = new EmergentEngine.IntentBoard();
        private static readonly EmergentEngine.IntentBoard _side1Intents = new EmergentEngine.IntentBoard();
        private static readonly Dictionary<IntPtr, SquadHeartbeatState> _squadHeartbeats = new Dictionary<IntPtr, SquadHeartbeatState>(64);
        private static readonly HashSet<IntPtr> _seenAliveSquadPtrs = new HashSet<IntPtr>(64);
        private static readonly List<IntPtr> _miaCheckKeyBuffer = new List<IntPtr>(64);

        // Telemetry Counters (Zero-Alloc)
        private static readonly int[] _actionCounts = new int[24];
        private static readonly int[] _reflexCounts = new int[8];
        private static int _totalT1Decisions;
        public static int TotalT1DecisionsCount => _totalT1Decisions;
        private static int _prereqChainsCount;
        private static int _unsafeChainsRejectedCount;
        private static int _troopInteractionsCount;
        private static int _thrashWatchdogCount;
        private static int _tier2BalksCount;
        private static int _tier3LeversCount;
        private static int _miaTimeoutsCount;
        private static int _dangerStampsEmittedCount;
        private static int _enemyReliefStampsCount;
        private static int _airGroundRunsCount;
        private static int _airInterceptsCount;
        private static float _nextPeriodicTelemetryTime;
        private static float _nextNetTickTime;
        private static EmergentEngine.Tier3NetLever _lastSide0Lever;
        private static EmergentEngine.Tier3NetLever _lastSide1Lever;
        private static float _side0Desperation;
        private static float _side1Desperation;
        private static float _nextMatchPulseTime;
        private static int _lastPulseS0Act;
        private static int _lastPulseS1Act;
        private static float _lastPulseS0Tkt = 1f;
        private static float _lastPulseS1Tkt = 1f;
        private static int _lastPulseS0Balk;
        private static int _lastPulseS1Balk;
        private static int _lastPulseS0Mia;
        private static int _lastPulseS1Mia;
        private static float _lastPulseS0Heat;
        private static float _lastPulseS1Heat;
        private static readonly List<Squad> _reusableConnectedInfSquads = new List<Squad>(32);
        private static readonly List<Squad> _reusableActiveVehAnvils = new List<Squad>(16);

        public static void ResetOnSceneRestart()
        {
            _side0Stamps.Clear();
            _side1Stamps.Clear();
            _side0Intents.Clear();
            _side1Intents.Clear();
            _squadHeartbeats.Clear();
            _seenAliveSquadPtrs.Clear();
            _miaCheckKeyBuffer.Clear();
            _reusableConnectedInfSquads.Clear();
            _reusableActiveVehAnvils.Clear();
            Array.Clear(_actionCounts, 0, _actionCounts.Length);
            Array.Clear(_reflexCounts, 0, _reflexCounts.Length);
            _totalT1Decisions = 0;
            _prereqChainsCount = 0;
            _unsafeChainsRejectedCount = 0;
            _troopInteractionsCount = 0;
            _thrashWatchdogCount = 0;
            _tier2BalksCount = 0;
            _tier3LeversCount = 0;
            _miaTimeoutsCount = 0;
            _dangerStampsEmittedCount = 0;
            _enemyReliefStampsCount = 0;
            _airGroundRunsCount = 0;
            _airInterceptsCount = 0;
            _nextPeriodicTelemetryTime = 0f;
            _nextNetTickTime = 0f;
            _lastSide0Lever = EmergentEngine.Tier3NetLever.None;
            _lastSide1Lever = EmergentEngine.Tier3NetLever.None;
            _side0Desperation = 0f;
            _side1Desperation = 0f;
            _nextMatchPulseTime = 0f;
            _lastPulseS0Act = 0;
            _lastPulseS1Act = 0;
            _lastPulseS0Tkt = 1f;
            _lastPulseS1Tkt = 1f;
            _lastPulseS0Balk = 0;
            _lastPulseS1Balk = 0;
            _lastPulseS0Mia = 0;
            _lastPulseS1Mia = 0;
            _lastPulseS0Heat = 0f;
            _lastPulseS1Heat = 0f;
        }

        public static void RecordLastHitContext(Soldier victim, byte hitKind, float hitDist, float now)
        {
            if (victim == null || victim.Pointer == IntPtr.Zero)
                return;
            if (!BotActionTrace.IsEnabled &&
                (VisionConfig.EnableEmergentDecisionEngine == null || !VisionConfig.EnableEmergentDecisionEngine.Value))
            {
                return;
            }

            AiCore.SoldierFastHandles vh = AiCore.GetOrCreateSoldierHandles(victim);
            if (vh != null)
            {
                vh.Emergent.LastHitKind = hitKind;
                vh.Emergent.LastHitDist = hitDist;
                vh.Emergent.LastHitTime = now;
            }
        }

        internal static int ResolveSideIndex(string faction)
        {
            if (string.IsNullOrEmpty(faction))
                return 0;

            AiCore.BattleTheaterSnapshot t = AiCore.GetBattleTheaterSnapshot();
            if (!string.IsNullOrEmpty(t.InvadersFactionId) && AiCore.AreSameSideFactions(faction, t.InvadersFactionId))
                return 0;
            if (!string.IsNullOrEmpty(t.DefendersFactionId) && AiCore.AreSameSideFactions(faction, t.DefendersFactionId))
                return 1;
            return faction.EndsWith("_axis", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        }

        private static EmergentEngine.DangerStampRing GetRing(int sideIdx) => sideIdx == 0 ? _side0Stamps : _side1Stamps;
        private static EmergentEngine.IntentBoard GetIntents(int sideIdx) => sideIdx == 0 ? _side0Intents : _side1Intents;

        internal static bool TryGetNearestDangerStampDirection(
            string faction,
            Vector3 myPos,
            long squadId,
            bool isConnectedToRadioNet,
            float now,
            out Vector3 toConflict,
            out float distSq)
        {
            toConflict = Vector3.zero;
            distSq = float.MaxValue;
            if (string.IsNullOrEmpty(faction))
                return false;

            int sideIdx = ResolveSideIndex(faction);
            EmergentEngine.DangerStampRing ring = GetRing(sideIdx);
            float bestEffSq = float.MaxValue;
            bool found = false;

            for (int i = 0; i < EmergentEngine.DangerStampRing.Capacity; i++)
            {
                EmergentEngine.DangerStamp s = ring.GetSlotCopy(i);
                if (!s.Active || s.IsEnemyRelief || s.Severity < 0.25f)
                    continue;

                float age = now - s.Time;
                if (age < 0f || age > s.DecaySec)
                    continue;

                Vector3 toPos = s.Pos - myPos;
                toPos.y = 0f;
                float posDistSq = toPos.sqrMagnitude;
                if (posDistSq > 165f * 165f)
                    continue;

                bool canPerceive = posDistSq <= 45f * 45f ||
                                   (squadId != 0L && s.OriginSquadId == squadId) ||
                                   (isConnectedToRadioNet && s.ReportedToNet);
                if (!canPerceive)
                    continue;

                Vector3 targetPos = (s.SourcePos.sqrMagnitude > 0.01f && (s.SourcePos - myPos).sqrMagnitude > 4.0f)
                    ? s.SourcePos
                    : s.Pos;
                Vector3 vec = targetPos - myPos;
                vec.y = 0f;
                float dSq = vec.sqrMagnitude;
                if (dSq < 0.25f)
                    continue;

                float effSq = dSq * (1.0f + Mathf.Clamp01(age / Mathf.Max(1f, s.DecaySec)) * 0.50f);
                if (effSq < bestEffSq)
                {
                    bestEffSq = effSq;
                    toConflict = vec;
                    distSq = dSq;
                    found = true;
                }
            }

            return found;
        }

        private static long PtrToKey(IntPtr p) => p.ToInt64();

        public static bool GetSquadDirectiveForSpectator(
            Squad sq,
            out EmergentEngine.Tier2SquadDirective dir,
            out EmergentEngine.Tier2BalkReason balkReason,
            out float desperation)
        {
            if (sq != null && sq.Pointer != IntPtr.Zero && _squadHeartbeats.TryGetValue(sq.Pointer, out SquadHeartbeatState hb))
            {
                dir = hb.Directive;
                balkReason = hb.BalkReason;
                desperation = hb.SectorDesperationBias;
                return true;
            }
            dir = EmergentEngine.Tier2SquadDirective.AdvanceBound;
            balkReason = EmergentEngine.Tier2BalkReason.None;
            desperation = 0f;
            return false;
        }

        // =====================================================================
        // Event Feeders -> Asymmetric Projected Harm (H_proj) Danger Stamps
        // =====================================================================
        public static void NotifySoldierKilled(Soldier victim, Vector3 shooterPos, bool hasShooter)
        {
            if (VisionConfig.EnableEmergentDecisionEngine == null || !VisionConfig.EnableEmergentDecisionEngine.Value)
                return;
            if (victim == null || victim.Pointer == IntPtr.Zero || victim.transform == null)
                return;

            AiCore.SoldierFastHandles vh = AiCore.GetOrCreateSoldierHandles(victim);
            Vector3 vPos = victim.transform.position;
            float now = Time.time;

            if (vh != null)
            {
                if (vh.Emergent.DeathDangerStamped)
                    return;
                vh.Emergent.DeathDangerStamped = true;

                if (BotActionTrace.IsEnabled && !AiCore.IsAnyHumanPlayerSoldier(victim))
                {
                    float holdSec = (vh.Emergent.HasDecision && vh.Emergent.ActionStartedAt > 0f && now >= vh.Emergent.ActionStartedAt)
                        ? (now - vh.Emergent.ActionStartedAt)
                        : -1f;
                    float moved5s = -1f;
                    if (vh.Emergent.RecentAnchorTime > 0f)
                    {
                        float anchorAge = now - vh.Emergent.RecentAnchorTime;
                        moved5s = anchorAge >= 2.0f
                            ? Vector3.Distance(vPos, vh.Emergent.RecentAnchorPos)
                            : (vh.Emergent.LastMoved5s >= 0f ? vh.Emergent.LastMoved5s : Vector3.Distance(vPos, vh.Emergent.RecentAnchorPos));
                    }

                    byte kKind = 0;
                    float kDist = -1f;
                    if (vh.Emergent.LastHitKind != 0 && vh.Emergent.LastHitTime > 0f && (now - vh.Emergent.LastHitTime) <= 2.5f)
                    {
                        kKind = vh.Emergent.LastHitKind;
                        kDist = vh.Emergent.LastHitDist;
                    }
                    else if (hasShooter)
                    {
                        kKind = 1;
                        kDist = Vector3.Distance(vPos, shooterPos);
                    }
                    else if (victim.isBleeding)
                    {
                        kKind = 4;
                    }

                    BotActionTrace.RecordBotKilled(
                        victim,
                        vh,
                        VeteranAndTacticsPatches.GetPersonality(victim),
                        kKind,
                        kDist,
                        holdSec,
                        moved5s,
                        vh.Emergent.LastInputFlags,
                        now);
                }
            }

            string victimFac = AiCore.GetCachedFaction(victim);
            if (string.IsNullOrEmpty(victimFac))
                return;

            Squad vSq = victim.joinedSquad;
            VeteranAndTacticsPatches.NotifySquadLeaderLost(victim, now);
            long sqKey = (vSq != null && vSq.Pointer != IntPtr.Zero) ? PtrToKey(vSq.Pointer) : 0L;
            bool reported = vSq != null && VeteranAndTacticsPatches.EmergentHasRadioLink(vSq);

            int victimSide = ResolveSideIndex(victimFac);
            GetRing(victimSide).StampFriendlyHarm(
                vPos,
                hasShooter ? shooterPos : vPos,
                sqKey,
                reported,
                EmergentEngine.DangerWeaponClass.SmallArms,
                severity: 0.78f,
                radius: 12.0f,
                decaySec: 20.0f,
                now);
            _dangerStampsEmittedCount++;

            // Asymmetric Enemy Relief: opposite side detects reduced threat around victimPos
            int enemySide = 1 - victimSide;
            GetRing(enemySide).StampEnemyHarmRelief(
                vPos,
                reporterSquadId: 0L,
                reportedToNet: true,
                severity: 0.55f,
                now);
            _enemyReliefStampsCount++;
        }

        public static void NotifyFriendlyHit(Soldier victim, Vector3 shooterPos, bool hasShooter, bool isFatalOrDown)
        {
            if (VisionConfig.EnableEmergentDecisionEngine == null || !VisionConfig.EnableEmergentDecisionEngine.Value)
                return;
            if (victim == null || victim.Pointer == IntPtr.Zero || victim.transform == null)
                return;

            if (isFatalOrDown)
            {
                NotifySoldierKilled(victim, shooterPos, hasShooter);
                return;
            }

            string victimFac = AiCore.GetCachedFaction(victim);
            if (string.IsNullOrEmpty(victimFac))
                return;

            float now = Time.time;
            Squad vSq = victim.joinedSquad;
            long sqKey = (vSq != null && vSq.Pointer != IntPtr.Zero) ? PtrToKey(vSq.Pointer) : 0L;
            bool reported = vSq != null && VeteranAndTacticsPatches.EmergentHasRadioLink(vSq);
            int side = ResolveSideIndex(victimFac);
            GetRing(side).StampFriendlyHarm(
                victim.transform.position,
                hasShooter ? shooterPos : victim.transform.position,
                sqKey,
                reported,
                EmergentEngine.DangerWeaponClass.SmallArms,
                severity: 0.42f,
                radius: 10.5f,
                decaySec: 14.0f,
                now);
            _dangerStampsEmittedCount++;
        }

        public static void NotifyIncomingSuppression(Soldier victim, Vector3 shooterPos, bool hasShooter, float suppressionVal)
        {
            if (VisionConfig.EnableEmergentDecisionEngine == null || !VisionConfig.EnableEmergentDecisionEngine.Value)
                return;
            if (victim == null || victim.Pointer == IntPtr.Zero || victim.transform == null || suppressionVal < 12f)
                return;

            string fac = AiCore.GetCachedFaction(victim);
            if (string.IsNullOrEmpty(fac))
                return;

            Squad sq = victim.joinedSquad;
            long sqKey = (sq != null && sq.Pointer != IntPtr.Zero) ? PtrToKey(sq.Pointer) : 0L;
            bool reported = sq != null && VeteranAndTacticsPatches.EmergentHasRadioLink(sq);
            int side = ResolveSideIndex(fac);
            float sev = Mathf.Clamp01(suppressionVal / 60.0f);
            GetRing(side).StampIncomingFireLane(
                victim.transform.position,
                hasShooter ? shooterPos : victim.transform.position,
                sqKey,
                reported,
                EmergentEngine.DangerWeaponClass.SmallArms,
                sev,
                Time.time);
            _dangerStampsEmittedCount++;
        }

        public static void NotifyExplosion(Vector3 pos, float radius, string sourceFaction, bool isHeavyOrAt)
        {
            if (VisionConfig.EnableEmergentDecisionEngine == null || !VisionConfig.EnableEmergentDecisionEngine.Value)
                return;

            float now = Time.time;
            float effRad = Mathf.Clamp(radius * 2.2f, 8.0f, 34.0f);
            float sev = isHeavyOrAt ? 0.88f : 0.65f;

            // Stamp radial hazard onto opposite side (or both sides if neutral/unknown source)
            if (string.IsNullOrEmpty(sourceFaction))
            {
                _side0Stamps.StampExplosionHazard(pos, 0L, true, isHeavyOrAt, effRad, sev, now);
                _side1Stamps.StampExplosionHazard(pos, 0L, true, isHeavyOrAt, effRad, sev, now);
                _dangerStampsEmittedCount += 2;
            }
            else
            {
                int srcSide = ResolveSideIndex(sourceFaction);
                int targetSide = 1 - srcSide;
                GetRing(targetSide).StampExplosionHazard(pos, 0L, true, isHeavyOrAt, effRad, sev, now);
                _dangerStampsEmittedCount++;
            }
        }

        // =====================================================================
        // Tier 1 Infantry Tick (Two-Speed Reflex + Continuous Utility + 2-3 Ply Prereqs)
        // =====================================================================
        public static void ExecuteEmergentSoldierTick(
            SoldierAI ai,
            Soldier soldier,
            VeteranAndTacticsPatches.SoldierPersonality personality,
            float vet01,
            float suppression,
            float dist,
            bool isGunner,
            bool isOnBridge,
            bool inPointBlankMelee,
            bool isEvadingArmor,
            bool isStaffingEmplacement)
        {
            if (ai == null ||
                soldier == null ||
                soldier.Pointer == IntPtr.Zero ||
                AiCore.IsSoldierInVehicleOrTransition(soldier) ||
                soldier.HasSurrended() ||
                soldier.isInBodyAnimation)
            {
                return;
            }

            AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandles(soldier);
            if (h == null || h.Tr == null || !AiCore.IsLocallyOwnedBot(h))
                return;

            if (soldier.IsCarryingBody())
            {
                VeteranAndTacticsPatches.EmergentHandleCarryTick(ai, soldier, h.Ptr, Time.time);
                return;
            }

            if (isStaffingEmplacement)
                return;

            Squad sq = AiCore.GetCachedSquad(h, Time.time);
            // Single Tier 2 Human Authority Gate
            if (AiCore.IsSquadOwnedOrOccupiedByRemoteHuman(sq))
                return;

            bool canMoveAutonomously = AiCore.CanBotMoveAutonomously(soldier);
            if (!canMoveAutonomously && !AiCore.CanBotChangePoseAutonomously(soldier))
                return;

            float now = Time.time;
            Vector3 myPos = h.Tr.position;
            string fac = AiCore.GetCachedFaction(h, now);
            int sideIdx = ResolveSideIndex(fac);
            EmergentEngine.DangerStampRing ring = GetRing(sideIdx);
            EmergentEngine.IntentBoard intents = GetIntents(sideIdx);

            // Keep unstick recovery working identically on this branch
            VeteranAndTacticsPatches.SquadBlackboard board = VeteranAndTacticsPatches.GetOrUpdateSquadBlackboard(sq, soldier);
            if (h.MoveOverride.Kind == AiCore.BotMoveOverrideKind.VehicleEvade && now >= h.MoveOverride.ExpireTime)
                AiCore.ClearBotMoveOverride(h, AiCore.BotMoveOverrideKind.VehicleEvade);

            bool canUnstickMove = canMoveAutonomously ||
                                  (!soldier.IsSquadLeader() && !soldier.IsOnVehicle() && (soldier.Params == null || soldier.Params.canMove));
            if (canUnstickMove &&
                !inPointBlankMelee &&
                VeteranAndTacticsPatches.EmergentTryUnstick(ai, soldier, h.Ptr, ref board, myPos, suppression, isOnBridge, dist, now))
            {
                if (sq != null && sq.Pointer != IntPtr.Zero)
                    VeteranAndTacticsPatches._squadBlackboards[sq.Pointer] = board;
                return;
            }

            if (VisionConfig.EnableFriendlyVehicleRightOfWay.Value &&
                !AiCore.IsSoldierInVehicleOrTransition(soldier) &&
                AiCore.TryGetBlockingFriendlyVehicleDangerCorridor(myPos, fac, 0f, out AiCore.VehicleDangerCorridor vehCorr, out float vehFwd, out float vehLat, out _))
            {
                if (VeteranAndTacticsPatches.TryEvadeFriendlyVehicleCorridor(
                        soldier,
                        ai,
                        h,
                        vehCorr.Ref,
                        vehCorr.HullCenter,
                        vehCorr.TravelDirFlat,
                        vehCorr.RightFlat,
                        vehCorr.HalfWidth,
                        now,
                        ignoreHullCollision: vehFwd <= vehCorr.HalfLength + 3.5f && Mathf.Abs(vehLat) <= vehCorr.HalfWidth + 0.9f))
                {
                    if (sq != null && sq.Pointer != IntPtr.Zero)
                        VeteranAndTacticsPatches._squadBlackboards[sq.Pointer] = board;
                    return;
                }
            }

            if (VisionConfig.EnableSpecialMunitionsAndSappers.Value &&
                canMoveAutonomously &&
                !inPointBlankMelee &&
                !soldier.isBleeding &&
                !isEvadingArmor &&
                !VeteranAndTacticsPatches.IsSoldierBailedOutVehicleCrew(soldier, sq) &&
                VeteranAndTacticsPatches.EmergentTrySpecialMunitions(ai, soldier, h.Ptr, sq, ref board, myPos, suppression, dist, isOnBridge, now))
            {
                if (sq != null && sq.Pointer != IntPtr.Zero)
                    VeteranAndTacticsPatches._squadBlackboards[sq.Pointer] = board;
                return;
            }

            // Compute/Cache Weights once per soldier unless promotion, SectorDesperationBias, or SL morale shifts
            int vetTier = VeteranAndTacticsPatches.GetVeteranTier(soldier);
            float desp = (sq != null && sq.Pointer != IntPtr.Zero && _squadHeartbeats.TryGetValue(sq.Pointer, out SquadHeartbeatState hb))
                ? hb.SectorDesperationBias
                : 0f;
            var facProfile = VeteranAndTacticsPatches.GetFactionCommandProfile(fac);
            if (VeteranAndTacticsPatches.IsTrueGyokusaiOrBanzaiCondition(in facProfile, sq, in board))
                desp = Mathf.Max(desp, 0.48f);

            float leaderMoraleDelta = 0f;
            bool isSlSelf = soldier.IsSquadLeader();
            if (!isSlSelf &&
                board.LeaderBraveryAura != 0f &&
                sq != null &&
                sq.Leader != null &&
                sq.Leader.IsAlive &&
                !sq.Leader.IsInjured &&
                sq.Leader.transform != null)
            {
                float ldrDist = Vector3.Distance(myPos, sq.Leader.transform.position);
                float farFactor = board.IsConnectedToRadioNet ? 0.35f : 0f;
                float proxScale = ldrDist <= 18.0f
                    ? 1.0f
                    : (ldrDist >= 32.0f ? farFactor : Mathf.Lerp(1.0f, farFactor, (ldrDist - 18.0f) / 14.0f));
                leaderMoraleDelta += board.LeaderBraveryAura * proxScale;
            }
            if (now < board.LeaderShockUntil && board.LeaderShockPeak > 0.001f)
            {
                float shockFrac = Mathf.Clamp01((board.LeaderShockUntil - now) / 20.0f);
                float vetResist = Mathf.Lerp(1.0f, 0.55f, vet01);
                float selfMult = isSlSelf ? (VeteranAndTacticsPatches.HasSoldierRisenToOccasion(soldier) ? 0f : 0.50f) : 1.0f;
                leaderMoraleDelta -= board.LeaderShockPeak * shockFrac * vetResist * selfMult;
            }
            leaderMoraleDelta = Mathf.Clamp(leaderMoraleDelta, -0.18f, 0.16f);

            if (!h.Emergent.WeightsInitialized ||
                h.Emergent.CachedVetTier != vetTier ||
                Mathf.Abs(h.Emergent.CachedDesperation - desp) > 0.04f ||
                Mathf.Abs(h.Emergent.CachedLeaderMorale - leaderMoraleDelta) > 0.025f)
            {
                uint eHash = (uint)AiCore.GetDeterministicHash(h.Ptr, 73);
                h.Emergent.Weights = EmergentEngine.ComputeWeights(in facProfile, personality, vet01, eHash, desp, isSlSelf, soldier.IsRadioman(), leaderMoraleDelta);
                h.Emergent.WeightsInitialized = true;
                h.Emergent.CachedVetTier = vetTier;
                h.Emergent.CachedDesperation = desp;
                h.Emergent.CachedLeaderMorale = leaderMoraleDelta;
            }

            if (BotActionTrace.IsEnabled &&
                (!h.Emergent.ProfileTraced ||
                 h.Emergent.LastProfileTraceVet != vetTier ||
                 Mathf.Abs(h.Emergent.LastProfileTraceDesp - desp) > 0.08f))
            {
                h.Emergent.ProfileTraced = true;
                h.Emergent.LastProfileTraceVet = vetTier;
                h.Emergent.LastProfileTraceDesp = desp;
                BotActionTrace.RecordBotProfile(soldier, h, personality, vetTier, in h.Emergent.Weights, desp, now);
            }

            if (h.Emergent.RecentAnchorTime <= 0f || now < h.Emergent.RecentAnchorTime)
            {
                h.Emergent.RecentAnchorPos = myPos;
                h.Emergent.RecentAnchorTime = now;
                h.Emergent.LastMoved5s = -1f;
            }
            else if (now - h.Emergent.RecentAnchorTime >= 4.0f)
            {
                h.Emergent.LastMoved5s = Vector3.Distance(myPos, h.Emergent.RecentAnchorPos);
                h.Emergent.RecentAnchorPos = myPos;
                h.Emergent.RecentAnchorTime = now;
            }

            // Build EmergentInput purely from cached handles & world snapshots (0 extra raycasts!)
            float dt = h.Emergent.LastSampleTime > 0f ? Mathf.Max(0.01f, now - h.Emergent.LastSampleTime) : 0.25f;
            h.Emergent.LastSampleTime = now;

            bool hasVisible = ai.visibleTarget != null && ai.visibleTarget.Pointer != IntPtr.Zero;
            bool canFireAtVis = ai.HasEnemyAndCanFireAtHim;
            GenericGun heldGun = soldier.GetHeldGun();
            bool gunEmpty = AiCore.IsHeldGunEmptyOrReloading(soldier, heldGun);
            bool hasUsableArms = AiCore.HasUsableSmallArmsAmmo(soldier);
            bool hasAtCap = VeteranAndTacticsPatches.HasUsableAntiTankCapability(soldier);

            // Platform-relative CanHarm: rifleman targeting heavy tank gets CanHarm = 0!
            float canHarm = 0f;
            bool visTargetIsHeavyArmor = false;
            if (hasVisible)
            {
                if (ai.visibleTarget.IsWheeledVehicleOrTank())
                {
                    Vehicle tv = ai.visibleTarget.TryCast<Vehicle>();
                    float mm = tv != null ? VeteranAndTacticsPatches.GetVehicleMaxHullArmorMm(tv) : 0f;
                    visTargetIsHeavyArmor = mm >= 20f;
                    if (hasAtCap)
                        canHarm = 0.85f * ComputeAtWeaponArmorWeight(soldier, heldGun, mm, dist);
                    else if (visTargetIsHeavyArmor)
                        canHarm = 0.0f;
                    else
                        canHarm = 0.35f * ComputeWeaponEffectiveRangeWeight(soldier, h, heldGun, isGunner, personality, dist, now);
                }
                else
                {
                    float baseCanHarm = gunEmpty && !inPointBlankMelee ? 0.10f : (canFireAtVis ? 0.90f : 0.45f);
                    canHarm = (gunEmpty && !inPointBlankMelee)
                        ? 0.10f
                        : (baseCanHarm * ComputeWeaponEffectiveRangeWeight(soldier, h, heldGun, isGunner, personality, dist, now));
                }
            }
            else if (board.HasActiveThreat)
            {
                if (board.ThreatIsArmor && !hasAtCap)
                {
                    canHarm = 0.0f;
                }
                else
                {
                    float threatDist = Vector3.Distance(myPos, board.ThreatPosition);
                    canHarm = 0.55f * ComputeWeaponEffectiveRangeWeight(soldier, h, heldGun, isGunner, personality, threatDist, now);
                }
            }

            float rawHarmNow = Mathf.Clamp01(suppression / 68.0f);
            if (hasVisible && canHarm <= 0.05f && visTargetIsHeavyArmor && dist <= 45f) rawHarmNow = Mathf.Max(rawHarmNow, 0.65f);
            h.Emergent.SmoothedHarmNow = EmergentEngine.SmoothHarmEnvelope(h.Emergent.SmoothedHarmNow, rawHarmNow, dt);

            long sqId = (sq != null && sq.Pointer != IntPtr.Zero) ? PtrToKey(sq.Pointer) : 0L;
            float projHarm = ring.EvaluateSpotProjectedHarm(
                myPos, myPos, sqId,
                board.IsConnectedToRadioNet,
                isHeavyArmor: false,
                isLightArmor: false,
                EmergentEngine.EmergentTier.Individual,
                now,
                out int activeStamps,
                out float enemyRelief,
                out bool doorwayCas);

            if (ai.IsMoving() && h.Tr != null)
            {
                Vector3 moveFwd = h.Tr.forward;
                moveFwd.y = 0f;
                if (moveFwd.sqrMagnitude > 0.01f)
                {
                    Vector3 aheadPos = myPos + moveFwd.normalized * 5.5f;
                    float aheadHarm = ring.EvaluateSpotProjectedHarm(
                        aheadPos, myPos, sqId,
                        board.IsConnectedToRadioNet,
                        isHeavyArmor: false,
                        isLightArmor: false,
                        EmergentEngine.EmergentTier.Individual,
                        now,
                        out int aheadStamps,
                        out float aheadRelief,
                        out bool aheadDoorwayCas);
                    if (aheadHarm > projHarm) projHarm = aheadHarm;
                    if (aheadStamps > activeStamps) activeStamps = aheadStamps;
                    if (aheadRelief > enemyRelief) enemyRelief = aheadRelief;
                    if (aheadDoorwayCas) doorwayCas = true;
                }
            }

            // Check lightweight world interactables from WorldSnapshots
            bool hasLiveGrenade = VeteranAndTacticsPatches.EmergentHasCloseLiveGrenade(myPos, fac);
            bool exposedWall = !isOnBridge && VeteranAndTacticsPatches.EmergentIsExposedWall(soldier, sq, myPos, board.ThreatDirection);
            bool hasBandage = soldier.inventory != null && soldier.inventory.FindItemOfType<VirtualBandages>() != null;
            bool hasSmoke = soldier.inventory != null && soldier.inventory.FindItemOfType<VirtualSmokeGrenade>() != null;
            bool hasFrag = AiCore.FindThrowableGrenadeInInventory(soldier, false, true) != null;

            bool isMedic = AiCore.IsMedicSoldierOrPersonality(soldier, personality) && soldier.hasSyringe;
            bool hasInjuredAlly = false;
            bool injuredInOpen = false;
            bool injuredSameSq = false;
            bool hasDownedDrag = false;
            bool downedSameSq = false;
            long injuredTargetKey = 0L;
            Vector3 injuredTargetPos = Vector3.zero;
            IntPtr mySqPtr = sq != null ? sq.Pointer : IntPtr.Zero;
            if (isMedic)
            {
                float bestSameDistSq = float.MaxValue;
                float bestOtherDistSq = float.MaxValue;
                long sameKey = 0L;
                long otherKey = 0L;
                Vector3 samePos = Vector3.zero;
                Vector3 otherPos = Vector3.zero;
                for (int i = 0; i < AiCore.WorldInjuredSoldierCount; i++)
                {
                    ref var inj = ref AiCore.WorldInjuredSoldiers[i];
                    if (inj.Ptr == IntPtr.Zero || inj.Ptr == h.Ptr || !AiCore.AreSameSideFactions(fac, inj.Faction))
                        continue;
                    float dSq = (inj.Pos - myPos).sqrMagnitude;
                    if (dSq > 42f * 42f)
                        continue;
                    long candKey = PtrToKey(inj.Ptr);
                    if (!intents.IsClaimableExclusive(EmergentEngine.IntentKind.ExclusiveMedicPatient, candKey, PtrToKey(h.Ptr), now))
                        continue;

                    bool isVip = inj.IsRadioman || inj.IsSquadLeader;
                    float rankSq = isVip ? (dSq * 0.40f) : dSq;
                    if (mySqPtr != IntPtr.Zero && inj.SquadPtr == mySqPtr)
                    {
                        if (rankSq < bestSameDistSq) { bestSameDistSq = rankSq; sameKey = candKey; samePos = inj.Pos; }
                    }
                    else if (rankSq < bestOtherDistSq)
                    {
                        bestOtherDistSq = rankSq; otherKey = candKey; otherPos = inj.Pos;
                    }
                }
                if (sameKey != 0L || otherKey != 0L)
                {
                    hasInjuredAlly = true;
                    injuredSameSq = sameKey != 0L;
                    injuredTargetKey = injuredSameSq ? sameKey : otherKey;
                    injuredTargetPos = injuredSameSq ? samePos : otherPos;
                    float pHarm = ring.EvaluateSpotProjectedHarm(injuredTargetPos, myPos, sqId, board.IsConnectedToRadioNet, false, false, EmergentEngine.EmergentTier.Individual, now, out _, out _, out _);
                    injuredInOpen = pHarm >= 0.35f;
                }
            }
            else if (!soldier.IsSquadLeader() && !soldier.IsRadioman() && VisionConfig.EnableBuddyCasualtyDragging.Value)
            {
                for (int i = 0; i < AiCore.WorldInjuredSoldierCount; i++)
                {
                    ref var inj = ref AiCore.WorldInjuredSoldiers[i];
                    if (inj.Ptr == IntPtr.Zero || !AiCore.AreSameSideFactions(fac, inj.Faction))
                        continue;
                    if ((inj.Pos - myPos).sqrMagnitude <= 14f * 14f)
                    {
                        hasDownedDrag = true;
                        if (mySqPtr != IntPtr.Zero && inj.SquadPtr == mySqPtr)
                        {
                            downedSameSq = true;
                            break;
                        }
                    }
                }
            }

            bool hasPrisoner = false;
            bool prisonerSameSq = false;
            if (VisionConfig.EnableComradeLiberation.Value && !soldier.IsSquadLeader())
            {
                for (int i = 0; i < AiCore.WorldSoldierCount; i++)
                {
                    ref var sol = ref AiCore.WorldSoldiers[i];
                    if (sol.HasSurrendered && AiCore.AreSameSideFactions(fac, sol.Faction) && (sol.Pos - myPos).sqrMagnitude <= 26f * 26f)
                    {
                        hasPrisoner = true;
                        if (mySqPtr != IntPtr.Zero && sol.SquadPtr == mySqPtr)
                        {
                            prisonerSameSq = true;
                            break;
                        }
                    }
                }
            }

            bool hasAmmoCrateOrCarrier = false;
            bool isAmmoCarrier = soldier.IsAmmoBoxCarrier();
            bool allyNeedsAmmoNearby = false;
            bool isBailedCrew = VeteranAndTacticsPatches.IsSoldierBailedOutVehicleCrew(soldier, sq);
            bool urgentBandageWhileBleeding = soldier.isBleeding && !hasBandage;
            bool armorThreatPresent = isEvadingArmor ||
                                      visTargetIsHeavyArmor ||
                                      (board.HasActiveThreat && (board.ThreatIsArmor || (board.PrimaryTarget != null && board.PrimaryTarget.TryCast<Vehicle>() != null))) ||
                                      (ai.visibleTarget != null && ai.visibleTarget.TryCast<Vehicle>() != null);
            bool safeLullForGear = !hasVisible && rawHarmNow < 0.20f && projHarm < 0.20f && suppression < 5.0f && !exposedWall && !isOnBridge;
            bool allowUrgentPrimary = !inPointBlankMelee &&
                                      !soldier.isBleeding &&
                                      rawHarmNow < 0.45f &&
                                      !(hasVisible && dist <= 16.0f) &&
                                      !VeteranAndTacticsPatches.HasRealPrimaryWithAmmo(soldier);
            bool allowUrgentAt = !inPointBlankMelee &&
                                 !soldier.isBleeding &&
                                 !hasAtCap &&
                                 armorThreatPresent &&
                                 suppression < 18.0f &&
                                 !soldier.IsSquadLeader() &&
                                 !soldier.IsRadioman() &&
                                 !isMedic &&
                                 !(hasVisible && !visTargetIsHeavyArmor && dist <= 16.0f);
            bool allowBailedCrewToolbox = isBailedCrew && !soldier.isBleeding && !isEvadingArmor && suppression < 20.0f && soldier.GetRepairTools() == null;
            bool foundUrgentPrimaryUpgrade = false;
            bool foundAtWeaponScavenge = false;
            if (!hasUsableArms || urgentBandageWhileBleeding || (isMedic && !hasBandage) || safeLullForGear || allowUrgentPrimary || allowUrgentAt || allowBailedCrewToolbox)
            {
                hasAmmoCrateOrCarrier = VeteranAndTacticsPatches.EmergentHasNearbyResupplySource(
                    ai,
                    soldier,
                    h,
                    sq,
                    ref board,
                    myPos,
                    personality,
                    suppression,
                    hasUsableArms,
                    hasBandage,
                    hasFrag,
                    hasSmoke,
                    hasAtCap,
                    isBailedCrew,
                    isEvadingArmor,
                    exposedWall,
                    safeLullForGear,
                    allowUrgentPrimary,
                    allowUrgentAt,
                    now,
                    out foundUrgentPrimaryUpgrade,
                    out foundAtWeaponScavenge);
            }
            if (isBailedCrew &&
                !inPointBlankMelee &&
                !soldier.isBleeding &&
                !hasLiveGrenade &&
                !VeteranAndTacticsPatches.EmergentIsBailedCrewPrioritizingScavenge(
                    soldier,
                    sq,
                    hasAmmoCrateOrCarrier,
                    (safeLullForGear && !exposedWall && !isOnBridge && suppression < 5f) ||
                    ((foundUrgentPrimaryUpgrade || foundAtWeaponScavenge) && !exposedWall && !isOnBridge && suppression < 18f),
                    now))
            {
                ref var crewPlan = ref h.Goap.Plan;
                crewPlan.PlanExpireTime = now + 2.5f;
                crewPlan.LockUnitCoverChange = false;
                crewPlan.HasMoveOverride = false;
                crewPlan.IsEvacuatingBailedCrew = false;
                VeteranAndTacticsPatches.EmergentRunBailedCrewOrLoneWolf(ai, soldier, sq, h.Ptr, h, ref board, ref crewPlan, isOnBridge, suppression, dist, myPos, now);
                if (crewPlan.HasMoveOverride && now < crewPlan.PlanExpireTime)
                    AiCore.SetBotMoveOverride(h, AiCore.BotMoveOverrideKind.GoapAction, crewPlan.MoveOverrideDest, crewPlan.PlanExpireTime);
                else
                    AiCore.ClearBotMoveOverride(h, AiCore.BotMoveOverrideKind.GoapAction);
                if (sq != null && sq.Pointer != IntPtr.Zero)
                    VeteranAndTacticsPatches._squadBlackboards[sq.Pointer] = board;
                return;
            }
            if (isAmmoCarrier)
            {
                for (int i = 0; i < AiCore.WorldSoldierCount; i++)
                {
                    ref var s = ref AiCore.WorldSoldiers[i];
                    if (s.Ptr != h.Ptr && s.SquadPtr == mySqPtr && (s.Pos - myPos).sqrMagnitude <= 14f * 14f && s.Ref != null && AiCore.IsHeldGunEmptyOrReloading(s.Ref, s.Ref.GetHeldGun()))
                    {
                        allyNeedsAmmoNearby = true;
                        break;
                    }
                }
            }

            EmergentEngine.Tier2SquadDirective sqDir = (sq != null && sq.Pointer != IntPtr.Zero && _squadHeartbeats.TryGetValue(sq.Pointer, out SquadHeartbeatState hbDir))
                ? hbDir.Directive
                : EmergentEngine.Tier2SquadDirective.AdvanceBound;

            Vector3 objPos = board.HasActiveObjectiveTask ? board.ObjectivePosition : myPos;
            float objDist = board.HasActiveObjectiveTask ? Vector3.Distance(myPos, objPos) : 0f;
            bool insideObjZone = board.HasActiveObjectiveTask &&
                ((sq != null && sq.currentTask != null) ? sq.currentTask.IsInsideTaskArea(myPos, 0.92f) : objDist <= 18f);
            float fragDist = board.HasActiveThreat ? Vector3.Distance(myPos, board.ThreatPosition) : 999f;

            bool capTickingAgainst = board.HasActiveObjectiveTask && board.IsObjectiveTickingAgainstUs(now);
            int clusteredEnemies = 0;
            if (hasFrag && (hasVisible || board.HasActiveThreat))
            {
                Vector3 fragRefPos = (hasVisible && !visTargetIsHeavyArmor && AiCore.TryGetSpottableCenter(ai.visibleTarget, out Vector3 fragVisCenter))
                    ? fragVisCenter
                    : board.ThreatPosition;
                float fragRefDistSq = (fragRefPos - myPos).sqrMagnitude;
                if (fragRefDistSq >= 8.0f * 8.0f && fragRefDistSq <= 32.0f * 32.0f)
                {
                    int wc = AiCore.WorldSoldierCount;
                    for (int i = 0; i < wc; i++)
                    {
                        ref var es = ref AiCore.WorldSoldiers[i];
                        if (es.Ptr == IntPtr.Zero || es.IsOnVehicle || es.HasSurrendered || es.IsInjured || AiCore.AreSameSideFactions(fac, es.Faction))
                            continue;
                        float edx = es.Pos.x - fragRefPos.x;
                        float edz = es.Pos.z - fragRefPos.z;
                        if (edx * edx + edz * edz <= 6.5f * 6.5f && Mathf.Abs(es.Pos.y - fragRefPos.y) <= 3.0f)
                        {
                            clusteredEnemies++;
                            if (clusteredEnemies >= 4)
                                break;
                        }
                    }
                }
            }

            bool hasMedAid = intents.HasOpenCanAid(EmergentEngine.IntentKind.CanAidOverwatchMedic, myPos, 42f, PtrToKey(h.Ptr), sqId, now, out bool sameSqMedAid);
            bool hasRepAid = intents.HasOpenCanAid(EmergentEngine.IntentKind.CanAidScreenRepair, myPos, 42f, PtrToKey(h.Ptr), sqId, now, out bool sameSqRepAid);
            float cohesionBonus = 0f;
            if (board.AliveMemberCount >= 2 && sq != null && sq.Leader != null && sq.Leader.IsAlive && sq.Leader.transform != null)
            {
                float ldrDist = Vector3.Distance(myPos, sq.Leader.transform.position);
                cohesionBonus = Mathf.Clamp01(1.0f - Mathf.Max(0f, ldrDist - 24.0f) / 36.0f);
            }

            bool closeAcousticReflex = false;
            if (!hasVisible && hasUsableArms && !soldier.isBleeding && !soldier.isInHandsAnimation && !soldier.isInBodyAnimation)
            {
                closeAcousticReflex = now < h.Vision.UnderFireUntil;
                if (!closeAcousticReflex && !string.IsNullOrEmpty(fac))
                {
                    closeAcousticReflex = VisionPatches.TryGetNearestCloseAcousticThreat(myPos, fac, now, out _, out float reflexSoundDist, out _) &&
                                          reflexSoundDist <= 16.0f;
                }
            }

            var inp = new EmergentEngine.EmergentInput
            {
                Now = now,
                MyPos = myPos,
                SquadId = sqId,
                IsLeader = soldier.IsSquadLeader() && board.AliveMemberCount >= 2,
                IsVehicle = false,
                HasRadioLink = board.IsConnectedToRadioNet,
                IsRadioman = soldier.IsRadioman(),
                RadioLinkDrifting = soldier.IsRadioman() && sq != null && sq.Leader != null && sq.Leader.transform != null && (myPos - sq.Leader.transform.position).sqrMagnitude > 14f * 14f,
                IsMedic = isMedic,
                IsAmmoCarrier = isAmmoCarrier,
                IsMachineGunnerOrBipod = isGunner,
                IsAtCapable = hasAtCap,
                RawHarmNow = rawHarmNow,
                SmoothedHarmNow = h.Emergent.SmoothedHarmNow,
                ProjectedHarm = projHarm,
                EnemyRelief = enemyRelief,
                ActiveDangerStamps = activeStamps,
                DoorwayFreshCasualtyAhead = doorwayCas,
                LiveGrenadeClose = hasLiveGrenade,
                CloseAcousticThreat = closeAcousticReflex,
                InCover = soldier.IsOnCover(),
                ExposedSideOfWall = exposedWall,
                IsOnBridge = isOnBridge,
                IsBleeding = soldier.isBleeding,
                HasBandage = hasBandage,
                HasSmokeGrenade = hasSmoke,
                HasFragGrenade = hasFrag,
                AmmoEmpty = !hasUsableArms,
                HasVisibleEnemy = hasVisible,
                PointBlankThreat = inPointBlankMelee,
                VisibleEnemyDist = dist,
                CanHarmTarget = canHarm,
                CoverAvailable = now >= h.Stance.NoLocalCoverUntil,
                FlankCoverAvailable = board.FlankLeftCover != null || board.FlankRightCover != null,
                AllyPinningThreat = board.IsThreatBeingPinned(now),
                SmokeActive = now < board.SmokeActiveUntil,
                HasLastKnownThreat = board.HasActiveThreat,
                LastKnownThreatDist = fragDist,
                TargetBehindCover = board.HasActiveThreat && !canFireAtVis,
                Overwhelmed = board.AliveMemberCount <= 2 && rawHarmNow >= 0.65f,
                UncounterableArmorClose = isEvadingArmor || (visTargetIsHeavyArmor && !hasAtCap && dist <= 45f),
                HasObjectiveTask = board.HasActiveObjectiveTask,
                InsideObjectiveZone = insideObjZone,
                IsHomeGroundDefender = AiCore.IsAsymmetricDefender(fac),
                ObjectiveDist = objDist,
                ObjectiveUrgency = board.HasActiveObjectiveTask ? Mathf.Clamp01((capTickingAgainst ? 0.85f : 0.45f) + board.ObjectiveCaptureProgress * 0.55f) : 0f,
                ClusteredEnemiesAtThreat = clusteredEnemies,
                ObjectiveTickingAgainstUs = capTickingAgainst,
                NearbyAmmoSource = hasAmmoCrateOrCarrier,
                NeedsPrimaryWeaponUpgrade = foundUrgentPrimaryUpgrade,
                NeedsAtWeaponScavenge = foundAtWeaponScavenge,
                AllyNeedsAmmoDropNearby = allyNeedsAmmoNearby,
                HasInjuredAlly = hasInjuredAlly,
                InjuredAllyHarm = projHarm,
                InjuredAllyInOpen = injuredInOpen,
                HasDownedAllyToDrag = hasDownedDrag,
                HasSurrenderedPrisoner = hasPrisoner,
                HasAidOverwatchNeed = hasMedAid || hasRepAid,
                InjuredAllySameSquad = injuredSameSq,
                DownedAllySameSquad = downedSameSq,
                PrisonerSameSquad = prisonerSameSq,
                AidOverwatchSameSquad = sameSqMedAid || sameSqRepAid,
                SquadCohesionBonus = cohesionBonus,
                PrereqBlockedUntil = h.Emergent.PrereqBlockedUntil,
                PrereqBlockedAction = (byte)h.Emergent.PrereqBlockedAction,
                SquadDirective = sqDir,
                SectorDesperationBias = desp,
                CurrentStep0 = h.Emergent.HasDecision ? h.Emergent.CurrentDecision.Step0 : EmergentEngine.EmergentActionId.None,
                LastReflexKind = h.Emergent.LastReflexKind,
                LastReflexTime = h.Emergent.LastReflexTime
            };

            // Layer 1: Fast Survival Reflex (every tick — bypasses commitment hysteresis!)
            bool ranReflex = EmergentEngine.EvaluateFastReflex(in inp, in h.Emergent.Weights, now, out EmergentEngine.EmergentDecision dec);
            if (ranReflex)
            {
                h.Emergent.LastReflexKind = dec.Reflex;
                h.Emergent.LastReflexTime = now;
                h.Emergent.NextPlanTime = Mathf.Max(h.Emergent.NextPlanTime, dec.CommitUntil);
                if (dec.Reflex == EmergentEngine.EmergentReflexKind.CloseAcousticAlert)
                {
                    h.Vision.StationaryWatch.NextEvalTime = 0f;
                    h.Vision.ThreatSector.ExpireTime = 0f;
                    soldier.SetAlertedFor(4.0f);
                    if (ai.IsMoving() && !soldier.IsOnCover() && rawHarmNow < 0.35f)
                    {
                        ai.moveDirectlyToward_end = 0f;
                        ai.Stop(true);
                    }
                }
            }
            else
            {
                // Layer 2: Deliberative Utility + 2-3 Ply Prereq Chaining (staggered ~3.3 Hz, or instant wake on close visible enemy)
                bool closeContactWake = hasVisible &&
                                        dist <= 16.0f &&
                                        hasUsableArms &&
                                        canHarm > 0.20f &&
                                        (!h.Emergent.HasDecision ||
                                         (h.Emergent.CurrentDecision.Step0 != EmergentEngine.EmergentActionId.HoldAndFight &&
                                          h.Emergent.CurrentDecision.Step0 != EmergentEngine.EmergentActionId.CloseBreach &&
                                          h.Emergent.CurrentDecision.Step0 != EmergentEngine.EmergentActionId.EvadeGrenade));
                if (!h.Emergent.HasDecision || closeContactWake || now >= h.Emergent.NextPlanTime)
                {
                    EmergentEngine.EmergentDecision prevForDelib = h.Emergent.CurrentDecision;
                    if (closeContactWake &&
                        (prevForDelib.Step0 == EmergentEngine.EmergentActionId.AdvanceObjective ||
                         prevForDelib.Step0 == EmergentEngine.EmergentActionId.FlankManeuver ||
                         prevForDelib.Step0 == EmergentEngine.EmergentActionId.MaintainRadioLink ||
                         prevForDelib.Step0 == EmergentEngine.EmergentActionId.SuppressPin))
                    {
                        prevForDelib.CommitUntil = 0f;
                    }
                    dec = EmergentEngine.EvaluateDeliberative(in inp, in h.Emergent.Weights, in prevForDelib, now);
                    h.Emergent.NextPlanTime = now + 0.28f + ((AiCore.GetDeterministicHash(h.Ptr, 17) & 7) * 0.02f);
                    if (dec.UnsafePrereqRejected)
                    {
                        _unsafeChainsRejectedCount++;
                        h.Emergent.PrereqBlockedAction = dec.RejectedPrereqAction;
                        h.Emergent.PrereqBlockedUntil = now + 4.5f;
                    }
                }
                else
                {
                    dec = h.Emergent.CurrentDecision;
                }
            }

            // Update Thrash Watchdog & Telemetry (deliberative flips only; reflex preemption isn't thrash)
            if (!ranReflex && dec.Step0 != h.Emergent.LastActionForFlip)
            {
                if (h.Emergent.FlipWindowStart <= 0f || (now - h.Emergent.FlipWindowStart) > 4.0f)
                {
                    h.Emergent.FlipWindowStart = now;
                    h.Emergent.RecentFlipCount = 1;
                }
                else
                {
                    h.Emergent.RecentFlipCount++;
                    if (h.Emergent.RecentFlipCount >= 5)
                        _thrashWatchdogCount++;
                }
                h.Emergent.LastActionForFlip = dec.Step0;
            }

            if (!h.Emergent.HasDecision || dec.DecideTime == now)
            {
                _totalT1Decisions++;
                int aIdx = (int)dec.Step0;
                if (aIdx >= 0 && aIdx < _actionCounts.Length) _actionCounts[aIdx]++;
                if (ranReflex && (int)dec.Reflex < _reflexCounts.Length) _reflexCounts[(int)dec.Reflex]++;
                if (dec.Step1 != EmergentEngine.EmergentActionId.None) _prereqChainsCount++;
            }

            if (!h.Emergent.HasDecision || dec.Step0 != h.Emergent.CurrentDecision.Step0)
                h.Emergent.ActionStartedAt = now;

            h.Emergent.HasDecision = true;
            h.Emergent.CurrentDecision = dec;

            uint inputFlags = EmergentEngine.PackInputFlags(in inp, dec.UnsafePrereqRejected);
            h.Emergent.LastInputFlags = inputFlags;

            // Record Lock-Free Transition Telemetry
            BotActionTrace.RecordEmergentDecision(ai, soldier, h, personality, sqDir, in dec, activeStamps, inputFlags, suppression, dist, now);

            // Execute Chosen Action / Prereq Step0 through Existing Verified Executors
            bool acousticBusy = isEvadingArmor || exposedWall || rawHarmNow >= 0.25f || inp.RadioLinkDrifting || hasVisible;
            ExecuteEmergentFootAction(ai, soldier, h, sq, ref board, sideIdx, in dec, injuredTargetKey, injuredTargetPos, suppression, dist, myPos, isOnBridge, acousticBusy, rawHarmNow, now);
            if (sq != null && sq.Pointer != IntPtr.Zero)
                VeteranAndTacticsPatches._squadBlackboards[sq.Pointer] = board;
        }

        private static void ExecuteEmergentFootAction(
            SoldierAI ai,
            Soldier soldier,
            AiCore.SoldierFastHandles h,
            Squad sq,
            ref VeteranAndTacticsPatches.SquadBlackboard board,
            int sideIdx,
            in EmergentEngine.EmergentDecision dec,
            long injuredTargetKey,
            Vector3 injuredTargetPos,
            float suppression,
            float dist,
            Vector3 myPos,
            bool isOnBridge,
            bool acousticBusy,
            float rawHarmNow,
            float now)
        {
            var intents = GetIntents(sideIdx);
            ref var plan = ref h.Goap.Plan;
            plan.PlanExpireTime = dec.CommitUntil;
            plan.LockUnitCoverChange = false;
            plan.HasMoveOverride = false;
            plan.IsEvacuatingBailedCrew = false;

            if (injuredTargetKey != 0L &&
                (dec.Step0 == EmergentEngine.EmergentActionId.MedicHeal ||
                 (dec.Step0 == EmergentEngine.EmergentActionId.PopSmoke && dec.Step1 == EmergentEngine.EmergentActionId.MedicHeal)))
            {
                intents.TryClaimExclusive(EmergentEngine.IntentKind.ExclusiveMedicPatient, injuredTargetKey, PtrToKey(h.Ptr), injuredTargetPos, 4.0f, now);
            }

            long sqId = (sq != null && sq.Pointer != IntPtr.Zero) ? PtrToKey(sq.Pointer) : 0L;
            if (dec.Step0 == EmergentEngine.EmergentActionId.SuppressPin ||
                dec.Step0 == EmergentEngine.EmergentActionId.HoldAndFight ||
                dec.Step0 == EmergentEngine.EmergentActionId.PopSmoke)
            {
                if (!intents.TryJoinCanAid(EmergentEngine.IntentKind.CanAidOverwatchMedic, myPos, 42f, PtrToKey(h.Ptr), sqId, now, out _, out _))
                    intents.TryJoinCanAid(EmergentEngine.IntentKind.CanAidScreenRepair, myPos, 42f, PtrToKey(h.Ptr), sqId, now, out _, out _);
            }

            switch (dec.Step0)
            {
                case EmergentEngine.EmergentActionId.EvadeGrenade:
                    VeteranAndTacticsPatches.EmergentRunGrenadeReflex(ai, soldier, h, VeteranAndTacticsPatches.GetPersonality(soldier), VeteranAndTacticsPatches.GetVeterancy01(soldier), isOnBridge, now);
                    break;

                case EmergentEngine.EmergentActionId.SeekCover:
                    VeteranAndTacticsPatches.EmergentRunSeekCover(ai, soldier, sq, h, ref board, ref plan, suppression, myPos, isOnBridge, rawHarmNow, dec.Step1 == EmergentEngine.EmergentActionId.FallbackRegroup, now);
                    break;

                case EmergentEngine.EmergentActionId.RepositionWallSide:
                    VeteranAndTacticsPatches.EmergentRunWallSide(ai, soldier, sq, h, ref board, ref plan, myPos, now);
                    break;

                case EmergentEngine.EmergentActionId.PopSmoke:
                    if (intents.TryClaimExclusive(EmergentEngine.IntentKind.ExclusiveSmokeLane, PtrToKey(sq != null ? sq.Pointer : h.Ptr), PtrToKey(h.Ptr), myPos, 8.0f, now))
                    {
                        VeteranAndTacticsPatches.EmergentRunSmoke(ai, soldier, sq, h.Ptr, h, ref board, ref plan, suppression, dist, myPos, now);
                    }
                    break;

                case EmergentEngine.EmergentActionId.SelfBandage:
                    plan.LockUnitCoverChange = true;
                    VeteranAndTacticsPatches.ApplyBandageLowPose(ai, soldier, h, now);
                    if (soldier.isBleeding && !soldier.isInHandsAnimation && !soldier.isInBodyAnimation)
                    {
                        soldier.SetGunTriggerPressed(false);
                        ai.TryStopBleeding();
                    }
                    break;

                case EmergentEngine.EmergentActionId.SuppressPin:
                    VeteranAndTacticsPatches.EmergentRunSuppress(ai, soldier, sq, h.Ptr, h, ref board, ref plan, dist, myPos, now);
                    break;

                case EmergentEngine.EmergentActionId.FlankManeuver:
                    VeteranAndTacticsPatches.EmergentRunFlank(ai, soldier, sq, h.Ptr, h, ref board, ref plan, suppression, myPos, now);
                    break;

                case EmergentEngine.EmergentActionId.FragFlush:
                    VeteranAndTacticsPatches.EmergentRunFrag(ai, soldier, h.Ptr, h, ref board, ref plan, myPos, now);
                    break;

                case EmergentEngine.EmergentActionId.CloseBreach:
                    plan.LockUnitCoverChange = true;
                    VeteranAndTacticsPatches.EmergentRunCqb(ai, soldier, h, dist, now);
                    break;

                case EmergentEngine.EmergentActionId.FallbackRegroup:
                    VeteranAndTacticsPatches.EmergentRunFallback(ai, soldier, sq, h, ref board, ref plan, dist, myPos, now);
                    break;

                case EmergentEngine.EmergentActionId.ResupplyAmmo:
                    _troopInteractionsCount++;
                    VeteranAndTacticsPatches.EmergentRunScavenge(ai, soldier, sq, h, ref plan, suppression, myPos, now);
                    break;

                case EmergentEngine.EmergentActionId.PushAmmoDrop:
                    if (soldier.IsAmmoBoxCarrier() && (!Photon.Pun.PhotonNetwork.InRoom || AiCore.TryConsumeBotRpcBudget(0.40f)))
                    {
                        _troopInteractionsCount++;
                        soldier.RequestAmmoDrop();
                        BotActionTrace.RecordEvent(soldier, BotTraceEventKind.TroopInteract, "push_ammo_drop", dist);
                    }
                    break;

                case EmergentEngine.EmergentActionId.MedicHeal:
                    _troopInteractionsCount++;
                    intents.PostCanAid(EmergentEngine.IntentKind.CanAidOverwatchMedic, PtrToKey(h.Ptr), PtrToKey(h.Ptr), myPos, 2, 5.0f, now, sqId);
                    if (VeteranAndTacticsPatches.IsActivelyHealingTeammate(soldier, out MedicHealingDestination healDest) && healDest != null)
                        VeteranAndTacticsPatches.DriveMedicHealingApproach(ai, soldier, h, healDest, now);
                    break;

                case EmergentEngine.EmergentActionId.DragCasualty:
                    _troopInteractionsCount++;
                    VeteranAndTacticsPatches.EmergentRunDrag(ai, soldier, sq, ref board, myPos, now);
                    break;

                case EmergentEngine.EmergentActionId.LiberatePrisoner:
                    if (board.SurrenderedComrade != null && AiCore.IsTrueSurrenderedPrisoner(board.SurrenderedComrade) &&
                        intents.TryClaimExclusive(EmergentEngine.IntentKind.ExclusivePrisoner, PtrToKey(board.SurrenderedComrade.Pointer), PtrToKey(h.Ptr), board.SurrenderedComrade.transform.position, 5.0f, now))
                    {
                        _troopInteractionsCount++;
                        VeteranAndTacticsPatches.EmergentRunLiberate(ai, soldier, h, board.SurrenderedComrade, myPos, now);
                    }
                    break;

                case EmergentEngine.EmergentActionId.MaintainRadioLink:
                    if (sq != null && sq.Leader != null && sq.Leader.IsAlive && sq.Leader.transform != null)
                    {
                        Vector3 leaderPos = sq.Leader.transform.position;
                        if (board.HasActiveObjectiveTask)
                        {
                            float leaderObjDistSq = (leaderPos - board.ObjectivePosition).sqrMagnitude;
                            float myObjDistSq = (myPos - board.ObjectivePosition).sqrMagnitude;
                            if (leaderObjDistSq > myObjDistSq + 25.0f * 25.0f &&
                                (leaderPos - myPos).sqrMagnitude > 60.0f * 60.0f)
                            {
                                break;
                            }
                        }

                        Vector3 targetPos = leaderPos - board.ThreatDirection * 3.5f;
                        plan.HasMoveOverride = true;
                        plan.MoveOverrideDest = targetPos;
                        AiCore.MoveBotTowardPosition(ai, soldier, targetPos, 0.5f, 2.8f);
                    }
                    break;

                case EmergentEngine.EmergentActionId.AdvanceObjective:
                    if (board.HasActiveObjectiveTask && !ai.IsMoving())
                    {
                        bool capTickingAgainst = board.IsObjectiveTickingAgainstUs(now);
                        bool inCapNow = sq != null && sq.currentTask != null && sq.currentTask.IsInsideTaskArea(myPos, 0.92f);
                        if (inCapNow && soldier.IsOnCover() && !capTickingAgainst)
                            break;

                        if (inCapNow && capTickingAgainst && !ai.HasEnemyAndCanFireAtHim && sq != null && sq.currentTask != null)
                        {
                            SquadTask curTask = sq.currentTask;
                            Vector3 sweepTarget = Vector3.zero;
                            bool hasSweepTarget = false;

                            if (board.HasActiveThreat && curTask.IsInsideTaskArea(board.ThreatPosition, 1.15f))
                            {
                                sweepTarget = board.ThreatPosition;
                                hasSweepTarget = true;
                            }
                            else if (h.Vision.LastKnownTarget.LastSeenTime > 0f &&
                                     (now - h.Vision.LastKnownTarget.LastSeenTime) <= 14.0f &&
                                     curTask.IsInsideTaskArea(h.Vision.LastKnownTarget.LastSeenPos, 1.15f))
                            {
                                sweepTarget = h.Vision.LastKnownTarget.LastSeenPos;
                                hasSweepTarget = true;
                            }
                            else if (VisionPatches.TryGetDominantEnemyGunfireDirection(
                                         myPos,
                                         soldier.faction,
                                         Mathf.Max(42f, curTask.GetTaskAreaRadius() * 1.4f),
                                         6.0f,
                                         out Vector3 gfDir,
                                         out float gfDist,
                                         out _))
                            {
                                Vector3 cand = myPos + gfDir * Mathf.Min(gfDist, 18.0f);
                                if (curTask.IsInsideTaskArea(cand, 1.10f))
                                {
                                    sweepTarget = cand;
                                    hasSweepTarget = true;
                                }
                            }

                            if (!hasSweepTarget)
                            {
                                Vector3 center = curTask.GetTaskDestination();
                                float taskRadius = Mathf.Max(8.0f, curTask.GetTaskAreaRadius());
                                int sweepSlot = (AiCore.GetDeterministicHash(h.Ptr, 509) + (int)(now / 7.5f)) & 7;
                                bool hasWater = AiCore.TryGetActiveWaterLevel(out float waterY);
                                for (int a = 0; a < 8 && !hasSweepTarget; a++)
                                {
                                    float ang = ((sweepSlot + a) & 7) * (Mathf.PI * 0.25f);
                                    Vector3 sweepRadial = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang));
                                    for (int f = 0; f < 2; f++)
                                    {
                                        Vector3 raw = center + sweepRadial * (taskRadius * (f == 0 ? 0.58f : 0.38f));
                                        if ((raw - myPos).sqrMagnitude < 8.0f * 8.0f && a < 4)
                                            continue;
                                        if (!curTask.IsInsideTaskArea(raw, 0.88f))
                                            continue;
                                        if (!AiCore.TrySnapWalkableGroundProbe(myPos, raw, 14.0f, 28.0f, 0.55f, 2.5f, hasWater, waterY, out Vector3 snapped))
                                            continue;
                                        if (!curTask.IsInsideTaskArea(snapped, 0.90f))
                                            continue;
                                        sweepTarget = snapped;
                                        hasSweepTarget = true;
                                        break;
                                    }
                                }
                            }

                            if (hasSweepTarget)
                            {
                                VeteranAndTacticsPatches.EmergentDetachCover(soldier);
                                Vector3 toSweep = sweepTarget - myPos;
                                toSweep.y = 0f;
                                Vector3 sweepDir = toSweep.sqrMagnitude > 0.01f ? toSweep.normalized : board.ThreatDirection;
                                bool canSeekSweepFallback = now >= h.Stance.NextLocalCoverSeekTime;
                                VeteranAndTacticsPatches.EmergentRunBound(ai, soldier, sq, myPos, sweepTarget, sweepDir, now, ref plan);
                                if (canSeekSweepFallback && !ai.IsMoving() && !ai.HasPath && !ai.HasPathRequest)
                                {
                                    h.Stance.NextLocalCoverSeekTime = now + 2.5f;
                                    DestinationWithoutCover sweepDest = new DestinationWithoutCover(sweepTarget, SoldierPose.Crouch);
                                    AiCore.AssignBotCoverOrDestination(ai, soldier, sweepDest.Cast<AiDestination>());
                                    plan.LockUnitCoverChange = true;
                                    plan.HasMoveOverride = true;
                                    plan.MoveOverrideDest = sweepTarget;
                                    AiCore.SetBotMoveDestinationAndRefreshPath(ai, sweepTarget);
                                }
                                break;
                            }
                        }

                        if (board.HasActiveThreat)
                        {
                            Vector3 objTarget = board.ObjectivePosition;
                            if (sq != null && sq.currentTask != null &&
                                VeteranAndTacticsPatches.TryComputeSquadObjectiveSectorAnchor(sq, sq.currentTask, myPos, out Vector3 sectorTarget, out _, out _))
                            {
                                objTarget = sectorTarget;
                            }
                            Vector3 toObj = (objTarget - myPos);
                            toObj.y = 0f;
                            Vector3 dir = toObj.sqrMagnitude > 0.01f ? toObj.normalized : board.ThreatDirection;
                            VeteranAndTacticsPatches.EmergentRunBound(ai, soldier, sq, myPos, objTarget, dir, now, ref plan);
                        }
                        if (now >= h.Stance.NextLocalCoverSeekTime && !ai.HasPath && !ai.HasPathRequest && soldier.HasDestinationAssigned)
                        {
                            h.Stance.NextLocalCoverSeekTime = now + 2.0f;
                            ai.moveDirectlyToward_end = 0f;
                            ai.UpdatePath();
                        }
                    }
                    break;

                case EmergentEngine.EmergentActionId.HoldAndFight:
                default:
                    if (ai.visibleTarget != null)
                    {
                        Vehicle targetVeh = ai.visibleTarget.TryCast<Vehicle>();
                        if (targetVeh != null &&
                            targetVeh.TryCast<VehiclePlane>() == null &&
                            (board.SquadHasAntiTank || VeteranAndTacticsPatches.HasUsableAntiTankCapability(soldier)))
                        {
                            bool isTank = targetVeh.TryCast<VehicleTank>() != null;
                            VeteranAndTacticsPatches.TrySayTacticalVoice(
                                soldier,
                                sq,
                                isTank ? VoiceManager.VoiceClip.attackThatTank : VoiceManager.VoiceClip.attackThatVehicle,
                                0.08f);
                        }
                    }

                    if (!soldier.IsOnCover() &&
                        ai.visibleTarget == null &&
                        !ai.IsMoving() &&
                        !ai.HasPath &&
                        !ai.HasPathRequest &&
                        !ai.PathCalculationLocked &&
                        soldier.HasDestinationAssigned &&
                        !soldier.isBleeding &&
                        !soldier.isInHandsAnimation &&
                        !soldier.isInBodyAnimation)
                    {
                        Vector3 assignedDest = VeteranAndTacticsPatches.GetCachedMoveEndpoint(h, ai, soldier, out _);
                        if ((assignedDest - myPos).sqrMagnitude > 3.5f * 3.5f)
                        {
                            ai.moveDirectlyToward_end = 0f;
                            ai.MoveDestination = assignedDest;
                            ai.UpdatePath();
                        }
                    }

                    if (!acousticBusy && !soldier.isBleeding && rawHarmNow < 0.20f)
                    {
                        if (board.AliveMemberCount <= 1)
                        {
                            VeteranAndTacticsPatches.EmergentRunBailedCrewOrLoneWolf(ai, soldier, sq, h.Ptr, h, ref board, ref plan, isOnBridge, suppression, dist, myPos, now);
                        }
                        else if (suppression < 15f && !isOnBridge &&
                                 VeteranAndTacticsPatches.EmergentTryCloseAcousticInvestigate(ai, soldier, h, sq, ref board, acousticBusy, suppression, VeteranAndTacticsPatches.GetPersonality(soldier), now))
                        {
                            if (h.MoveOverride.Kind == AiCore.BotMoveOverrideKind.GoapAction && now < h.MoveOverride.ExpireTime)
                            {
                                plan.HasMoveOverride = true;
                                plan.MoveOverrideDest = h.MoveOverride.Dest;
                                plan.PlanExpireTime = Mathf.Max(plan.PlanExpireTime, h.MoveOverride.ExpireTime);
                            }
                        }
                        else if (suppression < 15f && !isOnBridge &&
                                 ai.visibleTarget == null)
                        {
                            VeteranAndTacticsPatches.EmergentTryDefenderPerimeterPatrol(ai, soldier, h, sq, myPos, now);
                        }
                    }
                    break;
            }

            if (plan.HasMoveOverride && now < plan.PlanExpireTime)
                AiCore.SetBotMoveOverride(h, AiCore.BotMoveOverrideKind.GoapAction, plan.MoveOverrideDest, plan.PlanExpireTime);
            else
                AiCore.ClearBotMoveOverride(h, AiCore.BotMoveOverrideKind.GoapAction);
        }

        // =====================================================================
        // Tier 1 Vehicle Crew Decision Helper (Armor-Relative Harm + Safe Repair)
        // =====================================================================
        public static bool TryEvaluateEmergentVehicleCrew(Vehicle veh, out bool allowBailout, out string reason)
        {
            allowBailout = false;
            reason = "stay_buttoned";
            if (veh == null || veh.Pointer == IntPtr.Zero || !AiCore.IsLocallyOwnedVehicle(veh))
                return false;

            AiCore.VehicleFastHandles vh = AiCore.GetOrCreateVehicleHandles(veh);
            Squad crewSq = null;
            Soldier commander = null;
            var seats = veh.seats;
            if (seats != null)
            {
                for (int i = 0; i < seats.Length; i++)
                {
                    Soldier u = seats[i]?.unitSet;
                    if (u != null && u.IsAlive)
                    {
                        if (commander == null) commander = u;
                        if (u.joinedSquad != null) { crewSq = u.joinedSquad; break; }
                    }
                }
            }

            // Single Tier 2 Human Authority Gate
            if (AiCore.IsSquadOwnedOrOccupiedByRemoteHuman(crewSq) || AiCore.IsAnyHumanPlayerInsideVehicle(veh))
                return false;

            float now = Time.time;
            string fac = veh.GetVehicleFaction();
            int sideIdx = ResolveSideIndex(fac);
            float armorMm = VeteranAndTacticsPatches.GetVehicleMaxHullArmorMm(veh);
            bool isHeavy = armorMm >= 35f || (vh != null && vh.IsTank);
            bool isLight = !isHeavy && veh.IsWheeledVehicleOrTank();

            bool hullDoomed = veh.IsDisabled() || veh.GetDamage() >= 90 || !veh.CanFight();
            Vector3 vPos = veh.transform != null ? veh.transform.position : Vector3.zero;
            float projHarm = GetRing(sideIdx).EvaluateSpotProjectedHarm(
                vPos, vPos,
                crewSq != null ? PtrToKey(crewSq.Pointer) : 0L,
                true, isHeavy, isLight,
                EmergentEngine.EmergentTier.Individual,
                now, out _, out _, out _);

            float rawHarm = hullDoomed ? 0.98f : Mathf.Clamp01(veh.GetDamage() / 100f * (isHeavy ? 0.6f : 1.0f));
            bool needRepair = !hullDoomed && (veh.GetDamage() >= 35);
            float vdt = (vh != null && vh.Emergent.LastSampleTime > 0f) ? Mathf.Max(0.01f, now - vh.Emergent.LastSampleTime) : 0.25f;
            if (vh != null) vh.Emergent.LastSampleTime = now;

            var inp = new EmergentEngine.EmergentInput
            {
                Now = now,
                MyPos = vPos,
                IsVehicle = true,
                IsHeavyArmor = isHeavy,
                IsLightArmor = isLight,
                HasRadioLink = true,
                RawHarmNow = rawHarm,
                SmoothedHarmNow = vh != null ? (vh.Emergent.SmoothedHarmNow = EmergentEngine.SmoothHarmEnvelope(vh.Emergent.SmoothedHarmNow, rawHarm, vdt)) : rawHarm,
                ProjectedHarm = projHarm,
                HullBurningOrDoomed = hullDoomed,
                NeedsTrackOrEngineRepair = needRepair,
                CanHarmTarget = 0.80f,
                PrereqBlockedUntil = vh != null ? vh.Emergent.RepairBlockedUntil : 0f,
                PrereqBlockedAction = (byte)EmergentEngine.EmergentActionId.VehicleFieldRepair
            };

            var facProfile = VeteranAndTacticsPatches.GetFactionCommandProfile(fac);
            var pers = commander != null ? VeteranAndTacticsPatches.GetPersonality(commander) : VeteranAndTacticsPatches.SoldierPersonality.None;
            var w = EmergentEngine.ComputeWeights(in facProfile, pers, commander != null ? VeteranAndTacticsPatches.GetVeterancy01(commander) : 0.3f, (uint)AiCore.GetDeterministicHash(veh.Pointer, 51), 0f, true);

            if (!EmergentEngine.EvaluateFastReflex(in inp, in w, now, out EmergentEngine.EmergentDecision dec))
            {
                EmergentEngine.EmergentDecision prev = vh != null ? vh.Emergent.CurrentDecision : default;
                dec = EmergentEngine.EvaluateDeliberative(in inp, in w, in prev, now);
            }

            if (vh != null)
            {
                vh.Emergent.HasDecision = true;
                vh.Emergent.CurrentDecision = dec;
                if (dec.UnsafePrereqRejected)
                {
                    _unsafeChainsRejectedCount++;
                    vh.Emergent.RepairBlockedUntil = now + 8.0f;
                }
            }

            if (dec.Step0 == EmergentEngine.EmergentActionId.VehicleFieldRepair && vh != null)
            {
                long crewSqKey = (crewSq != null && crewSq.Pointer != IntPtr.Zero) ? PtrToKey(crewSq.Pointer) : 0L;
                GetIntents(sideIdx).PostCanAid(EmergentEngine.IntentKind.CanAidScreenRepair, PtrToKey(veh.Pointer), PtrToKey(veh.Pointer), vPos, 2, 10.0f, now, crewSqKey);
            }

            allowBailout = dec.Step0 == EmergentEngine.EmergentActionId.VehicleBailout;
            reason = dec.Reason ?? "veh_eval";
            return true;
        }

        // =====================================================================
        // Tier 1 Air Mission Decision Helper (Emergent Bombing vs Intercept)
        // =====================================================================
        public static bool TryEvaluateEmergentAirDecision(
            AIPlane plane,
            Spottable bestEnemyAir,
            bool enemyAirIsBomber,
            float nearestEnemyAirDist,
            bool hasGroundTarget,
            float groundScore01,
            bool groundIsArmorOrCluster,
            bool hasAntiArmorCannons,
            out EmergentEngine.AirMissionDecision dec)
        {
            dec = default;
            if (plane == null || plane.veh == null || plane.veh.Pointer == IntPtr.Zero)
                return false;

            AiCore.VehicleFastHandles planeVh = AiCore.GetOrCreateVehicleHandles(plane.veh);
            string myFac = VeteranAndTacticsPatches.GetVehicleCrewFaction(plane.veh);
            if (string.IsNullOrEmpty(myFac))
                return false;

            Soldier pilot = null;
            var seats = plane.veh.seats;
            if (seats != null)
            {
                for (int i = 0; i < seats.Length; i++)
                {
                    Soldier u = seats[i]?.unitSet;
                    if (u != null && u.IsAlive)
                    {
                        pilot = u;
                        break;
                    }
                }
            }

            float now = Time.time;
            bool underFireByAir = false;
            bool underFireByGround = false;
            if (planeVh != null && now < planeVh.UnderFireUntil && AiCore.IsValidAliveSpottable(planeVh.UnderFireAttacker))
            {
                if (planeVh.UnderFireAttacker.IsAirVehicle())
                    underFireByAir = true;
                else
                    underFireByGround = true;
            }

            int sideIdx = ResolveSideIndex(myFac);
            bool friendlyUnderThreat = (sideIdx == 0 ? _lastPulseS0Heat : _lastPulseS1Heat) >= 0.35f ||
                                       (sideIdx == 0 ? _lastPulseS0Balk : _lastPulseS1Balk) > 0;

            var airInp = new EmergentEngine.AirMissionInput
            {
                IsBomber = plane.veh.planeType == PlaneType.Bomber,
                HasBombs = plane.HasBombsToDeliver(),
                HasPilotGunAmmo = plane.PilotGunHasAnyAmmo(),
                HasAntiArmorCannons = hasAntiArmorCannons,
                UnderFireByAir = underFireByAir,
                UnderFireByGround = underFireByGround,
                HasEnemyAir = bestEnemyAir != null && bestEnemyAir.Pointer != IntPtr.Zero,
                EnemyAirIsBomber = enemyAirIsBomber,
                HasGroundTarget = hasGroundTarget,
                GroundTargetIsArmorOrCluster = groundIsArmorOrCluster,
                FriendlyGroundUnderThreat = friendlyUnderThreat,
                Damage01 = Mathf.Clamp01(plane.veh.GetDamage() / 100f),
                NearestEnemyAirDist = nearestEnemyAirDist,
                BestGroundTargetScore01 = Mathf.Clamp01(groundScore01),
                AltitudeAgl = plane.transform != null ? plane.transform.position.y : 200f
            };

            var facProfile = VeteranAndTacticsPatches.GetFactionCommandProfile(myFac);
            var pers = pilot != null
                ? VeteranAndTacticsPatches.GetPersonality(pilot)
                : VeteranAndTacticsPatches.SoldierPersonality.Pilot_Ace;
            float vet01 = pilot != null ? VeteranAndTacticsPatches.GetVeterancy01(pilot) : 0.35f;
            var w = EmergentEngine.ComputeWeights(in facProfile, pers, vet01, (uint)AiCore.GetDeterministicHash(plane.veh.Pointer, 61), 0f, isLeader: true);

            dec = EmergentEngine.EvaluateAirMission(in airInp, in w);
            if (dec.Action == EmergentEngine.AirActionId.AttackGround)
                _airGroundRunsCount++;
            else if (dec.Action == EmergentEngine.AirActionId.InterceptAir)
                _airInterceptsCount++;
            return true;
        }

        // =====================================================================
        // Tier 2 & Tier 3 Command Net Coordinator (SL Approve/Balk + 13 Net Levers + MIA)
        // =====================================================================
        public static void RunEmergentCommandNetTick(float now)
        {
            if (now < _nextNetTickTime)
                return;
            _nextNetTickTime = now + 0.85f;

            AiCore.EnsureWorldSnapshot();
            int sqCount = AiCore.WorldSquadCount;
            if (sqCount < 1)
                return;

            VeteranAndTacticsPatches.EmergentRefreshArtilleryZones(now);

            int[] sideActive = { 0, 0 };
            int[] sideBalking = { 0, 0 };
            int[] sideMia = { 0, 0 };
            float[] sideHeat = { 0f, 0f };
            float[] sidePeakHeat = { 0f, 0f };
            float[] sideHotHeatSum = { 0f, 0f };
            int[] sideHotSquads = { 0, 0 };
            float[] sideRelief = { 0f, 0f };
            float[] sideMinTickets = { 1f, 1f };
            float[] sideMaxDefCap = { 0f, 0f };
            bool[] sideCollapsingCap = { false, false };
            bool[] sideArmorBlock = { false, false };
            bool[] sideHasAt = { false, false };
            bool[] sideHasFlankOpen = { false, false };
            bool[] sideTankNeedsScreen = { false, false };
            string[] sideFaction = { null, null };
            bool issuedSquadCoverOrderThisTick = false;

            _reusableConnectedInfSquads.Clear();
            _reusableActiveVehAnvils.Clear();

            var squads = AiCore.WorldSquads;
            _seenAliveSquadPtrs.Clear();
            for (int i = 0; i < sqCount; i++)
            {
                ref AiCore.SquadSnapshot sqSnap = ref squads[i];
                Squad sq = sqSnap.Ref;
                if (sq == null || sq.Pointer == IntPtr.Zero || string.IsNullOrEmpty(sqSnap.Faction))
                    continue;

                int sIdx = ResolveSideIndex(sqSnap.Faction);
                sideFaction[sIdx] = sqSnap.Faction;
                EmergentEngine.DangerStampRing ring = GetRing(sIdx);
                long sqId = PtrToKey(sq.Pointer);

                _squadHeartbeats.TryGetValue(sq.Pointer, out SquadHeartbeatState hb);

                if (!sqSnap.LeaderIsAlive)
                {
                    // Check MIA Comms Heartbeat Timeout under Radio Fog-of-War!
                    if (hb.HadRadioLink && !hb.MiaStamped && hb.LastHeartbeatTime > 0f && (now - hb.LastHeartbeatTime) >= 18.0f)
                    {
                        hb.MiaStamped = true;
                        ring.StampMiaCommsTimeout(hb.LastKnownPos, sqId, 0.78f, now);
                        _squadHeartbeats[sq.Pointer] = hb;
                        _miaTimeoutsCount++;
                        sideMia[sIdx]++;
                        if (VisionConfig.EnableEmergentDebugTelemetry.Value)
                        {
                            AiCore.EnqueueAsyncLog(false, $"[HARMMIA] Side={sIdx} squad '{sq.squadName}' silent >18s; stamped MIA Harm @ {hb.LastKnownPos:F0}");
                        }
                    }
                    continue;
                }

                _seenAliveSquadPtrs.Add(sq.Pointer);
                sideActive[sIdx]++;
                Soldier leader = sqSnap.Leader;
                bool isHumanSq = sqSnap.IsPlayerInSquad || AiCore.IsSquadOwnedOrOccupiedByRemoteHuman(sq);

                hb.LastHeartbeatTime = now;
                hb.LastKnownPos = sqSnap.RefPos;
                hb.Faction = sqSnap.Faction;
                hb.HadRadioLink = sqSnap.HasRadioOrRelay || hb.HadRadioLink;
                hb.MiaStamped = false;

                float laneHarm = ring.EvaluateSpotProjectedHarm(
                    sqSnap.RefPos, sqSnap.RefPos, sqId,
                    sqSnap.HasRadioOrRelay,
                    sqSnap.IsVehicleCrew, false,
                    EmergentEngine.EmergentTier.SquadCrew,
                    now, out _, out float sqRelief, out _);
                sideHeat[sIdx] += laneHarm;
                if (laneHarm > sidePeakHeat[sIdx])
                    sidePeakHeat[sIdx] = laneHarm;
                if (laneHarm >= 0.22f)
                {
                    sideHotHeatSum[sIdx] += laneHarm;
                    sideHotSquads[sIdx]++;
                }
                sideRelief[sIdx] += sqRelief;

                if (isHumanSq || !AiCore.IsLocallyOwnedBot(leader))
                {
                    if (sqSnap.IsVehicleCrew && !sqSnap.RefOnPlane && sqSnap.HasRadioOrRelay)
                    {
                        VeteranAndTacticsPatches.SquadBlackboard vehBoard = VeteranAndTacticsPatches.GetOrUpdateSquadBlackboard(sq, leader);
                        if (vehBoard.HasActiveThreat)
                            _reusableActiveVehAnvils.Add(sq);
                    }
                    _squadHeartbeats[sq.Pointer] = hb;
                    continue;
                }

                // Evaluate Tier 2 SL Approve / Balk Pushback
                VeteranAndTacticsPatches.SquadBlackboard board = VeteranAndTacticsPatches.GetOrUpdateSquadBlackboard(sq, leader);
                if (board.SquadHasAntiTank)
                    sideHasAt[sIdx] = true;
                if (board.FlankLeftCover != null || board.FlankRightCover != null)
                    sideHasFlankOpen[sIdx] = true;
                if (sqSnap.IsVehicleCrew && !sqSnap.RefOnPlane && sqSnap.HasRadioOrRelay && board.HasActiveThreat)
                {
                    _reusableActiveVehAnvils.Add(sq);
                }
                else if (!sqSnap.IsVehicleCrew && !sqSnap.RefOnPlane && sqSnap.HasRadioOrRelay)
                {
                    _reusableConnectedInfSquads.Add(sq);
                }

                if (board.TicketRatio01 < sideMinTickets[sIdx])
                    sideMinTickets[sIdx] = board.TicketRatio01;
                if (board.IsDefendingObjective && board.HasActiveObjectiveTask)
                {
                    if (board.ObjectiveCaptureProgress > sideMaxDefCap[sIdx])
                        sideMaxDefCap[sIdx] = board.ObjectiveCaptureProgress;
                    if (board.ObjectiveCaptureProgress >= 0.60f)
                        sideCollapsingCap[sIdx] = true;
                }
                var prof = VeteranAndTacticsPatches.GetFactionCommandProfile(sqSnap.Faction);
                var pers = VeteranAndTacticsPatches.GetPersonality(leader);

                var t2Inp = new EmergentEngine.Tier2SquadInput
                {
                    Now = now,
                    AliveMembers = board.AliveMemberCount,
                    AvgSquadHarm = laneHarm,
                    ProjectedLaneHarm = laneHarm,
                    DecayedRecentDeaths = board.DecayedRecentDeaths,
                    EnemyRelief = sqRelief,
                    ObjectiveDist = board.HasActiveObjectiveTask ? Vector3.Distance(sqSnap.RefPos, board.ObjectivePosition) : 0f,
                    ObjectiveUrgency = board.HasActiveObjectiveTask ? Mathf.Clamp01(0.45f + board.ObjectiveCaptureProgress * 0.55f) : 0.25f,
                    HasActiveThreat = board.HasActiveThreat,
                    ThreatDist = board.HasActiveThreat ? Vector3.Distance(sqSnap.RefPos, board.ThreatPosition) : 999f,
                    ThreatIsArmor = board.ThreatIsArmor,
                    SquadHasAT = board.SquadHasAntiTank,
                    SquadHasSmoke = board.SquadHasSmokeAvailable,
                    SquadOutOfAmmo = board.SquadOutOfAmmo,
                    AllySquadPinning = board.IsThreatBeingPinned(now),
                    FlankCoverAvailable = board.FlankLeftCover != null || board.FlankRightCover != null,
                    HasIntactRadio = board.IsConnectedToRadioNet,
                    IsVehicleCrew = sqSnap.IsVehicleCrew,
                    LeaderIsSpearhead = pers == VeteranAndTacticsPatches.SoldierPersonality.Leader_Spearhead,
                    IsGyokusaiOrFinalStand = VeteranAndTacticsPatches.IsTrueGyokusaiOrBanzaiCondition(in prof, sq, in board),
                    SectorDesperationBias = sIdx == 0 ? _side0Desperation : _side1Desperation
                };

                var t2Dec = EmergentEngine.EvaluateTier2Squad(in t2Inp, in prof);
                hb.Directive = t2Dec.Directive;
                hb.BalkReason = t2Dec.BalkReason;
                hb.SectorDesperationBias = t2Inp.SectorDesperationBias;

                if (t2Dec.Directive == EmergentEngine.Tier2SquadDirective.BalkNeedSupport)
                {
                    if (!hb.BalkActive)
                    {
                        _tier2BalksCount++;
                        BotActionTrace.RecordEvent(leader, BotTraceEventKind.Tier2Balk, t2Dec.BalkReason.ToString(), t2Inp.ThreatDist);
                    }
                    hb.BalkActive = true;
                    sideBalking[sIdx]++;
                    if (t2Dec.BalkReason == EmergentEngine.Tier2BalkReason.BlockedByArmor)
                        sideArmorBlock[sIdx] = true;
                    if (t2Dec.RequestArmorOrArty && sqSnap.HasRadioOrRelay)
                        VeteranAndTacticsPatches.EmergentTriggerRadioSupport(sq, leader);
                }
                else
                {
                    hb.BalkActive = false;
                }

                // Sync Tier 2 Directive onto SquadBlackboard so spectator overlays & Hammer/Anvil stay unified
                if (t2Dec.Directive == EmergentEngine.Tier2SquadDirective.HammerFlank)
                {
                    board.NetDirective = VeteranAndTacticsPatches.SquadNetDirective.Hammer_WideFlankEnvelopment;
                    board.NetDirectiveExpireTime = now + 4.5f;
                    if (board.HasActiveThreat) board.NetDirectiveTargetPos = board.ThreatPosition;
                    else if (board.HasActiveObjectiveTask) board.NetDirectiveTargetPos = board.ObjectivePosition;
                }
                else if (t2Dec.Directive == EmergentEngine.Tier2SquadDirective.AnvilPin)
                {
                    board.NetDirective = VeteranAndTacticsPatches.SquadNetDirective.Anvil_PinAndCallSupport;
                    board.NetDirectiveExpireTime = now + 4.5f;
                    if (board.HasActiveThreat) board.NetDirectiveTargetPos = board.ThreatPosition;
                }
                else if (t2Dec.Directive == EmergentEngine.Tier2SquadDirective.LocalFallback)
                {
                    board.NetDirective = VeteranAndTacticsPatches.SquadNetDirective.Regroup_SectorWithdrawal;
                    board.NetDirectiveExpireTime = now + 4.5f;
                }
                else if (board.IsConnectedToRadioNet)
                {
                    board.NetDirective = VeteranAndTacticsPatches.SquadNetDirective.Connected_StandardObjective;
                    board.NetDirectiveExpireTime = 0f;
                }

                if (!sqSnap.IsVehicleCrew && !sqSnap.RefOnPlane)
                {
                    EmergentEngine.Tier3NetLever activeT3Lever = sIdx == 0 ? _lastSide0Lever : _lastSide1Lever;
                    VeteranAndTacticsPatches.ExecuteEmergentSquadLeaderOrders(
                        sq,
                        leader,
                        ref board,
                        t2Dec.Directive,
                        activeT3Lever,
                        now,
                        ref issuedSquadCoverOrderThisTick);
                }
                else
                {
                    VeteranAndTacticsPatches.EmergentCheckChargeAbort(sq, leader, ref board, now);
                }
                VeteranAndTacticsPatches._squadBlackboards[sq.Pointer] = board;
                _squadHeartbeats[sq.Pointer] = hb;
            }

            // Detect whether any engaged ground tank/vehicle anvil lacks close infantry screening (<= 55m)
            for (int v = 0; v < _reusableActiveVehAnvils.Count; v++)
            {
                Squad vehSq = _reusableActiveVehAnvils[v];
                if (vehSq == null || vehSq.Leader == null || vehSq.Leader.transform == null || string.IsNullOrEmpty(vehSq.Leader.faction))
                    continue;
                int vs = ResolveSideIndex(vehSq.Leader.faction);
                if (sideTankNeedsScreen[vs])
                    continue;

                Vector3 vPos = vehSq.Leader.transform.position;
                bool hasCloseInf = false;
                for (int s = 0; s < sqCount; s++)
                {
                    ref AiCore.SquadSnapshot infSnap = ref squads[s];
                    if (!infSnap.LeaderIsAlive || infSnap.IsVehicleCrew || infSnap.RefOnPlane || string.IsNullOrEmpty(infSnap.Faction))
                        continue;
                    if (!AiCore.AreSameSideFactions(vehSq.Leader.faction, infSnap.Faction))
                        continue;
                    if ((infSnap.RefPos - vPos).sqrMagnitude <= 55.0f * 55.0f)
                    {
                        hasCloseInf = true;
                        break;
                    }
                }
                if (!hasCloseInf)
                    sideTankNeedsScreen[vs] = true;
            }

            // Check wiped squads that dropped out of WorldSquads for MIA comms timeout (>18s silent) & prune old (>120s) entries
            Vector3[] sideLastMiaPos = { Vector3.zero, Vector3.zero };
            bool[] sideHasMiaPos = { false, false };
            if (_squadHeartbeats.Count > 0)
            {
                _miaCheckKeyBuffer.Clear();
                foreach (var kv in _squadHeartbeats)
                    _miaCheckKeyBuffer.Add(kv.Key);
                for (int k = 0; k < _miaCheckKeyBuffer.Count; k++)
                {
                    IntPtr sqPtr = _miaCheckKeyBuffer[k];
                    if (_seenAliveSquadPtrs.Contains(sqPtr) || !_squadHeartbeats.TryGetValue(sqPtr, out SquadHeartbeatState hb))
                        continue;
                    if (hb.LastHeartbeatTime > 0f && (now - hb.LastHeartbeatTime) > 120.0f)
                    {
                        _squadHeartbeats.Remove(sqPtr);
                        continue;
                    }
                    if (hb.HadRadioLink && !hb.MiaStamped && hb.LastHeartbeatTime > 0f && (now - hb.LastHeartbeatTime) >= 18.0f && !string.IsNullOrEmpty(hb.Faction))
                    {
                        int sIdx = ResolveSideIndex(hb.Faction);
                        GetRing(sIdx).StampMiaCommsTimeout(hb.LastKnownPos, PtrToKey(sqPtr), 0.78f, now);
                        hb.MiaStamped = true;
                        _squadHeartbeats[sqPtr] = hb;
                        _miaTimeoutsCount++;
                        sideMia[sIdx]++;
                        sideLastMiaPos[sIdx] = hb.LastKnownPos;
                        sideHasMiaPos[sIdx] = true;
                        if (VisionConfig.EnableEmergentDebugTelemetry.Value)
                        {
                            AiCore.EnqueueAsyncLog(false, $"[HARMMIA] Side={sIdx} wiped squad 0x{sqPtr.ToInt64():X} silent >18s; stamped MIA Harm @ {hb.LastKnownPos:F0}");
                        }
                    }
                }
            }

            // Detect live artillery/tanks/aircraft from WorldSoldiers mounted crews (zero-alloc)
            bool[] sideHasArty = { false, false };
            bool[] sideHasTank = { false, false };
            bool[] sideHasPlane = { false, false };
            int solCount = AiCore.WorldSoldierCount;
            var worldSoldiers = AiCore.WorldSoldiers;
            for (int si = 0; si < solCount; si++)
            {
                ref AiCore.SoldierSnapshot sSnap = ref worldSoldiers[si];
                if (!sSnap.IsOnVehicle || sSnap.Ref == null || string.IsNullOrEmpty(sSnap.Faction))
                    continue;
                int vs = ResolveSideIndex(sSnap.Faction);
                Vehicle v = sSnap.Ref.GetCurrentVehicle();
                if (v == null || v.Pointer == IntPtr.Zero || v.IsDisabled() || v.IsCoverDestroyed())
                    continue;
                if (v.IsAirVehicle())
                {
                    sideHasPlane[vs] = true;
                }
                else if (!v.IsStatic())
                {
                    if (!sideHasTank[vs] && VeteranAndTacticsPatches.GetVehicleMaxHullArmorMm(v) >= 20f)
                        sideHasTank[vs] = true;
                }
                if (!sideHasArty[vs])
                {
                    Turret mt = v.GetMainTurret(false);
                    if (mt != null && (mt.IsArtillery() || mt.IsUpIndirectFire()))
                        sideHasArty[vs] = true;
                }
            }

            // Evaluate Tier 3 Net per side (Frontline Peak/Hot Heat prevents rear-squad dilution!)
            float[] sideFrontlineHeat = { 0f, 0f };
            for (int sIdx = 0; sIdx < 2; sIdx++)
            {
                if (sideActive[sIdx] <= 0 || string.IsNullOrEmpty(sideFaction[sIdx]))
                    continue;

                var prof = VeteranAndTacticsPatches.GetFactionCommandProfile(sideFaction[sIdx]);
                float frontlineHeat = sideHotSquads[sIdx] > 0
                    ? Mathf.Clamp01(Mathf.Max(sidePeakHeat[sIdx] * 0.72f, (sideHotHeatSum[sIdx] / sideHotSquads[sIdx]) * 0.85f))
                    : Mathf.Clamp01(sideHeat[sIdx] / Mathf.Max(1, sideActive[sIdx]));
                sideFrontlineHeat[sIdx] = frontlineHeat;
                float avgRelief = Mathf.Clamp01(sideRelief[sIdx] / Mathf.Max(1, sideActive[sIdx]));

                var t3Inp = new EmergentEngine.Tier3NetInput
                {
                    Now = now,
                    ActiveSquads = sideActive[sIdx],
                    BalkingSquads = sideBalking[sIdx],
                    MiaSilentTimeouts = sideMia[sIdx],
                    LaneHarmHeat = frontlineHeat,
                    EnemyRelief = avgRelief,
                    TicketRatio01 = sideMinTickets[sIdx],
                    CriticalObjectiveThreat01 = Mathf.Max(sideMaxDefCap[sIdx], sideBalking[sIdx] > 0 ? 0.55f : 0.25f),
                    SmokeAvailable = sideBalking[sIdx] > 0 || frontlineHeat >= 0.42f,
                    ArtilleryAvailable = sideHasArty[sIdx],
                    TanksAvailable = sideHasTank[sIdx] || sideArmorBlock[sIdx],
                    CasAvailable = sideHasPlane[sIdx],
                    AtAssetsAvailable = sideHasAt[sIdx],
                    InfantryBlockedByArmor = sideArmorBlock[sIdx],
                    TankNeedsInfantryScreen = sideTankNeedsScreen[sIdx],
                    AlternativeFlankOpen = sideHasFlankOpen[sIdx] || sideActive[sIdx] >= 2,
                    FreshSpawnNeedsStaging = frontlineHeat >= 0.45f,
                    HasCollapsingFriendlyCap = sideCollapsingCap[sIdx],
                    HasShreddedEncircledSquad = sideBalking[sIdx] >= 2
                };

                var t3Dec = EmergentEngine.EvaluateTier3Net(in t3Inp, in prof);
                if (sIdx == 0) _side0Desperation = t3Dec.SectorDesperationBias;
                else _side1Desperation = t3Dec.SectorDesperationBias;

                EmergentEngine.Tier3NetLever prevLev = sIdx == 0 ? _lastSide0Lever : _lastSide1Lever;
                if (t3Dec.PrimaryLever != prevLev)
                {
                    if (sIdx == 0) _lastSide0Lever = t3Dec.PrimaryLever;
                    else _lastSide1Lever = t3Dec.PrimaryLever;
                    _tier3LeversCount++;
                    BotActionTrace.RecordTier3Lever(
                        sIdx,
                        sideFaction[sIdx],
                        t3Dec.PrimaryLever,
                        t3Dec.Reason,
                        t3Dec.NetHarm,
                        t3Dec.NetWin,
                        sideBalking[sIdx],
                        sideMia[sIdx],
                        t3Dec.SectorDesperationBias,
                        now);
                    if (VisionConfig.EnableEmergentDebugTelemetry.Value)
                    {
                        AiCore.EnqueueAsyncLog(false, $"[HARMT3] Side={sIdx}({sideFaction[sIdx]}) Lever={t3Dec.PrimaryLever} ({t3Dec.Reason}) NetH={t3Dec.NetHarm:F2} NetW={t3Dec.NetWin:F2} Balk={sideBalking[sIdx]} MIA={sideMia[sIdx]} Desp={t3Dec.SectorDesperationBias:F2}");
                    }
                    VeteranAndTacticsPatches.NotifyPlayerSquadLeaderOfTier3Shift(sideFaction[sIdx], t3Dec.PrimaryLever, sideLastMiaPos[sIdx], sideHasMiaPos[sIdx], now);
                }
            }

            VeteranAndTacticsPatches.ExecuteEmergentTeamNetManeuvers(
                _reusableConnectedInfSquads,
                _reusableActiveVehAnvils,
                _lastSide0Lever,
                _lastSide1Lever,
                now,
                ref issuedSquadCoverOrderThisTick);
            _reusableConnectedInfSquads.Clear();
            _reusableActiveVehAnvils.Clear();

            _lastPulseS0Act = sideActive[0];
            _lastPulseS1Act = sideActive[1];
            _lastPulseS0Tkt = sideMinTickets[0];
            _lastPulseS1Tkt = sideMinTickets[1];
            _lastPulseS0Balk = sideBalking[0];
            _lastPulseS1Balk = sideBalking[1];
            _lastPulseS0Mia = sideMia[0];
            _lastPulseS1Mia = sideMia[1];
            _lastPulseS0Heat = sideFrontlineHeat[0];
            _lastPulseS1Heat = sideFrontlineHeat[1];

            if ((sideActive[0] + sideActive[1]) > 0 && now >= _nextMatchPulseTime)
            {
                _nextMatchPulseTime = now + 60.0f;
                BotActionTrace.RecordMatchPulse(
                    "60s",
                    _lastPulseS0Act,
                    _lastPulseS1Act,
                    _lastPulseS0Tkt,
                    _lastPulseS1Tkt,
                    _lastPulseS0Balk,
                    _lastPulseS1Balk,
                    _lastPulseS0Mia,
                    _lastPulseS1Mia,
                    _lastPulseS0Heat,
                    _lastPulseS1Heat,
                    _lastSide0Lever,
                    _lastSide1Lever,
                    _totalT1Decisions,
                    _thrashWatchdogCount,
                    now);
            }
        }

        private static float _nextHumanShadowSampleTime;
        private static Vector3 _lastHumanShadowPos;
        private static float _lastHumanShadowPosTime;
        private static float _humanSmoothedHarm;
        private static EmergentEngine.EmergentDecision _lastHumanHarmDec;

        public static void TickPeriodicTelemetry(float now)
        {
            TickHumanShadowSampler(now);

            if (VisionConfig.EnableEmergentDecisionEngine == null ||
                !VisionConfig.EnableEmergentDecisionEngine.Value ||
                VisionConfig.EnableEmergentDebugTelemetry == null ||
                !VisionConfig.EnableEmergentDebugTelemetry.Value)
            {
                return;
            }

            if (now >= _nextPeriodicTelemetryTime)
            {
                _nextPeriodicTelemetryTime = now + 30.0f;
                if (_totalT1Decisions > 0)
                    EmitTelemetrySummary("30s");
            }
        }

        private static void TickHumanShadowSampler(float now)
        {
            if (VisionConfig.EnableMod == null || !VisionConfig.EnableMod.Value ||
                VisionConfig.EnableBotActionTrace == null || !VisionConfig.EnableBotActionTrace.Value ||
                VisionConfig.EnableEmergentDecisionEngine == null || !VisionConfig.EnableEmergentDecisionEngine.Value)
            {
                return;
            }

            if (now < _nextHumanShadowSampleTime)
                return;
            _nextHumanShadowSampleTime = now + 0.50f;

            PlayerController pc = PlayerController.currentController;
            Soldier p = pc != null ? pc.ControlledCharacter : null;
            if (p == null || p.IsDead || p.IsOnVehicle() || p.transform == null)
                return;

            AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandles(p);
            Vector3 myPos = p.transform.position;
            float dt = Mathf.Clamp(now - _lastHumanShadowPosTime, 0.1f, 2.0f);
            Vector3 flatDelta = myPos - _lastHumanShadowPos;
            flatDelta.y = 0f;
            float flatSpeed = _lastHumanShadowPosTime > 0f ? (flatDelta.magnitude / dt) : 0f;
            Vector3 moveDir = flatDelta.sqrMagnitude > 0.01f ? flatDelta.normalized : p.transform.forward;
            _lastHumanShadowPos = myPos;
            _lastHumanShadowPosTime = now;

            string fac = AiCore.GetCachedFaction(p);
            if (string.IsNullOrEmpty(fac))
                return;

            Vector3 lookFwd = p.transform.forward;
            lookFwd.y = 0f;
            if (lookFwd.sqrMagnitude < 0.001f) lookFwd = Vector3.forward;
            else lookFwd.Normalize();

            // Find nearest visible enemy in player's forward 78-deg cone (max 2 raycasts/0.5s; never assume un-raycasted LOS)
            bool hasVisEnemy = false;
            float visEnemyDist = -1f;
            Vector3 visEnemyPos = myPos + lookFwd * 30f;
            float best1Sq = 220f * 220f;
            float best2Sq = 220f * 220f;
            Vector3 best1Pos = Vector3.zero;
            Vector3 best2Pos = Vector3.zero;
            int wsCount = AiCore.WorldSoldierCount;
            var ws = AiCore.WorldSoldiers;
            for (int i = 0; i < wsCount; i++)
            {
                if (ws[i].Ptr == IntPtr.Zero || ws[i].Ptr == p.Pointer || string.IsNullOrEmpty(ws[i].Faction) || !AiCore.AreEnemyFactions(fac, ws[i].Faction))
                    continue;
                Vector3 toEn = ws[i].Pos - myPos;
                float dSq = toEn.sqrMagnitude;
                if (dSq >= best2Sq || dSq < 0.25f)
                    continue;
                Vector3 flatToEn = toEn;
                flatToEn.y = 0f;
                if (flatToEn.sqrMagnitude > 0.01f && Vector3.Angle(lookFwd, flatToEn) > 78f)
                    continue;
                if (dSq < best1Sq)
                {
                    best2Sq = best1Sq;
                    best2Pos = best1Pos;
                    best1Sq = dSq;
                    best1Pos = ws[i].Pos;
                }
                else
                {
                    best2Sq = dSq;
                    best2Pos = ws[i].Pos;
                }
            }

            Vector3 eyeOrigin = myPos + Vector3.up * 1.45f;
            if (best1Sq < 220f * 220f)
            {
                float d1 = Mathf.Sqrt(best1Sq);
                if (!AiCore.IsDirectionBlockedByEnvironment(eyeOrigin, (best1Pos - eyeOrigin).normalized, d1 - 0.5f, p.transform.root))
                {
                    hasVisEnemy = true;
                    visEnemyDist = d1;
                    visEnemyPos = best1Pos;
                }
                else if (best2Sq < 220f * 220f)
                {
                    float d2 = Mathf.Sqrt(best2Sq);
                    if (!AiCore.IsDirectionBlockedByEnvironment(eyeOrigin, (best2Pos - eyeOrigin).normalized, d2 - 0.5f, p.transform.root))
                    {
                        hasVisEnemy = true;
                        visEnemyDist = d2;
                        visEnemyPos = best2Pos;
                    }
                }
            }

            GenericGun heldGun = p.GetHeldGun();
            string weaponClass = "Unarmed";
            if (heldGun != null)
            {
                AmmoItem ammo = heldGun.GetAmmoItem() ?? heldGun.usedAmmoItem;
                BulletData bd = ammo != null ? ammo.GetBulletData() : null;
                float dmgRange = heldGun.DamageRange();
                if (heldGun.isFlamethrower || (bd != null && bd.IsFire())) weaponClass = "Flame";
                else if (bd != null && bd.IsShotgun()) weaponClass = "Shotgun";
                else if (heldGun.IsPistol()) weaponClass = "Pistol";
                else if (heldGun.IsAtWeapon()) weaponClass = "AT";
                else if (h != null && AiCore.GetCachedOpticZoom(h, now) >= 2.2f) weaponClass = "Sniper";
                else if (p.hasMg || p.IsGunner() || p.GetHeldFunctionalBipod() != null) weaponClass = "MG";
                else if (dmgRange <= 135f) weaponClass = "SMG";
                else weaponClass = "Rifle";
            }

            float suppression = p.GetSuppressionValue();
            float rawHarmNow = Mathf.Clamp01(suppression / 68.0f);
            _humanSmoothedHarm = EmergentEngine.SmoothHarmEnvelope(_humanSmoothedHarm, rawHarmNow, dt);
            int side = ResolveSideIndex(fac);
            float projHarm = GetRing(side).EvaluateSpotProjectedHarm(
                myPos, myPos, 0L, true, false, false, EmergentEngine.EmergentTier.Individual, now,
                out int activeStamps, out _, out _);

            bool inCover = p.IsOnCover() || VeteranAndTacticsPatches.IsPositionShelteredByFrontCover(myPos, lookFwd, p.transform.root);
            bool hasBandage = p.inventory != null && p.inventory.FindItemOfType<VirtualBandages>() != null;
            bool hasSmoke = p.inventory != null && p.inventory.FindItemOfType<VirtualSmokeGrenade>() != null;
            bool hasFrag = p.inventory != null && p.inventory.FindItemOfType<VirtualThrowable>() != null;
            bool ammoEmpty = !AiCore.HasUsableSmallArmsAmmo(p);

            Squad sq = p.joinedSquad;
            SquadTask task = sq != null ? sq.currentTask : null;
            bool hasObj = task != null;
            Vector3 objPos = hasObj ? task.GetTaskDestination() : myPos;
            float objDist = hasObj ? Vector3.Distance(myPos, objPos) : 0f;
            bool insideObj = hasObj && task.IsInsideTaskArea(myPos, 1.0f);

            float canHarm = 0f;
            if (hasVisEnemy)
            {
                bool isGunner = weaponClass == "MG";
                canHarm = (ammoEmpty ? 0.10f : 0.90f) * ComputeWeaponEffectiveRangeWeight(
                    p, h, heldGun, isGunner, VeteranAndTacticsPatches.SoldierPersonality.Rifleman_Regular, visEnemyDist, now);
            }

            var inp = new EmergentEngine.EmergentInput
            {
                Now = now,
                MyPos = myPos,
                IsLeader = p.IsSquadLeader(),
                HasRadioLink = true,
                SmoothedHarmNow = _humanSmoothedHarm,
                ProjectedHarm = projHarm,
                RawHarmNow = rawHarmNow,
                ActiveDangerStamps = activeStamps,
                InCover = inCover,
                IsBleeding = p.isBleeding,
                HasBandage = hasBandage,
                HasSmokeGrenade = hasSmoke,
                HasFragGrenade = hasFrag,
                AmmoEmpty = ammoEmpty,
                HasVisibleEnemy = hasVisEnemy,
                VisibleEnemyDist = visEnemyDist,
                CanHarmTarget = canHarm,
                CoverAvailable = true,
                HasObjectiveTask = hasObj,
                InsideObjectiveZone = insideObj,
                ObjectiveDist = objDist,
                IsHomeGroundDefender = AiCore.IsAsymmetricDefender(fac)
            };

            var neutralW = new EmergentEngine.EmergentWeights
            {
                WSurvive = 1.15f,
                WWin = 1.00f,
                WAid = 1.00f,
                RiskThreshold = 0.50f,
                HarmAccuracy = 0.75f,
                BiasStandoff = 1.0f,
                BiasFlank = 1.0f,
                BiasSmoke = 1.0f,
                BiasBayonet = 1.0f
            };

            EmergentEngine.EmergentDecision harmDec = EmergentEngine.EvaluateDeliberative(in inp, in neutralW, in _lastHumanHarmDec, now);
            _lastHumanHarmDec = harmDec;

            Vector3 toThreat = hasVisEnemy ? (visEnemyPos - myPos) : (hasObj ? (objPos - myPos) : lookFwd);
            toThreat.y = 0f;
            if (toThreat.sqrMagnitude > 0.01f) toThreat.Normalize();
            float dotToThreat = Vector3.Dot(moveDir, toThreat);
            Vector3 toObj = hasObj ? (objPos - myPos) : lookFwd;
            toObj.y = 0f;
            if (toObj.sqrMagnitude > 0.01f) toObj.Normalize();
            float dotToObj = Vector3.Dot(moveDir, toObj);

            string humanAction;
            if (p.isBleeding && p.isInHandsAnimation)
                humanAction = "Bandage";
            else if (p.triggerState == Soldier.TriggerState.Firing || (p.IsAiming && hasVisEnemy))
                humanAction = "Engage";
            else if (flatSpeed >= 1.2f)
            {
                if ((suppression >= 30f || p.isBleeding) && dotToThreat < -0.25f)
                    humanAction = "Fallback";
                else if ((suppression >= 25f || p.isBleeding) && inCover)
                    humanAction = "SeekCover";
                else if (hasObj && dotToObj >= 0.45f)
                    humanAction = "Advance";
                else if ((hasObj || hasVisEnemy) && Mathf.Abs(dotToThreat) < 0.55f)
                    humanAction = "Flank";
                else
                    humanAction = "Advance";
            }
            else
            {
                if ((suppression >= 30f || p.isBleeding) && (p.m_pose != SoldierPose.Idle || inCover))
                    humanAction = "SeekCover";
                else
                    humanAction = "Hold";
            }

            bool agree = false;
            switch (humanAction)
            {
                case "Bandage":
                    agree = harmDec.Step0 == EmergentEngine.EmergentActionId.SelfBandage ||
                            (harmDec.Step0 == EmergentEngine.EmergentActionId.SeekCover && harmDec.Step1 == EmergentEngine.EmergentActionId.SelfBandage);
                    break;
                case "Engage":
                    agree = harmDec.Step0 == EmergentEngine.EmergentActionId.HoldAndFight ||
                            harmDec.Step0 == EmergentEngine.EmergentActionId.SuppressPin ||
                            harmDec.Step0 == EmergentEngine.EmergentActionId.CloseBreach ||
                            harmDec.Step0 == EmergentEngine.EmergentActionId.FragFlush;
                    break;
                case "SeekCover":
                    agree = harmDec.Step0 == EmergentEngine.EmergentActionId.SeekCover ||
                            harmDec.Step0 == EmergentEngine.EmergentActionId.RepositionWallSide ||
                            harmDec.Step0 == EmergentEngine.EmergentActionId.PopSmoke ||
                            harmDec.Step0 == EmergentEngine.EmergentActionId.SelfBandage;
                    break;
                case "Advance":
                    agree = harmDec.Step0 == EmergentEngine.EmergentActionId.AdvanceObjective ||
                            harmDec.Step0 == EmergentEngine.EmergentActionId.FlankManeuver ||
                            harmDec.Step0 == EmergentEngine.EmergentActionId.CloseBreach;
                    break;
                case "Flank":
                    agree = harmDec.Step0 == EmergentEngine.EmergentActionId.FlankManeuver ||
                            harmDec.Step0 == EmergentEngine.EmergentActionId.AdvanceObjective;
                    break;
                case "Fallback":
                    agree = harmDec.Step0 == EmergentEngine.EmergentActionId.FallbackRegroup ||
                            harmDec.Step0 == EmergentEngine.EmergentActionId.SeekCover ||
                            harmDec.Step0 == EmergentEngine.EmergentActionId.PopSmoke;
                    break;
                default:
                    agree = harmDec.Step0 == EmergentEngine.EmergentActionId.HoldAndFight ||
                            harmDec.Step0 == EmergentEngine.EmergentActionId.SuppressPin ||
                            harmDec.Step0 == EmergentEngine.EmergentActionId.SeekCover;
                    break;
            }

            uint inputFlags = EmergentEngine.PackInputFlags(in inp, harmDec.UnsafePrereqRejected);
            BotActionTrace.RecordHumanShadow(
                p, h, weaponClass, humanAction, agree ? "AGREE" : "DISAGREE",
                in harmDec, activeStamps, inputFlags, suppression, visEnemyDist, inCover, objPos, now);
        }

        public static void EmitTelemetrySummary(string tag)
        {
            if (VisionConfig.EnableEmergentDecisionEngine == null ||
                !VisionConfig.EnableEmergentDecisionEngine.Value)
            {
                return;
            }

            if (tag == "match_end")
            {
                float now = 0f;
                try { now = Time.time; } catch { }
                BotActionTrace.RecordMatchPulse(
                    "match_end",
                    _lastPulseS0Act,
                    _lastPulseS1Act,
                    _lastPulseS0Tkt,
                    _lastPulseS1Tkt,
                    _lastPulseS0Balk,
                    _lastPulseS1Balk,
                    _lastPulseS0Mia,
                    _lastPulseS1Mia,
                    _lastPulseS0Heat,
                    _lastPulseS1Heat,
                    _lastSide0Lever,
                    _lastSide1Lever,
                    _totalT1Decisions,
                    _thrashWatchdogCount,
                    now);
            }

            if (VisionConfig.EnableEmergentDebugTelemetry == null ||
                !VisionConfig.EnableEmergentDebugTelemetry.Value)
            {
                return;
            }

            AiCore.EnqueueAsyncLog(
                false,
                $"[HARMTelemetry:{tag}] T1Decs:{_totalT1Decisions} (Cover:{_actionCounts[(int)EmergentEngine.EmergentActionId.SeekCover]} Engage:{_actionCounts[(int)EmergentEngine.EmergentActionId.HoldAndFight]} Adv:{_actionCounts[(int)EmergentEngine.EmergentActionId.AdvanceObjective]} Flank:{_actionCounts[(int)EmergentEngine.EmergentActionId.FlankManeuver]} Smoke:{_actionCounts[(int)EmergentEngine.EmergentActionId.PopSmoke]} Bnd:{_actionCounts[(int)EmergentEngine.EmergentActionId.SelfBandage]} Med:{_actionCounts[(int)EmergentEngine.EmergentActionId.MedicHeal]} Drag:{_actionCounts[(int)EmergentEngine.EmergentActionId.DragCasualty]} Lib:{_actionCounts[(int)EmergentEngine.EmergentActionId.LiberatePrisoner]} Ammo:{_actionCounts[(int)EmergentEngine.EmergentActionId.ResupplyAmmo] + _actionCounts[(int)EmergentEngine.EmergentActionId.PushAmmoDrop]}) | Reflex[Gren:{_reflexCounts[(int)EmergentEngine.EmergentReflexKind.LiveGrenade]} DoorCas:{_reflexCounts[(int)EmergentEngine.EmergentReflexKind.DoorwayFreshCasualty]} HeavyFire:{_reflexCounts[(int)EmergentEngine.EmergentReflexKind.SuddenPenetratingFire]} CQB:{_reflexCounts[(int)EmergentEngine.EmergentReflexKind.PointBlankAmbush]} Doom:{_reflexCounts[(int)EmergentEngine.EmergentReflexKind.HullCookoffDoom]} Acoust:{_reflexCounts[(int)EmergentEngine.EmergentReflexKind.CloseAcousticAlert]}] | Prereqs:{_prereqChainsCount}(UnsafeRej:{_unsafeChainsRejectedCount}) | TroopInt:{_troopInteractionsCount} | Air[CAS:{_airGroundRunsCount} Int:{_airInterceptsCount}] | Stamps[Danger:{_dangerStampsEmittedCount} Relief:{_enemyReliefStampsCount} MIA:{_miaTimeoutsCount}] | T2Balks:{_tier2BalksCount} T3Levers:{_tier3LeversCount} ({_lastSide0Lever}/{_lastSide1Lever}) | Thrash:{_thrashWatchdogCount}");
        }
    }

    // Internal thin facades over battle-tested executors in VeteranAndTacticsPatches (zero code duplication)
    public static partial class VeteranAndTacticsPatches
    {
        internal static bool EmergentHasRadioLink(Squad sq) => HasLiveRadioOrRelayLink(sq);
        internal static void EmergentRefreshArtilleryZones(float now) => RefreshFriendlyArtilleryZones(now);
        internal static void EmergentTriggerRadioSupport(Squad sq, Soldier leader) => TryTriggerSquadRadioSupport(sq, leader);

        internal static void EmergentHandleCarryTick(SoldierAI ai, Soldier s, IntPtr ptr, float now)
            => HandleActiveCasualtyCarryTick(ai, s, ptr, now);

        internal static bool EmergentTryUnstick(SoldierAI ai, Soldier s, IntPtr ptr, ref SquadBlackboard b, Vector3 pos, float supp, bool onBridge, float dist, float now)
            => TryRecoverChronicallyStuckFootBot(ai, s, ptr, ref b, pos, supp, onBridge, dist, b.HasActiveObjectiveTask ? b.ObjectivePosition : Vector3.zero, b.HasActiveObjectiveTask, now);

        internal static bool EmergentHasCloseLiveGrenade(Vector3 pos, string fac)
        {
            for (int i = 0; i < _activeLiveGrenades.Count; i++)
            {
                var g = _activeLiveGrenades[i];
                if ((g.Pos - pos).sqrMagnitude <= 7.5f * 7.5f)
                    return true;
            }
            return false;
        }

        internal static bool EmergentIsExposedWall(Soldier s, Squad sq, Vector3 pos, Vector3 threatDir)
            => VisionConfig.EnableSmartWallSideCover.Value &&
               IsPositionOnExposedSideOfWall(pos, GetEffectiveThreatDirection(s, sq, pos, threatDir), s.transform.root, out _);

        internal static bool EmergentHasNearbyResupplySource(
            SoldierAI ai,
            Soldier s,
            AiCore.SoldierFastHandles h,
            Squad sq,
            ref SquadBlackboard board,
            Vector3 pos,
            SoldierPersonality pers,
            float suppression,
            bool hasUsableArms,
            bool hasBandage,
            bool hasFrag,
            bool hasSmoke,
            bool hasAtCap,
            bool isBailedCrew,
            bool isEvadingArmor,
            bool exposedWall,
            bool safeLull,
            bool allowUrgentPrimary,
            bool allowUrgentAt,
            float now,
            out bool foundUrgentPrimaryUpgrade,
            out bool foundAtWeaponScavenge)
        {
            foundUrgentPrimaryUpgrade = false;
            foundAtWeaponScavenge = false;

            bool onRetreatTask = IsSquadOnRetreatTask(sq);
            bool isRetreatingBailedCrew = isBailedCrew && (onRetreatTask || AiCore.IsConfirmedPlaneCrewSquad(sq));
            bool urgentBandageWhileBleeding = s.isBleeding && !hasBandage;
            bool casualScavengeAllowed = !urgentBandageWhileBleeding && !isEvadingArmor && !isRetreatingBailedCrew;
            bool recentFootContact = h != null && h.Vision.LastKnownTarget.LastSeenTime > 0f && (now - h.Vision.LastKnownTarget.LastSeenTime) < 10.0f;
            bool inCoverDwellForCalmLoot = s.IsOnCover() &&
                                           h != null &&
                                           (now < h.Stance.CoverSettledUntil ||
                                            (h.Vision.LastKnownTarget.LastSeenTime > 0f && (now - h.Vision.LastKnownTarget.LastSeenTime) <= 3.0f)) &&
                                           !s.isBleeding &&
                                           !exposedWall &&
                                           !isEvadingArmor &&
                                           (sq == null || sq.IsInsideMoveOrderArea(pos));
            bool nonUrgentScavengeAllowed = casualScavengeAllowed && safeLull && !recentFootContact && !board.HasActiveThreat && !inCoverDwellForCalmLoot;
            bool isMedicRole = AiCore.IsMedicSoldierOrPersonality(s, pers);
            bool isAtRole = AiCore.IsAtSoldierPersonality(pers);
            bool isRad = s.IsRadioman();

            GenericGun gun0 = s.GetHeldGun(0);
            GenericGun gun1 = s.GetHeldGun(1);
            GenericGun gun2 = s.GetHeldGun(2);
            bool needAmmo = !urgentBandageWhileBleeding &&
                            !isRetreatingBailedCrew &&
                            (!hasUsableArms ||
                             (nonUrgentScavengeAllowed &&
                              ((gun0 != null && s.IsOutOfAmmosFor(gun0)) ||
                               (gun1 != null && gun1.IsAtWeapon() && !string.IsNullOrEmpty(gun1.compatibleAmmo) && s.IsOutOfAmmosFor(gun1)) ||
                               (gun2 != null && !gun2.IsPistol() && !gun2.IsAtWeapon() && s.IsOutOfAmmosFor(gun2)))));
            bool needBandage = urgentBandageWhileBleeding ||
                               (!hasBandage && ((isMedicRole && s.hasSyringe) || nonUrgentScavengeAllowed));
            bool needAtWeapon = !urgentBandageWhileBleeding &&
                                !hasAtCap &&
                                !onRetreatTask &&
                                !s.IsSquadLeader() &&
                                !isRad &&
                                !isMedicRole &&
                                (allowUrgentAt || (isAtRole && nonUrgentScavengeAllowed));

            bool needUrgentPrimary = false, wantSquadMg = false, wantRoleUpgrade = false;
            if ((nonUrgentScavengeAllowed || allowUrgentPrimary) && casualScavengeAllowed)
            {
                EvaluateRoleWeaponUpgradeNeed(s, sq, pers, isMedicRole, onRetreatTask, ref board, now, out needUrgentPrimary, out wantSquadMg, out wantRoleUpgrade);
                if (!nonUrgentScavengeAllowed)
                {
                    wantSquadMg = false;
                    wantRoleUpgrade = false;
                }
            }

            bool wantFragGrenade = nonUrgentScavengeAllowed &&
                                   !onRetreatTask &&
                                   !isMedicRole &&
                                   !hasFrag &&
                                   suppression < 16f &&
                                   s.inventory != null;

            float nextSmkScav = h != null ? h.Goap.NextSmokeGrenadeTime : 0f;
            bool wantSmokeGrenade = nonUrgentScavengeAllowed &&
                                    !onRetreatTask &&
                                    !hasSmoke &&
                                    suppression < 16f &&
                                    (now >= nextSmkScav || nextSmkScav - now > 60.0f) &&
                                    (s.IsSquadLeader() ||
                                     isMedicRole ||
                                     pers == SoldierPersonality.Assaulter_Breacher ||
                                     pers == SoldierPersonality.Assaulter_Skirmisher) &&
                                    s.inventory != null;

            bool wantRadio = nonUrgentScavengeAllowed &&
                             VisionConfig.EnableEmergentWearableScavenge.Value &&
                             !onRetreatTask &&
                             sq != null &&
                             !AiCore.IsTrueVehicleCrewSquad(sq) &&
                             !board.PreviouslyHadAliveRadioman &&
                             !sq.HasRadioman() &&
                             (!_nextSquadRadioScavengeTime.TryGetValue(sq.Pointer, out float nextRadT) || now >= nextRadT) &&
                             !IsRadioPackAlreadyClaimedForSquad(s.Pointer, sq.Pointer, now) &&
                             !isRad &&
                             !isMedicRole &&
                             (s.vest_Obj == null || !s.vest_Obj.isFuelTank);

            bool wantMedicSyringe = nonUrgentScavengeAllowed &&
                                    !onRetreatTask &&
                                    sq != null &&
                                    !AiCore.IsTrueVehicleCrewSquad(sq) &&
                                    !board.HasAliveMedic &&
                                    !s.hasSyringe &&
                                    !isRad &&
                                    !s.IsSquadLeader();

            bool wantHelmet = nonUrgentScavengeAllowed &&
                              VisionConfig.EnableEmergentWearableScavenge.Value &&
                              !onRetreatTask &&
                              !s.hasHeadgear &&
                              suppression < 12f;

            bool canFeedNeedyAlly = nonUrgentScavengeAllowed &&
                                    VisionConfig.EnableBuddyResupply.Value &&
                                    !onRetreatTask &&
                                    !needAmmo &&
                                    s.IsAmmoBoxCarrier();

            bool wantToolbox = isBailedCrew &&
                               !isRetreatingBailedCrew &&
                               !urgentBandageWhileBleeding &&
                               !isEvadingArmor &&
                               suppression < 20f &&
                               s.GetRepairTools() == null;

            bool wantAtMine = nonUrgentScavengeAllowed &&
                              VisionConfig.EnableSpecialMunitionsAndSappers.Value &&
                              !onRetreatTask &&
                              !isMedicRole &&
                              !s.IsSquadLeader() &&
                              suppression < 14f &&
                              (isAtRole || board.IsDefendingObjective) &&
                              AiCore.AnyEnemyGroundVehiclesPresent(s.faction) &&
                              !HasPressureAtMineInInventory(s);

            bool wantDeployableTripod = nonUrgentScavengeAllowed &&
                                        VisionConfig.EnableSpecialMunitionsAndSappers.Value &&
                                        !onRetreatTask &&
                                        !isMedicRole &&
                                        !s.IsSquadLeader() &&
                                        !isRad &&
                                        suppression < 12f &&
                                        s.inventory != null &&
                                        s.inventory.FindItemOfType<VirtualDeployableVehicle>() == null;

            bool wantShellAmmo = nonUrgentScavengeAllowed &&
                                 !onRetreatTask &&
                                 suppression < 14f &&
                                 h != null &&
                                 AiCore.HasHeldMortarCached(h, ai, now);

            bool wantRecoverLife = nonUrgentScavengeAllowed &&
                                   !onRetreatTask &&
                                   suppression < 6f &&
                                   s.IsAlive &&
                                   s.life_total.Value > 0 &&
                                   s.life_total.Value < 85;

            if ((needAmmo || needBandage || wantFragGrenade || wantSmokeGrenade || needAtWeapon ||
                 needUrgentPrimary || wantSquadMg || wantRoleUpgrade || wantRadio || wantMedicSyringe ||
                 wantHelmet || canFeedNeedyAlly || wantToolbox || wantAtMine || wantDeployableTripod ||
                 wantShellAmmo || wantRecoverLife) &&
                TryGetNearbyScavengeTarget(
                    s,
                    pos,
                    pers,
                    needAmmo,
                    needBandage,
                    wantFragGrenade,
                    wantSmokeGrenade,
                    needAtWeapon,
                    needUrgentPrimary,
                    wantSquadMg,
                    wantRoleUpgrade,
                    wantRadio,
                    wantMedicSyringe,
                    wantHelmet,
                    canFeedNeedyAlly,
                    wantToolbox,
                    wantAtMine,
                    wantDeployableTripod,
                    wantShellAmmo,
                    wantRecoverLife,
                    now,
                    out BotScavengeCache scavRes))
            {
                AiCore.SoldierFastHandles sh = h ?? AiCore.GetOrCreateSoldierHandles(s);
                foundUrgentPrimaryUpgrade = needUrgentPrimary &&
                                            (scavRes.IsUrgentPrimaryPickup || (sh != null && sh.Goap.Scavenge.IsUrgentPrimaryPickup));
                foundAtWeaponScavenge = needAtWeapon &&
                                        (scavRes.IsAtWeaponPickup || (sh != null && sh.Goap.Scavenge.IsAtWeaponPickup));
                return true;
            }
            if (!needAmmo)
                return false;
            string sFac = AiCore.GetCachedFaction(s);
            if (string.IsNullOrEmpty(sFac))
                return false;
            for (int i = 0; i < AiCore.WorldSoldierCount; i++)
            {
                ref var snap = ref AiCore.WorldSoldiers[i];
                if (snap.Ptr != s.Pointer &&
                    snap.IsAmmoCarrier &&
                    snap.Ref != null &&
                    AiCore.AreSameSideFactions(sFac, snap.Faction) &&
                    (snap.Pos - pos).sqrMagnitude <= 18f * 18f)
                {
                    AiCore.SoldierFastHandles sh = AiCore.GetOrCreateSoldierHandles(s);
                    if (sh != null)
                    {
                        sh.Goap.Scavenge = new VeteranAndTacticsPatches.BotScavengeCache
                        {
                            TargetBuddyDonor = snap.Ref,
                            IsBuddyAmmoBorrow = true,
                            NeedAmmo = true,
                            TargetPosition = snap.Pos
                        };
                    }
                    return true;
                }
            }
            return false;
        }

        internal static void EmergentRunGrenadeReflex(SoldierAI ai, Soldier s, AiCore.SoldierFastHandles h, SoldierPersonality pers, float vet01, bool onBridge, float now)
            => TryHandleLiveGrenadeReflex(ai, s, h, pers, vet01, onBridge, now);

        internal static bool EmergentTrySpecialMunitions(SoldierAI ai, Soldier s, IntPtr ptr, Squad sq, ref SquadBlackboard b, Vector3 pos, float supp, float dist, bool onBridge, float now)
            => TryExecuteSpecialMunitionsAndTripods(ai, s, ptr, sq, ref b, pos, supp, dist, onBridge, now);

        internal static bool EmergentIsBailedCrewPrioritizingScavenge(Soldier s, Squad sq, bool hasScavengeOpportunity, bool safeForScavenge, float now)
            => IsBailedCrewPrioritizingScavenge(s, sq, hasScavengeOpportunity, safeForScavenge, now);

        internal static void EmergentRunBailedCrewOrLoneWolf(SoldierAI ai, Soldier s, Squad sq, IntPtr ptr, AiCore.SoldierFastHandles h, ref SquadBlackboard b, ref BotGoapPlan p, bool isOnBridge, float supp, float dist, Vector3 pos, float now)
            => ExecuteGoapLoneWolfLinkUpOrAmbush(ai, s, sq, ptr, h, ref b, ref p, isOnBridge, supp, dist, pos, now);

        internal static bool EmergentTryCloseAcousticInvestigate(SoldierAI ai, Soldier s, AiCore.SoldierFastHandles h, Squad sq, ref SquadBlackboard b, bool isBusy, float supp, SoldierPersonality pers, float now)
            => TryExecuteCloseAcousticInvestigate(ai, s, h, sq, ref b, isBusy, supp, pers, now);

        internal static void EmergentCheckChargeAbort(Squad sq, Soldier leader, ref SquadBlackboard b, float now)
            => CheckAndAbortSuicidalAiCharge(sq, leader, ref b, now);

        internal static void EmergentRunSeekCover(SoldierAI ai, Soldier s, Squad sq, AiCore.SoldierFastHandles h, ref SquadBlackboard b, ref BotGoapPlan p, float supp, Vector3 pos, bool isOnBridge, float rawHarmNow, bool armorThreatClose, float now)
            => ExecuteGoapSeekEmergencyCover(
                ai, s, sq, h, ref b, ref p,
                isOnBridge,
                rawHarmNow >= 0.45f,
                armorThreatClose,
                supp, pos, now);

        internal static void EmergentRunWallSide(SoldierAI ai, Soldier s, Squad sq, AiCore.SoldierFastHandles h, ref SquadBlackboard b, ref BotGoapPlan p, Vector3 pos, float now)
        {
            Vector3 wDir = GetEffectiveThreatDirection(s, sq, pos, b.ThreatDirection);
            if (TryFindShelteredSideOfWall(pos, wDir, s.transform.root, out Vector3 shPos))
            {
                AiCore.AssignBotCoverOrDestination(ai, s, new DestinationWithoutCover(shPos, SoldierPose.Crouch).Cast<AiDestination>());
                p.LockUnitCoverChange = true;
                p.HasMoveOverride = (shPos - pos).sqrMagnitude > 3.2f;
                p.MoveOverrideDest = shPos;
            }
            else
            {
                TrySeekNearbyCover(ai, s);
            }
        }

        internal static void EmergentRunSmoke(SoldierAI ai, Soldier s, Squad sq, IntPtr ptr, AiCore.SoldierFastHandles h, ref SquadBlackboard b, ref BotGoapPlan p, float supp, float dist, Vector3 pos, float now)
            => ExecuteGoapThrowSmokeScreen(ai, s, sq, ptr, h, ref b, ref p, supp, dist, pos, now);

        internal static void EmergentRunSuppress(SoldierAI ai, Soldier s, Squad sq, IntPtr ptr, AiCore.SoldierFastHandles h, ref SquadBlackboard b, ref BotGoapPlan p, float dist, Vector3 pos, float now)
            => ExecuteGoapHoldOverwatchOrSuppress(ai, s, sq, ptr, h, ref b, ref p, dist, pos, now);

        internal static void EmergentRunFlank(SoldierAI ai, Soldier s, Squad sq, IntPtr ptr, AiCore.SoldierFastHandles h, ref SquadBlackboard b, ref BotGoapPlan p, float supp, Vector3 pos, float now)
            => ExecuteGoapMoveToFlankCover(ai, s, sq, ptr, h, ref b, ref p, supp, pos, now);

        internal static void EmergentRunFrag(SoldierAI ai, Soldier s, IntPtr ptr, AiCore.SoldierFastHandles h, ref SquadBlackboard b, ref BotGoapPlan p, Vector3 pos, float now)
            => ExecuteGoapFlushThreatWithFrag(ai, s, ptr, h, ref b, ref p, pos, now);

        internal static void EmergentRunCqb(SoldierAI ai, Soldier s, AiCore.SoldierFastHandles h, float dist, float now)
            => ExecuteSoldierPointBlankCqbStep(ai, s, h, dist, now);

        internal static void EmergentRunFallback(SoldierAI ai, Soldier s, Squad sq, AiCore.SoldierFastHandles h, ref SquadBlackboard b, ref BotGoapPlan p, float dist, Vector3 pos, float now)
            => ExecuteGoapTacticalFallbackRetreat(ai, s, sq, h, ref b, ref p, dist, pos, now);

        internal static void EmergentRunScavenge(SoldierAI ai, Soldier s, Squad sq, AiCore.SoldierFastHandles h, ref BotGoapPlan p, float supp, Vector3 pos, float now)
            => ExecuteGoapScavengeSupplyFromCorpse(ai, s, sq, h, ref p, supp, pos, now);

        internal static void EmergentRunDrag(SoldierAI ai, Soldier s, Squad sq, ref SquadBlackboard b, Vector3 pos, float now)
            => TryExecuteBuddyCasualtyDrag(ai, s, sq, ref b, pos, now);

        internal static void EmergentRunLiberate(SoldierAI ai, Soldier s, AiCore.SoldierFastHandles h, Soldier targetSurr, Vector3 pos, float now)
            => TryExecuteComradeLiberationStep(ai, s, h, targetSurr, (targetSurr.transform.position - pos).sqrMagnitude, 28f * 28f, 8.5f, !s.IsSquadLeader(), now, out _);

        internal static void EmergentRunBound(SoldierAI ai, Soldier s, Squad sq, Vector3 pos, Vector3 targetPos, Vector3 dir, float now, ref BotGoapPlan p)
            => TryAdvanceBoundTowardThreat(ai, s, sq, pos, targetPos, dir, now, ref p);

        internal static void EmergentDetachCover(Soldier s)
            => DetachStaleFootCoverForEvacuation(s);
    }
}

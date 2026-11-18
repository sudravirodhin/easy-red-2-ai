using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EasyRed2HumanAI
{
    public static partial class VeteranAndTacticsPatches
    {
        // =====================================================================
        // Point-Blank Bayonet, Melee & Butt-Stroke CQB Combat
        // =====================================================================
        private static void TryEnsureBayonetMounted(Soldier soldier)
        {
            if (!AiCore.IsLocallyOwnedBot(soldier) ||
                soldier.IsOnVehicle() ||
                soldier.HasBayonetReady ||
                soldier.isInHandsAnimation ||
                soldier.IsReloading)
            {
                return;
            }

            AiCore.SoldierFastHandles bayH = AiCore.GetOrCreateSoldierHandles(soldier);
            if (bayH == null || bayH.Combat.BayonetChecked)
                return;
            bayH.Combat.BayonetChecked = true;

            GenericGun heldGun = soldier.GetHeldGun();
            if (heldGun == null)
                return;

            if (heldGun.integratedBayonet != null && !heldGun.integratedBayonet.IsMounted)
            {
                heldGun.integratedBayonet.SetMounted(true);
            }
        }

        private static bool TryClaimMeleeTargetSlot(Soldier attacker, Soldier target, float now, out int slotIndex)
        {
            slotIndex = 0;
            if (attacker == null || target == null || attacker.Pointer == IntPtr.Zero || target.Pointer == IntPtr.Zero)
                return false;

            IntPtr atkPtr = attacker.Pointer;
            IntPtr tgtPtr = target.Pointer;
            const float slotHoldSeconds = 0.90f;

            _meleeTargetSlots.TryGetValue(tgtPtr, out MeleeTargetSlotState state);

            if (state.Attacker0 == atkPtr)
            {
                state.Expire0 = now + slotHoldSeconds;
                _meleeTargetSlots[tgtPtr] = state;
                slotIndex = 0;
                return true;
            }

            if (state.Attacker1 == atkPtr)
            {
                state.Expire1 = now + slotHoldSeconds;
                _meleeTargetSlots[tgtPtr] = state;
                slotIndex = 1;
                return true;
            }

            if (state.Attacker0 == IntPtr.Zero || now >= state.Expire0)
            {
                state.Attacker0 = atkPtr;
                state.Expire0 = now + slotHoldSeconds;
                _meleeTargetSlots[tgtPtr] = state;
                slotIndex = 0;
                return true;
            }

            if (state.Attacker1 == IntPtr.Zero || now >= state.Expire1)
            {
                state.Attacker1 = atkPtr;
                state.Expire1 = now + slotHoldSeconds;
                _meleeTargetSlots[tgtPtr] = state;
                slotIndex = 1;
                return true;
            }

            return false;
        }

        private static SoldierPose GetDesiredMeleePose(Soldier enemy, float dist)
        {
            bool enemyIsLow = enemy != null &&
                              (enemy.m_pose == SoldierPose.Prone || enemy.m_pose == SoldierPose.Crouch);
            return (enemyIsLow && dist <= 2.75f) ? SoldierPose.Crouch : SoldierPose.Idle;
        }

        private static Vector3 GetMeleeApproachPosition(Soldier attacker, Soldier enemy, int slotIndex, float standoffDist)
        {
            Vector3 enemyPos = enemy.transform.position;
            Vector3 attackerPos = attacker.transform.position;
            Vector3 fromEnemy = attackerPos - enemyPos;
            fromEnemy.y = 0f;
            if (fromEnemy.sqrMagnitude < 0.04f)
            {
                fromEnemy = -attacker.transform.forward;
                fromEnemy.y = 0f;
            }
            fromEnemy = fromEnemy.sqrMagnitude > 0.001f ? fromEnemy.normalized : Vector3.back;

            // Primary attacker (Slot 0) approaches straight along the line of contact to standoffDist (0-deg offset)
            // so he never spirals in a tangential orbit or walks onto the enemy's center capsule!
            if (slotIndex == 0)
                return enemyPos + fromEnemy * standoffDist;

            // Secondary attacker (Slot 1) anchors +-65 deg off Attacker 0's bearing (NOT Slot 1's own live bearing),
            // creating a stable world-anchored 2-man CQB pincer that never orbits or overlaps Slot 0.
            if (_meleeTargetSlots.TryGetValue(enemy.Pointer, out MeleeTargetSlotState slotState) &&
                slotState.Attacker0 != IntPtr.Zero &&
                slotState.Attacker0 != attacker.Pointer &&
                AiCore.TryGetSoldierHandles(slotState.Attacker0, out AiCore.SoldierFastHandles atk0H) &&
                atk0H != null &&
                atk0H.Tr != null)
            {
                Vector3 baseDir = atk0H.Tr.position - enemyPos;
                baseDir.y = 0f;
                if (baseDir.sqrMagnitude > 0.04f)
                {
                    baseDir.Normalize();
                    const float cos65 = 0.42262f;
                    const float sin65 = 0.90631f;
                    Vector3 flankLeft = new Vector3(
                        baseDir.x * cos65 - baseDir.z * sin65,
                        0f,
                        baseDir.x * sin65 + baseDir.z * cos65);
                    Vector3 flankRight = new Vector3(
                        baseDir.x * cos65 + baseDir.z * sin65,
                        0f,
                        -baseDir.x * sin65 + baseDir.z * cos65);

                    bool preferLeft = Vector3.Dot(flankLeft, fromEnemy) >= Vector3.Dot(flankRight, fromEnemy);
                    Vector3 primaryFlank = preferLeft ? flankLeft : flankRight;
                    Vector3 altFlank = preferLeft ? flankRight : flankLeft;
                    Transform enemyRoot = enemy.transform.root;
                    Vector3 probeOrigin = enemyPos + Vector3.up * 0.85f;

                    if (!AiCore.IsDirectionBlockedByEnvironment(probeOrigin, primaryFlank, standoffDist + 0.25f, enemyRoot))
                        return enemyPos + primaryFlank * standoffDist;
                    if (!AiCore.IsDirectionBlockedByEnvironment(probeOrigin, altFlank, standoffDist + 0.25f, enemyRoot))
                        return enemyPos + altFlank * standoffDist;
                }
            }

            return enemyPos + fromEnemy * standoffDist;
        }

        private static void UpdatePointBlankMeleeEngagement(
            SoldierAI ai,
            Soldier soldier,
            AiCore.SoldierFastHandles h,
            Soldier meleeEnemy,
            float dist,
            float strikeRange,
            int meleeSlot,
            float now,
            bool isPerFrameHook)
        {
            AiCore.SoldierFastHandles ch = h ?? AiCore.GetOrCreateSoldierHandles(soldier);
            SoldierPose desiredPose = GetDesiredMeleePose(meleeEnemy, dist);
            if (ch != null)
            {
                ch.Stance.NoProneUntil = now + 2.5f;
                if (desiredPose == SoldierPose.Crouch)
                    ch.Stance.CombatCrouchLockUntil = now + 1.0f;
            }

            if (soldier.m_pose != desiredPose || ai.actualPose != desiredPose)
            {
                SetBotPose(ai, soldier, desiredPose, true);
            }

            // Maintain clean weapon-reach standoff (1.35m bayonet / 1.15m knife or rifle butt) outside capsule
            // collision diameter (~0.90m) so combatants never stack on top of each other or whip yaw in circles.
            float standoffDist = soldier.HasBayonetReady ? 1.35f : 1.15f;
            const float minPersonalSpace = 0.94f;

            if (!soldier.isInBodyAnimation)
            {
                if (dist > standoffDist + 0.12f)
                {
                    if (!isPerFrameHook || ch == null || now >= ch.Combat.NextMeleeStepTime)
                    {
                        if (ch != null)
                            ch.Combat.NextMeleeStepTime = now + 0.18f;
                        Vector3 approachPos = GetMeleeApproachPosition(soldier, meleeEnemy, meleeSlot, standoffDist);
                        AiCore.MoveBotTowardPosition(ai, soldier, approachPos, 0f, isPerFrameHook ? 0.55f : 0.85f);
                        ai.moveLookingTarget = true;
                    }
                }
                else if (dist < minPersonalSpace)
                {
                    Vector3 enemyPos = meleeEnemy.transform.position;
                    Vector3 myPos = (h != null && h.Tr != null) ? h.Tr.position : soldier.transform.position;
                    Vector3 away = myPos - enemyPos;
                    away.y = 0f;
                    if (away.sqrMagnitude < 0.01f)
                    {
                        away = -soldier.transform.forward;
                        away.y = 0f;
                    }
                    away = away.sqrMagnitude > 0.001f ? away.normalized : Vector3.back;
                    Transform myRoot = (h != null && h.RootTr != null) ? h.RootTr : soldier.transform.root;

                    if (!AiCore.IsMovementBlockedByObstacle(myPos + Vector3.up * 0.48f, away, standoffDist + 0.25f))
                    {
                        if (!isPerFrameHook || ch == null || now >= ch.Combat.NextMeleeStepTime)
                        {
                            if (ch != null)
                                ch.Combat.NextMeleeStepTime = now + 0.18f;
                            AiCore.MoveBotTowardPosition(ai, soldier, enemyPos + away * standoffDist, 0f, 0.45f);
                            ai.moveLookingTarget = true;
                        }
                    }
                    else if (ai.IsMoving())
                    {
                        ai.Stop(true);
                    }
                }
                else
                {
                    if (ai.moveDirectlyToward_end > now)
                        ai.moveDirectlyToward_end = 0f;
                    if (ai.IsMoving())
                        ai.Stop(true);
                }
            }

            if (dist <= strikeRange)
            {
                TryExecuteMeleeAttack(ai, soldier, meleeEnemy);
            }
        }

        // Combat-safety gate preventing bleeding soldiers from holstering their weapon to bandage
        // while an enemy is at close quarters, actively engaging them with LOF, or firing right next to them.
        internal static bool CanSafelyBandageNow(
            SoldierAI ai,
            Soldier soldier,
            float now,
            out string blockReason,
            out float gunfireDist)
        {
            blockReason = null;
            gunfireDist = -1f;
            if (soldier == null)
                return false;

            AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandles(soldier);
            if (h == null || h.Tr == null)
                return true;

            bool hasVisibleTarget = ai != null && ai.visibleTarget != null;
            float enemyDist = hasVisibleTarget ? ai.distFromTarget : 999f;

            // Never pull out a bandage within melee / point-blank reach of a visible enemy, even if dry on ammo!
            if (hasVisibleTarget && enemyDist <= 4.5f)
            {
                blockReason = "enemy_melee_range";
                return false;
            }

            bool canFightWithGun = AiCore.HasUsableSmallArmsAmmo(soldier);
            Vector3 myPos = h.Tr.position;
            string myFac = AiCore.GetCachedFaction(h, now);
            bool hasNearGunfire = VisionPatches.TryGetNearestEnemyGunfire(myPos, myFac, 18.0f, 3.0f, out gunfireDist);

            if (!canFightWithGun)
            {
                // Out of ammo: only block bandaging if still standing in the open under point-blank gunfire
                if (!soldier.IsOnCover() && hasNearGunfire && gunfireDist <= 10.5f)
                {
                    blockReason = "unarmed_exposed_gunfire";
                    return false;
                }
                return true;
            }

            if (hasVisibleTarget && enemyDist <= 10.5f)
            {
                blockReason = "enemy_very_close";
                return false;
            }

            bool tuckedInLowSuppCover = soldier.IsOnCover() && soldier.GetSuppressionValue() < 24.0f;
            float closeLofGate = tuckedInLowSuppCover ? 18.0f : 26.0f;
            if (ai != null && ai.HasEnemyAndCanFireAtHim && enemyDist <= closeLofGate)
            {
                blockReason = "enemy_lof_close";
                return false;
            }

            if (hasNearGunfire)
            {
                if (gunfireDist <= 10.5f)
                {
                    blockReason = "gunfire_point_blank";
                    return false;
                }

                // Between 10.5m and 18m: safe ONLY if tucked in cover with low active suppression (and no sub-18m visible shooter)
                if (!tuckedInLowSuppCover || (hasVisibleTarget && enemyDist <= 18.0f))
                {
                    blockReason = "gunfire_exposed";
                    return false;
                }
            }

            if (!soldier.IsOnCover() && soldier.GetSuppressionValue() >= 35f && hasVisibleTarget && enemyDist <= 45.0f)
            {
                blockReason = "suppressed_in_open";
                return false;
            }

            return true;
        }

        internal static void EvaluateSoldierBandageStateForTick(
            SoldierAI ai,
            Soldier soldier,
            AiCore.SoldierFastHandles h,
            float now,
            out bool isBleedingWithBandage,
            out bool canSafelyBandage,
            out string blockReason,
            out float gunfireDist)
        {
            blockReason = null;
            gunfireDist = -1f;
            if (soldier == null || !soldier.isBleeding)
            {
                isBleedingWithBandage = false;
                canSafelyBandage = false;
                return;
            }

            int frame = Time.frameCount;
            if (h != null && frame > 0 && h.Stance.LastBandageEvalFrame == frame)
            {
                isBleedingWithBandage = h.Stance.CachedHasBandage;
                canSafelyBandage = h.Stance.CachedCanSafelyBandage;
                blockReason = h.Stance.CachedBandageBlockReason;
                gunfireDist = h.Stance.CachedBandageGunfireDist;
                return;
            }

            isBleedingWithBandage = soldier.inventory != null &&
                                    soldier.inventory.FindItemOfType<VirtualBandages>() != null;
            canSafelyBandage = isBleedingWithBandage && CanSafelyBandageNow(ai, soldier, now, out blockReason, out gunfireDist);

            if (h != null && frame > 0)
            {
                h.Stance.LastBandageEvalFrame = frame;
                h.Stance.CachedHasBandage = isBleedingWithBandage;
                h.Stance.CachedCanSafelyBandage = canSafelyBandage;
                h.Stance.CachedBandageBlockReason = blockReason;
                h.Stance.CachedBandageGunfireDist = gunfireDist;
            }
        }

        private static void CountBandageBlocked(IntPtr ptr, float now)
        {
            if (AiCore.TryGetSoldierHandles(ptr, out var h) && now >= h.Combat.NextBandageBlockCountTime)
            {
                h.Combat.NextBandageBlockCountTime = now + 2.5f;
                UnsafeBandagesBlockedCount++;
            }
        }

        [HarmonyPatch(typeof(SoldierAI), nameof(SoldierAI.TryStopBleeding))]
        public static class SoldierAI_TryStopBleeding_Patch
        {
            public static bool Prefix(SoldierAI __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || ReferenceEquals(__instance, null))
                        return true;

                    AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandlesByAi(__instance);
                    if (!AiCore.IsLocallyOwnedBot(h))
                        return true;

                    float now = Time.time;
                    Soldier soldier = h.Ref;
                    if (!CanSafelyBandageNow(__instance, soldier, now, out string blockReason, out float gfDist))
                    {
                        CountBandageBlocked(h.Ptr, now);
                        BotActionTrace.RecordEvent(soldier, BotTraceEventKind.BandageBlocked, blockReason, __instance.distFromTarget, gfDist);
                        return false;
                    }

                    BotActionTrace.RecordEvent(
                        soldier,
                        BotTraceEventKind.BandageStart,
                        soldier.IsOnCover() ? "in_cover" : "safe_window",
                        __instance.distFromTarget,
                        gfDist);
                }
                catch { }
                return true;
            }
        }

        private static bool IsEngagedInPointBlankMelee(
            SoldierAI ai,
            Soldier soldier,
            float dist,
            out Soldier enemySoldier,
            out float lungeRange,
            out float ceaseFireRange,
            out float strikeRange,
            out int meleeSlotIndex)
        {
            enemySoldier = null;
            lungeRange = 0f;
            ceaseFireRange = 0f;
            strikeRange = 0f;
            meleeSlotIndex = 0;

            if (!VisionConfig.EnablePointBlankMelee.Value ||
                ai == null ||
                soldier == null ||
                dist > 6.0f ||
                AiCore.GetRawVisibleTargetPtr(ai) == IntPtr.Zero ||
                ai.HeldMortar != null ||
                soldier.IsOnVehicle() ||
                soldier.UsingBipod() ||
                soldier.IsBipodTransitioning() ||
                soldier.HasSurrended() ||
                soldier.IsCarryingBody() ||
                soldier.IsCarried())
            {
                return false;
            }

            AiCore.SoldierFastHandles meleeEvalH = AiCore.GetOrCreateSoldierHandles(soldier);
            int curFrame = Time.frameCount;
            if (meleeEvalH != null && curFrame > 0 && meleeEvalH.Combat.LastMeleeEvalFrame == curFrame)
            {
                if (!meleeEvalH.Combat.CachedInPointBlankMelee)
                    return false;

                Soldier cachedEnemy = meleeEvalH.Combat.CachedMeleeEnemy;
                if (cachedEnemy == null || !cachedEnemy.IsAlive || cachedEnemy.HasSurrended())
                    return false;

                enemySoldier = cachedEnemy;
                lungeRange = meleeEvalH.Combat.CachedMeleeLungeRange;
                ceaseFireRange = meleeEvalH.Combat.CachedMeleeCeaseFireRange;
                strikeRange = meleeEvalH.Combat.CachedMeleeStrikeRange;
                meleeSlotIndex = meleeEvalH.Combat.CachedMeleeSlotIndex;
                return true;
            }

            if (meleeEvalH != null && curFrame > 0)
            {
                meleeEvalH.Combat.LastMeleeEvalFrame = curFrame;
                meleeEvalH.Combat.CachedInPointBlankMelee = false;
                meleeEvalH.Combat.CachedMeleeEnemy = null;
            }

            enemySoldier = ai.visibleTarget.TryCast<Soldier>();
            if (enemySoldier == null ||
                !enemySoldier.IsAlive ||
                enemySoldier.IsOnVehicle() ||
                enemySoldier.HasSurrended())
            {
                return false;
            }

            Vector3 myPos = soldier.transform.position;
            Vector3 enemyPos = enemySoldier.transform.position;
            if (Mathf.Abs(enemyPos.y - myPos.y) > 1.65f)
                return false;

            float now = Time.time;
            bool hasBayonet = soldier.HasBayonetReady;
            bool holdsMelee = soldier.HoldsMeleeWeapon;
            bool isChargingOrder = soldier.IsCharging();
            GenericGun heldGun = soldier.GetHeldGun();
            bool gunEmptyOrReloading = AiCore.IsHeldGunEmptyOrReloading(soldier, heldGun);
            // CRITICAL: Use heldGun.boltActionRequired (isManualBoltOperated && bolt_required @ 0x180876300),
            // NEVER raw heldGun.bolt_required!
            bool boltCyclingClose = soldier.CantAimDueToBoltActionUsage ||
                                    (heldGun != null && (heldGun.boltActionRequired || heldGun.IsReloadingOrUsingBolt()));

            string myFac = AiCore.GetCachedFaction(soldier);
            bool hasCloseGunfire = VisionPatches.TryGetNearestEnemyGunfire(myPos, myFac, 15.0f, 2.5f, out float nearGunfireDist) ||
                                   VisionPatches.HasFiredRecently(enemySoldier, 2.5f);

            if (holdsMelee)
            {
                lungeRange = 5.6f;
                ceaseFireRange = 5.6f;
                strikeRange = 1.90f;
            }
            else if (isChargingOrder)
            {
                lungeRange = hasBayonet ? 5.2f : 3.6f;
                ceaseFireRange = (gunEmptyOrReloading || boltCyclingClose) ? 4.5f : 2.5f;
                strikeRange = hasBayonet ? 2.25f : 1.65f;
            }
            else if (gunEmptyOrReloading)
            {
                lungeRange = hasBayonet ? 4.2f : 3.2f;
                ceaseFireRange = lungeRange;
                strikeRange = hasBayonet ? 2.25f : 1.65f;
            }
            else if (hasCloseGunfire)
            {
                // Active gunfire within 15m while holding a loaded firearm (and NOT on a squad bayonet charge):
                // Do NOT break stance/cover to sprint into melee! Keep point-shooting unless the enemy physically
                // enters immediate contact reach.
                lungeRange = boltCyclingClose ? (hasBayonet ? 2.35f : 1.85f) : (hasBayonet ? 1.85f : 1.55f);
                ceaseFireRange = boltCyclingClose ? lungeRange : 1.25f;
                strikeRange = hasBayonet ? 2.20f : 1.65f;

                if (dist > lungeRange && dist <= 4.2f)
                {
                    AiCore.SoldierFastHandles supH = AiCore.GetOrCreateSoldierHandles(soldier);
                    if (supH != null && now >= supH.Combat.NextMeleeSuppressCountTime)
                    {
                        supH.Combat.NextMeleeSuppressCountTime = now + 2.5f;
                        CloseGunfireMeleeSuppressedCount++;
                    }
                    BotActionTrace.RecordEvent(soldier, BotTraceEventKind.MeleeSuppressed, "close_gunfire", dist, nearGunfireDist);
                    return false;
                }
            }
            else
            {
                // Quiet CQB / indoor corner encounter with no surrounding gunfire
                lungeRange = boltCyclingClose ? (hasBayonet ? 3.1f : 2.2f) : (hasBayonet ? 2.55f : 1.80f);
                ceaseFireRange = boltCyclingClose ? lungeRange : (hasBayonet ? 1.85f : 1.40f);
                strikeRange = hasBayonet ? 2.25f : 1.65f;
            }

            if (dist > lungeRange)
                return false;

            // Anti-stacking cap: allow at most 2 friendly bots to actively melee the same target;
            // additional bots keep squad spacing and cover with firearms instead of dogpiling.
            if (!TryClaimMeleeTargetSlot(soldier, enemySoldier, now, out meleeSlotIndex))
                return false;

            Vector3 toEnemy = (enemyPos + Vector3.up * 1.05f) - (myPos + Vector3.up * 1.05f);
            float checkDist = Mathf.Min(dist - 0.25f, 3.5f);
            if (checkDist > 0.30f &&
                AiCore.IsDirectionBlockedByEnvironment(myPos + Vector3.up * 1.05f, toEnemy.normalized, checkDist, soldier.transform.root))
            {
                return false;
            }

            if (meleeEvalH != null && curFrame > 0)
            {
                meleeEvalH.Combat.CachedInPointBlankMelee = true;
                meleeEvalH.Combat.CachedMeleeEnemy = enemySoldier;
                meleeEvalH.Combat.CachedMeleeLungeRange = lungeRange;
                meleeEvalH.Combat.CachedMeleeCeaseFireRange = ceaseFireRange;
                meleeEvalH.Combat.CachedMeleeStrikeRange = strikeRange;
                meleeEvalH.Combat.CachedMeleeSlotIndex = meleeSlotIndex;
            }

            BotActionTrace.RecordEvent(
                soldier,
                BotTraceEventKind.MeleeEnter,
                isChargingOrder ? "charge_order" : (gunEmptyOrReloading ? "gun_empty" : "point_blank"),
                dist,
                nearGunfireDist);
            return true;
        }

        private static void TryExecuteMeleeAttack(SoldierAI ai, Soldier soldier, Soldier enemy)
        {
            if (ai == null || !AiCore.IsLocallyOwnedBot(soldier) || enemy == null)
                return;

            Vector3 toEnemy = enemy.transform.position - soldier.transform.position;
            toEnemy.y = 0f;
            if (toEnemy.sqrMagnitude > 0.01f && AiCore.HorizontalAngleToDirection(soldier, toEnemy) > 42f)
                return;

            // Native Soldier+<MeleeDamageCR>d__489 (0x18077EB52) casts a horizontal 0.50m-radius OverlapCapsule
            // forward from Soldier.FacePosition() (animated headBone.position @ [rdx+160h]). Standing FacePosition().y (~1.62m)
            // puts the capsule bottom at ~1.12m, passing completely over prone enemies (0.15m-0.55m) and skimming crouched ones!
            // Crouch first and wait (~0.15s) until the animated headBone drops within strike height of the enemy's feet.
            if (enemy.m_pose == SoldierPose.Prone || enemy.m_pose == SoldierPose.Crouch)
            {
                if (soldier.m_pose != SoldierPose.Crouch || ai.actualPose != SoldierPose.Crouch)
                    SetBotPose(ai, soldier, SoldierPose.Crouch, true);

                if (AiCore.CanBotChangePoseAutonomously(soldier) &&
                    (soldier.FacePosition().y - enemy.transform.position.y) > 1.34f)
                {
                    return;
                }
            }

            bool shouldBlock = (enemy.DoingMelee() || enemy.HoldsMeleeWeapon || enemy.HasBayonetReady) &&
                               ai.MeleeBlockWindow();
            if (soldier.CanBlock)
            {
                soldier.SetAiming(shouldBlock);
            }
            if (shouldBlock || soldier.IsBlocking || soldier.isInHandsAnimation || soldier.isInBodyAnimation)
                return;

            AiCore.SoldierFastHandles meleeH = AiCore.GetOrCreateSoldierHandles(soldier);
            if (meleeH == null || Time.time < meleeH.Combat.NextMeleeAttemptTime)
                return;

            if (!AiCore.TryConsumeBotRpcBudget(0.25f))
                return;

            meleeH.Combat.NextMeleeAttemptTime = Time.time + 0.45f;
            soldier.SetGunTriggerPressed(false);
            soldier.Melee();
            MeleeAttacksTriggered++;
        }

        internal static bool ExecuteSoldierPointBlankCqbStep(
            SoldierAI ai,
            Soldier soldier,
            AiCore.SoldierFastHandles h,
            float dist,
            float now)
        {
            if (!IsEngagedInPointBlankMelee(
                    ai,
                    soldier,
                    dist,
                    out Soldier meleeEnemy,
                    out _,
                    out _,
                    out float seqStrikeRange,
                    out int seqMeleeSlot) ||
                meleeEnemy == null)
            {
                return false;
            }

            ai.knifeCharge = soldier.HoldsMeleeWeapon || soldier.HasBayonetReady;
            ai.moveLookingTarget = true;

            if (!soldier.HasBayonetReady &&
                !soldier.HoldsMeleeWeapon &&
                soldier.InventoryKnife != null &&
                !soldier.isInHandsAnimation)
            {
                GenericGun heldGun = soldier.GetHeldGun();
                bool gunEmpty = AiCore.IsHeldGunEmptyOrReloading(soldier, heldGun);
                // Only holster firearm for inventory knife if the held gun is actually empty/reloading;
                // if loaded at point-blank, buttstroke with Melee() or keep point-shooting!
                if (gunEmpty && AiCore.TryConsumeBotRpcBudget(0.35f))
                {
                    soldier.ToggleInventoryKnife();
                }
            }

            UpdatePointBlankMeleeEngagement(
                ai,
                soldier,
                h ?? AiCore.GetOrCreateSoldierHandles(soldier),
                meleeEnemy,
                dist,
                seqStrikeRange,
                seqMeleeSlot,
                now,
                isPerFrameHook: false);
            return true;
        }

        internal static bool TryExecuteComradeLiberationStep(
            SoldierAI ai,
            Soldier rescuer,
            AiCore.SoldierFastHandles rescH,
            Soldier targetSurr,
            float dSq,
            float maxApproachDistSq,
            float timeoutSeconds,
            bool canApproach,
            float now,
            out bool liberatedNow)
        {
            liberatedNow = false;
            if (rescuer == null || targetSurr == null)
                return false;

            if (dSq <= 3.8f * 3.8f)
            {
                if (rescH != null)
                    rescH.Combat.LiberateMoveStartTime = 0f;
                if (ai != null && ai.moveDirectlyToward_end > now)
                    ai.moveDirectlyToward_end = 0f;
                if (AiCore.TryConsumeBotRpcBudget(0.45f))
                {
                    Squad sq = rescuer.joinedSquad;
                    string sqId = sq != null ? sq.InstanceID.ToString() : "";
                    targetSurr.UnSurrenderSynched(sqId);
                    ComradesLiberated++;
                    AddCombatExperienceBonus(rescuer, 30f);
                    liberatedNow = true;
                }
                return true;
            }

            if (canApproach && dSq <= maxApproachDistSq)
            {
                if (rescH == null || now < rescH.Combat.LiberateMoveCooldownUntil)
                    return false;

                if (rescH.Combat.LiberateMoveStartTime <= 0f)
                {
                    rescH.Combat.LiberateMoveStartTime = now;
                }
                else if (now - rescH.Combat.LiberateMoveStartTime > timeoutSeconds)
                {
                    rescH.Combat.LiberateMoveStartTime = 0f;
                    rescH.Combat.LiberateMoveCooldownUntil = now + 20.0f;
                    return false;
                }

                AiCore.MoveBotTowardPosition(ai, rescuer, targetSurr.transform.position, 2.0f, 2.8f);
                return true;
            }

            return false;
        }

        internal static void TryLiberateNearbyComrade(SoldierAI ai, Soldier rescuer, SoldierPersonality personality)
        {
            AiCore.EnsureWorldSnapshot();
            int solCount = AiCore.WorldSoldierCount;
            if (solCount == 0) return;

            IntPtr rescuerPtr = rescuer.Pointer;
            string myFaction = rescuer.faction;
            Vector3 myPos = rescuer.transform.position;
            float searchRadius = VisionConfig.LiberateSearchRadius.Value;
            if (personality == SoldierPersonality.Medic_Heroic || personality == SoldierPersonality.Leader_OldGuard)
                searchRadius *= 1.50f;
            else if (personality == SoldierPersonality.Rifleman_Cautious || personality == SoldierPersonality.Medic_Triage)
                searchRadius *= 0.60f;

            float bestDistSq = searchRadius * searchRadius;
            Soldier bestSurrendered = null;
            var soldiers = AiCore.WorldSoldiers;

            for (int i = 0; i < solCount; i++)
            {
                ref AiCore.SoldierSnapshot sol = ref soldiers[i];
                if (!sol.HasSurrendered || sol.Ptr == rescuerPtr || sol.Faction != myFaction)
                    continue;

                // Ignore surrendered teammates on a different building floor or cliff level
                if (Mathf.Abs(sol.Pos.y - myPos.y) > 2.4f)
                    continue;

                float dSq = (sol.Pos - myPos).sqrMagnitude;
                if (dSq < bestDistSq && AiCore.IsTrueSurrenderedPrisoner(sol.Ref, sol.Handles))
                {
                    bestDistSq = dSq;
                    bestSurrendered = sol.Ref;
                }
            }

            if (bestSurrendered != null)
            {
                AiCore.SoldierFastHandles rescH = AiCore.GetOrCreateSoldierHandles(rescuer);
                bool canApproach = !rescuer.IsSquadLeader() && !rescuer.IsOnCover() && !ai.HasEnemyAndCanFireAtHim;
                TryExecuteComradeLiberationStep(ai, rescuer, rescH, bestSurrendered, bestDistSq, 9.5f * 9.5f, 5.5f, canApproach, Time.time, out _);
            }
            else if (AiCore.TryGetSoldierHandles(rescuerPtr, out var clearRescH))
            {
                clearRescH.Combat.LiberateMoveStartTime = 0f;
            }
        }

        // =====================================================================
        // Shell-Shocked & Skittish Dynamic Surrender Helper
        // =====================================================================
        private static bool TryEvaluateShellShockSurrender(
            SoldierAI ai,
            Soldier soldier,
            SoldierPersonality personality,
            float vet01,
            float suppression)
        {
            if (ai == null ||
                soldier == null ||
                soldier.IsOnVehicle() ||
                soldier.HasSurrended() ||
                soldier.IsCharging() ||
                soldier.IsSquadLeader())
            {
                return false;
            }

            // Officers and fearless/fanatic archetypes never surrender dynamically
            if (personality == SoldierPersonality.Leader_Spearhead ||
                personality == SoldierPersonality.Leader_Tactician ||
                personality == SoldierPersonality.Leader_OldGuard ||
                personality == SoldierPersonality.Assaulter_Shock ||
                personality == SoldierPersonality.Gunner_Anchor ||
                personality == SoldierPersonality.Medic_Heroic)
            {
                return false;
            }

            bool isSkittish = personality == SoldierPersonality.Rifleman_Cautious ||
                              personality == SoldierPersonality.Medic_Triage;
            if (isSkittish && VisionConfig.VeterancyCuresSkittishness.Value && vet01 >= 0.35f)
            {
                isSkittish = false;
            }

            FactionCommandProfile doc = GetFactionCommandProfile(soldier.faction);
            bool isFanaticDoctrine = doc.PreferAmbushWhenAlone && doc.BayonetBreachCostMult <= 0.78f;

            GenericGun heldGun = soldier.GetHeldGun();
            bool isCompletelyOutOfAmmo = heldGun != null &&
                                         heldGun.GetCurrentAmmoCount() <= 0 &&
                                         soldier.IsOutOfAmmosFor(heldGun);
            bool isBleeding = soldier.isBleeding || soldier.GetBleedValue() > 0.01f;
            bool isBleedingOrDry = isBleeding ||
                                   heldGun == null ||
                                   isCompletelyOutOfAmmo ||
                                   (heldGun.GetCurrentAmmoCount() <= 0 && soldier.MustReload());

            // Fanatic doctrines (Japan, Gurkha, Finland) never surrender unless critically wounded/bleeding under extreme suppression
            if (isFanaticDoctrine && !isBleeding)
            {
                return false;
            }

            bool isGreenRookie = !isSkittish && vet01 < 0.20f;
            bool isDryRegular = !isSkittish && !isGreenRookie && isCompletelyOutOfAmmo && vet01 < 0.45f;
            if (!isSkittish && !isGreenRookie && !isDryRegular)
            {
                return false;
            }

            float reqSupp;
            if (isSkittish)
            {
                float vetBonus = VisionConfig.VeterancyCuresSkittishness.Value ? (vet01 * 24f) : 0f;
                reqSupp = (isBleedingOrDry ? 52f : 68f) + vetBonus;
            }
            else if (isDryRegular)
            {
                reqSupp = 62f + (vet01 * 20f);
            }
            else
            {
                reqSupp = isBleedingOrDry ? 70f : 86f;
            }

            if (isFanaticDoctrine)
            {
                reqSupp += 22f;
            }

            if (suppression < reqSupp)
            {
                return false;
            }

            AiCore.SoldierFastHandles surH = AiCore.GetOrCreateSoldierHandles(soldier);
            IntPtr ptr = soldier.Pointer;
            float now = Time.time;
            if (surH == null || now < surH.Combat.NextSurrenderCheckTime)
            {
                return false;
            }
            surH.Combat.NextSurrenderCheckTime = now + 1.50f;

            AiCore.EnsureWorldSnapshot();
            int sqCount = AiCore.WorldSquadCount;
            if (sqCount == 0)
            {
                return false;
            }

            Vector3 myPos = soldier.transform.position;
            string myFaction = soldier.faction;
            int enemyCloseCount = 0;
            int friendlyCloseCount = 0;
            float closestEnemyDistSq = 9999f;
            var squads = AiCore.WorldSquads;
            var soldiers = AiCore.WorldSoldiers;

            for (int s = 0; s < sqCount; s++)
            {
                ref AiCore.SquadSnapshot sqSnap = ref squads[s];
                if (sqSnap.UnitCount == 0 || (sqSnap.RefPos - myPos).sqrMagnitude > 65f * 65f)
                    continue;

                int end = sqSnap.UnitStartIndex + sqSnap.UnitCount;
                for (int i = sqSnap.UnitStartIndex; i < end; i++)
                {
                    ref AiCore.SoldierSnapshot uSnap = ref soldiers[i];
                    if (uSnap.Ptr == ptr || uSnap.HasSurrendered)
                        continue;

                    float dSq = (uSnap.Pos - myPos).sqrMagnitude;
                    if (uSnap.Faction == myFaction)
                    {
                        if (dSq <= 22f * 22f)
                        {
                            Soldier u = uSnap.Ref;
                            if (u != null)
                            {
                                SoldierPersonality allyPers = GetPersonality(u);
                                if (uSnap.IsSquadLeader ||
                                    allyPers == SoldierPersonality.Leader_Spearhead ||
                                    allyPers == SoldierPersonality.Leader_OldGuard)
                                {
                                    // Inspiring officer nearby keeps wavering soldiers in the fight
                                    return false;
                                }
                            }
                        }

                        if (dSq <= 16f * 16f)
                        {
                            friendlyCloseCount++;
                            if (friendlyCloseCount > 1)
                                return false;
                        }
                    }
                    else if (dSq <= 18f * 18f && Mathf.Abs(uSnap.Pos.y - myPos.y) <= 6.5f)
                    {
                        enemyCloseCount++;
                        if (dSq < closestEnemyDistSq)
                            closestEnemyDistSq = dSq;
                    }
                }
            }

            Soldier player = Soldier.CurrentControlledSoldierOrNull();
            if (player != null && player.IsAlive && player.Pointer != ptr && !player.HasSurrended() && player.transform != null)
            {
                float pDistSq = (player.transform.position - myPos).sqrMagnitude;
                if (player.faction == myFaction)
                {
                    if (pDistSq <= 18f * 18f)
                        return false;
                }
                else if (pDistSq <= 18f * 18f)
                {
                    enemyCloseCount++;
                    if (pDistSq < closestEnemyDistSq)
                        closestEnemyDistSq = pDistSq;
                }
            }

            if (enemyCloseCount < 1 || friendlyCloseCount > 1)
            {
                return false;
            }

            bool isOverrun = enemyCloseCount > friendlyCloseCount ||
                             (closestEnemyDistSq <= 10.5f * 10.5f && isBleedingOrDry);
            if (!isOverrun)
            {
                return false;
            }

            // Before surrendering at mid-close range (> 11m), able-bodied isolated soldiers first attempt
            // an "Oh Shit!" break-contact sprint rearward (with smoke if carried) toward friendly lines/cover!
            if (VisionConfig.EnableTacticalRetreatsAndRegroup.Value &&
                !soldier.isBleeding &&
                closestEnemyDistSq > 11.0f * 11.0f &&
                TryTriggerImmediateIndividualFallback(soldier, myPos))
            {
                return true;
            }

            if (!AiCore.TryConsumeBotRpcBudget(0.60f))
            {
                return false;
            }

            soldier.SetGunTriggerPressed(false);
            soldier.SurrenderSynched();
            ShellShockSurrendersCount++;

            if (VisionConfig.DebugLogging.Value)
            {
                string reason = isSkittish ? "Skittish/Shell-Shocked" : "Green Rookie Overrun";
                AiCore.LogDebug("Surrender", $"{soldier.faction} soldier surrendered ({reason}, supp={suppression:F0}, enemiesWithin18m={enemyCloseCount}).");
            }

            return true;
        }

        // =====================================================================
        // Downed Casualty vs Surrender Animator State Fix
        // Native Root Causes (GameAssembly.dll):
        //  1) Soldier.SetIncapacitated(true) (0x180765290) sets life_total = 0 (IsAlive == false,
        //     IsInjured == true), triggers Base Layer 0 "Incapacitated", and disables Upper Body
        //     Layer 1 (m_Animator.SetLayerWeight(1, 0f) @ 0x180765A64) without clearing suppressionValue.
        //  2) BattleManager.SurrenderUnitsOnMissionAreas (0x1805961A0 on phase advance) and
        //     BattleManager.OnWin (0x1805937CB) call SurrenderSynched() on downed IsInjured casualties,
        //     and Soldier.Surrender() (0x18076A500) permits !IsAlive && IsInjured! Setting
        //     m_Animator.SetBool("Surrender", true) transitions Base Layer 0 out of "Incapacitated"
        //     into standing upright "Surrender" while LayerWeight(1) remains 0f and IsAlive remains false,
        //     leaving downed bots standing upright with arms down staring blankly into the distance!
        //  3) In Soldier+<UseSyryngeCR>d__571.MoveNext (0x1807A5466), enemy syringe capture calls
        //     patient.SurrenderSynched() BEFORE patient.SetIncapacitated(false) (0x1807A548B), which
        //     then overwrites m_pose to Crouch and calls SetAlertedFor(5.0f).
        //  4) Soldier.OnRetreat (0x180758650) sets surrended = true (+0x49E = 1) during the 3s despawn
        //     without setting Animator "Surrender", causing liberation / RefreshAnimatorState side effects.
        // =====================================================================
        private static bool IsDownedPatientUndergoingEnemySyringeCapture(Soldier patient, AiCore.SoldierFastHandles patH)
        {
            if (patient == null || patient.Pointer == IntPtr.Zero || patient.IsDead)
                return false;

            float now = Time.time;
            if (patH != null && now < patH.EnemySyringeCaptureUntil)
                return true;

            // Remote Co-op fallback: if a remote player/client sends SyncSurrender RPC while reviving a downed casualty
            if (AiCore.IsInMultiplayerRoom() && patient.transform != null)
            {
                string patFac = patH != null ? AiCore.GetCachedFaction(patH, now) : AiCore.GetCachedFaction(patient);
                if (!string.IsNullOrEmpty(patFac))
                {
                    Vector3 patPos = patient.transform.position;
                    AiCore.EnsureWorldSnapshot();
                    int count = AiCore.WorldSoldierCount;
                    var soldiers = AiCore.WorldSoldiers;
                    for (int i = 0; i < count; i++)
                    {
                        ref AiCore.SoldierSnapshot snap = ref soldiers[i];
                        if (snap.Ptr == IntPtr.Zero ||
                            snap.Ptr == patient.Pointer ||
                            snap.IsInjured ||
                            snap.HasSurrendered ||
                            string.IsNullOrEmpty(snap.Faction) ||
                            !AiCore.AreEnemyFactions(patFac, snap.Faction))
                        {
                            continue;
                        }

                        if ((snap.Pos - patPos).sqrMagnitude <= 4.2f * 4.2f)
                            return true;
                    }
                }
            }

            return false;
        }

        private static void EnsureSurrenderedPrisonerAnimatorAndPose(Soldier s)
        {
            if (s == null || s.Pointer == IntPtr.Zero || !s.IsAlive || s.IsInjured || s.IsDead || !s.HasSurrended())
                return;

            AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandles(s);
            if (h != null && h.IsRetreatDespawning)
                return;

            s.SetGunTriggerPressed(false);
            s.pointGunEndTime = 0f;
            s.alertEndTime = 0f;

            SoldierAI ai = h != null ? h.Ai : s.aiController;
            if (!ReferenceEquals(ai, null) && ai != null)
            {
                ai.actualPose = SoldierPose.Idle;
                ai.moveDirectlyToward_end = 0f;
            }

            if (s.m_pose != SoldierPose.Idle && !s.IsOnVehicle())
            {
                s.SetPose(SoldierPose.Idle, true, false, -1f);
            }

            Animator anim = s.m_Animator;
            if (anim != null)
            {
                anim.SetLayerWeight(1, 1.0f);
                anim.SetBool("Incapacitated", false);
                anim.SetBool("Surrender", true);
            }
        }

        [HarmonyPatch(typeof(Soldier), nameof(Soldier.SurrenderSynched))]
        public static class Soldier_SurrenderSynched_Patch
        {
            public static bool Prefix(Soldier __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null || __instance.Pointer == IntPtr.Zero)
                        return true;

                    if (__instance.IsDead)
                        return false;

                    if (!__instance.IsAlive || __instance.IsInjured)
                    {
                        AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandles(__instance);
                        if (IsDownedPatientUndergoingEnemySyringeCapture(__instance, h))
                        {
                            // Revive the patient BEFORE SurrenderSynched -> Surrender runs so Upper Body Layer 1
                            // weight is 1.0f and the subsequent SetIncapacitated(false) in UseSyryngeCR is a no-op!
                            if (h != null)
                                h.EnemySyringeCaptureUntil = 0f;
                            __instance.SetIncapacitated(false);
                            return true;
                        }

                        // Block BattleManager.SurrenderUnitsOnMissionAreas / OnWin from surrendering downed casualties!
                        return false;
                    }
                }
                catch { }
                return true;
            }
        }

        [HarmonyPatch(typeof(Soldier), nameof(Soldier.Surrender))]
        public static class Soldier_Surrender_Patch
        {
            public static bool Prefix(Soldier __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null || __instance.Pointer == IntPtr.Zero)
                        return true;

                    if (__instance.IsDead)
                        return false;

                    if (!__instance.IsAlive || __instance.IsInjured)
                    {
                        AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandles(__instance);
                        if (IsDownedPatientUndergoingEnemySyringeCapture(__instance, h))
                        {
                            if (h != null)
                                h.EnemySyringeCaptureUntil = 0f;
                            __instance.SetIncapacitated(false);
                            return true;
                        }

                        return false;
                    }
                }
                catch { }
                return true;
            }

            public static void Postfix(Soldier __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null || __instance.Pointer == IntPtr.Zero)
                        return;

                    EnsureSurrenderedPrisonerAnimatorAndPose(__instance);
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Soldier), nameof(Soldier.SetIncapacitated))]
        public static class Soldier_SetIncapacitated_Patch
        {
            public static void Postfix(Soldier __instance, bool __0)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null || __instance.Pointer == IntPtr.Zero)
                        return;

                    if (__0)
                    {
                        // Clear suppressionValue when downed so BattleManager.SurrenderUnitsOnMissionAreas skips this casualty,
                        // and clear Animator "Surrender" so an already-surrendered prisoner who gets downed stays lying on the
                        // ground in "Incapacitated" instead of popping upright with LayerWeight(1) == 0f!
                        __instance.suppressionValue = 0;
                        if (__instance.m_Animator != null)
                        {
                            __instance.m_Animator.SetBool("Surrender", false);
                        }
                    }
                    else if (__instance.IsAlive && !__instance.IsInjured && __instance.HasSurrended())
                    {
                        EnsureSurrenderedPrisonerAnimatorAndPose(__instance);
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Soldier), nameof(Soldier.RefreshAnimatorState))]
        public static class Soldier_RefreshAnimatorState_Patch
        {
            public static void Postfix(Soldier __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null || __instance.Pointer == IntPtr.Zero)
                        return;

                    Animator anim = __instance.m_Animator;
                    if (anim == null)
                        return;

                    if ((__instance.IsInjured || !__instance.IsAlive) && !__instance.IsDead)
                    {
                        anim.SetBool("Surrender", false);
                    }
                    else if (__instance.IsAlive && __instance.HasSurrended())
                    {
                        AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandles(__instance);
                        if (h != null && h.IsRetreatDespawning)
                        {
                            anim.SetBool("Surrender", false);
                        }
                        else
                        {
                            anim.SetLayerWeight(1, 1.0f);
                        }
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Soldier), nameof(Soldier.OnRetreat))]
        public static class Soldier_OnRetreat_Patch
        {
            public static void Prefix(Soldier __instance)
            {
                try
                {
                    if (__instance == null || __instance.Pointer == IntPtr.Zero)
                        return;

                    AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandles(__instance);
                    if (h != null)
                        h.IsRetreatDespawning = true;
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Soldier), nameof(Soldier.UnSurrender))]
        public static class Soldier_UnSurrender_Patch
        {
            public static void Postfix(Soldier __instance)
            {
                try
                {
                    if (__instance == null || __instance.Pointer == IntPtr.Zero)
                        return;

                    AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandles(__instance);
                    if (h != null)
                    {
                        h.IsRetreatDespawning = false;
                        h.EnemySyringeCaptureUntil = 0f;
                    }
                }
                catch { }
            }
        }

        // =====================================================================
        // Vanilla AI Fix: Anti-Dogpile Medic Triage Reservation (Squad.MedicRoutine)
        // In vanilla Squad.MedicRoutine (0x1808079B0), every medic calls character.GetNearestInjured()
        // and creates a new MedicHealingDestination(injured, character) with ZERO reservation check,
        // causing 3-4 medics to dogpile the same wounded soldier while other casualties bleed out.
        // This patch caps each casualty to 1 medic (or 2 max ONLY if the primary medic is > 20m away
        // or heavily suppressed >= 40f), IMMEDIATELY releases claims if the medic dies, goes down
        // (IsInjured), surrenders, or drops the task, and spreads extra medics to other wounded allies!
        // =====================================================================
        internal struct MedicPatientClaim
        {
            public Soldier PrimaryMedic;
            public Soldier BackupMedic;
        }

        internal static readonly Dictionary<IntPtr, MedicPatientClaim> _medicPatientClaims = new Dictionary<IntPtr, MedicPatientClaim>(32);
        private static readonly List<IntPtr> _reusableDeadPatientPtrs = new List<IntPtr>(32);
        public static int MedicTriageRedirectsCount;

        private static bool IsMedicClaimStillActiveForPatient(Soldier medic, IntPtr patientPtr)
        {
            AiCore.SoldierFastHandles mh = AiCore.GetOrCreateSoldierHandles(medic);
            if (mh == null ||
                !medic.IsAlive ||
                medic.IsInjured ||
                medic.HasSurrended() ||
                medic.IsOnVehicle() ||
                medic.IsCarried())
            {
                return false;
            }

            if (AiCore.ResolveTargetDestination(mh) != AiCore.CachedDestKind.MedicHeal || mh.CachedHealDest == null)
                return false;

            MedicHealingDestination healDest = mh.CachedHealDest;
            if (healDest.soldierToHealUp == null ||
                healDest.soldierToHealUp.Pointer != patientPtr ||
                healDest.soldierToHealUp.IsDead ||
                !healDest.soldierToHealUp.IsInjured ||
                healDest.soldierToHealUp.IsCarried())
            {
                return false;
            }

            return true;
        }

        private static void PruneAndValidateMedicClaims(IntPtr selfMedicPtr, out IntPtr alreadyClaimedPatientPtr)
        {
            alreadyClaimedPatientPtr = IntPtr.Zero;
            if (_medicPatientClaims.Count == 0)
                return;

            _reusableDeadPatientPtrs.Clear();
            foreach (var kvp in _medicPatientClaims)
            {
                IntPtr patPtr = kvp.Key;
                MedicPatientClaim claim = kvp.Value;

                bool p1Valid = IsMedicClaimStillActiveForPatient(claim.PrimaryMedic, patPtr);
                bool p2Valid = IsMedicClaimStillActiveForPatient(claim.BackupMedic, patPtr);

                if (!p1Valid && p2Valid)
                {
                    claim.PrimaryMedic = claim.BackupMedic;
                    claim.BackupMedic = null;
                    p1Valid = true;
                    p2Valid = false;
                }
                else if (!p2Valid)
                {
                    claim.BackupMedic = null;
                }

                if (!p1Valid)
                {
                    _reusableDeadPatientPtrs.Add(patPtr);
                }
                else
                {
                    _medicPatientClaims[patPtr] = claim;
                    if (claim.PrimaryMedic != null && claim.PrimaryMedic.Pointer == selfMedicPtr)
                        alreadyClaimedPatientPtr = patPtr;
                    else if (claim.BackupMedic != null && claim.BackupMedic.Pointer == selfMedicPtr)
                        alreadyClaimedPatientPtr = patPtr;
                }
            }

            for (int i = 0; i < _reusableDeadPatientPtrs.Count; i++)
            {
                _medicPatientClaims.Remove(_reusableDeadPatientPtrs[i]);
            }
            _reusableDeadPatientPtrs.Clear();
        }

        [HarmonyPatch(typeof(Squad), nameof(Squad.MedicRoutine))]
        public static class Squad_MedicRoutine_Patch
        {
            public static bool Prefix(SoldierAI ai, Soldier character, out long __state)
            {
                __state = HitchProfiler.BeginTopLevel();
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableAntiDogpileMedicTriage.Value ||
                        ReferenceEquals(character, null) ||
                        character.Pointer == IntPtr.Zero)
                    {
                        return true;
                    }

                    AiCore.SoldierFastHandles mh = AiCore.GetOrCreateSoldierHandles(character);
                    if (!AiCore.IsLocallyOwnedBot(mh))
                        return true;

                    // Match vanilla Squad.MedicRoutine (@ 0x1808079B0): check !hasSyringe FIRST before pruning global medic claims!
                    if (!character.hasSyringe ||
                        !character.IsAlive ||
                        character.IsInjured ||
                        (character.Params != null && (!character.Params.canDoMedic || !character.Params.canMove)) ||
                        character.IsCharging() ||
                        character.isInBodyAnimation ||
                        character.HasSurrended() ||
                        character.IsOnVehicle() ||
                        character.IsCarryingBody() ||
                        mh.Tr == null)
                    {
                        return false;
                    }

                    IntPtr medicPtr = mh.Ptr;
                    PruneAndValidateMedicClaims(medicPtr, out IntPtr activePatientPtr);

                    // If this medic is already actively heading to a valid incapacitated (!IsDead && IsInjured) patient, let him continue!
                    if (activePatientPtr != IntPtr.Zero &&
                        AiCore.ResolveTargetDestination(mh) == AiCore.CachedDestKind.MedicHeal &&
                        mh.CachedHealDest != null)
                    {
                        MedicHealingDestination curHeal = mh.CachedHealDest;
                        if (curHeal.soldierToHealUp != null &&
                            !curHeal.soldierToHealUp.IsDead &&
                            curHeal.soldierToHealUp.IsInjured &&
                            !curHeal.soldierToHealUp.IsCarried())
                        {
                            return false;
                        }
                    }

                    Vector3 medicPos = mh.Tr.position;
                    string myFaction = AiCore.GetCachedFaction(mh, Time.time);
                    if (string.IsNullOrEmpty(myFaction))
                        return true;

                    AiCore.EnsureWorldSnapshot();
                    int injCount = AiCore.WorldInjuredSoldierCount;
                    var injuredSoldiers = AiCore.WorldInjuredSoldiers;

                    Soldier bestPatient = null;
                    float bestScoreDistSq = 55.0f * 55.0f;
                    bool skippedDogpiledNearest = false;

                    for (int i = 0; i < injCount; i++)
                    {
                        ref AiCore.SoldierSnapshot sSnap = ref injuredSoldiers[i];
                        if (!sSnap.IsInjured || sSnap.Ptr == medicPtr || sSnap.IsOnVehicle || sSnap.Ref == null)
                            continue;

                        if (!AiCore.AreSameSideFactions(myFaction, sSnap.Faction))
                            continue;

                        Soldier pat = sSnap.Ref;
                        if (pat.IsDead || !pat.IsInjured || pat.IsCarried() || pat.IsOnVehicle())
                            continue;

                        float dSq = (sSnap.Pos - medicPos).sqrMagnitude;
                        if (dSq > 55.0f * 55.0f)
                            continue;

                        bool isBackupSlot = false;
                        if (_medicPatientClaims.TryGetValue(sSnap.Ptr, out MedicPatientClaim existingClaim) &&
                            existingClaim.PrimaryMedic != null &&
                            existingClaim.PrimaryMedic.Pointer != medicPtr)
                        {
                            // Hard cap of 2 medics max per casualty (and only allow a 2nd medic if the 1st medic is > 20m away or heavily suppressed >= 40f!)
                            if (existingClaim.BackupMedic != null && existingClaim.BackupMedic.Pointer != medicPtr)
                            {
                                skippedDogpiledNearest = true;
                                continue;
                            }

                            Soldier p1 = existingClaim.PrimaryMedic;
                            float p1DistSq = p1.transform != null ? (p1.transform.position - sSnap.Pos).sqrMagnitude : 0f;
                            bool p1NeedsBackup = p1DistSq > 20.0f * 20.0f || p1.GetSuppressionValue() >= 40.0f;
                            if (!p1NeedsBackup)
                            {
                                skippedDogpiledNearest = true;
                                continue;
                            }
                            isBackupSlot = true;
                        }

                        // Strongly prefer completely unclaimed casualties over adding a 2nd backup medic
                        float effectiveDistSq = isBackupSlot ? (dSq * 2.5f + 225.0f) : dSq;
                        if (effectiveDistSq < bestScoreDistSq)
                        {
                            bestScoreDistSq = effectiveDistSq;
                            bestPatient = pat;
                        }
                    }

                    if (bestPatient != null)
                    {
                        IntPtr bestPtr = bestPatient.Pointer;
                        _medicPatientClaims.TryGetValue(bestPtr, out MedicPatientClaim claim);
                        if (claim.PrimaryMedic == null || claim.PrimaryMedic.Pointer == medicPtr)
                            claim.PrimaryMedic = character;
                        else
                            claim.BackupMedic = character;
                        _medicPatientClaims[bestPtr] = claim;

                        MedicHealingDestination newHealDest = new MedicHealingDestination(bestPatient, character);
                        AiCore.AssignBotCoverOrDestination(ai, character, newHealDest.Cast<AiDestination>(), requestImmediatePath: false);

                        if (skippedDogpiledNearest)
                        {
                            MedicTriageRedirectsCount++;
                            AiCore.LogDebug("MedicTriage", $"{myFaction} medic redirected to secondary casualty (avoided dogpile).");
                        }
                    }
                    else
                    {
                        if (character.targetDestination != null && character.targetDestination.TryCast<MedicHealingDestination>() != null)
                        {
                            character.CoverPosition(null);
                        }
                        if (skippedDogpiledNearest)
                        {
                            // All nearby wounded already have a healthy nearby medic (or 2 max): keep rifle up in cover!
                            MedicTriageRedirectsCount++;
                        }
                    }

                    return false;
                }
                catch { }
                return true;
            }

            public static void Postfix(long __state)
            {
                HitchProfiler.End(HitchBucket.VanMedicRtn, __state);
            }
        }

        // =====================================================================
        // Vanilla AI Fix: Handheld Knee-Mortar Danger-Close Friendly-Fire Guard
        // In vanilla SoldierAI.MortarTarget (0x18055CA60), mortar bots pick the single closest
        // spotted enemy (or keep shelling Squad.mortarOrderPos) with ZERO friendly-troop blast check.
        // Redirect danger-close mortar aims to a spotted enemy cluster clear of friendlies, or hold fire!
        // =====================================================================
        [HarmonyPatch(typeof(SoldierAI), nameof(SoldierAI.MortarTarget))]
        public static class SoldierAI_MortarTarget_Patch
        {
            public static void Postfix(SoldierAI __instance, ref float __0, ref Spottable __result)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        VisionConfig.FriendlyFireConeDegrees.Value <= 0.1f ||
                        __instance == null ||
                        __result == null ||
                        __result.Pointer == IntPtr.Zero)
                    {
                        return;
                    }

                    AiCore.SoldierFastHandles sh = AiCore.GetOrCreateSoldierHandlesByAi(__instance);
                    if (!AiCore.IsLocallyOwnedBot(sh) || sh.Tr == null)
                        return;

                    Soldier soldier = sh.Ref;
                    string myFaction = AiCore.GetCachedFaction(sh, Time.time);
                    if (string.IsNullOrEmpty(myFaction))
                        return;

                    Vector3 targetPos = __result.GetCenterOfUnit();
                    if (!AiCore.IsIndirectFireImpactDangerClose(myFaction, targetPos, sh.Ptr, 29.0f, 35.0f))
                        return;

                    FriendlyFirePrevented++;

                    KneeMortar mortar = __instance.HeldMortar;
                    Spottable bestSafeTarget = null;
                    float bestSafeDist = 0f;
                    float bestScore = -9999f;

                    if (mortar != null && mortar.Pointer != IntPtr.Zero)
                    {
                        RefreshCombinedArmsIntelIfNeeded();
                        Vector3 myPos = sh.Tr.position;
                        int clusterCount = _knownEnemyClusters.Count;

                        for (int i = 0; i < clusterCount; i++)
                        {
                            KnownEnemyCluster cluster = _knownEnemyClusters[i];
                            if (AiCore.AreSameSideFactions(myFaction, cluster.Faction))
                                continue;

                            for (int pass = 0; pass < 2; pass++)
                            {
                                Spottable cand = pass == 0 ? cluster.PrimarySoldier : cluster.SecondarySoldier;
                                if (!AiCore.IsValidAliveSpottable(cand))
                                    continue;

                                Vector3 candPos = cand.GetCenterOfUnit();
                                float d = Vector3.Distance(myPos, candPos);
                                if (!mortar.InRange(soldier, d))
                                    continue;

                                if (AiCore.IsIndirectFireImpactDangerClose(myFaction, candPos, sh.Ptr, 29.0f, 35.0f))
                                    continue;

                                float score = cluster.BasePriority - d * 0.15f;
                                if (score > bestScore)
                                {
                                    bestScore = score;
                                    bestSafeTarget = cand;
                                    bestSafeDist = d;
                                }
                                break;
                            }
                        }
                    }

                    if (bestSafeTarget != null)
                    {
                        __result = bestSafeTarget;
                        __0 = bestSafeDist;
                        AiCore.LogDebug("MortarFFSaved", $"{myFaction} mortarman shifted target away from danger-close friendlies ({bestSafeDist:F0}m).", 5.0f);
                    }
                    else
                    {
                        __result = null;
                        __0 = 0f;
                        AiCore.LogDebug("MortarFFSaved", $"{myFaction} mortarman held fire (all targets danger-close to friendlies).", 5.0f);
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(KneeMortar), nameof(KneeMortar.Use))]
        public static class KneeMortar_Use_Patch
        {
            public static bool Prefix(KneeMortar __instance, Creature __0, ref bool __result)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        VisionConfig.FriendlyFireConeDegrees.Value <= 0.1f ||
                        __instance == null ||
                        __0 == null)
                    {
                        return true;
                    }

                    Soldier soldier = __0.TryCast<Soldier>();
                    if (!AiCore.IsLocallyOwnedBot(soldier) || soldier.transform == null || string.IsNullOrEmpty(soldier.faction))
                        return true;

                    float range = Mathf.Max(20.0f, __instance.CurrentRange);
                    Vector3 flatFwd = soldier.transform.forward;
                    flatFwd.y = 0f;
                    flatFwd = flatFwd.sqrMagnitude > 0.001f ? flatFwd.normalized : Vector3.forward;

                    Vector3 predictedImpact = soldier.transform.position + flatFwd * range;
                    if (AiCore.IsIndirectFireImpactDangerClose(soldier.faction, predictedImpact, soldier.Pointer, 27.0f, 34.0f))
                    {
                        FriendlyFirePrevented++;
                        __result = false;
                        return false;
                    }
                }
                catch { }
                return true;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
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
                AiCore.IsSoldierInVehicleOrTransition(soldier) ||
                soldier.HasBayonetReady ||
                soldier.isInHandsAnimation ||
                soldier.IsReloading ||
                soldier.HoldsInventoryItem ||
                soldier.UsingBipod())
            {
                return;
            }

            float now = Time.time;
            AiCore.SoldierFastHandles bayH = AiCore.GetOrCreateSoldierHandles(soldier);
            if (bayH == null ||
                AiCore.IsSoldierNonCombatantOrSurrendered(soldier, bayH) ||
                now < bayH.Combat.NextBayonetCheckTime)
            {
                return;
            }

            try
            {
                GenericGun heldGun = soldier.GetHeldGun();
                if (heldGun == null || heldGun.Pointer == IntPtr.Zero)
                    return;

                if (bayH.Combat.LastBayonetCheckedGunPtr == heldGun.Pointer)
                    return;

                if (heldGun.HasAttachmentInstalled(AttachmentType.bayonet))
                {
                    bayH.Combat.LastBayonetCheckedGunPtr = heldGun.Pointer;
                    return;
                }

                if (heldGun.integratedBayonet != null)
                {
                    if (!heldGun.integratedBayonet.IsMounted)
                    {
                        if (AiCore.IsInMultiplayerRoom())
                        {
                            if (!AiCore.TryConsumeBotRpcBudget(0.45f))
                            {
                                bayH.Combat.NextBayonetCheckTime = now + 2.0f;
                                return;
                            }

                            SyncSoldier syncher = bayH.Syncher ?? soldier.soldierSyncher ?? soldier.GetSyncher();
                            if (syncher != null)
                                syncher.SetIntegratedBayonet(heldGun, true);
                            else
                                heldGun.integratedBayonet.SetMounted(true);
                        }
                        else
                        {
                            heldGun.integratedBayonet.SetMounted(true);
                        }

                        AiCore.LogDebug("Bayonet", $"{AiCore.GetCachedFaction(soldier)} unfolded integrated bayonet on '{heldGun.name}'.");
                    }

                    bayH.Combat.LastBayonetCheckedGunPtr = heldGun.Pointer;
                    return;
                }

                Inventory inv = soldier.inventory != null ? soldier.inventory.inventory : null;
                if (inv == null || heldGun.supportedAttachments == null || heldGun.supportedAttachments.Length == 0)
                {
                    bayH.Combat.LastBayonetCheckedGunPtr = heldGun.Pointer;
                    return;
                }

                foreach (SupportedAttachment sa in heldGun.supportedAttachments)
                {
                    if (sa == null || sa.GetAttachmentType(heldGun) != AttachmentType.bayonet)
                        continue;

                    string attId = sa.GetAttachmentId(heldGun);
                    if (string.IsNullOrEmpty(attId))
                        continue;

                    VirtualItem bayonetItem = inv.FindItemWithID(attId);
                    if (bayonetItem == null)
                        continue;

                    if (AiCore.IsInMultiplayerRoom() && !AiCore.TryConsumeBotRpcBudget(0.55f))
                    {
                        bayH.Combat.NextBayonetCheckTime = now + 2.5f;
                        return;
                    }

                    ItemObject prefab = bayonetItem.GetItemPrefab();
                    if (prefab == null || prefab.gameObject == null)
                        continue;

                    UnityEngine.GameObject instGo = UnityEngine.Object.Instantiate(prefab.gameObject);
                    if (instGo == null)
                        continue;

                    Attachment att = instGo.GetComponent<Attachment>();
                    if (att == null)
                    {
                        UnityEngine.Object.Destroy(instGo);
                        continue;
                    }

                    heldGun.InstallAttachment(att, false);
                    if (heldGun.HasAttachmentInstalled(AttachmentType.bayonet))
                    {
                        inv.RemoveVirtualItem(bayonetItem);
                        if (AiCore.IsInMultiplayerRoom())
                        {
                            SyncSoldier syncher = bayH.Syncher ?? soldier.soldierSyncher ?? soldier.GetSyncher();
                            if (syncher != null)
                                syncher.OnEquipmentChanged();
                        }

                        AiCore.LogDebug("Bayonet", $"{AiCore.GetCachedFaction(soldier)} fixed detachable bayonet '{attId}' on '{heldGun.name}'.");
                        bayH.Combat.LastBayonetCheckedGunPtr = heldGun.Pointer;
                        return;
                    }

                    UnityEngine.Object.Destroy(instGo);
                }

                bayH.Combat.LastBayonetCheckedGunPtr = heldGun.Pointer;
            }
            catch (Exception ex)
            {
                AiCore.ReportPatchException("TryEnsureBayonetMounted", ex);
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
            if (ch != null && meleeEnemy != null)
            {
                ch.Combat.PointBlankFaceUntil = now + 0.65f;
                ch.Combat.CachedMeleeEnemy = meleeEnemy;
            }
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

            if (!soldier.HasBayonetReady &&
                !soldier.HoldsMeleeWeapon &&
                soldier.InventoryKnife != null &&
                !soldier.isInHandsAnimation &&
                AiCore.IsHeldGunEmptyOrReloading(soldier, soldier.GetHeldGun()) &&
                AiCore.TryConsumeBotRpcBudget(0.35f))
            {
                soldier.ToggleInventoryKnife();
            }

            // Maintain clean weapon-reach standoff matched to bayonet / combat knife / pistol-whip reach
            // while avoiding capsule overlap.
            float standoffDist;
            float minPersonalSpace;
            if (soldier.HasBayonetReady)
            {
                standoffDist = 1.25f;
                minPersonalSpace = 0.85f;
            }
            else if (soldier.HoldsMeleeWeapon)
            {
                standoffDist = 0.92f;
                minPersonalSpace = 0.65f;
            }
            else
            {
                standoffDist = 0.82f;
                minPersonalSpace = 0.58f;
            }

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

                    if (VisionConfig.EnableEmergentDecisionEngine.Value)
                    {
                        if (!h.Emergent.HasDecision || h.Emergent.CurrentDecision.Step0 != EmergentEngine.EmergentActionId.SelfBandage)
                            return false;

                        if (__instance.visibleTarget != null && __instance.distFromTarget <= 4.5f)
                        {
                            CountBandageBlocked(h.Ptr, now);
                            BotActionTrace.RecordEvent(soldier, BotTraceEventKind.BandageBlocked, "enemy_melee_range", __instance.distFromTarget);
                            return false;
                        }

                        BotActionTrace.RecordEvent(
                            soldier,
                            BotTraceEventKind.BandageStart,
                            soldier.IsOnCover() ? "in_cover" : "harm_approved",
                            __instance.distFromTarget);

                        ApplyBandageLowPose(__instance, soldier, h, now);
                        return true;
                    }

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

                    ApplyBandageLowPose(__instance, soldier, h, now);
                }
                catch { }
                return true;
            }
        }

        [HarmonyPatch(typeof(Soldier), nameof(Soldier.UseBandages))]
        public static class Soldier_UseBandages_Patch
        {
            public static void Prefix(Soldier __instance, VirtualBandages __0)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null || __0 == null ||
                        !__instance.IsAI() || AiCore.IsAnyHumanPlayerSoldier(__instance) || !__instance.isBleeding || !__instance.IsAlive)
                        return;

                    if (!AiCore.IsLocallyOwnedBot(__instance) || AiCore.IsSoldierInVehicleOrTransition(__instance))
                        return;

                    if (!__instance.CanPlaySpecialAnim())
                        return;

                    AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandles(__instance);
                    AiCore.InvalidateSoldierInventoryCache(h);
                    SoldierAI ai = h != null ? h.Ai : __instance.aiController;
                    if (ai != null)
                    {
                        ApplyBandageLowPose(ai, __instance, h, Time.time);
                    }
                }
                catch { }
            }
        }

        internal static bool TryResolvePointBlankTargetSoldier(
            SoldierAI ai,
            out Soldier enemySoldier,
            out bool isExposedEmplacementGunner)
        {
            enemySoldier = null;
            isExposedEmplacementGunner = false;
            if (ai == null || ai.visibleTarget == null)
                return false;

            Soldier s = ai.visibleTarget.TryCast<Soldier>();
            if (s != null)
            {
                if (!s.IsAlive || s.HasSurrended())
                    return false;

                if (!s.IsOnVehicle())
                {
                    enemySoldier = s;
                    return true;
                }

                Seats seat = s.currentVehicleSeat;
                Vehicle v = s.GetCurrentVehicle();
                if (seat != null && seat.showUnitSet && (v == null || v.TryCast<MovableVehicle>() == null))
                {
                    enemySoldier = s;
                    isExposedEmplacementGunner = true;
                    return true;
                }
                return false;
            }

            Vehicle veh = ai.visibleTarget.TryCast<Vehicle>();
            if (veh != null && veh.TryCast<MovableVehicle>() == null && veh.seats != null)
            {
                var seats = veh.seats;
                for (int i = 0; i < seats.Length; i++)
                {
                    Seats seat = seats[i];
                    if (seat != null && seat.showUnitSet && seat.unitSet != null &&
                        seat.unitSet.IsAlive && !seat.unitSet.HasSurrended())
                    {
                        enemySoldier = seat.unitSet;
                        isExposedEmplacementGunner = true;
                        return true;
                    }
                }
            }

            return false;
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
                AiCore.IsSoldierInVehicleOrTransition(soldier) ||
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

            if (!TryResolvePointBlankTargetSoldier(ai, out enemySoldier, out bool isExposedEmplacementGunner))
                return false;

            Vector3 myPos = soldier.transform.position;
            Vector3 enemyPos = enemySoldier.transform.position;
            if (Mathf.Abs(enemyPos.y - myPos.y) > 1.65f)
                return false;

            float now = Time.time;
            bool hasBayonet = soldier.HasBayonetReady;
            bool holdsMelee = soldier.HoldsMeleeWeapon;
            bool isChargingOrder = soldier.IsCharging();
            GenericGun heldGun = soldier.GetHeldGun();
            bool isPistol = heldGun != null && heldGun.IsPistol();
            bool gunEmptyOrReloading = AiCore.IsHeldGunEmptyOrReloading(soldier, heldGun);
            if (meleeEvalH != null && now < meleeEvalH.Combat.MeleeWhiffFallbackUntil && !gunEmptyOrReloading && !holdsMelee)
            {
                if (ai.knifeCharge)
                    ai.knifeCharge = false;
                return false;
            }
            // When facing an exposed static-emplacement gunner while holding a loaded firearm
            // (and not charging or holding a melee weapon), shoot point-blank instead of lunging into the tripod!
            if (isExposedEmplacementGunner && !gunEmptyOrReloading && !holdsMelee && !isChargingOrder)
                return false;

            // CRITICAL: Use heldGun.boltActionRequired (isManualBoltOperated && bolt_required @ 0x180876300),
            // NEVER raw heldGun.bolt_required!
            bool boltCyclingClose = soldier.CantAimDueToBoltActionUsage ||
                                    (heldGun != null && (heldGun.boltActionRequired || heldGun.IsReloadingOrUsingBolt()));

            string myFac = AiCore.GetCachedFaction(soldier);
            bool hasCloseGunfire = VisionPatches.TryGetNearestEnemyGunfire(myPos, myFac, 15.0f, 2.5f, out float nearGunfireDist) ||
                                   VisionPatches.HasFiredRecently(enemySoldier, 2.5f);

            const float unbayonetedStrikeRange = 1.15f;
            const float meleeWeaponStrikeRange = 1.28f;
            const float bayonetStrikeRange = 1.68f;

            if (holdsMelee)
            {
                lungeRange = 5.6f;
                ceaseFireRange = 5.6f;
                strikeRange = meleeWeaponStrikeRange;
            }
            else if (isPistol && !gunEmptyOrReloading && !hasBayonet)
            {
                // Keep firing a loaded pistol at CQB range unless already inside physical contact reach.
                lungeRange = isChargingOrder ? 1.45f : 0.95f;
                ceaseFireRange = 0.72f;
                strikeRange = unbayonetedStrikeRange;
            }
            else if (isChargingOrder)
            {
                lungeRange = hasBayonet ? 5.2f : 3.6f;
                ceaseFireRange = (gunEmptyOrReloading || boltCyclingClose) ? 4.5f : 2.5f;
                strikeRange = hasBayonet ? bayonetStrikeRange : unbayonetedStrikeRange;
            }
            else if (gunEmptyOrReloading)
            {
                lungeRange = hasBayonet ? 4.2f : 3.2f;
                ceaseFireRange = lungeRange;
                strikeRange = hasBayonet ? bayonetStrikeRange : unbayonetedStrikeRange;
            }
            else if (hasCloseGunfire)
            {
                // Active gunfire within 15m while holding a loaded firearm (and NOT on a squad bayonet charge):
                // Do NOT break stance/cover to sprint into melee! Keep point-shooting unless the enemy physically
                // enters immediate contact reach.
                lungeRange = boltCyclingClose ? (hasBayonet ? 2.35f : 1.85f) : (hasBayonet ? 1.85f : 1.55f);
                ceaseFireRange = boltCyclingClose ? lungeRange : 1.25f;
                strikeRange = hasBayonet ? bayonetStrikeRange : unbayonetedStrikeRange;

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
                strikeRange = hasBayonet ? bayonetStrikeRange : unbayonetedStrikeRange;
            }

            if (isExposedEmplacementGunner)
            {
                strikeRange = Mathf.Max(strikeRange, 1.55f);
                lungeRange = Mathf.Max(lungeRange, strikeRange + 0.35f);
            }

            if (dist > lungeRange)
                return false;

            // 1-on-1 CQB melee discipline: never lunge into 1-vs-many melee when holding a loaded firearm.
            if (!gunEmptyOrReloading && !holdsMelee && dist > 2.0f)
            {
                int nearbyEnemies = 0;
                int wCount = AiCore.WorldSoldierCount;
                var wSoldiers = AiCore.WorldSoldiers;
                for (int i = 0; i < wCount; i++)
                {
                    Soldier ws = wSoldiers[i].Ref;
                    if (ws == null || !ws.IsAlive || ws.HasSurrended())
                        continue;
                    if (!AiCore.AreEnemyFactions(myFac, wSoldiers[i].Faction))
                        continue;
                    if ((wSoldiers[i].Pos - enemyPos).sqrMagnitude <= 36.0f && ++nearbyEnemies >= 2)
                        return false;
                }
            }

            // Anti-stacking cap: allow at most 2 friendly bots to actively melee the same target;
            // additional bots keep squad spacing and cover with firearms instead of dogpiling.
            if (!TryClaimMeleeTargetSlot(soldier, enemySoldier, now, out meleeSlotIndex))
                return false;

            if (!isExposedEmplacementGunner)
            {
                Vector3 toEnemy = (enemyPos + Vector3.up * 1.05f) - (myPos + Vector3.up * 1.05f);
                float checkDist = Mathf.Min(dist - 0.25f, 3.5f);
                if (checkDist > 0.30f &&
                    AiCore.IsDirectionBlockedByEnvironment(myPos + Vector3.up * 1.05f, toEnemy.normalized, checkDist, soldier.transform.root))
                {
                    return false;
                }
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
            if (toEnemy.sqrMagnitude > 0.01f)
            {
                soldier.RotateToward(toEnemy, Time.deltaTime * 3.0f);
                if (AiCore.HorizontalAngleToDirection(soldier, toEnemy) > 24f)
                    return;
            }

            // Native Soldier+<MeleeDamageCR>d__489 (0x18077EDDA) casts a horizontal 0.30m-radius OverlapCapsule
            // forward from Soldier.FacePosition() (animated headBone.position @ [rdx+160h]). Standing FacePosition().y (~1.62m)
            // puts the capsule bottom at ~1.12m, passing completely over prone/crouched enemies or seated low tripod gunners!
            // Crouch first and wait briefly so the animated headBone drops within strike height.
            bool isSeatedLowGunner = enemy.IsOnVehicle() && (soldier.FacePosition().y - enemy.FacePosition().y) > 0.38f;
            if (enemy.m_pose == SoldierPose.Prone || enemy.m_pose == SoldierPose.Crouch || isSeatedLowGunner)
            {
                if (soldier.m_pose != SoldierPose.Crouch || ai.actualPose != SoldierPose.Crouch)
                    SetBotPose(ai, soldier, SoldierPose.Crouch, true);

                if (AiCore.CanBotChangePoseAutonomously(soldier) &&
                    ((!enemy.IsOnVehicle() && (soldier.FacePosition().y - enemy.transform.position.y) > 1.34f) ||
                     (isSeatedLowGunner && soldier.m_pose != SoldierPose.Crouch)))
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
            {
                meleeH.Combat.NextMeleeAttemptTime = Time.time + 0.12f;
                return;
            }

            meleeH.Combat.NextMeleeAttemptTime = Time.time + 0.45f;
            if (soldier.isSprinting)
                soldier.isSprinting = false;
            soldier.SetGunTriggerPressed(false);
            soldier.Melee();
            MeleeAttacksTriggered++;

            float swingNow = Time.time;
            if (meleeH.Combat.LastMeleeSwingTargetPtr == enemy.Pointer && (swingNow - meleeH.Combat.LastMeleeSwingTime) < 3.5f)
            {
                if (meleeH.Combat.ConsecutiveMeleeSwingsOnTarget < 250)
                    meleeH.Combat.ConsecutiveMeleeSwingsOnTarget++;
            }
            else
            {
                meleeH.Combat.LastMeleeSwingTargetPtr = enemy.Pointer;
                meleeH.Combat.ConsecutiveMeleeSwingsOnTarget = 1;
            }
            meleeH.Combat.LastMeleeSwingTime = swingNow;
            if (meleeH.Combat.ConsecutiveMeleeSwingsOnTarget >= 3 &&
                enemy.IsAlive &&
                !enemy.IsInjured)
            {
                meleeH.Combat.MeleeWhiffFallbackUntil = swingNow + 4.0f;
                meleeH.Combat.ConsecutiveMeleeSwingsOnTarget = 0;
                if (swingNow >= meleeH.NextAnomalyAlertTime)
                {
                    meleeH.NextAnomalyAlertTime = swingNow + 8.0f;
                    AiCore.AnomalyMeleeCount++;
                    BotActionTrace.RecordEvent(soldier, BotTraceEventKind.AnomalyAlert, "melee_whiff_loop", Mathf.Sqrt(toEnemy.sqrMagnitude), 3f);
                }
            }
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

        [HarmonyPatch(typeof(Soldier._MeleeDamageCR_d__489), nameof(Soldier._MeleeDamageCR_d__489.MoveNext))]
        public static class Soldier_MeleeDamageCR_Patch
        {
            public static void Prefix(Soldier._MeleeDamageCR_d__489 __instance)
            {
                try
                {
                    if (__instance == null || __instance.__1__state != 1)
                        return;

                    if (!VisionConfig.EnableMod.Value || !VisionConfig.EnablePointBlankMelee.Value)
                        return;

                    Soldier s = __instance.__4__this;
                    if (s == null || AiCore.IsAnyHumanPlayerSoldier(s))
                        return;

                    bool isEmpGunner = false;
                    if (s.aiController != null &&
                        TryResolvePointBlankTargetSoldier(s.aiController, out Soldier target, out isEmpGunner) &&
                        target != null)
                    {
                        Vector3 toTarget = target.transform.position - s.transform.position;
                        toTarget.y = 0f;
                        if (toTarget.sqrMagnitude > 0.01f &&
                            toTarget.sqrMagnitude <= 2.6f * 2.6f &&
                            AiCore.HorizontalAngleToDirection(s, toTarget) <= 55f)
                        {
                            s.RotateToward(toTarget, 1.0f);
                        }
                    }

                    if (__instance.mountedBlade == null)
                    {
                        float minReach = isEmpGunner ? 1.50f : 1.15f;
                        if (__instance.dist < minReach)
                            __instance.dist = minReach;
                    }
                }
                catch { }
            }
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
                AiCore.IsSoldierInVehicleOrTransition(soldier) ||
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
            bool isEncircledRegular = !isSkittish && !isGreenRookie && !isDryRegular && vet01 < 0.60f && !isFanaticDoctrine;
            if (!isSkittish && !isGreenRookie && !isDryRegular && !isEncircledRegular)
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
            else if (isEncircledRegular)
            {
                reqSupp = isBleedingOrDry ? 55f : 72f;
            }
            else
            {
                reqSupp = isBleedingOrDry ? 70f : 86f;
            }

            if (isFanaticDoctrine)
            {
                reqSupp += 22f;
            }

            reqSupp += doc.SurrenderSuppDelta;
            if (isCompletelyOutOfAmmo)
            {
                AiCore.BattleTheaterSnapshot surTheater = AiCore.GetBattleTheaterSnapshot();
                if (surTheater.Atmosphere == AiCore.BattleAtmosphereArchetype.KesselEncircledPocket &&
                    !string.IsNullOrEmpty(surTheater.DefendersFactionId) &&
                    AiCore.AreSameSideFactions(soldier.faction, surTheater.DefendersFactionId))
                {
                    reqSupp -= 10f;
                }
            }
            reqSupp = Mathf.Clamp(reqSupp, 42f, 98f);

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
                if (AiCore.AreSameSideFactions(myFaction, player.faction))
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

            if (isEncircledRegular && (enemyCloseCount < 4 || friendlyCloseCount > 0 || closestEnemyDistSq > 12.0f * 12.0f))
            {
                return false;
            }

            bool isOverrun = isEncircledRegular ||
                             enemyCloseCount > friendlyCloseCount ||
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
                string reason = isEncircledRegular
                    ? "Encircled Regular"
                    : (isSkittish ? "Skittish/Shell-Shocked" : "Green Rookie Overrun");
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
        internal static void ClearAllFriendlyTargetLocksOnSoldier(Soldier target)
        {
            if (ReferenceEquals(target, null) || target == null || target.Pointer == IntPtr.Zero)
                return;

            IntPtr targetPtr = target.Pointer;
            AiCore.EnsureWorldSnapshot();
            int count = AiCore.WorldSoldierCount;
            var soldiers = AiCore.WorldSoldiers;
            for (int i = 0; i < count; i++)
            {
                ref AiCore.SoldierSnapshot snap = ref soldiers[i];
                if (snap.Ptr == IntPtr.Zero || snap.Ptr == targetPtr)
                    continue;

                AiCore.SoldierFastHandles sh = snap.Handles;
                Soldier s = snap.Ref;
                SoldierAI ai = sh != null ? sh.Ai : (s != null ? s.aiController : null);

                if (sh != null)
                {
                    if (sh.Vision.LastKnownTarget.Target != null &&
                        sh.Vision.LastKnownTarget.Target.Pointer == targetPtr)
                    {
                        sh.Vision.LastKnownTarget = default;
                    }

                    if (!ReferenceEquals(sh.Combat.CachedMeleeEnemy, null) &&
                        sh.Combat.CachedMeleeEnemy != null &&
                        sh.Combat.CachedMeleeEnemy.Pointer == targetPtr)
                    {
                        sh.Combat.CachedMeleeEnemy = null;
                    }
                }

                if (!ReferenceEquals(ai, null) && ai != null && AiCore.GetRawVisibleTargetPtr(ai) == targetPtr)
                {
                    ai.visibleTarget = null;
                    ai.allowFireAtEnemy = false;
                    ai.targetInWeaponRange = false;
                    ai.aimingEnemy = false;
                    ai.shotFiresEnd = 0f;
                    if (s != null)
                    {
                        s.CurrentVisibleTarget = null;
                        s.SetGunTriggerPressed(false);
                    }
                    if (sh != null)
                        sh.Combat.RotateFireFrame.Frame = -1;
                }
                else if (s != null && s.CurrentVisibleTarget != null && s.CurrentVisibleTarget.Pointer == targetPtr)
                {
                    s.CurrentVisibleTarget = null;
                    s.SetGunTriggerPressed(false);
                }
            }

            if (_turretAiByTurretPtr.Count > 0)
            {
                foreach (var kvp in _turretAiByTurretPtr)
                {
                    TurretAI tAi = kvp.Value;
                    if (ReferenceEquals(tAi, null) || tAi == null)
                        continue;

                    Spottable curVis = tAi.CurrentVisibleTarget;
                    if (!ReferenceEquals(curVis, null) && curVis != null && curVis.Pointer == targetPtr)
                    {
                        tAi.CurrentVisibleTarget = null;
                        tAi.targetInWeaponRange = false;
                        if (tAi.turret != null)
                            tAi.turret.SetGunTriggerPressed(false, null);
                    }
                }
            }
        }

        private static bool IsDownedPatientUndergoingEnemySyringeCapture(Soldier patient, AiCore.SoldierFastHandles patH)
        {
            if (patient == null || patient.Pointer == IntPtr.Zero || patient.IsDead)
                return false;

            float now = Time.time;
            if (patH != null && now < patH.EnemySyringeCaptureUntil)
                return true;

            if (patient.transform != null)
            {
                string patFac = patH != null ? AiCore.GetCachedFaction(patH, now) : AiCore.GetCachedFaction(patient);
                if (!string.IsNullOrEmpty(patFac))
                {
                    Vector3 patPos = patient.transform.position;
                    const float maxCaptureDistSq = 6.5f * 6.5f;

                    Soldier localCtrl = Soldier.CurrentControlledSoldierOrNull();
                    if (localCtrl != null &&
                        localCtrl.Pointer != IntPtr.Zero &&
                        localCtrl.Pointer != patient.Pointer &&
                        localCtrl.IsAlive &&
                        !localCtrl.IsInjured &&
                        !localCtrl.HasSurrended() &&
                        localCtrl.transform != null)
                    {
                        string localFac = AiCore.GetCachedFaction(localCtrl);
                        if (!string.IsNullOrEmpty(localFac) &&
                            AiCore.AreEnemyFactions(patFac, localFac) &&
                            (localCtrl.transform.position - patPos).sqrMagnitude <= maxCaptureDistSq)
                        {
                            return true;
                        }
                    }

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

                        Vector3 livePos = (snap.Handles != null && snap.Handles.Tr != null)
                            ? snap.Handles.Tr.position
                            : snap.Pos;
                        if ((livePos - patPos).sqrMagnitude <= maxCaptureDistSq)
                            return true;
                    }
                }
            }

            return false;
        }

        internal static void EnsureSurrenderedPrisonerAnimatorAndPose(Soldier s)
        {
            if (s == null || s.Pointer == IntPtr.Zero || !s.IsAlive || s.IsInjured || s.IsDead || !s.HasSurrended())
                return;

            AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandles(s);
            if (h != null && h.IsRetreatDespawning)
                return;

            if (s.IsCarryingBody())
                s.CarryBody(null);

            s.SetGunTriggerPressed(false);
            s.pointGunEndTime = 0f;
            s.alertEndTime = 0f;

            SoldierAI ai = h != null ? h.Ai : s.aiController;
            if (!ReferenceEquals(ai, null) && ai != null)
            {
                ai.actualPose = SoldierPose.Idle;
                ai.moveDirectlyToward_end = 0f;
                ai.visibleTarget = null;
                ai.allowFireAtEnemy = false;
                ai.shotFiresEnd = 0f;
            }

            if (s.m_pose != SoldierPose.Idle && !AiCore.IsSoldierInVehicleOrTransition(s))
            {
                s.SetPose(SoldierPose.Idle, true, false, -1f);
            }

            Animator anim = s.m_Animator;
            if (anim != null)
            {
                if (anim.layerCount > 1)
                    anim.SetLayerWeight(1, 1.0f);
                anim.ResetTrigger("StartInjured");
                anim.SetBool("Injured", false);
                anim.SetBool("Surrended", true);
            }

            ClearAllFriendlyTargetLocksOnSoldier(s);
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

                    if (__instance.IsDead || AiCore.IsSoldierInVehicleOrTransition(__instance))
                        return false;

                    if (!__instance.IsAlive || __instance.IsInjured)
                    {
                        AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandles(__instance);
                        if (IsDownedPatientUndergoingEnemySyringeCapture(__instance, h))
                        {
                            // Revive the patient BEFORE SurrenderSynched -> Surrender runs so Upper Body Layer 1
                            // weight is 1.0f and the subsequent SetIncapacitated(false) in UseSyryngeCR is a no-op!
                            // Keep EnemySyringeCaptureUntil active so Soldier_Surrender_Patch.Prefix and friendly AI
                            // target filters continue protecting this unit through revival + surrender!
                            if (h != null)
                                h.EnemySyringeCaptureUntil = Mathf.Max(h.EnemySyringeCaptureUntil, Time.time + 3.5f);
                            __instance.SetIncapacitated(false);
                            ClearAllFriendlyTargetLocksOnSoldier(__instance);
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

                    if (__instance.IsDead || AiCore.IsSoldierInVehicleOrTransition(__instance))
                        return false;

                    if (__instance.IsCarryingBody())
                        __instance.CarryBody(null);

                    if (!__instance.IsAlive || __instance.IsInjured)
                    {
                        AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandles(__instance);
                        if (IsDownedPatientUndergoingEnemySyringeCapture(__instance, h))
                        {
                            if (h != null)
                                h.EnemySyringeCaptureUntil = Mathf.Max(h.EnemySyringeCaptureUntil, Time.time + 3.5f);
                            __instance.SetIncapacitated(false);
                            ClearAllFriendlyTargetLocksOnSoldier(__instance);
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

                    if (__instance.HasSurrended())
                    {
                        NotifySquadLeaderLost(__instance, Time.time);
                        BotActionTrace.RecordEvent(
                            __instance,
                            BotTraceEventKind.Surrender,
                            __instance.IsOnVehicle() ? "on_vehicle" : "on_foot");
                    }

                    EnsureSurrenderedPrisonerAnimatorAndPose(__instance);
                    VeteranAndTacticsPatches.RefreshSquadSoldierLabelIfShown(__instance);
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
                        if (__instance.IsCarryingBody())
                        {
                            __instance.CarryBody(null);
                        }

                        // Clear suppressionValue when downed so BattleManager.SurrenderUnitsOnMissionAreas skips this casualty,
                        // clear Animator "Surrended", and force Injured/LayerWeight(1, 0f) even when downed in a vehicle seat.
                        __instance.suppressionValue = 0;
                        MarkPlayerAttributionIncapacitated(__instance);
                        VeteranAndTacticsPatches.NotifyFriendlyHitOrCasualty(__instance, Vector3.zero, false, isFatalOrDown: true);
                        VeteranAndTacticsPatches.NotifySquadLeaderLost(__instance, Time.time);
                        if (__instance.m_Animator != null)
                        {
                            __instance.m_Animator.SetBool("Surrended", false);
                            __instance.m_Animator.SetBool("Injured", true);
                            if (__instance.m_Animator.layerCount > 1)
                                __instance.m_Animator.SetLayerWeight(1, 0f);
                        }
                    }
                    else
                    {
                        if (__instance.m_Animator != null)
                        {
                            __instance.m_Animator.ResetTrigger("StartInjured");
                            __instance.m_Animator.SetBool("Injured", false);
                        }

                        if (__instance.IsAlive && !__instance.IsInjured && __instance.HasSurrended())
                        {
                            EnsureSurrenderedPrisonerAnimatorAndPose(__instance);
                        }
                    }

                    VeteranAndTacticsPatches.RefreshSquadSoldierLabelIfShown(__instance);
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
                        anim.SetBool("Surrended", false);
                        anim.SetBool("Injured", true);
                        if (anim.layerCount > 1)
                            anim.SetLayerWeight(1, 0f);
                        if (!__instance.IsOnVehicle())
                        {
                            anim.ResetTrigger("StartInjured");
                            __instance.ForceAnimatorState("Injured", 0, 0f);
                        }
                    }
                    else if (__instance.IsAlive && !__instance.IsInjured)
                    {
                        anim.ResetTrigger("StartInjured");
                        anim.SetBool("Injured", false);
                        if (__instance.HasSurrended())
                        {
                            AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandles(__instance);
                            if (h != null && h.IsRetreatDespawning)
                            {
                                anim.SetBool("Surrended", false);
                            }
                            else
                            {
                                anim.SetBool("Surrended", true);
                                if (anim.layerCount > 1)
                                    anim.SetLayerWeight(1, 1.0f);
                            }
                        }
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Soldier), nameof(Soldier.SetAnimatorController))]
        public static class Soldier_SetAnimatorController_Patch
        {
            public static void Postfix(Soldier __instance, RuntimeAnimatorController __0, bool __1, string __2)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null || __instance.Pointer == IntPtr.Zero || __0 == null)
                        return;

                    // Fix vanilla Soldier.SetAnimatorController (0x1807636F3: cmp byte ptr [rbx+49Eh],0; jne return)
                    // aborting when SetWalkingAnimatorControllerCR tries to replace a vehicle seat AnimatorController
                    // on a bailed-out soldier whose surrended flag (+0x49E) became true during exit.
                    if (__instance.HasSurrended() &&
                        (!__instance.IsOnVehicle() || __1) &&
                        (__instance.IsAlive || __instance.IsInjured))
                    {
                        Animator anim = __instance.m_Animator;
                        if (anim != null && anim.runtimeAnimatorController != __0)
                        {
                            __instance.cur_anim_controller_name = !string.IsNullOrEmpty(__2) ? __2 : __0.name;
                            anim.runtimeAnimatorController = __0;
                            __instance.RefreshAnimatorState();
                            if (AiCore.IsTrueSurrenderedPrisoner(__instance))
                            {
                                EnsureSurrenderedPrisonerAnimatorAndPose(__instance);
                            }
                        }
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Soldier), nameof(Soldier.OnRetreat))]
        public static class Soldier_OnRetreat_Patch
        {
            public static bool Prefix(Soldier __instance)
            {
                try
                {
                    if (__instance == null || __instance.Pointer == IntPtr.Zero)
                        return true;

                    if (VisionConfig.EnableMod.Value &&
                        AiCore.IsInMultiplayerRoom() &&
                        !AiCore.IsAnyHumanPlayerSoldier(__instance) &&
                        (!AiCore.IsLocallyOwnedSoldier(__instance) ||
                         (__instance.joinedSquad != null && AiCore.IsSquadOwnedOrOccupiedByRemoteHuman(__instance.joinedSquad))))
                    {
                        return false;
                    }

                    AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandles(__instance);
                    if (h != null)
                    {
                        h.IsRetreatDespawning = true;
                        h.RetreatDespawnForceCleanupAt = Time.time + 0.35f;
                    }
                    BotActionTrace.RecordEvent(__instance, BotTraceEventKind.RetreatDespawn, "mission_task_retreat");
                }
                catch { }
                return true;
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
                        h.RetreatDespawnForceCleanupAt = 0f;
                        h.EnemySyringeCaptureUntil = 0f;
                        h.Goap.FootStuck.SurrenderedFromTrap = false;
                        h.Goap.FootStuck.RoomTrapSince = 0f;
                        h.Goap.FootStuck.CliffStepOffUntil = 0f;
                    }
                    BotActionTrace.RecordEvent(__instance, BotTraceEventKind.UnSurrender, "liberated");
                }
                catch { }
            }
        }

        // =====================================================================
        // Tactical Smoke Aim & Ballistic Arc Intercept (Vanilla + Mod Unified)
        // Native Soldier.Throw -> InstantiateAndThrowAfter (<>d__594) falls back to
        // Soldier.LookRotation() whenever aimTarget == null OR CalculateBallisticThrow
        // returns false (>max range). Guarantee every AI smoke throw targets between
        // thrower and ground threat via CalculateBallisticThrow instead of backwards!
        // =====================================================================
        private static bool _activeSmokeBallisticOverride;
        private static Vector3 _activeSmokeBallisticTarget;

        internal static bool TryHandleBotSmokeThrowAim(Soldier soldier, VirtualItem item, ref Spottable aimTarget)
        {
            try
            {
                if (!VisionConfig.EnableMod.Value || soldier == null || item == null)
                    return true;

                AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandles(soldier);
                if (!AiCore.IsLocallyOwnedBot(h))
                    return true;

                if (item.TryCast<VirtualSmokeGrenade>() == null)
                    return true;

                float now = Time.time;
                if (now <= h.Combat.PendingSmokeAimUntil)
                {
                    if (aimTarget == null || !AiCore.IsValidAliveSpottable(aimTarget) || aimTarget.IsAirVehicle())
                        aimTarget = soldier.Cast<Spottable>();
                    return true;
                }

                Vector3 myPos = h.Tr != null ? h.Tr.position : (soldier.transform != null ? soldier.transform.position : Vector3.zero);
                if (!TryResolveBotSmokeThreatPosition(soldier, h, myPos, now, aimTarget, out Vector3 threatPos, out Spottable resolvedTarget))
                    return false;

                bool isDefensive = soldier.GetSuppressionValue() >= 55f ||
                                   (h.Ai != null && !h.Ai.HasEnemyAndCanFireAtHim);
                if (!TryPrepareBotSmokeThrowAim(soldier, h, item, threatPos, isDefensive, now, checkRpcBudget: false))
                    return false;

                aimTarget = resolvedTarget ?? soldier.Cast<Spottable>();
                return true;
            }
            catch
            {
                return true;
            }
        }

        [HarmonyPatch(typeof(Soldier._InstantiateAndThrowAfter_d__594), nameof(Soldier._InstantiateAndThrowAfter_d__594.MoveNext))]
        public static class Soldier_InstantiateAndThrowAfter_MoveNext_Patch
        {
            public static void Prefix(Soldier._InstantiateAndThrowAfter_d__594 __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null || __instance.__1__state != 2)
                        return;

                    Soldier thrower = __instance.__4__this;
                    if (!AiCore.IsLocallyOwnedBot(thrower) ||
                        !AiCore.TryGetSoldierHandles(thrower.Pointer, out AiCore.SoldierFastHandles h))
                    {
                        return;
                    }

                    if (Time.time > h.Combat.PendingSmokeAimUntil)
                        return;

                    if (h.Combat.PendingSmokePrefabPtr != IntPtr.Zero &&
                        __instance.objPrefab != null &&
                        __instance.objPrefab.Pointer != h.Combat.PendingSmokePrefabPtr)
                    {
                        return;
                    }

                    _activeSmokeBallisticOverride = true;
                    _activeSmokeBallisticTarget = h.Combat.PendingSmokeAimPoint;
                    h.Combat.PendingSmokeAimUntil = 0f;
                    h.Combat.PendingSmokePrefabPtr = IntPtr.Zero;

                    if (__instance.aimTarget == null ||
                        !AiCore.IsValidAliveSpottable(__instance.aimTarget) ||
                        __instance.aimTarget.IsAirVehicle())
                    {
                        __instance.aimTarget = thrower.Cast<Spottable>();
                    }
                }
                catch { }
            }

            public static Exception Finalizer(Exception __exception)
            {
                _activeSmokeBallisticOverride = false;
                return __exception;
            }
        }

        [HarmonyPatch(typeof(Soldier), nameof(Soldier.CalculateBallisticThrow))]
        public static class Soldier_CalculateBallisticThrow_Patch
        {
            public static bool Prefix(Vector3 __0, ref Vector3 __1, float __2)
            {
                if (!_activeSmokeBallisticOverride)
                    return true;

                try
                {
                    Vector3 target = _activeSmokeBallisticTarget;
                    Vector3 flat = new Vector3(target.x - __0.x, 0f, target.z - __0.z);
                    float flatDist = flat.magnitude;
                    float g = Mathf.Max(1.0f, Mathf.Abs(Physics.gravity.y));
                    float angleRad = Mathf.Clamp(Soldier.aiThrowAngleDeg, 15.0f, 75.0f) * Mathf.Deg2Rad;
                    float safeMaxHoriz = Mathf.Max(4.0f, (__2 * __2 / g) * 0.68f * Mathf.Clamp(Mathf.Sin(2.0f * angleRad), 0.50f, 1.0f));
                    float clampedDist = Mathf.Clamp(flatDist, 2.0f, safeMaxHoriz);
                    Vector3 dir = flatDist > 0.001f ? (flat / flatDist) : Vector3.forward;

                    float targetY = Mathf.Clamp(target.y, __0.y - 6.0f, __0.y + 1.25f);
                    float maxCeilingY = __0.y + clampedDist * Mathf.Tan(angleRad) - Mathf.Max(1.0f, clampedDist * 0.25f);
                    if (targetY > maxCeilingY)
                        targetY = maxCeilingY;

                    __1 = new Vector3(__0.x + dir.x * clampedDist, targetY, __0.z + dir.z * clampedDist);
                }
                catch { }
                return true;
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
                AiCore.IsSoldierInVehicleOrTransition(medic) ||
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

        internal static void ReleaseMedicClaimForSoldier(IntPtr medicPtr, IntPtr patientPtr)
        {
            if (medicPtr == IntPtr.Zero || patientPtr == IntPtr.Zero || _medicPatientClaims.Count == 0)
                return;

            if (!_medicPatientClaims.TryGetValue(patientPtr, out MedicPatientClaim claim))
                return;

            if (claim.PrimaryMedic != null && claim.PrimaryMedic.Pointer == medicPtr)
            {
                if (claim.BackupMedic != null && claim.BackupMedic.Pointer != IntPtr.Zero)
                {
                    claim.PrimaryMedic = claim.BackupMedic;
                    claim.BackupMedic = null;
                    _medicPatientClaims[patientPtr] = claim;
                }
                else
                {
                    _medicPatientClaims.Remove(patientPtr);
                }
            }
            else if (claim.BackupMedic != null && claim.BackupMedic.Pointer == medicPtr)
            {
                claim.BackupMedic = null;
                _medicPatientClaims[patientPtr] = claim;
            }
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

                    float now = Time.time;
                    // Match vanilla Squad.MedicRoutine (@ 0x1808079B0): check !hasSyringe FIRST before pruning global medic claims!
                    if (!character.hasSyringe ||
                        !character.IsAlive ||
                        character.IsInjured ||
                        (character.Params != null && (!character.Params.canDoMedic || !character.Params.canMove)) ||
                        character.IsCharging() ||
                        character.isInBodyAnimation ||
                        now < mh.Goap.ActiveSyringeHealUntil ||
                        character.HasSurrended() ||
                        AiCore.IsSoldierInVehicleOrTransition(character) ||
                        character.IsCarryingBody() ||
                        mh.Tr == null)
                    {
                        return false;
                    }

                    IntPtr medicPtr = mh.Ptr;
                    PruneAndValidateMedicClaims(medicPtr, out IntPtr activePatientPtr);

                    // If this medic is already actively heading to a valid incapacitated (!IsDead && IsInjured) patient, let him continue!
                    if (activePatientPtr != IntPtr.Zero)
                    {
                        IntPtr curDestRaw = AiCore.GetRawTargetDestinationPtr(character);
                        if (curDestRaw != IntPtr.Zero && curDestRaw != mh.CachedDestPtr)
                            mh.NextDestCheckTime = 0f;
                        if (AiCore.ResolveTargetDestination(mh) == AiCore.CachedDestKind.MedicHeal &&
                            mh.CachedHealDest != null)
                        {
                            MedicHealingDestination curHeal = mh.CachedHealDest;
                            Soldier curPat = curHeal.soldierToHealUp;
                            if (curPat != null &&
                                !curPat.IsDead &&
                                curPat.IsInjured &&
                                !curPat.IsCarried())
                            {
                                string myActiveFac = AiCore.GetCachedFaction(mh, now);
                                string patActiveFac = AiCore.GetCachedFaction(curPat);
                                if (AiCore.AreEnemyFactions(myActiveFac, patActiveFac) &&
                                    !IsMedicConfidentlySafeToHealEnemy(ai, character, mh, curPat, now))
                                {
                                    ReleaseMedicClaimForSoldier(medicPtr, curPat.Pointer);
                                    character.CoverPosition(null);
                                    AiCore.InvalidateTargetDestinationCache(character);
                                }
                                else
                                {
                                    DriveMedicHealingApproach(ai, character, mh, curHeal, now);
                                    return false;
                                }
                            }
                        }
                    }

                    Vector3 medicPos = mh.Tr.position;
                    string myFaction = AiCore.GetCachedFaction(mh, now);
                    if (string.IsNullOrEmpty(myFaction))
                        return true;

                    AiCore.EnsureWorldSnapshot();
                    int injCount = AiCore.WorldInjuredSoldierCount;
                    var injuredSoldiers = AiCore.WorldInjuredSoldiers;

                    Soldier bestPatient = null;
                    float bestScoreDistSq = 55.0f * 55.0f;
                    Soldier bestEnemyPow = null;
                    float bestEnemyDistSq = 20.0f * 20.0f;
                    bool skippedDogpiledNearest = false;

                    for (int i = 0; i < injCount; i++)
                    {
                        ref AiCore.SoldierSnapshot sSnap = ref injuredSoldiers[i];
                        if (!sSnap.IsInjured || sSnap.Ptr == medicPtr || sSnap.IsOnVehicle || sSnap.Ref == null)
                            continue;

                        if (mh.Goap.MedicUnreachablePatientPtr == sSnap.Ptr && now < mh.Goap.MedicUnreachableUntil)
                            continue;

                        float dSq = (sSnap.Pos - medicPos).sqrMagnitude;
                        bool sameFloorOrFar = !(Mathf.Abs(sSnap.Pos.y - medicPos.y) > 4.5f && dSq < 12.0f * 12.0f);
                        if (!AiCore.AreSameSideFactions(myFaction, sSnap.Faction))
                        {
                            if (bestPatient == null &&
                                dSq < bestEnemyDistSq &&
                                sameFloorOrFar &&
                                AiCore.AreEnemyFactions(myFaction, sSnap.Faction))
                            {
                                Soldier ePat = sSnap.Ref;
                                if (!ePat.IsDead &&
                                    ePat.IsInjured &&
                                    !ePat.IsCarried() &&
                                    !ePat.IsOnVehicle() &&
                                    !ePat.HasSurrended() &&
                                    (!_medicPatientClaims.TryGetValue(sSnap.Ptr, out MedicPatientClaim eClaim) ||
                                     eClaim.PrimaryMedic == null ||
                                     eClaim.PrimaryMedic.Pointer == medicPtr))
                                {
                                    bestEnemyDistSq = dSq;
                                    bestEnemyPow = ePat;
                                }
                            }
                            continue;
                        }

                        Soldier pat = sSnap.Ref;
                        if (pat.IsDead || !pat.IsInjured || pat.IsCarried() || pat.IsOnVehicle())
                            continue;

                        if (dSq > 55.0f * 55.0f || !sameFloorOrFar)
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

                    if (bestPatient == null &&
                        bestEnemyPow != null &&
                        IsMedicConfidentlySafeToHealEnemy(ai, character, mh, bestEnemyPow, now, knownHasWoundedFriendlyNearby: false))
                    {
                        bestPatient = bestEnemyPow;
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
                        AiCore.AssignBotCoverOrDestination(ai, character, newHealDest.Cast<AiDestination>(), requestImmediatePath: true);
                        DriveMedicHealingApproach(ai, character, mh, newHealDest, now);

                        if (skippedDogpiledNearest)
                        {
                            MedicTriageRedirectsCount++;
                            AiCore.LogDebug("MedicTriage", $"{myFaction} medic redirected to secondary casualty (avoided dogpile).");
                        }
                    }
                    else
                    {
                        IntPtr postDestRaw = AiCore.GetRawTargetDestinationPtr(character);
                        if (postDestRaw != IntPtr.Zero)
                        {
                            if (postDestRaw != mh.CachedDestPtr)
                                mh.NextDestCheckTime = 0f;
                            if (AiCore.ResolveTargetDestination(mh) == AiCore.CachedDestKind.MedicHeal)
                            {
                                character.CoverPosition(null);
                                AiCore.InvalidateTargetDestinationCache(character);
                            }
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

        [HarmonyPatch(typeof(MedicHealingDestination), nameof(MedicHealingDestination.OnCoverReached))]
        public static class MedicHealingDestination_OnCoverReached_Patch
        {
            public static bool Prefix(MedicHealingDestination __instance, Soldier __0)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        ReferenceEquals(__instance, null) ||
                        ReferenceEquals(__0, null) ||
                        __0.Pointer == IntPtr.Zero)
                    {
                        return true;
                    }

                    AiCore.SoldierFastHandles medH = AiCore.GetOrCreateSoldierHandles(__0);
                    if (!AiCore.IsLocallyOwnedBot(medH))
                        return true;

                    Soldier patient = __instance.soldierToHealUp;
                    if (ReferenceEquals(patient, null) || patient == null || patient.Pointer == IntPtr.Zero)
                        return true;

                    float now = Time.time;
                    string medFac = AiCore.GetCachedFaction(medH, now);
                    string patFac = AiCore.GetCachedFaction(patient);
                    if (AiCore.AreEnemyFactions(medFac, patFac) &&
                        !IsMedicConfidentlySafeToHealEnemy(medH.Ai, __0, medH, patient, now))
                    {
                        __instance.coverReached = true;
                        ReleaseMedicClaimForSoldier(__0.Pointer, patient.Pointer);
                        __0.CoverPosition(null);
                        AiCore.InvalidateTargetDestinationCache(__0);
                        return false;
                    }
                }
                catch { }
                return true;
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

        // =====================================================================
        // Live Grenade Kick-Back & Evasion Reflexes (Single-Kicker Auction + Co-op Sync)
        // =====================================================================
        internal const byte GrenadeKickPhotonEventCode = 198;

        internal struct LiveGrenadeEntry
        {
            public IntPtr Ptr;
            public ItemGrenade Ref;
            public Vector3 Pos;
            public Vector3 ThrowOrigin;
            public string ThrowerFaction;
            public float ActivatedTime;
            public float ExpectedExplodeTime;
            public float BlastRadius;
            public float SpeedSq;
            public bool IsKickable;
            public bool CanDamageLocal;
            public bool WasKicked;
            public float NextKickAllowedTime;
            public IntPtr AssignedKickerPtr;
            public float AssignedKickerUntil;
        }

        private static readonly List<LiveGrenadeEntry> _activeLiveGrenades = new List<LiveGrenadeEntry>(16);
        private static float _nextLiveGrenadeRefreshTime;

        internal static void ClearActiveLiveGrenades()
        {
            _activeLiveGrenades.Clear();
            _nextLiveGrenadeRefreshTime = 0f;
        }

        [HarmonyPatch(typeof(ItemGrenade), nameof(ItemGrenade.ActivateGrenade))]
        public static class ItemGrenade_ActivateGrenade_Patch
        {
            public static void Postfix(ItemGrenade __instance, Soldier __0, bool __1)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        __instance == null ||
                        __instance.Pointer == IntPtr.Zero)
                    {
                        return;
                    }

                    // Prevent fast-thrown/kicked grenades from tunneling through thin walls, doors, or sandbags,
                    // and pull back grenades whose rightHand spawn point clipped through a wall from the thrower's chest.
                    Rigidbody grenRb = __instance.RB;
                    if (grenRb != null && !grenRb.isKinematic)
                    {
                        grenRb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                    }

                    if (__0 != null && __0.Pointer != IntPtr.Zero && __0.transform != null && __instance.transform != null)
                    {
                        float originH = __0.m_pose == SoldierPose.Prone ? 0.45f : (__0.m_pose == SoldierPose.Crouch ? 1.00f : 1.35f);
                        Vector3 originPos = __0.transform.position + Vector3.up * originH;
                        Vector3 grenPos = __instance.transform.position;
                        Vector3 delta = grenPos - originPos;
                        float distSq = delta.sqrMagnitude;
                        if (distSq > 0.04f && distSq <= 1.9f * 1.9f &&
                            AiCore.LinecastEnvironment(originPos, grenPos, __0.transform.root, out RaycastHit wallHit, ignoreVehicles: false))
                        {
                            Vector3 safePos = wallHit.point + wallHit.normal * 0.14f;
                            if ((safePos - originPos).sqrMagnitude > distSq)
                                safePos = Vector3.Lerp(originPos, wallHit.point, 0.72f);
                            __instance.transform.position = safePos;
                            if (grenRb != null && !grenRb.isKinematic)
                                grenRb.position = safePos;
                        }
                    }

                    if (!VisionConfig.EnableLiveGrenadeReflexes.Value)
                        return;

                    if (__instance.grenadeType == GrenadeType.smokeGrenade)
                        return;

                    FuseType fuse = __instance.fuseType;
                    if (fuse != FuseType.TimeFuse &&
                        fuse != FuseType.StickyTimeFuse &&
                        fuse != FuseType.ExplodeOnImpact)
                    {
                        return;
                    }

                    float now = Time.time;
                    Vector3 pos = __instance.transform != null ? __instance.transform.position : Vector3.zero;
                    Vector3 throwOrigin = (__0 != null && __0.transform != null) ? __0.transform.position : pos;
                    string throwerFaction = __0 != null ? AiCore.GetCachedFaction(__0) : string.Empty;

                    float rawFuse = __instance.explodeAfter;
                    float fuseSeconds = Mathf.Clamp(rawFuse > 0.1f ? rawFuse : 4.0f, 0.8f, 12.0f);
                    float blastRadius = Mathf.Clamp(__instance.explosionRadius > 1.0f ? __instance.explosionRadius : 7.5f, 5.5f, 11.5f);

                    IntPtr ptr = __instance.Pointer;
                    for (int i = 0; i < _activeLiveGrenades.Count; i++)
                    {
                        if (_activeLiveGrenades[i].Ptr == ptr)
                        {
                            LiveGrenadeEntry existing = _activeLiveGrenades[i];
                            existing.Ref = __instance;
                            existing.Pos = pos;
                            existing.ThrowOrigin = throwOrigin;
                            existing.ThrowerFaction = throwerFaction;
                            existing.ActivatedTime = now;
                            existing.ExpectedExplodeTime = now + fuseSeconds;
                            existing.BlastRadius = blastRadius;
                            existing.IsKickable = fuse == FuseType.TimeFuse;
                            existing.CanDamageLocal = __1;
                            existing.WasKicked = false;
                            existing.NextKickAllowedTime = 0f;
                            existing.AssignedKickerPtr = IntPtr.Zero;
                            existing.AssignedKickerUntil = 0f;
                            _activeLiveGrenades[i] = existing;
                            return;
                        }
                    }

                    if (_activeLiveGrenades.Count >= 48)
                        return;

                    _activeLiveGrenades.Add(new LiveGrenadeEntry
                    {
                        Ptr = ptr,
                        Ref = __instance,
                        Pos = pos,
                        ThrowOrigin = throwOrigin,
                        ThrowerFaction = throwerFaction,
                        ActivatedTime = now,
                        ExpectedExplodeTime = now + fuseSeconds,
                        BlastRadius = blastRadius,
                        SpeedSq = 0f,
                        IsKickable = fuse == FuseType.TimeFuse,
                        CanDamageLocal = __1,
                        WasKicked = false,
                        NextKickAllowedTime = 0f,
                        AssignedKickerPtr = IntPtr.Zero,
                        AssignedKickerUntil = 0f
                    });
                }
                catch (Exception ex)
                {
                    AiCore.ReportPatchException("Grenade.Activate", ex);
                }
            }
        }

        internal static void RefreshActiveLiveGrenadesIfDue()
        {
            if (_activeLiveGrenades.Count == 0)
                return;

            float now = Time.time;
            if (now < _nextLiveGrenadeRefreshTime)
                return;

            _nextLiveGrenadeRefreshTime = now + 0.08f;

            for (int i = _activeLiveGrenades.Count - 1; i >= 0; i--)
            {
                LiveGrenadeEntry g = _activeLiveGrenades[i];
                if (ReferenceEquals(g.Ref, null) ||
                    g.Ref == null ||
                    g.Ref.gameObject == null ||
                    !g.Ref.gameObject.activeInHierarchy ||
                    now > g.ExpectedExplodeTime + 1.5f)
                {
                    _activeLiveGrenades.RemoveAt(i);
                    continue;
                }

                if (g.Ref.transform != null)
                    g.Pos = g.Ref.transform.position;

                Rigidbody rb = g.Ref.RB;
                g.SpeedSq = rb != null ? rb.velocity.sqrMagnitude : 0f;

                if (g.AssignedKickerPtr != IntPtr.Zero &&
                    (now > g.AssignedKickerUntil ||
                     !AiCore.TryGetSoldierHandles(g.AssignedKickerPtr, out AiCore.SoldierFastHandles kh) ||
                     kh.Ref == null ||
                     !kh.Ref.IsAlive))
                {
                    g.AssignedKickerPtr = IntPtr.Zero;
                    g.AssignedKickerUntil = 0f;
                }

                _activeLiveGrenades[i] = g;
            }
        }

        private static Vector3 RotateHorizontalDirection(Vector3 dir, float cosAngle, float sinAngle)
        {
            return new Vector3(dir.x * cosAngle - dir.z * sinAngle, 0f, dir.x * sinAngle + dir.z * cosAngle);
        }

        private static bool IsKickCorridorClearOfFriendlies(Vector3 grenPos, Vector3 dir, string kickerFaction)
        {
            int solCount = AiCore.WorldSoldierCount;
            var soldiers = AiCore.WorldSoldiers;
            const float corridorRadiusSq = 4.2f * 4.2f;

            for (int i = 0; i < solCount; i++)
            {
                ref AiCore.SoldierSnapshot solSnap = ref soldiers[i];
                if (solSnap.Ref == null || string.IsNullOrEmpty(solSnap.Faction))
                    continue;

                if (!AiCore.AreSameSideFactions(kickerFaction, solSnap.Faction))
                    continue;

                float dx = solSnap.Pos.x - grenPos.x;
                float dz = solSnap.Pos.z - grenPos.z;
                float proj = dx * dir.x + dz * dir.z;
                if (proj >= 2.5f && proj <= 14.0f)
                {
                    float perpSq = (dx * dx + dz * dz) - proj * proj;
                    if (perpSq <= corridorRadiusSq)
                        return false;
                }
            }

            return true;
        }

        private static bool TryFindSafeGrenadeKickDirection(
            Vector3 grenPos,
            Vector3 kickerPos,
            Vector3 throwOrigin,
            string kickerFaction,
            Transform ignoreRoot,
            out Vector3 bestKickDir)
        {
            bestKickDir = Vector3.zero;
            AiCore.EnsureWorldSnapshot();

            Vector3 awayDir = AiCore.HorizontalDirectionOrFallback(kickerPos, grenPos, Vector3.forward);
            Vector3 toThrower = throwOrigin - grenPos;
            toThrower.y = 0f;
            bool hasThrowerDir = toThrower.sqrMagnitude > 2.5f * 2.5f;
            Vector3 throwerDir = hasThrowerDir ? toThrower.normalized : Vector3.zero;

            const float cos60 = 0.5f;
            const float sin60 = 0.8660254f;

            Vector3 rayOrigin = grenPos + Vector3.up * 0.35f;
            for (int i = 0; i < 4; i++)
            {
                Vector3 cand;
                if (i == 0)
                {
                    if (!hasThrowerDir)
                        continue;
                    cand = throwerDir;
                }
                else if (i == 1)
                {
                    cand = awayDir;
                }
                else if (i == 2)
                {
                    cand = RotateHorizontalDirection(awayDir, cos60, sin60);
                }
                else
                {
                    cand = RotateHorizontalDirection(awayDir, cos60, -sin60);
                }

                if (AiCore.RaycastEnvironment(rayOrigin, cand, 5.2f, ignoreRoot, out _))
                    continue;

                if (!IsKickCorridorClearOfFriendlies(grenPos, cand, kickerFaction))
                    continue;

                bestKickDir = cand;
                return true;
            }

            return false;
        }

        private static bool IsEligibleToKickGrenade(
            ref LiveGrenadeEntry g,
            SoldierAI ai,
            Soldier soldier,
            AiCore.SoldierFastHandles h,
            SoldierPersonality personality,
            float vet01,
            string myFaction,
            Vector3 myPos,
            float dist,
            float fuseRemaining,
            float now,
            out Vector3 kickDir)
        {
            kickDir = Vector3.zero;
            bool alreadyClaimedByMe = g.AssignedKickerPtr == h.Ptr;
            if (!g.IsKickable ||
                g.WasKicked ||
                now < g.NextKickAllowedTime ||
                g.SpeedSq > 22.0f ||
                (g.AssignedKickerPtr != IntPtr.Zero && !alreadyClaimedByMe) ||
                !AiCore.CanBotMoveAutonomously(soldier) ||
                soldier.IsCarryingBody() ||
                soldier.isInBodyAnimation ||
                soldier.isInHandsAnimation)
            {
                return false;
            }

            bool reachable = (dist <= 2.25f && fuseRemaining >= 0.70f) ||
                             (dist <= 3.80f && fuseRemaining >= 1.35f);
            if (!reachable)
                return false;

            if (!alreadyClaimedByMe)
            {
                int threshold;
                if (soldier.isBleeding ||
                    personality == SoldierPersonality.Rifleman_Cautious ||
                    personality == SoldierPersonality.Medic_Triage ||
                    personality == SoldierPersonality.TankCrew_Support ||
                    personality == SoldierPersonality.LightCrew_Escort ||
                    personality == SoldierPersonality.Pilot_Disciplined)
                {
                    if (dist > 1.85f)
                        return false;
                    threshold = 18;
                }
                else if (personality == SoldierPersonality.Assaulter_Shock ||
                         personality == SoldierPersonality.Assaulter_Breacher ||
                         personality == SoldierPersonality.Leader_Spearhead ||
                         personality == SoldierPersonality.AT_Demolitionist ||
                         vet01 >= 0.50f)
                {
                    threshold = dist <= 2.40f ? 78 : 55;
                }
                else
                {
                    threshold = dist <= 2.40f ? 48 : 28;
                }

                int salt = (int)(((long)g.Ptr >> 4) & 0x7FFFFFFF) ^ 733;
                int roll = AiCore.GetDeterministicHash(h.Ptr, salt) % 100;
                if (roll >= threshold)
                    return false;
            }

            return TryFindSafeGrenadeKickDirection(g.Pos, myPos, g.ThrowOrigin, myFaction, h.Tr.root, out kickDir);
        }

        private static bool TryFindGrenadeEscapePosition(
            Vector3 myPos,
            Vector3 grenPos,
            Vector3 awayDir,
            float escapeDist,
            bool isOnBridge,
            out Vector3 bestEscPos,
            out Vector3 bestEscDir)
        {
            bestEscPos = Vector3.zero;
            bestEscDir = awayDir;
            float bestScore = -999f;
            bool found = false;

            const float cos40 = 0.7660444f;
            const float sin40 = 0.6427876f;
            const float cos80 = 0.1736482f;
            const float sin80 = 0.9848078f;

            bool hasWater = AiCore.TryGetActiveWaterLevel(out float waterY);
            Vector3 waist = myPos + Vector3.up * 0.55f;
            Vector3 grenProbePos = grenPos + Vector3.up * 0.35f;

            for (int a = 0; a < 5; a++)
            {
                Vector3 escDir = a == 0
                    ? awayDir
                    : (a == 1
                        ? RotateHorizontalDirection(awayDir, cos40, sin40)
                        : (a == 2
                            ? RotateHorizontalDirection(awayDir, cos40, -sin40)
                            : (a == 3
                                ? RotateHorizontalDirection(awayDir, cos80, sin80)
                                : RotateHorizontalDirection(awayDir, cos80, -sin80))));

                if (AiCore.IsMovementBlockedByObstacle(waist, escDir, 1.8f))
                    continue;

                Vector3 rawCand = myPos + escDir * escapeDist;
                Vector3 snappedEsc;
                if (isOnBridge)
                {
                    if (!AiCore.IsSafeBridgeDeckStep(myPos, rawCand, out snappedEsc))
                        continue;
                }
                else if (!AiCore.TrySnapWalkableGroundProbe(myPos, rawCand, 4.5f, 9.5f, 0.58f, 2.2f, hasWater, waterY, out snappedEsc))
                {
                    continue;
                }

                if (AiCore.TryGetNearbyKnownMine(snappedEsc, 4.0f, false, out _))
                    continue;

                Vector3 shieldOrigin = snappedEsc + Vector3.up * 0.75f;
                Vector3 toGren = grenProbePos - shieldOrigin;
                float gDist = toGren.magnitude;
                bool shielded = gDist > 0.5f && AiCore.RaycastEnvironment(shieldOrigin, toGren / gDist, gDist, null, out _);

                float score = (shielded ? 35.0f : 0f) + (snappedEsc - grenPos).sqrMagnitude * 0.15f - a * 1.2f;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestEscPos = snappedEsc;
                    bestEscDir = escDir;
                    found = true;
                }
            }

            return found;
        }

        private static void SendGrenadeKickPhotonEvent(Vector3 grenPos, Vector3 kickVel, Soldier kicker)
        {
            try
            {
                SyncSoldier syn = kicker != null ? kicker.soldierSyncher : null;
                int kickerViewId = syn != null ? syn.ViewID : 0;

                var payload = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray(new string[]
                {
                    grenPos.x.ToString("R", CultureInfo.InvariantCulture),
                    grenPos.y.ToString("R", CultureInfo.InvariantCulture),
                    grenPos.z.ToString("R", CultureInfo.InvariantCulture),
                    kickVel.x.ToString("R", CultureInfo.InvariantCulture),
                    kickVel.y.ToString("R", CultureInfo.InvariantCulture),
                    kickVel.z.ToString("R", CultureInfo.InvariantCulture),
                    kickerViewId.ToString(CultureInfo.InvariantCulture)
                });

                var opts = new Photon.Realtime.RaiseEventOptions
                {
                    Receivers = Photon.Realtime.ReceiverGroup.Others
                };

                Photon.Pun.PhotonNetwork.RaiseEvent(
                    GrenadeKickPhotonEventCode,
                    payload.Cast<Il2CppSystem.Object>(),
                    opts,
                    ExitGames.Client.Photon.SendOptions.SendReliable);
            }
            catch { }
        }

        internal static void HandleRemoteGrenadeKickPhotonEvent(ExitGames.Client.Photon.EventData ev)
        {
            try
            {
                if (ev == null || _activeLiveGrenades.Count == 0 || ev.CustomData == null)
                    return;

                var arr = ev.CustomData.TryCast<Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray>();
                if (arr == null || arr.Length < 7)
                    return;

                if (!float.TryParse(arr[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float px) ||
                    !float.TryParse(arr[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float py) ||
                    !float.TryParse(arr[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float pz) ||
                    !float.TryParse(arr[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float vx) ||
                    !float.TryParse(arr[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float vy) ||
                    !float.TryParse(arr[5], NumberStyles.Float, CultureInfo.InvariantCulture, out float vz))
                {
                    return;
                }

                int.TryParse(arr[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out int kickerViewId);
                Vector3 eventPos = new Vector3(px, py, pz);
                Vector3 kickVel = new Vector3(vx, vy, vz);

                int bestIdx = -1;
                float bestDistSq = 4.5f * 4.5f;
                for (int i = 0; i < _activeLiveGrenades.Count; i++)
                {
                    LiveGrenadeEntry entry = _activeLiveGrenades[i];
                    if (ReferenceEquals(entry.Ref, null) || entry.Ref == null)
                        continue;

                    Vector3 livePos = entry.Ref.transform != null ? entry.Ref.transform.position : entry.Pos;
                    float dSq = (livePos - eventPos).sqrMagnitude;
                    if (dSq < bestDistSq)
                    {
                        bestDistSq = dSq;
                        bestIdx = i;
                    }
                }

                if (bestIdx < 0)
                    return;

                LiveGrenadeEntry target = _activeLiveGrenades[bestIdx];
                Rigidbody rb = target.Ref.RB;
                if (rb != null)
                {
                    if (!rb.isKinematic)
                        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                    rb.velocity = kickVel;
                }

                if (kickerViewId > 0)
                {
                    AiCore.EnsureWorldSnapshot();
                    int solCount = AiCore.WorldSoldierCount;
                    var soldiers = AiCore.WorldSoldiers;
                    for (int i = 0; i < solCount; i++)
                    {
                        Soldier s = soldiers[i].Ref;
                        if (s == null)
                            continue;
                        SyncSoldier syn = s.soldierSyncher;
                        if (syn != null && syn.ViewID == kickerViewId)
                        {
                            target.Ref.activator = s;
                            target.ThrowerFaction = soldiers[i].Faction;
                            target.ThrowOrigin = soldiers[i].Pos;
                            break;
                        }
                    }
                }

                target.WasKicked = true;
                target.AssignedKickerPtr = IntPtr.Zero;
                target.NextKickAllowedTime = Time.time + 1.2f;
                _activeLiveGrenades[bestIdx] = target;
            }
            catch (Exception ex)
            {
                AiCore.ReportPatchException("Grenade.RemoteKick", ex);
            }
        }

        internal static bool TryHandleLiveGrenadeReflex(
            SoldierAI ai,
            Soldier soldier,
            AiCore.SoldierFastHandles h,
            SoldierPersonality personality,
            float vet01,
            bool isOnBridge,
            float now)
        {
            if (!VisionConfig.EnableLiveGrenadeReflexes.Value ||
                _activeLiveGrenades.Count == 0 ||
                !VisionConfig.EnableMod.Value ||
                ai == null ||
                h == null ||
                h.Tr == null ||
                soldier == null ||
                !soldier.IsAlive)
            {
                return false;
            }

            Vector3 myPos = h.Tr.position;
            string myFaction = AiCore.GetCachedFaction(h, now);

            int bestIdx = -1;
            float bestDistSq = float.MaxValue;
            float bestFuseRem = 0f;

            for (int i = 0; i < _activeLiveGrenades.Count; i++)
            {
                LiveGrenadeEntry entry = _activeLiveGrenades[i];
                if (ReferenceEquals(entry.Ref, null) || entry.Ref == null || now > entry.ExpectedExplodeTime + 0.5f)
                    continue;

                if (entry.Ptr == h.Combat.LastEvadedGrenadePtr && now < h.Combat.GrenadeReflexCooldownUntil)
                    continue;

                float dy = entry.Pos.y - myPos.y;
                if (Mathf.Abs(dy) > 3.5f)
                    continue;

                float fuseRem = Mathf.Max(0f, entry.ExpectedExplodeTime - now);
                if (dy > 2.4f && fuseRem > 1.2f)
                    continue;

                float dx = entry.Pos.x - myPos.x;
                float dz = entry.Pos.z - myPos.z;
                float dSq = dx * dx + dz * dz;
                float maxThreat = entry.BlastRadius + 2.5f;
                if (dSq > maxThreat * maxThreat)
                    continue;

                float reactDelay = Mathf.Lerp(0.75f, 0.32f, vet01);
                if (dSq <= 2.2f * 2.2f)
                    reactDelay *= 0.5f;
                if (now < entry.ActivatedTime + reactDelay)
                    continue;

                if (!string.IsNullOrEmpty(entry.ThrowerFaction) &&
                    AiCore.AreSameSideFactions(myFaction, entry.ThrowerFaction) &&
                    dSq > (entry.BlastRadius * 0.85f) * (entry.BlastRadius * 0.85f))
                {
                    continue;
                }

                if (dSq < bestDistSq)
                {
                    bestDistSq = dSq;
                    bestIdx = i;
                    bestFuseRem = fuseRem;
                }
            }

            if (bestIdx < 0)
            {
                AiCore.ClearBotMoveOverride(h, AiCore.BotMoveOverrideKind.GrenadeReflex);
                return false;
            }

            LiveGrenadeEntry g = _activeLiveGrenades[bestIdx];
            float dist = Mathf.Sqrt(bestDistSq);

            if (IsEligibleToKickGrenade(ref g, ai, soldier, h, personality, vet01, myFaction, myPos, dist, bestFuseRem, now, out Vector3 kickDir))
            {
                g.AssignedKickerPtr = h.Ptr;
                g.AssignedKickerUntil = now + 1.6f;
                _activeLiveGrenades[bestIdx] = g;

                TrySayTacticalVoice(soldier, soldier.joinedSquad, VoiceManager.VoiceClip.granade, 0.65f);

                if (dist <= 2.25f)
                {
                    Vector3 kickVel = (kickDir + Vector3.up * 0.42f) * 14.5f;
                    if (g.Ref.activated && g.Ref.gameObject != null)
                        g.Ref.Kick(soldier.gameObject);

                    Rigidbody rb = g.Ref.RB;
                    if (rb != null)
                        rb.velocity = kickVel;

                    g.Ref.activator = soldier;
                    g.WasKicked = true;
                    g.NextKickAllowedTime = now + 1.2f;
                    g.AssignedKickerPtr = IntPtr.Zero;
                    g.AssignedKickerUntil = 0f;
                    g.ThrowerFaction = myFaction;
                    g.ThrowOrigin = myPos;
                    _activeLiveGrenades[bestIdx] = g;

                    if (AiCore.IsInMultiplayerRoom())
                        SendGrenadeKickPhotonEvent(g.Pos, kickVel, soldier);

                    GrenadesKickedCount++;
                    BotActionTrace.RecordEvent(soldier, BotTraceEventKind.GrenadeKick, "kicked", dist, bestFuseRem);
                    AiCore.ClearBotMoveOverride(h, AiCore.BotMoveOverrideKind.GrenadeReflex);
                    h.Combat.LastEvadedGrenadePtr = g.Ptr;
                    h.Combat.GrenadeReflexCooldownUntil = now + 0.65f;
                    SetBotPose(ai, soldier, SoldierPose.Crouch, true);
                    return true;
                }

                SetBotPose(ai, soldier, SoldierPose.Idle, true);
                AiCore.SetBotMoveOverride(h, AiCore.BotMoveOverrideKind.GrenadeReflex, g.Pos, now + 1.2f);
                AiCore.MoveBotTowardPosition(ai, soldier, g.Pos, 0.1f, 0.9f);
                return true;
            }

            if (g.AssignedKickerPtr == h.Ptr)
            {
                g.AssignedKickerPtr = IntPtr.Zero;
                g.AssignedKickerUntil = 0f;
                _activeLiveGrenades[bestIdx] = g;
            }

            TrySayTacticalVoice(soldier, soldier.joinedSquad, VoiceManager.VoiceClip.granade, 0.55f);
            h.Stance.LocalCoverProtectedUntil = 0f;
            h.Goap.Plan.LockUnitCoverChange = false;

            if (h.Combat.LastEvadedGrenadePtr != g.Ptr)
            {
                h.Combat.LastEvadedGrenadePtr = g.Ptr;
                GrenadesEvadedCount++;
                BotActionTrace.RecordEvent(
                    soldier,
                    BotTraceEventKind.GrenadeEvade,
                    bestFuseRem <= 1.05f ? "dive_prone" : "sprint_evade",
                    dist,
                    bestFuseRem);
            }

            if (bestFuseRem <= 1.05f || !AiCore.CanBotMoveAutonomously(soldier))
            {
                AiCore.ClearBotMoveOverride(h, AiCore.BotMoveOverrideKind.GrenadeReflex);
                if (ai.IsMoving())
                    ai.Stop(false);
                SetBotPose(ai, soldier, isOnBridge ? SoldierPose.Crouch : SoldierPose.Prone, true);
                return true;
            }

            Vector3 awayDir = AiCore.HorizontalDirectionOrFallback(g.Pos, myPos, h.Tr.forward);
            float escapeDist = Mathf.Clamp(g.BlastRadius + 3.5f - dist, 4.5f, 10.5f);
            if (TryFindGrenadeEscapePosition(myPos, g.Pos, awayDir, escapeDist, isOnBridge, out Vector3 escPos, out Vector3 escDir))
            {
                SetBotPose(ai, soldier, SoldierPose.Idle, true);
                AiCore.SetBotMoveOverride(
                    h,
                    AiCore.BotMoveOverrideKind.GrenadeReflex,
                    escPos,
                    Mathf.Min(g.ExpectedExplodeTime + 0.5f, now + 3.2f));

                if ((ai.MoveDestination - escPos).sqrMagnitude > 1.5f * 1.5f ||
                    (!ai.HasPath && !ai.HasPathRequest && ai.moveDirectlyToward_end <= now))
                {
                    AiCore.SetBotMoveDestinationAndRefreshPath(ai, escPos);
                    Vector3 firstStep = (escPos - myPos).sqrMagnitude > 2.2f * 2.2f ? (myPos + escDir * 2.0f) : escPos;
                    if (AiCore.TryValidateAndSnapDirectMicroStep(myPos, firstStep, out Vector3 microEsc, out _, 2.2f, 0.75f))
                        ai.MoveDirectlyToward(microEsc, 0.45f);
                }

                return true;
            }

            AiCore.ClearBotMoveOverride(h, AiCore.BotMoveOverrideKind.GrenadeReflex);
            if (ai.IsMoving())
                ai.Stop(false);
            SetBotPose(ai, soldier, isOnBridge ? SoldierPose.Crouch : SoldierPose.Prone, true);
            return true;
        }
    }
}

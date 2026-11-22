using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using BepInEx;
using UnityEngine;

namespace EasyRed2HumanAI
{
    internal enum BotTraceEventKind : byte
    {
        None = 0,
        SessionStart = 1,
        GoapPlan = 2,
        BandageStart = 3,
        BandageBlocked = 4,
        MeleeEnter = 5,
        MeleeSuppressed = 6,
        Vault = 7,
        VaultRejected = 8,
        LeaderBalk = 9,
        SelfBalk = 10,
        FallbackRetreat = 11,
        CrewEvac = 12,
        SquadRetreat = 13,
        StuckAlert = 14,
        Surrender = 15,
        UnSurrender = 16,
        RetreatDespawn = 17,
        VehicleBailout = 18,
        AnomalyAlert = 19,
        EmplacementBoard = 20,
        EmplacementBail = 21,
        GrenadeKick = 22,
        GrenadeEvade = 23,
        EmergentPlan = 24,
        EmergentReflex = 25,
        Tier2Balk = 26,
        Tier3Lever = 27,
        TroopInteract = 28,
        BotProfile = 29,
        BotKilled = 30,
        MatchPulse = 31
    }

    /// <summary>
    /// Lock-free, transition-only per-bot JSONL action logger (<c>BepInEx/BotActionTrace.jsonl</c>).
    /// Designed for zero main-thread I/O hitches and zero per-event heap allocations.
    /// </summary>
    internal static class BotActionTrace
    {
        private const int RingCapacity = 4096;
        private const int RingMask = RingCapacity - 1;
        private const long MaxLogBytes = 25L * 1024L * 1024L; // 25 MB rolling cap

        private struct TraceRecord
        {
            public float Time;
            public int BotId;
            public int SquadId;
            public BotTraceEventKind Kind;
            public byte Pose;
            public byte Bleed;
            public byte InCover;
            public short Suppression;
            public short Ammo;
            public float EnemyDist;
            public float GunfireDist;
            public Vector3 Pos;
            public Vector3 Dest;
            public string Faction;
            public string Name;
            public string Role;
            public string Net;
            public string Goal;
            public string Act0;
            public string Act1;
            public string Reason;
            public byte IsEmergent;
            public int Stamps;
            public float HNow;
            public float HProj;
            public float Win;
            public float CanH;
            public float Score;
            public float RunnerScore;
            public string RunnerUp;
            public uint Flags;
            public float ExtraA;
            public float ExtraB;
            public float ExtraC;
            public int ExtraIntA;
            public int ExtraIntB;
        }

        private struct BotLastTraceState
        {
            public BotTraceEventKind LastDiscreteKind;
            public string LastDiscreteReason;
            public float LastDiscreteTime;
            public float LastBandageBlockTime;
            public float LastVaultRejectTime;
            public float LastMeleeEnterTime;
        }

        private static volatile bool _enabled;
        private static bool _threadStarted;
        private static readonly object _initLock = new object();
        private static AutoResetEvent _wakeSignal;
        private static Thread _writerThread;

        private static readonly TraceRecord[] _ring = new TraceRecord[RingCapacity];
        private static int _writeSeq;
        private static int _readSeq;
        private static int _droppedEvents;

        private static readonly Dictionary<IntPtr, BotLastTraceState> _botStates = new Dictionary<IntPtr, BotLastTraceState>(256);

        public static bool IsEnabled
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _enabled;
        }

        public static string TraceFilePath => Path.Combine(Paths.BepInExRootPath, "BotActionTrace.jsonl");

        public static void RefreshConfigState()
        {
            bool wantEnabled = VisionConfig.EnableBotActionTrace != null && VisionConfig.EnableBotActionTrace.Value;
            bool wasEnabled = _enabled;
            _enabled = wantEnabled;

            if (wantEnabled)
            {
                EnsureWriterThreadStarted();
                if (!wasEnabled)
                {
                    EnqueueSessionMarker("trace_enabled");
                }
            }
        }

        public static void OnSceneRestart()
        {
            _botStates.Clear();
            if (_enabled)
            {
                EnqueueSessionMarker("scene_restart");
            }
        }

        public static void PruneAliveBots(HashSet<IntPtr> aliveSoldierPtrs)
        {
            if (_botStates.Count == 0 || aliveSoldierPtrs == null)
                return;
            AiCore.PruneDict(_botStates, aliveSoldierPtrs);
        }

        private static void EnsureWriterThreadStarted()
        {
            if (_threadStarted)
                return;

            lock (_initLock)
            {
                if (_threadStarted)
                    return;

                _wakeSignal = new AutoResetEvent(false);
                _writerThread = new Thread(WriterThreadLoop)
                {
                    Name = "ER2AI_BotActionTrace",
                    IsBackground = true,
                    Priority = System.Threading.ThreadPriority.BelowNormal
                };
                _threadStarted = true;
                _writerThread.Start();
            }
        }

        private static void EnqueueSessionMarker(string reason)
        {
            float now = 0f;
            try { now = Time.time; } catch { }

            string mapId = "unknown";
            try
            {
                if (MatchData.data != null && MatchData.data.loadedBattle != null)
                {
                    mapId = MatchData.data.loadedBattle.GetBattleName() ?? "unknown";
                }
                else
                {
                    BattleData bd = BattleManager.GetCurrentBattleData();
                    if (bd != null && !string.IsNullOrEmpty(bd.SaveDataId))
                        mapId = bd.SaveDataId;
                }
            }
            catch { }

            string verTag = (VisionConfig.EnableEmergentDecisionEngine != null && VisionConfig.EnableEmergentDecisionEngine.Value)
                ? (Plugin.PluginVersion + "-harm")
                : Plugin.PluginVersion;

            TraceRecord rec = new TraceRecord
            {
                Time = now,
                Kind = BotTraceEventKind.SessionStart,
                Faction = mapId,
                Role = verTag,
                Reason = reason
            };
            PushRecord(ref rec);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void PushRecord(ref TraceRecord rec)
        {
            int w = _writeSeq;
            int r = Volatile.Read(ref _readSeq);
            if (w - r >= RingCapacity - 1)
            {
                Interlocked.Increment(ref _droppedEvents);
                return;
            }

            _ring[w & RingMask] = rec;
            Volatile.Write(ref _writeSeq, w + 1);
            _wakeSignal?.Set();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RecordEvent(
            Soldier soldier,
            BotTraceEventKind kind,
            string reason,
            float enemyDist = -1f,
            float gunfireDist = -1f)
        {
            if (!_enabled || ReferenceEquals(soldier, null) || soldier == null)
                return;

            RecordEventInternal(soldier, kind, reason, enemyDist, gunfireDist);
        }

        private static void RecordEventInternal(
            Soldier soldier,
            BotTraceEventKind kind,
            string reason,
            float enemyDist,
            float gunfireDist)
        {
            AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandles(soldier);
            if (h == null || h.Tr == null)
                return;

            float now = Time.time;
            IntPtr ptr = h.Ptr;
            if (_botStates.TryGetValue(ptr, out BotLastTraceState st))
            {
                if (kind == BotTraceEventKind.BandageBlocked &&
                    st.LastBandageBlockTime > 0f &&
                    now - st.LastBandageBlockTime < 3.50f &&
                    now >= st.LastBandageBlockTime)
                {
                    return;
                }

                if (kind == BotTraceEventKind.VaultRejected &&
                    st.LastVaultRejectTime > 0f &&
                    now - st.LastVaultRejectTime < 3.50f &&
                    now >= st.LastVaultRejectTime)
                {
                    return;
                }

                if (kind == BotTraceEventKind.MeleeEnter &&
                    st.LastMeleeEnterTime > 0f &&
                    now - st.LastMeleeEnterTime < 3.50f &&
                    now >= st.LastMeleeEnterTime)
                {
                    return;
                }

                if (st.LastDiscreteKind == kind &&
                    ReferenceEquals(st.LastDiscreteReason, reason) &&
                    st.LastDiscreteTime > 0f &&
                    now - st.LastDiscreteTime < 2.75f &&
                    now >= st.LastDiscreteTime)
                {
                    return;
                }
            }

            st.LastDiscreteKind = kind;
            st.LastDiscreteReason = reason;
            st.LastDiscreteTime = now;
            if (kind == BotTraceEventKind.BandageBlocked)
                st.LastBandageBlockTime = now;
            else if (kind == BotTraceEventKind.VaultRejected)
                st.LastVaultRejectTime = now;
            else if (kind == BotTraceEventKind.MeleeEnter)
                st.LastMeleeEnterTime = now;
            _botStates[ptr] = st;

            SoldierAI ai = h.Ai ?? soldier.aiController;
            Vector3 pos = h.Tr.position;
            Vector3 dest = ai != null ? ai.MoveDestination : pos;
            string fac = AiCore.GetCachedFaction(h, now);

            if (gunfireDist < 0f)
            {
                if (!VisionPatches.TryGetNearestEnemyGunfire(pos, fac, 25.0f, 3.0f, out gunfireDist))
                    gunfireDist = -1f;
            }
            if (enemyDist < 0f && ai != null && ai.visibleTarget != null)
            {
                enemyDist = ai.distFromTarget;
            }

            GenericGun heldGun = soldier.GetHeldGun();
            short ammo = (short)(heldGun != null ? Mathf.Clamp(heldGun.GetCurrentAmmoCount(), 0, 999) : -1);
            Squad sq = AiCore.GetCachedSquad(h, now);
            int sqId = sq != null ? AiCore.GetSquadDiagId(sq.Pointer) : 0;

            VeteranAndTacticsPatches.SquadNetDirective netDir = VeteranAndTacticsPatches.SquadNetDirective.Disconnected_Autonomous;
            if (sq != null && sq.Pointer != IntPtr.Zero &&
                VeteranAndTacticsPatches._squadBlackboards.TryGetValue(sq.Pointer, out VeteranAndTacticsPatches.SquadBlackboard sqB))
            {
                netDir = sqB.NetDirective;
            }
            bool hasEmerg = h.Emergent.HasDecision;
            string goalLabel = hasEmerg
                ? (h.Emergent.CurrentDecision.Reflex != EmergentEngine.EmergentReflexKind.None
                    ? EmergentEngine.GetReflexShortName(h.Emergent.CurrentDecision.Reflex)
                    : "Harm")
                : "None";

            TraceRecord rec = new TraceRecord
            {
                Time = now,
                BotId = h.DiagId,
                SquadId = sqId,
                Kind = kind,
                Pose = (byte)soldier.m_pose,
                Bleed = (byte)(soldier.isBleeding ? 1 : 0),
                InCover = (byte)(soldier.IsOnCover() ? 1 : 0),
                Suppression = (short)Mathf.Clamp(Mathf.RoundToInt(soldier.GetSuppressionValue()), 0, 999),
                Ammo = ammo,
                EnemyDist = enemyDist,
                GunfireDist = gunfireDist,
                Pos = pos,
                Dest = dest,
                Faction = fac,
                Name = h.CachedFullName,
                Role = VeteranAndTacticsPatches.GetPersonalityShortLabel(VeteranAndTacticsPatches.GetPersonality(soldier)),
                Net = VeteranAndTacticsPatches.GetNetDirectiveShortName(netDir),
                Goal = goalLabel,
                Act0 = hasEmerg ? EmergentEngine.GetActionShortName(h.Emergent.CurrentDecision.Step0) : "None",
                Act1 = hasEmerg ? EmergentEngine.GetActionShortName(h.Emergent.CurrentDecision.Step1) : "None",
                Reason = reason ?? string.Empty
            };
            PushRecord(ref rec);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RecordEmergentDecision(
            SoldierAI ai,
            Soldier soldier,
            AiCore.SoldierFastHandles h,
            VeteranAndTacticsPatches.SoldierPersonality personality,
            EmergentEngine.Tier2SquadDirective squadDir,
            in EmergentEngine.EmergentDecision dec,
            int activeStamps,
            uint inputFlags,
            float suppression,
            float enemyDist,
            float now)
        {
            if (!_enabled || ReferenceEquals(soldier, null) || h == null || h.Tr == null)
                return;

            RecordEmergentDecisionInternal(ai, soldier, h, personality, squadDir, in dec, activeStamps, inputFlags, suppression, enemyDist, now);
        }

        private static void RecordEmergentDecisionInternal(
            SoldierAI ai,
            Soldier soldier,
            AiCore.SoldierFastHandles h,
            VeteranAndTacticsPatches.SoldierPersonality personality,
            EmergentEngine.Tier2SquadDirective squadDir,
            in EmergentEngine.EmergentDecision dec,
            int activeStamps,
            uint inputFlags,
            float suppression,
            float enemyDist,
            float now)
        {
            ulong sig = ((ulong)(byte)dec.Step0) |
                        ((ulong)(byte)dec.Step1 << 8) |
                        ((ulong)(byte)dec.Reflex << 16) |
                        ((ulong)(byte)squadDir << 24) |
                        ((ulong)(dec.UnsafePrereqRejected ? 1 : 0) << 32) |
                        ((ulong)(soldier.isBleeding ? 1 : 0) << 33);

            bool changed = h.Emergent.LastTraceSig != sig;
            bool periodicDue = h.Emergent.LastTraceTime <= 0f || (now - h.Emergent.LastTraceTime) >= 3.25f || now < h.Emergent.LastTraceTime;
            if (!changed && !periodicDue && dec.Reflex == EmergentEngine.EmergentReflexKind.None)
                return;

            h.Emergent.LastTraceSig = sig;
            h.Emergent.LastTraceTime = now;

            Vector3 pos = h.Tr.position;
            Vector3 dest = ai != null ? ai.MoveDestination : pos;
            string fac = AiCore.GetCachedFaction(h, now);
            if (!VisionPatches.TryGetNearestEnemyGunfire(pos, fac, 25.0f, 3.0f, out float gfDist))
                gfDist = -1f;

            GenericGun heldGun = soldier.GetHeldGun();
            short ammo = (short)(heldGun != null ? Mathf.Clamp(heldGun.GetCurrentAmmoCount(), 0, 999) : -1);
            Squad sq = AiCore.GetCachedSquad(h, now);
            int sqId = sq != null ? AiCore.GetSquadDiagId(sq.Pointer) : 0;

            TraceRecord rec = new TraceRecord
            {
                Time = now,
                BotId = h.DiagId,
                SquadId = sqId,
                Kind = dec.Reflex != EmergentEngine.EmergentReflexKind.None ? BotTraceEventKind.EmergentReflex : BotTraceEventKind.EmergentPlan,
                Pose = (byte)soldier.m_pose,
                Bleed = (byte)(soldier.isBleeding ? 1 : 0),
                InCover = (byte)(soldier.IsOnCover() ? 1 : 0),
                Suppression = (short)Mathf.Clamp(Mathf.RoundToInt(suppression), 0, 999),
                Ammo = ammo,
                EnemyDist = (ai != null && ai.visibleTarget != null) ? enemyDist : -1f,
                GunfireDist = gfDist,
                Pos = pos,
                Dest = dest,
                Faction = fac,
                Name = h.CachedFullName,
                Role = VeteranAndTacticsPatches.GetPersonalityShortLabel(personality),
                Net = squadDir.ToString(),
                Goal = dec.Reflex != EmergentEngine.EmergentReflexKind.None ? ("Reflex:" + EmergentEngine.GetReflexShortName(dec.Reflex)) : "SurviveWin",
                Act0 = EmergentEngine.GetActionShortName(dec.Step0),
                Act1 = EmergentEngine.GetActionShortName(dec.Step1),
                Reason = dec.Reason ?? string.Empty,
                IsEmergent = 1,
                Stamps = activeStamps,
                Flags = inputFlags,
                HNow = dec.HarmNow,
                HProj = dec.HarmProj,
                Win = dec.WinUrgency,
                CanH = dec.CanHarm,
                Score = dec.Score,
                RunnerScore = dec.RunnerUpScore,
                RunnerUp = EmergentEngine.GetActionShortName(dec.RunnerUp)
            };
            PushRecord(ref rec);
        }

        public static void RecordBotProfile(
            Soldier soldier,
            AiCore.SoldierFastHandles h,
            VeteranAndTacticsPatches.SoldierPersonality personality,
            int vetTier,
            in EmergentEngine.EmergentWeights w,
            float desp,
            float now)
        {
            if (!_enabled || ReferenceEquals(soldier, null) || h == null)
                return;

            Squad sq = AiCore.GetCachedSquad(h, now);
            int sqId = sq != null ? AiCore.GetSquadDiagId(sq.Pointer) : 0;

            TraceRecord rec = new TraceRecord
            {
                Time = now,
                BotId = h.DiagId,
                SquadId = sqId,
                Kind = BotTraceEventKind.BotProfile,
                Faction = AiCore.GetCachedFaction(h, now),
                Name = h.CachedFullName,
                Role = VeteranAndTacticsPatches.GetPersonalityShortLabel(personality),
                Net = (sq != null && !string.IsNullOrEmpty(sq.squadName)) ? sq.squadName : string.Empty,
                Goal = AiCore.GetSoldierClassLabel(soldier),
                ExtraIntA = vetTier,
                HNow = w.WSurvive,
                Win = w.WWin,
                CanH = w.WAid,
                Score = w.RiskThreshold,
                RunnerScore = w.Individualism,
                ExtraA = desp
            };
            PushRecord(ref rec);
        }

        public static void RecordBotKilled(
            Soldier victim,
            AiCore.SoldierFastHandles h,
            VeteranAndTacticsPatches.SoldierPersonality personality,
            byte killerKind,
            float killerDist,
            float holdSec,
            float moved5s,
            uint lastFlags,
            float now)
        {
            if (!_enabled || ReferenceEquals(victim, null) || h == null)
                return;

            byte pose = 0;
            byte bleed = 0;
            byte inCov = 0;
            short sup = 0;
            short ammo = -1;
            float enemyDist = -1f;
            Vector3 pos = Vector3.zero;
            int sqId = 0;
            string fac = string.Empty;

            try
            {
                SoldierAI ai = h.Ai ?? victim.aiController;
                if (h.Tr != null)
                    pos = h.Tr.position;
                else if (victim.transform != null)
                    pos = victim.transform.position;

                pose = (byte)victim.m_pose;
                bleed = (byte)(victim.isBleeding ? 1 : 0);
                inCov = (byte)(victim.IsOnCover() ? 1 : 0);
                sup = (short)Mathf.Clamp(Mathf.RoundToInt(victim.GetSuppressionValue()), 0, 999);
                GenericGun heldGun = victim.GetHeldGun();
                ammo = (short)(heldGun != null ? Mathf.Clamp(heldGun.GetCurrentAmmoCount(), 0, 999) : -1);
                enemyDist = (ai != null && ai.visibleTarget != null) ? ai.distFromTarget : -1f;
                fac = AiCore.GetCachedFaction(h, now);
                Squad sq = AiCore.GetCachedSquad(h, now);
                sqId = sq != null ? AiCore.GetSquadDiagId(sq.Pointer) : 0;
            }
            catch { }

            string act0 = "None";
            string act1 = "None";
            string why = "no_decision";
            float hNow = 0f;
            float hProj = 0f;
            float canH = 0f;
            if (h.Emergent.HasDecision)
            {
                ref readonly var dec = ref h.Emergent.CurrentDecision;
                act0 = EmergentEngine.GetActionShortName(dec.Step0);
                act1 = EmergentEngine.GetActionShortName(dec.Step1);
                why = dec.Reason ?? string.Empty;
                hNow = dec.HarmNow;
                hProj = dec.HarmProj;
                canH = dec.CanHarm;
            }

            TraceRecord rec = new TraceRecord
            {
                Time = now,
                BotId = h.DiagId,
                SquadId = sqId,
                Kind = BotTraceEventKind.BotKilled,
                Pose = pose,
                Bleed = bleed,
                InCover = inCov,
                Suppression = sup,
                Ammo = ammo,
                EnemyDist = enemyDist,
                Pos = pos,
                Faction = fac,
                Name = h.CachedFullName,
                Role = VeteranAndTacticsPatches.GetPersonalityShortLabel(personality),
                Act0 = act0,
                Act1 = act1,
                Reason = why,
                HNow = hNow,
                HProj = hProj,
                CanH = canH,
                Flags = lastFlags,
                ExtraA = holdSec,
                ExtraB = moved5s,
                ExtraC = killerDist,
                ExtraIntA = killerKind
            };
            PushRecord(ref rec);
        }

        public static void RecordTier3Lever(
            int sideIdx,
            string faction,
            EmergentEngine.Tier3NetLever lever,
            string reason,
            float netH,
            float netW,
            int balks,
            int mias,
            float desp,
            float now)
        {
            if (!_enabled)
                return;

            TraceRecord rec = new TraceRecord
            {
                Time = now,
                BotId = sideIdx,
                Kind = BotTraceEventKind.Tier3Lever,
                Faction = faction ?? string.Empty,
                Net = GetTier3LeverShortName(lever),
                Reason = reason ?? string.Empty,
                HNow = netH,
                Win = netW,
                ExtraIntA = balks,
                ExtraIntB = mias,
                ExtraA = desp
            };
            PushRecord(ref rec);
        }

        public static void RecordMatchPulse(
            string tag,
            int s0Active,
            int s1Active,
            float s0Tickets,
            float s1Tickets,
            int s0Balk,
            int s1Balk,
            int s0Mia,
            int s1Mia,
            float s0Heat,
            float s1Heat,
            EmergentEngine.Tier3NetLever s0Lever,
            EmergentEngine.Tier3NetLever s1Lever,
            int totalT1Decs,
            int thrashCount,
            float now)
        {
            if (!_enabled)
                return;

            TraceRecord rec = new TraceRecord
            {
                Time = now,
                Kind = BotTraceEventKind.MatchPulse,
                Reason = tag ?? "60s",
                BotId = s0Active,
                SquadId = s1Active,
                HNow = s0Tickets,
                HProj = s1Tickets,
                Win = s0Heat,
                CanH = s1Heat,
                ExtraIntA = Mathf.Clamp(s0Balk, 0, 65535) | (Mathf.Clamp(s1Balk, 0, 65535) << 16),
                ExtraIntB = Mathf.Clamp(s0Mia, 0, 65535) | (Mathf.Clamp(s1Mia, 0, 65535) << 16),
                Goal = GetTier3LeverShortName(s0Lever),
                Act0 = GetTier3LeverShortName(s1Lever),
                Stamps = totalT1Decs,
                Flags = (uint)Math.Max(0, thrashCount)
            };
            PushRecord(ref rec);
        }

        private static string GetOverrideTraceReason(AiCore.BotMoveOverrideKind kind)
        {
            switch (kind)
            {
                case AiCore.BotMoveOverrideKind.CasualtyDrag: return "ovr_drag";
                case AiCore.BotMoveOverrideKind.UnstickSidestep: return "ovr_unstick";
                case AiCore.BotMoveOverrideKind.SapperStepBack: return "ovr_sapper_back";
                case AiCore.BotMoveOverrideKind.EmergencyFallback: return "ovr_fallback";
                case AiCore.BotMoveOverrideKind.VehicleEvade: return "ovr_veh_evade";
                default: return "ovr_active";
            }
        }

        private static void WriterThreadLoop()
        {
            StringBuilder sb = new StringBuilder(512);
            char[] charBuf = new char[1024];
            StreamWriter writer = null;
            string activePath = TraceFilePath;
            string prevPath = Path.Combine(Paths.BepInExRootPath, "BotActionTrace.prev.jsonl");

            while (true)
            {
                try
                {
                    _wakeSignal.WaitOne(1000);

                    int r = _readSeq;
                    int w = Volatile.Read(ref _writeSeq);
                    if (r == w)
                        continue;

                    Thread.Sleep(100);
                    w = Volatile.Read(ref _writeSeq);

                    if (writer == null)
                    {
                        RolloverIfOversized(activePath, prevPath);
                        FileStream fs = new FileStream(activePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 8192);
                        writer = new StreamWriter(fs, new UTF8Encoding(false), 4096);
                    }
                    else if (writer.BaseStream.Length >= MaxLogBytes)
                    {
                        writer.Flush();
                        writer.Dispose();
                        writer = null;
                        RolloverIfOversized(activePath, prevPath);
                        FileStream fs = new FileStream(activePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 8192);
                        writer = new StreamWriter(fs, new UTF8Encoding(false), 4096);
                    }

                    int batchCount = 0;
                    while (r != w && batchCount < 512)
                    {
                        TraceRecord rec = _ring[r & RingMask];
                        r++;
                        batchCount++;

                        FormatJsonLine(sb, ref rec);
                        int len = sb.Length;
                        if (len > charBuf.Length)
                            charBuf = new char[Math.Max(len + 128, charBuf.Length * 2)];
                        sb.CopyTo(0, charBuf, 0, len);
                        writer.Write(charBuf, 0, len);
                    }

                    Volatile.Write(ref _readSeq, r);
                    writer.Flush();
                }
                catch
                {
                    try { writer?.Dispose(); } catch { }
                    writer = null;
                    Thread.Sleep(500);
                }
            }
        }

        private static void RolloverIfOversized(string activePath, string prevPath)
        {
            try
            {
                if (!File.Exists(activePath))
                    return;
                FileInfo fi = new FileInfo(activePath);
                if (fi.Length < MaxLogBytes)
                    return;
                if (File.Exists(prevPath))
                    File.Delete(prevPath);
                File.Move(activePath, prevPath);
            }
            catch { }
        }

        private static void FormatJsonLine(StringBuilder sb, ref TraceRecord r)
        {
            sb.Clear();
            if (r.Kind == BotTraceEventKind.SessionStart)
            {
                sb.Append("{\"t\":");
                AppendFloat1(sb, r.Time);
                sb.Append(",\"ev\":\"session\",\"ver\":\"");
                AppendEscaped(sb, r.Role);
                sb.Append("\",\"map\":\"");
                AppendEscaped(sb, r.Faction);
                sb.Append("\",\"why\":\"");
                AppendEscaped(sb, r.Reason);
                sb.Append("\",\"dropped\":");
                AppendInt(sb, Volatile.Read(ref _droppedEvents));
                sb.Append("}\n");
                return;
            }
            if (r.Kind == BotTraceEventKind.BotProfile)
            {
                sb.Append("{\"t\":");
                AppendFloat2(sb, r.Time);
                sb.Append(",\"id\":");
                AppendInt(sb, r.BotId);
                sb.Append(",\"name\":\"");
                AppendEscaped(sb, r.Name);
                sb.Append("\",\"sq\":");
                AppendInt(sb, r.SquadId);
                sb.Append(",\"sqName\":\"");
                AppendEscaped(sb, r.Net);
                sb.Append("\",\"fac\":\"");
                AppendEscaped(sb, r.Faction);
                sb.Append("\",\"role\":\"");
                AppendEscaped(sb, r.Role);
                sb.Append("\",\"cls\":\"");
                AppendEscaped(sb, r.Goal);
                sb.Append("\",\"ev\":\"bot_profile\",\"vet\":");
                AppendInt(sb, r.ExtraIntA);
                sb.Append(",\"wSurv\":");
                AppendFloat2(sb, r.HNow);
                sb.Append(",\"wWin\":");
                AppendFloat2(sb, r.Win);
                sb.Append(",\"wAid\":");
                AppendFloat2(sb, r.CanH);
                sb.Append(",\"risk\":");
                AppendFloat2(sb, r.Score);
                sb.Append(",\"ind\":");
                AppendFloat2(sb, r.RunnerScore);
                sb.Append(",\"desp\":");
                AppendFloat2(sb, r.ExtraA);
                sb.Append("}\n");
                return;
            }
            if (r.Kind == BotTraceEventKind.BotKilled)
            {
                sb.Append("{\"t\":");
                AppendFloat2(sb, r.Time);
                sb.Append(",\"id\":");
                AppendInt(sb, r.BotId);
                sb.Append(",\"name\":\"");
                AppendEscaped(sb, r.Name);
                sb.Append("\",\"sq\":");
                AppendInt(sb, r.SquadId);
                sb.Append(",\"fac\":\"");
                AppendEscaped(sb, r.Faction);
                sb.Append("\",\"role\":\"");
                AppendEscaped(sb, r.Role);
                sb.Append("\",\"ev\":\"bot_killed\",\"act0\":\"");
                sb.Append(r.Act0 ?? "None");
                sb.Append("\",\"act1\":\"");
                sb.Append(r.Act1 ?? "None");
                sb.Append("\",\"why\":\"");
                AppendEscaped(sb, r.Reason);
                sb.Append("\",\"pose\":\"");
                sb.Append(GetPoseShortName(r.Pose));
                sb.Append("\",\"cov\":");
                AppendInt(sb, r.InCover);
                sb.Append(",\"bleed\":");
                AppendInt(sb, r.Bleed);
                sb.Append(",\"sup\":");
                AppendInt(sb, r.Suppression);
                sb.Append(",\"ammo\":");
                AppendInt(sb, r.Ammo);
                sb.Append(",\"enemyDist\":");
                AppendFloat1(sb, r.EnemyDist);
                sb.Append(",\"hNow\":");
                AppendFloat2(sb, r.HNow);
                sb.Append(",\"hProj\":");
                AppendFloat2(sb, r.HProj);
                sb.Append(",\"canH\":");
                AppendFloat2(sb, r.CanH);
                sb.Append(",\"flags\":");
                AppendUInt(sb, r.Flags);
                sb.Append(",\"hold\":");
                AppendFloat1(sb, r.ExtraA);
                sb.Append(",\"moved\":");
                AppendFloat1(sb, r.ExtraB);
                sb.Append(",\"kKind\":\"");
                sb.Append(GetKillerKindName(r.ExtraIntA));
                sb.Append("\",\"kDist\":");
                AppendFloat1(sb, r.ExtraC);
                sb.Append(",\"pos\":[");
                AppendFloat1(sb, r.Pos.x);
                sb.Append(',');
                AppendFloat1(sb, r.Pos.y);
                sb.Append(',');
                AppendFloat1(sb, r.Pos.z);
                sb.Append("]}\n");
                return;
            }
            if (r.Kind == BotTraceEventKind.Tier3Lever)
            {
                sb.Append("{\"t\":");
                AppendFloat2(sb, r.Time);
                sb.Append(",\"ev\":\"tier3_lever\",\"side\":");
                AppendInt(sb, r.BotId);
                sb.Append(",\"fac\":\"");
                AppendEscaped(sb, r.Faction);
                sb.Append("\",\"net\":\"");
                sb.Append(r.Net ?? "None");
                sb.Append("\",\"why\":\"");
                AppendEscaped(sb, r.Reason);
                sb.Append("\",\"hNow\":");
                AppendFloat2(sb, r.HNow);
                sb.Append(",\"win\":");
                AppendFloat2(sb, r.Win);
                sb.Append(",\"balk\":");
                AppendInt(sb, r.ExtraIntA);
                sb.Append(",\"mia\":");
                AppendInt(sb, r.ExtraIntB);
                sb.Append(",\"desp\":");
                AppendFloat2(sb, r.ExtraA);
                sb.Append("}\n");
                return;
            }
            if (r.Kind == BotTraceEventKind.MatchPulse)
            {
                sb.Append("{\"t\":");
                AppendFloat2(sb, r.Time);
                sb.Append(",\"ev\":\"match_pulse\",\"why\":\"");
                AppendEscaped(sb, r.Reason);
                sb.Append("\",\"s0Act\":");
                AppendInt(sb, r.BotId);
                sb.Append(",\"s1Act\":");
                AppendInt(sb, r.SquadId);
                sb.Append(",\"s0Tkt\":");
                AppendFloat2(sb, r.HNow);
                sb.Append(",\"s1Tkt\":");
                AppendFloat2(sb, r.HProj);
                sb.Append(",\"s0Balk\":");
                AppendInt(sb, r.ExtraIntA & 0xFFFF);
                sb.Append(",\"s1Balk\":");
                AppendInt(sb, (r.ExtraIntA >> 16) & 0xFFFF);
                sb.Append(",\"s0Mia\":");
                AppendInt(sb, r.ExtraIntB & 0xFFFF);
                sb.Append(",\"s1Mia\":");
                AppendInt(sb, (r.ExtraIntB >> 16) & 0xFFFF);
                sb.Append(",\"s0H\":");
                AppendFloat2(sb, r.Win);
                sb.Append(",\"s1H\":");
                AppendFloat2(sb, r.CanH);
                sb.Append(",\"s0Lev\":\"");
                sb.Append(r.Goal ?? "None");
                sb.Append("\",\"s1Lev\":\"");
                sb.Append(r.Act0 ?? "None");
                sb.Append("\",\"t1Dec\":");
                AppendInt(sb, r.Stamps);
                sb.Append(",\"thrash\":");
                AppendUInt(sb, r.Flags);
                sb.Append("}\n");
                return;
            }

            sb.Append("{\"t\":");
            AppendFloat2(sb, r.Time);
            sb.Append(",\"id\":");
            AppendInt(sb, r.BotId);
            sb.Append(",\"name\":\"");
            AppendEscaped(sb, r.Name);
            sb.Append("\",\"sq\":");
            AppendInt(sb, r.SquadId);
            sb.Append(",\"fac\":\"");
            AppendEscaped(sb, r.Faction);
            sb.Append("\",\"role\":\"");
            AppendEscaped(sb, r.Role);
            sb.Append("\",\"ev\":\"");
            sb.Append(GetEventKindName(r.Kind));
            sb.Append("\",\"net\":\"");
            sb.Append(r.Net ?? "None");
            sb.Append("\",\"goal\":\"");
            sb.Append(r.Goal ?? "None");
            sb.Append("\",\"act0\":\"");
            sb.Append(r.Act0 ?? "None");
            sb.Append("\",\"act1\":\"");
            sb.Append(r.Act1 ?? "None");
            sb.Append("\",\"pose\":\"");
            sb.Append(GetPoseShortName(r.Pose));
            sb.Append("\",\"cov\":");
            AppendInt(sb, r.InCover);
            sb.Append(",\"bleed\":");
            AppendInt(sb, r.Bleed);
            sb.Append(",\"sup\":");
            AppendInt(sb, r.Suppression);
            sb.Append(",\"ammo\":");
            AppendInt(sb, r.Ammo);
            sb.Append(",\"enemyDist\":");
            AppendFloat1(sb, r.EnemyDist);
            sb.Append(",\"gunfireDist\":");
            AppendFloat1(sb, r.GunfireDist);
            sb.Append(",\"pos\":[");
            AppendFloat1(sb, r.Pos.x);
            sb.Append(',');
            AppendFloat1(sb, r.Pos.y);
            sb.Append(',');
            AppendFloat1(sb, r.Pos.z);
            sb.Append("],\"dest\":[");
            AppendFloat1(sb, r.Dest.x);
            sb.Append(',');
            AppendFloat1(sb, r.Dest.y);
            sb.Append(',');
            AppendFloat1(sb, r.Dest.z);
            sb.Append("],\"why\":\"");
            AppendEscaped(sb, r.Reason);
            if (r.IsEmergent != 0)
            {
                sb.Append("\",\"hNow\":");
                AppendFloat2(sb, r.HNow);
                sb.Append(",\"hProj\":");
                AppendFloat2(sb, r.HProj);
                sb.Append(",\"win\":");
                AppendFloat2(sb, r.Win);
                sb.Append(",\"canH\":");
                AppendFloat2(sb, r.CanH);
                sb.Append(",\"score\":");
                AppendFloat2(sb, r.Score);
                sb.Append(",\"runUp\":\"");
                sb.Append(r.RunnerUp ?? "None");
                sb.Append("\",\"runScore\":");
                AppendFloat2(sb, r.RunnerScore);
                sb.Append(",\"stamps\":");
                AppendInt(sb, r.Stamps);
                sb.Append(",\"flags\":");
                AppendUInt(sb, r.Flags);
                sb.Append("}\n");
            }
            else
            {
                sb.Append("\"}\n");
            }
        }

        private static string GetEventKindName(BotTraceEventKind k)
        {
            switch (k)
            {
                case BotTraceEventKind.GoapPlan: return "goap_plan";
                case BotTraceEventKind.BandageStart: return "bandage_start";
                case BotTraceEventKind.BandageBlocked: return "bandage_blocked";
                case BotTraceEventKind.MeleeEnter: return "melee_enter";
                case BotTraceEventKind.MeleeSuppressed: return "melee_suppressed";
                case BotTraceEventKind.Vault: return "vault";
                case BotTraceEventKind.VaultRejected: return "vault_rejected";
                case BotTraceEventKind.LeaderBalk: return "balk_leader";
                case BotTraceEventKind.SelfBalk: return "balk_self";
                case BotTraceEventKind.FallbackRetreat: return "retreat_fallback";
                case BotTraceEventKind.CrewEvac: return "retreat_crew";
                case BotTraceEventKind.SquadRetreat: return "retreat_squad";
                case BotTraceEventKind.StuckAlert: return "stuck_alert";
                case BotTraceEventKind.Surrender: return "surrender";
                case BotTraceEventKind.UnSurrender: return "unsurrender";
                case BotTraceEventKind.RetreatDespawn: return "retreat_despawn";
                case BotTraceEventKind.VehicleBailout: return "vehicle_bailout";
                case BotTraceEventKind.AnomalyAlert: return "anomaly_alert";
                case BotTraceEventKind.EmplacementBoard: return "emplacement_board";
                case BotTraceEventKind.EmplacementBail: return "emplacement_bail";
                case BotTraceEventKind.GrenadeKick: return "grenade_kick";
                case BotTraceEventKind.GrenadeEvade: return "grenade_evade";
                case BotTraceEventKind.EmergentPlan: return "emergent_plan";
                case BotTraceEventKind.EmergentReflex: return "emergent_reflex";
                case BotTraceEventKind.Tier2Balk: return "tier2_balk";
                case BotTraceEventKind.Tier3Lever: return "tier3_lever";
                case BotTraceEventKind.TroopInteract: return "troop_interact";
                case BotTraceEventKind.BotProfile: return "bot_profile";
                case BotTraceEventKind.BotKilled: return "bot_killed";
                case BotTraceEventKind.MatchPulse: return "match_pulse";
                default: return "event";
            }
        }

        private static string GetKillerKindName(int kind)
        {
            switch (kind)
            {
                case 1: return "inf";
                case 2: return "veh";
                case 3: return "blast";
                case 4: return "bleed";
                default: return "unk";
            }
        }

        private static string GetTier3LeverShortName(EmergentEngine.Tier3NetLever lev)
        {
            switch (lev)
            {
                case EmergentEngine.Tier3NetLever.SmokeBarrage: return "SmokeBarrage";
                case EmergentEngine.Tier3NetLever.ArtilleryHE: return "ArtilleryHE";
                case EmergentEngine.Tier3NetLever.TankDispatch: return "TankDispatch";
                case EmergentEngine.Tier3NetLever.ArmorInfantryPair: return "ArmorInfantryPair";
                case EmergentEngine.Tier3NetLever.CasVector: return "CasVector";
                case EmergentEngine.Tier3NetLever.AtCounterRoute: return "AtCounterRoute";
                case EmergentEngine.Tier3NetLever.MassAssault: return "MassAssault";
                case EmergentEngine.Tier3NetLever.HoldAndFormUp: return "HoldAndFormUp";
                case EmergentEngine.Tier3NetLever.ShiftAxisFlank: return "ShiftAxisFlank";
                case EmergentEngine.Tier3NetLever.AnvilAssign: return "AnvilAssign";
                case EmergentEngine.Tier3NetLever.FireBrigade: return "FireBrigade";
                case EmergentEngine.Tier3NetLever.AuthorizeWithdraw: return "AuthorizeWithdraw";
                case EmergentEngine.Tier3NetLever.SectorDesperationBias: return "SectorDesperationBias";
                default: return "None";
            }
        }

        private static string GetPoseShortName(byte pose)
        {
            switch ((SoldierPose)pose)
            {
                case SoldierPose.Idle: return "Stand";
                case SoldierPose.Crouch: return "Crouch";
                case SoldierPose.Prone: return "Prone";
                default: return "Other";
            }
        }

        private static void AppendEscaped(StringBuilder sb, string s)
        {
            if (string.IsNullOrEmpty(s))
                return;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"' || c == '\\')
                {
                    sb.Append('\\');
                    sb.Append(c);
                }
                else if (c >= ' ')
                {
                    sb.Append(c);
                }
            }
        }

        private static void AppendInt(StringBuilder sb, int val)
        {
            if (val == 0)
            {
                sb.Append('0');
                return;
            }
            if (val < 0)
            {
                sb.Append('-');
                if (val == int.MinValue)
                {
                    sb.Append("2147483648");
                    return;
                }
                val = -val;
            }
            int start = sb.Length;
            while (val > 0)
            {
                sb.Append((char)('0' + (val % 10)));
                val /= 10;
            }
            int end = sb.Length - 1;
            while (start < end)
            {
                char tmp = sb[start];
                sb[start] = sb[end];
                sb[end] = tmp;
                start++;
                end--;
            }
        }

        private static void AppendUInt(StringBuilder sb, uint val)
        {
            if (val == 0u)
            {
                sb.Append('0');
                return;
            }
            int start = sb.Length;
            while (val > 0u)
            {
                sb.Append((char)('0' + (val % 10u)));
                val /= 10u;
            }
            int end = sb.Length - 1;
            while (start < end)
            {
                char tmp = sb[start];
                sb[start] = sb[end];
                sb[end] = tmp;
                start++;
                end--;
            }
        }

        private static void AppendFloat1(StringBuilder sb, float val)
        {
            if (float.IsNaN(val) || float.IsInfinity(val))
            {
                sb.Append("-1");
                return;
            }
            int scaled = (int)Math.Round(val * 10.0f);
            if (scaled < 0)
            {
                sb.Append('-');
                scaled = -scaled;
            }
            AppendInt(sb, scaled / 10);
            sb.Append('.');
            sb.Append((char)('0' + (scaled % 10)));
        }

        private static void AppendFloat2(StringBuilder sb, float val)
        {
            if (float.IsNaN(val) || float.IsInfinity(val))
            {
                sb.Append("0.00");
                return;
            }
            int scaled = (int)Math.Round(val * 100.0f);
            if (scaled < 0)
            {
                sb.Append('-');
                scaled = -scaled;
            }
            AppendInt(sb, scaled / 100);
            sb.Append('.');
            int rem = scaled % 100;
            sb.Append((char)('0' + (rem / 10)));
            sb.Append((char)('0' + (rem % 10)));
        }
    }
}

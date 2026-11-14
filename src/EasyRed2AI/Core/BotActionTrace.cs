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
        StuckAlert = 14
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
            public string Role;
            public string Net;
            public string Goal;
            public string Act0;
            public string Act1;
            public string Reason;
        }

        private struct BotLastTraceState
        {
            public ulong LastPlanSig;
            public BotTraceEventKind LastDiscreteKind;
            public string LastDiscreteReason;
            public float LastDiscreteTime;
            public Vector3 AnchorPos;
            public float AnchorTime;
            public float NextStuckEmitTime;
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

            TraceRecord rec = new TraceRecord
            {
                Time = now,
                Kind = BotTraceEventKind.SessionStart,
                Faction = mapId,
                Role = Plugin.PluginVersion,
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
                if (st.LastDiscreteKind == kind &&
                    ReferenceEquals(st.LastDiscreteReason, reason) &&
                    now - st.LastDiscreteTime < 2.75f &&
                    now >= st.LastDiscreteTime)
                {
                    return;
                }
            }

            st.LastDiscreteKind = kind;
            st.LastDiscreteReason = reason;
            st.LastDiscreteTime = now;
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
            Squad sq = soldier.joinedSquad;
            int sqId = sq != null ? AiCore.GetSquadDiagId(sq.Pointer) : 0;

            VeteranAndTacticsPatches.TryGetBotGoapSummary(
                soldier,
                out var netDir,
                out var goalBit,
                out var step0,
                out var step1,
                out _,
                out _);

            TraceRecord rec = new TraceRecord
            {
                Time = now,
                BotId = h.DiagId,
                SquadId = sqId,
                Kind = kind,
                Pose = (byte)soldier.m_pose,
                Bleed = (byte)((soldier.isBleeding || soldier.GetBleedValue() > 0.01f) ? 1 : 0),
                InCover = (byte)(soldier.IsOnCover() ? 1 : 0),
                Suppression = (short)Mathf.Clamp(Mathf.RoundToInt(soldier.GetSuppressionValue()), 0, 999),
                Ammo = ammo,
                EnemyDist = enemyDist,
                GunfireDist = gunfireDist,
                Pos = pos,
                Dest = dest,
                Faction = fac,
                Role = VeteranAndTacticsPatches.GetPersonalityShortLabel(VeteranAndTacticsPatches.GetPersonality(soldier)),
                Net = VeteranAndTacticsPatches.GetNetDirectiveShortName(netDir),
                Goal = VeteranAndTacticsPatches.GetGoapGoalShortName(goalBit),
                Act0 = VeteranAndTacticsPatches.GetGoapActionShortName(step0),
                Act1 = VeteranAndTacticsPatches.GetGoapActionShortName(step1),
                Reason = reason ?? string.Empty
            };
            PushRecord(ref rec);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RecordGoapState(
            SoldierAI ai,
            Soldier soldier,
            VeteranAndTacticsPatches.SoldierPersonality personality,
            VeteranAndTacticsPatches.SquadNetDirective netDir,
            VeteranAndTacticsPatches.GoapStateBits goalBit,
            VeteranAndTacticsPatches.GoapActionId step0,
            VeteranAndTacticsPatches.GoapActionId step1,
            bool isEvacCrew,
            float suppression,
            float enemyDist,
            float now)
        {
            if (!_enabled || ReferenceEquals(soldier, null) || ReferenceEquals(ai, null))
                return;

            RecordGoapStateInternal(ai, soldier, personality, netDir, goalBit, step0, step1, isEvacCrew, suppression, enemyDist, now);
        }

        private static void RecordGoapStateInternal(
            SoldierAI ai,
            Soldier soldier,
            VeteranAndTacticsPatches.SoldierPersonality personality,
            VeteranAndTacticsPatches.SquadNetDirective netDir,
            VeteranAndTacticsPatches.GoapStateBits goalBit,
            VeteranAndTacticsPatches.GoapActionId step0,
            VeteranAndTacticsPatches.GoapActionId step1,
            bool isEvacCrew,
            float suppression,
            float enemyDist,
            float now)
        {
            AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandles(soldier);
            if (h == null || h.Tr == null)
                return;

            IntPtr ptr = h.Ptr;
            Vector3 pos = h.Tr.position;
            Vector3 dest = ai.MoveDestination;
            byte bleed = (byte)((soldier.isBleeding || soldier.GetBleedValue() > 0.01f) ? 1 : 0);
            byte inCov = (byte)(soldier.IsOnCover() ? 1 : 0);
            byte pose = (byte)soldier.m_pose;

            ulong sig = ((ulong)(byte)step0) |
                        ((ulong)(byte)step1 << 8) |
                        ((ulong)(byte)netDir << 16) |
                        ((ulong)bleed << 24) |
                        ((ulong)(isEvacCrew ? 1 : 0) << 28) |
                        (((ulong)goalBit >> 26) << 32);

            _botStates.TryGetValue(ptr, out BotLastTraceState st);

            bool emitPlanChange = st.LastPlanSig != sig;
            bool emitStuck = false;

            if (st.AnchorTime <= 0f || (pos - st.AnchorPos).sqrMagnitude >= 0.36f)
            {
                st.AnchorPos = pos;
                st.AnchorTime = now;
            }
            else if (now - st.AnchorTime >= 6.5f &&
                     now >= st.NextStuckEmitTime &&
                     inCov == 0 &&
                     !soldier.UsingBipod() &&
                     ai.visibleTarget == null &&
                     (dest - pos).sqrMagnitude > 16.0f)
            {
                emitStuck = true;
                st.NextStuckEmitTime = now + 10.0f;
            }

            st.LastPlanSig = sig;
            _botStates[ptr] = st;

            if (!emitPlanChange && !emitStuck)
                return;

            string fac = AiCore.GetCachedFaction(h, now);
            if (!VisionPatches.TryGetNearestEnemyGunfire(pos, fac, 25.0f, 3.0f, out float gfDist))
                gfDist = -1f;

            GenericGun heldGun = soldier.GetHeldGun();
            short ammo = (short)(heldGun != null ? Mathf.Clamp(heldGun.GetCurrentAmmoCount(), 0, 999) : -1);
            Squad sq = soldier.joinedSquad;
            int sqId = sq != null ? AiCore.GetSquadDiagId(sq.Pointer) : 0;

            TraceRecord rec = new TraceRecord
            {
                Time = now,
                BotId = h.DiagId,
                SquadId = sqId,
                Kind = emitStuck ? BotTraceEventKind.StuckAlert : (isEvacCrew ? BotTraceEventKind.CrewEvac : BotTraceEventKind.GoapPlan),
                Pose = pose,
                Bleed = bleed,
                InCover = inCov,
                Suppression = (short)Mathf.Clamp(Mathf.RoundToInt(suppression), 0, 999),
                Ammo = ammo,
                EnemyDist = ai.visibleTarget != null ? enemyDist : -1f,
                GunfireDist = gfDist,
                Pos = pos,
                Dest = dest,
                Faction = fac,
                Role = VeteranAndTacticsPatches.GetPersonalityShortLabel(personality),
                Net = VeteranAndTacticsPatches.GetNetDirectiveShortName(netDir),
                Goal = VeteranAndTacticsPatches.GetGoapGoalShortName(goalBit),
                Act0 = VeteranAndTacticsPatches.GetGoapActionShortName(step0),
                Act1 = VeteranAndTacticsPatches.GetGoapActionShortName(step1),
                Reason = emitStuck ? "no_progress_6s" : (isEvacCrew ? "bailed_crew_evac" : "plan_transition")
            };
            PushRecord(ref rec);
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
                    _wakeSignal.WaitOne(250);

                    int r = _readSeq;
                    int w = Volatile.Read(ref _writeSeq);
                    if (r == w)
                        continue;

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

            sb.Append("{\"t\":");
            AppendFloat2(sb, r.Time);
            sb.Append(",\"id\":");
            AppendInt(sb, r.BotId);
            sb.Append(",\"sq\":");
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
            sb.Append("\"}\n");
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
                default: return "event";
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

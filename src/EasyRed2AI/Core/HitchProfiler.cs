using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Profiling;

namespace EasyRed2HumanAI
{
    internal enum HitchBucket
    {
        VanBMUpdate = 0,
        ModBMUpdate = 1,
        VanLuaPhase = 2,
        VanRecalcObjs = 3,
        VanSeqTick = 4,
        ModExtraSeqTicks = 5,
        VanSoldierSeq = 6,
        ModSeqUpdate = 7,
        GoapSoldier = 8,
        GoapSquadBoard = 9,
        GoapFlankCovers = 10,
        GoapScavenge = 11,
        GoapFloorSnap = 12,
        WorldSnapshot = 13,
        MacroDirector = 14,
        Emplacements = 15,
        TeamNetGoap = 16,
        CombinedArmsIntel = 17,
        ConquestQM = 18,
        DictCleanup = 19,
        CanSeeTotal = 20,
        CanSeeMod = 21,
        TurretPlaneSee = 22,
        MovementRoutine = 23,
        RotateAndFire = 24,
        UpdateMoveDir = 25,
        CoverAvail = 26,
        TurretRefresh = 27,
        GroundVehicles = 28,
        Aircraft = 29,
        BallisticsExpl = 30,
        BloodImpactFx = 31,
        OnGUI = 32,
        VanSpawnSquad = 33,
        VanSquadLeaderRtn = 34,
        VanSendCovers = 35,
        VanMedicRtn = 36,
        VanSoldierPath = 37,
        VanVehiclePath = 38,
        VehFixedUpdate = 39,
        Count = 40
    }

    /// <summary>
    /// Zero-allocation Black-Box Flight Recorder for diagnosing frame-time hitches (> 28ms).
    /// Attributes Unity's <c>Time.unscaledDeltaTime</c> on frame transition (Frame N+1) against
    /// the finished Frame N's recorded phases, buckets, event breadcrumbs, and .NET/IL2CPP GC deltas.
    /// Emits unconditional <c>[HitchAutopsy]</c> warnings and 15s <c>[HitchSummary]</c> baselines
    /// even when <c>DebugLogging</c> is OFF or <c>EnableMod</c> is toggled OFF via <c>F8</c>.
    /// </summary>
    internal static class HitchProfiler
    {
        private const int BucketCount = (int)HitchBucket.Count;
        private const float HitchThresholdMs = 28.0f;

        private static readonly string[] _bucketNames = new string[BucketCount]
        {
            "VanBMUpdate",
            "ModBMUpdate",
            "VanLuaPhase",
            "VanRecalcObjs",
            "VanSeqTick",
            "ModExtraSeqTicks",
            "VanSoldierSeq",
            "ModSeqUpdate",
            "GoapSoldier",
            "GoapSquadBoard",
            "GoapFlankCovers",
            "GoapScavenge",
            "GoapFloorSnap",
            "WorldSnapshot",
            "MacroDirector",
            "Emplacements",
            "TeamNetGoap",
            "CombinedArmsIntel",
            "ConquestQM",
            "DictCleanup",
            "CanSeeTotal",
            "CanSeeMod",
            "TurretPlaneSee",
            "MovementRoutine",
            "RotateAndFire",
            "UpdateMoveDir",
            "CoverAvail",
            "TurretRefresh",
            "GroundVehicles",
            "Aircraft",
            "BallisticsExpl",
            "BloodImpactFx",
            "OnGUI",
            "VanSpawnSquad",
            "VanSquadLeaderRtn",
            "VanSendCovers",
            "VanMedicRtn",
            "VanSoldierPath",
            "VanVehiclePath",
            "VehFixedUpdate"
        };

        private static readonly double _ticksToMs = 1000.0 / Stopwatch.Frequency;
        private static readonly long _recheckIntervalTicks = Math.Max(1L, (long)(Stopwatch.Frequency * 0.00015)); // 0.15ms

        private static readonly long[] _ticks = new long[BucketCount];
        private static readonly int[] _calls = new int[BucketCount];
        private static readonly long[] _maxTicks = new long[BucketCount];
        private static readonly int[] _sortIndices = new int[BucketCount];
        private static readonly StringBuilder _sb = new StringBuilder(1280);

        // Active Frame N state
        private static int _activeFrame = -1;
        private static long _lastFrameCheckStamp;
        private static long _firstHookStamp;
        private static long _bmStartStamp;
        private static long _bmEndStamp;
        private static long _lastHookEndStamp;
        private static float _bmFixedTime = -1f;
        private static float _prevBmFixedTime = -1f;
        private static float _bmScaledTime;
        private static float _bmTimeSinceLevelLoad;
        private static float _bmTimeScale = 1f;
        private static bool _bmModEnabled = true;
        private static bool _bmBattleEnded;

        // Per-frame event breadcrumbs (zero allocations)
        private static int _canSeeLinecastPassThroughs;
        private static int _uncachedCoverProbes;
        private static bool _worldSnapRebuilt;
        private static int _floorSnapCount = -1;
        private static int _scavengeInvScanned;
        private static int _macroStepRan = -1;
        private static int _cleanupSliceRan = -1;
        private static bool _conqQmRan;
        private static int _spawnedSquadCount;
        private static bool _spawnedWithVehicle;
        private static string _lastSpawnedSquadId;

        // Memory / GC baselines at the start of Frame N
        private static bool _il2cppGcSupported = true;
        private static bool _unityProfilerSupported = true;
        private static int _frameStartIl2CppGc0;
        private static long _frameStartIl2CppUsed;
        private static long _frameStartMonoHeap;
        private static long _frameStartNativeAlloc;
        private static int _frameStartNetGc0;
        private static int _frameStartNetGc1;
        private static int _frameStartNetGc2;
        private static long _frameStartNetMem;

        // Rolling & global counters
        private static int _totalAutopsyHitches;
        private static int _suppressedBurstHitches;
        private static float _autopsyBurstWindowStart;
        private static int _autopsyLogsInCurrentSecond;

        // 15-second [HitchSummary] rolling telemetry
        private static float _nextSummaryUnscaledTime;
        private static int _winFrames;
        private static int _winHitches;
        private static float _winMaxDtMs;
        private static double _winSumDtMs;
        private static long _winIl2CppPosAllocBytes;
        private static long _winPeakIl2CppFrameAllocBytes;
        private static int _winIl2CppGcCount;
        private static long _winNetPosAllocBytes;
        private static int _winNetGcCount;

        internal static bool Enabled;
        private static Harmony _probeHarmony;
        private static bool _probesPatched;

        public static void RefreshConfigState()
        {
            bool wantEnabled = VisionConfig.EnableHitchFlightRecorder != null && VisionConfig.EnableHitchFlightRecorder.Value;
            if (wantEnabled == Enabled && wantEnabled == _probesPatched)
                return;

            Enabled = wantEnabled;
            _activeFrame = -1;
            _lastFrameCheckStamp = 0L;
            _firstHookStamp = 0L;
            _bmStartStamp = 0L;
            _bmEndStamp = 0L;
            _lastHookEndStamp = 0L;
            _prevBmFixedTime = -1f;
            _bmFixedTime = -1f;
            _nextSummaryUnscaledTime = 0f;
            _winFrames = 0;
            _winHitches = 0;
            _winMaxDtMs = 0f;
            _winSumDtMs = 0.0;
            _winIl2CppPosAllocBytes = 0;
            _winPeakIl2CppFrameAllocBytes = 0;
            _winIl2CppGcCount = 0;
            _winNetPosAllocBytes = 0;
            _winNetGcCount = 0;

            try
            {
                if (wantEnabled && !_probesPatched)
                {
                    if (_probeHarmony == null)
                        _probeHarmony = new Harmony(Plugin.PluginGuid + ".hitchprobes");

                    PatchProbe(typeof(SoldierAI), nameof(SoldierAI.UpdatePath), nameof(ProbeBeginPrefix), nameof(SoldierPathPostfix));
                    PatchProbe(typeof(AIVehicle), nameof(AIVehicle.UpdatePath), nameof(ProbeBeginPrefix), nameof(VehiclePathPostfix));
                    PatchProbe(typeof(BattleManager), nameof(BattleManager.TryExecutingPhaseScript), nameof(ProbeBeginPrefix), nameof(LuaPhasePostfix));
                    PatchProbe(typeof(BattleManager), nameof(BattleManager.RecalculateObjectivesForAll), nameof(ProbeBeginPrefix), nameof(RecalcObjsPostfix));
                    _probesPatched = true;
                }
                else if (!wantEnabled && _probesPatched && _probeHarmony != null)
                {
                    _probeHarmony.UnpatchSelf();
                    _probesPatched = false;
                }
            }
            catch (Exception ex)
            {
                if (Plugin.Log != null)
                    Plugin.Log.LogError($"[HitchProfiler] Probe patch sync failed: {ex.Message}");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long Begin()
        {
            if (!Enabled)
                return 0L;

            long ts = Stopwatch.GetTimestamp();
            if (_activeFrame < 0 || (ts - _lastFrameCheckStamp) >= _recheckIntervalTicks)
            {
                ts = EnsureFrameSlow(ts);
            }
            return ts;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long BeginTopLevel()
        {
            if (!Enabled)
                return 0L;

            long ts = Stopwatch.GetTimestamp();
            return EnsureFrameSlow(ts);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void End(HitchBucket bucket, long startStamp)
        {
            if (!Enabled || startStamp <= 0)
                return;

            long endStamp = Stopwatch.GetTimestamp();
            _lastHookEndStamp = endStamp;
            long elapsed = endStamp - startStamp;
            if (elapsed < 0)
                return;

            int idx = (int)bucket;
            _ticks[idx] += elapsed;
            _calls[idx]++;
            if (elapsed > _maxTicks[idx])
                _maxTicks[idx] = elapsed;
        }

        public static long OnBattleManagerUpdatePrefix()
        {
            bool cfgEnabled = VisionConfig.EnableHitchFlightRecorder != null && VisionConfig.EnableHitchFlightRecorder.Value;
            if (cfgEnabled != Enabled)
                RefreshConfigState();
            if (!Enabled)
                return 0L;

            long ts = Stopwatch.GetTimestamp();
            ts = EnsureFrameSlow(ts);
            _bmStartStamp = ts;
            _bmFixedTime = Time.fixedTime;
            _bmScaledTime = Time.time;
            _bmTimeSinceLevelLoad = Time.timeSinceLevelLoad;
            _bmTimeScale = Time.timeScale;
            _bmBattleEnded = BattleManager.isBattleEnded;
            _bmModEnabled = VisionConfig.EnableMod != null && VisionConfig.EnableMod.Value;
            return ts;
        }

        public static long OnBattleManagerUpdatePostfixStart(long vanStartStamp)
        {
            if (!Enabled)
                return 0L;

            End(HitchBucket.VanBMUpdate, vanStartStamp);
            return Stopwatch.GetTimestamp();
        }

        public static void OnBattleManagerUpdatePostfixEnd(long modStartStamp)
        {
            if (!Enabled)
                return;

            End(HitchBucket.ModBMUpdate, modStartStamp);
            _bmEndStamp = _lastHookEndStamp;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RecordCanSeeLinecastPassThrough()
        {
            if (Enabled) _canSeeLinecastPassThroughs++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RecordUncachedCoverProbe()
        {
            if (Enabled) _uncachedCoverProbes++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RecordWorldSnapshotRebuilt()
        {
            if (Enabled) _worldSnapRebuilt = true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RecordFloorSnapshotRebuilt(int itemCount)
        {
            if (Enabled) _floorSnapCount = itemCount;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RecordScavengeInventoriesScanned(int invCount)
        {
            if (Enabled) _scavengeInvScanned += invCount;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RecordMacroStep(int step)
        {
            if (Enabled) _macroStepRan = step;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RecordCleanupSlice(int slice, bool forceFull)
        {
            if (Enabled) _cleanupSliceRan = forceFull ? 99 : slice;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RecordConquestQuartermasterRan()
        {
            if (Enabled) _conqQmRan = true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RecordSquadSpawned(string squadId, bool onVehicle)
        {
            if (!Enabled)
                return;
            _spawnedSquadCount++;
            if (onVehicle)
                _spawnedWithVehicle = true;
            if (!string.IsNullOrEmpty(squadId))
                _lastSpawnedSquadId = squadId;
        }

        public static void ResetOnSceneChange()
        {
            _prevBmFixedTime = -1f;
            _bmFixedTime = -1f;
            _suppressedBurstHitches = 0;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static long EnsureFrameSlow(long nowStamp)
        {
            _lastFrameCheckStamp = nowStamp;
            int curFrame;
            try
            {
                curFrame = Time.frameCount;
            }
            catch
            {
                return nowStamp;
            }

            if (curFrame == _activeFrame)
                return nowStamp;

            if (_activeFrame >= 0)
            {
                FlushFinishedFrame(nowStamp);
                // Re-sample timestamp so any log file I/O from Frame N never pollutes Frame N+1's timers
                nowStamp = Stopwatch.GetTimestamp();
                _lastFrameCheckStamp = nowStamp;
            }

            ResetActiveFrameState(curFrame, nowStamp);
            return nowStamp;
        }

        private static void SampleMemoryCounters(
            out int il2cppGc0,
            out long il2cppUsed,
            out long monoHeap,
            out long nativeAlloc,
            out int netGc0,
            out int netGc1,
            out int netGc2,
            out long netMem)
        {
            il2cppGc0 = 0;
            il2cppUsed = 0;
            monoHeap = 0;
            nativeAlloc = 0;

            if (_il2cppGcSupported)
            {
                try
                {
                    il2cppGc0 = Il2CppSystem.GC.CollectionCount(0);
                    il2cppUsed = Il2CppSystem.GC.GetTotalMemory(false);
                }
                catch
                {
                    _il2cppGcSupported = false;
                }
            }

            if (_unityProfilerSupported)
            {
                try
                {
                    monoHeap = Profiler.GetMonoHeapSizeLong();
                    if (il2cppUsed <= 0)
                        il2cppUsed = Profiler.GetMonoUsedSizeLong();
                    nativeAlloc = Profiler.GetTotalAllocatedMemoryLong();
                }
                catch
                {
                    _unityProfilerSupported = false;
                }
            }

            netGc0 = GC.CollectionCount(0);
            netGc1 = GC.CollectionCount(1);
            netGc2 = GC.CollectionCount(2);
            netMem = GC.GetTotalMemory(false);
        }

        private static void ResetActiveFrameState(int newFrame, long startStamp)
        {
            Array.Clear(_ticks, 0, BucketCount);
            Array.Clear(_calls, 0, BucketCount);
            Array.Clear(_maxTicks, 0, BucketCount);

            if (_bmFixedTime >= 0f)
                _prevBmFixedTime = _bmFixedTime;

            _activeFrame = newFrame;
            _firstHookStamp = startStamp;
            _bmStartStamp = 0;
            _bmEndStamp = 0;
            _lastHookEndStamp = startStamp;
            _bmFixedTime = -1f;

            _canSeeLinecastPassThroughs = 0;
            _uncachedCoverProbes = 0;
            _worldSnapRebuilt = false;
            _floorSnapCount = -1;
            _scavengeInvScanned = 0;
            _macroStepRan = -1;
            _cleanupSliceRan = -1;
            _conqQmRan = false;
            _spawnedSquadCount = 0;
            _spawnedWithVehicle = false;
            _lastSpawnedSquadId = null;

            SampleMemoryCounters(
                out _frameStartIl2CppGc0,
                out _frameStartIl2CppUsed,
                out _frameStartMonoHeap,
                out _frameStartNativeAlloc,
                out _frameStartNetGc0,
                out _frameStartNetGc1,
                out _frameStartNetGc2,
                out _frameStartNetMem);
        }

        private static void FlushFinishedFrame(long nextFrameFirstStamp)
        {
            float dtMs = _firstHookStamp > 0L && nextFrameFirstStamp > _firstHookStamp
                ? (float)((nextFrameFirstStamp - _firstHookStamp) * _ticksToMs)
                : (Time.unscaledDeltaTime * 1000f);
            float unscaledNow = Time.unscaledTime;

            SampleMemoryCounters(
                out int curIl2CppGc0,
                out long curIl2CppUsed,
                out long curMonoHeap,
                out long curNativeAlloc,
                out int curNetGc0,
                out int curNetGc1,
                out int curNetGc2,
                out long curNetMem);

            int il2cppGcDelta = curIl2CppGc0 - _frameStartIl2CppGc0;
            long il2cppUsedDelta = curIl2CppUsed - _frameStartIl2CppUsed;
            long nativeDelta = curNativeAlloc - _frameStartNativeAlloc;
            bool il2cppGcRan = il2cppGcDelta > 0 || il2cppUsedDelta < -4096L;

            int netG0Delta = curNetGc0 - _frameStartNetGc0;
            int netG1Delta = curNetGc1 - _frameStartNetGc1;
            int netG2Delta = curNetGc2 - _frameStartNetGc2;
            long netMemDelta = curNetMem - _frameStartNetMem;
            bool netGcRan = netG0Delta > 0 || netG1Delta > 0 || netG2Delta > 0 || netMemDelta < -4096L;

            bool inGameplay = _bmStartStamp > 0L &&
                              !_bmBattleEnded &&
                              _bmTimeSinceLevelLoad >= 3.0f &&
                              _bmTimeScale > 0.01f &&
                              dtMs > 0.05f &&
                              dtMs < 500.0f;
            if (inGameplay)
            {
                _winFrames++;
                _winSumDtMs += dtMs;
                if (dtMs > _winMaxDtMs)
                    _winMaxDtMs = dtMs;

                if (il2cppUsedDelta > 0)
                {
                    _winIl2CppPosAllocBytes += il2cppUsedDelta;
                    if (il2cppUsedDelta > _winPeakIl2CppFrameAllocBytes)
                        _winPeakIl2CppFrameAllocBytes = il2cppUsedDelta;
                }
                if (il2cppGcRan)
                    _winIl2CppGcCount += Math.Max(1, il2cppGcDelta);

                if (netMemDelta > 0)
                    _winNetPosAllocBytes += netMemDelta;
                if (netGcRan)
                    _winNetGcCount += Math.Max(1, netG0Delta);
            }

            if (inGameplay && dtMs > HitchThresholdMs)
            {
                _totalAutopsyHitches++;
                _winHitches++;

                if (unscaledNow - _autopsyBurstWindowStart >= 1.0f)
                {
                    _autopsyBurstWindowStart = unscaledNow;
                    _autopsyLogsInCurrentSecond = 0;
                }

                if (_autopsyLogsInCurrentSecond < 4)
                {
                    _autopsyLogsInCurrentSecond++;
                    EmitHitchAutopsyLog(
                        nextFrameFirstStamp,
                        dtMs,
                        il2cppGcRan,
                        il2cppGcDelta,
                        il2cppUsedDelta,
                        curIl2CppUsed,
                        curMonoHeap,
                        nativeDelta,
                        netGcRan,
                        netG0Delta,
                        netG1Delta,
                        netG2Delta,
                        netMemDelta,
                        curNetMem);
                }
                else
                {
                    _suppressedBurstHitches++;
                }
            }

            if (inGameplay && unscaledNow >= _nextSummaryUnscaledTime)
            {
                if (_nextSummaryUnscaledTime > 0f && _winFrames > 0)
                {
                    EmitRollingSummaryLog(curIl2CppUsed, curMonoHeap, curNetMem);
                }
                _nextSummaryUnscaledTime = unscaledNow + 15.0f;
                _winFrames = 0;
                _winHitches = 0;
                _winMaxDtMs = 0f;
                _winSumDtMs = 0.0;
                _winIl2CppPosAllocBytes = 0;
                _winPeakIl2CppFrameAllocBytes = 0;
                _winIl2CppGcCount = 0;
                _winNetPosAllocBytes = 0;
                _winNetGcCount = 0;
            }
        }

        private static void EmitHitchAutopsyLog(
            long nextFrameFirstStamp,
            float dtMs,
            bool il2cppGcRan,
            int il2cppGcDelta,
            long il2cppUsedDelta,
            long curIl2CppUsed,
            long curMonoHeap,
            long nativeDelta,
            bool netGcRan,
            int netG0Delta,
            int netG1Delta,
            int netG2Delta,
            long netMemDelta,
            long curNetMem)
        {
            if (Plugin.Log == null)
                return;

            try
            {
                double preBmMs = (_bmStartStamp > _firstHookStamp && _firstHookStamp > 0)
                    ? (_bmStartStamp - _firstHookStamp) * _ticksToMs
                    : 0.0;
                double bmMs = (_bmEndStamp >= _bmStartStamp && _bmStartStamp > 0)
                    ? (_bmEndStamp - _bmStartStamp) * _ticksToMs
                    : 0.0;
                double postBmMs = (_bmEndStamp > 0 && _lastHookEndStamp > _bmEndStamp)
                    ? (_lastHookEndStamp - _bmEndStamp) * _ticksToMs
                    : 0.0;
                double hookSpanMs = (_lastHookEndStamp > _firstHookStamp && _firstHookStamp > 0)
                    ? (_lastHookEndStamp - _firstHookStamp) * _ticksToMs
                    : 0.0;
                double outsideHooksMs = Math.Max(0.0, dtMs - hookSpanMs);
                double wallGapMs = (_lastHookEndStamp > 0 && nextFrameFirstStamp > _lastHookEndStamp)
                    ? (nextFrameFirstStamp - _lastHookEndStamp) * _ticksToMs
                    : 0.0;

                int fixedSteps = -1;
                if (_prevBmFixedTime >= 0f && _bmFixedTime >= _prevBmFixedTime)
                {
                    float fdt = Mathf.Max(0.001f, Time.fixedDeltaTime);
                    fixedSteps = Mathf.RoundToInt((_bmFixedTime - _prevBmFixedTime) / fdt);
                }

                double avgIl2CppKbPerFrame = _winFrames > 0 ? (_winIl2CppPosAllocBytes / 1024.0) / _winFrames : 0.0;

                int creatureCount = 0;
                int squadCount = 0;
                int vehCount = 0;
                int floorItemCount = 0;
                int activeInvCount = 0;
                try
                {
                    var cr = Creature.allCreatures;
                    if (cr != null) creatureCount = cr.Count;
                    var sq = Squad.AllSquads;
                    if (sq != null) squadCount = sq.Count;
                    var vh = Vehicle.allVehicles;
                    if (vh != null) vehCount = vh.Count;
                    var fi = ItemObject.spawnedItems;
                    if (fi != null) floorItemCount = fi.Count;
                    var inv = InventoryManager.activeInventories;
                    if (inv != null) activeInvCount = inv.Count;
                }
                catch { }

                _sb.Clear();
                _sb.Append("[HitchAutopsy] #").Append(_totalAutopsyHitches);
                if (_suppressedBurstHitches > 0)
                {
                    _sb.Append("(+").Append(_suppressedBurstHitches).Append("burst)");
                    _suppressedBurstHitches = 0;
                }
                _sb.Append(" F:").Append(_activeFrame)
                   .Append(" t:").Append(_bmScaledTime.ToString("F1"))
                   .Append("s | dt:").Append(dtMs.ToString("F1")).Append("ms")
                   .Append(" Mod:").Append(_bmModEnabled ? "ON" : "OFF")
                   .Append(" FixedSteps:").Append(fixedSteps);

                _sb.Append(" | Phases[PreBM:").Append(preBmMs.ToString("F2"))
                   .Append("ms BM:").Append(bmMs.ToString("F2"))
                   .Append("ms PostBM:").Append(postBmMs.ToString("F2"))
                   .Append("ms OutsideHooks(Render/Present/GC):").Append(outsideHooksMs.ToString("F2"))
                   .Append("ms WallGap:").Append(wallGapMs.ToString("F2")).Append("ms]");

                _sb.Append(" | GC[IL2CPP:").Append(il2cppGcRan ? "YES" : "no");
                if (il2cppGcDelta > 0)
                    _sb.Append("(+").Append(il2cppGcDelta).Append(')');
                _sb.Append('(').Append((il2cppUsedDelta / 1024.0).ToString("+0.0;-0.0;0.0")).Append("KB, ")
                   .Append((curIl2CppUsed / (1024.0 * 1024.0)).ToString("F1")).Append('/')
                   .Append((curMonoHeap / (1024.0 * 1024.0)).ToString("F1")).Append("MB, Avg:")
                   .Append(avgIl2CppKbPerFrame.ToString("F1")).Append("KB/f)");

                if (nativeDelta != 0)
                    _sb.Append(" Native:").Append((nativeDelta / 1024.0).ToString("+0;-0;0")).Append("KB");

                _sb.Append(" Net:").Append(netGcRan ? "YES" : "no");
                if (netG0Delta > 0 || netG1Delta > 0 || netG2Delta > 0)
                    _sb.Append('(').Append(netG0Delta).Append('/').Append(netG1Delta).Append('/').Append(netG2Delta).Append(')');
                _sb.Append('(').Append((netMemDelta / 1024.0).ToString("+0.0;-0.0;0.0")).Append("KB, ")
                   .Append((curNetMem / (1024.0 * 1024.0)).ToString("F1")).Append("MB)]");

                _sb.Append(" | Scene[Cr:").Append(creatureCount)
                   .Append("(Alive:").Append(AiCore.WorldSoldierCount).Append(')')
                   .Append(" Sq:").Append(squadCount)
                   .Append(" Veh:").Append(vehCount)
                   .Append(" Floor:").Append(floorItemCount)
                   .Append(" Inv:").Append(activeInvCount).Append(']');

                // Event breadcrumbs
                _sb.Append(" | Events[");
                bool anyEvent = false;
                if (_spawnedSquadCount > 0)
                {
                    _sb.Append("Spawned:").Append(_spawnedSquadCount)
                       .Append('(').Append(_lastSpawnedSquadId ?? "?")
                       .Append(_spawnedWithVehicle ? ",VEH)" : ")");
                    anyEvent = true;
                }
                if (_worldSnapRebuilt)
                {
                    if (anyEvent) _sb.Append(' ');
                    _sb.Append("WorldSnap");
                    anyEvent = true;
                }
                if (_floorSnapCount >= 0)
                {
                    if (anyEvent) _sb.Append(' ');
                    _sb.Append("FloorSnap:").Append(_floorSnapCount);
                    anyEvent = true;
                }
                if (_scavengeInvScanned > 0)
                {
                    if (anyEvent) _sb.Append(' ');
                    _sb.Append("ScavInvScan:").Append(_scavengeInvScanned);
                    anyEvent = true;
                }
                if (_macroStepRan >= 0)
                {
                    if (anyEvent) _sb.Append(' ');
                    _sb.Append("MacroStep:").Append(_macroStepRan);
                    anyEvent = true;
                }
                if (_cleanupSliceRan >= 0)
                {
                    if (anyEvent) _sb.Append(' ');
                    _sb.Append("Cleanup:").Append(_cleanupSliceRan == 99 ? "FULL" : _cleanupSliceRan.ToString());
                    anyEvent = true;
                }
                if (_conqQmRan)
                {
                    if (anyEvent) _sb.Append(' ');
                    _sb.Append("ConqQM");
                    anyEvent = true;
                }
                if (_canSeeLinecastPassThroughs > 0)
                {
                    if (anyEvent) _sb.Append(' ');
                    _sb.Append("SeeRays:").Append(_canSeeLinecastPassThroughs);
                    anyEvent = true;
                }
                if (_uncachedCoverProbes > 0)
                {
                    if (anyEvent) _sb.Append(' ');
                    _sb.Append("CovProbes:").Append(_uncachedCoverProbes);
                    anyEvent = true;
                }
                if (!anyEvent)
                    _sb.Append("none");
                _sb.Append(']');

                // Sort non-zero buckets >= 0.08ms descending by elapsed ticks (insertion sort on <= 40 ints: zero allocs)
                int activeCount = 0;
                long minDisplayTicks = (long)(Stopwatch.Frequency * 0.00008); // 0.08ms
                for (int i = 0; i < BucketCount; i++)
                {
                    if (_ticks[i] >= minDisplayTicks)
                    {
                        _sortIndices[activeCount++] = i;
                    }
                }
                for (int i = 1; i < activeCount; i++)
                {
                    int key = _sortIndices[i];
                    long keyTicks = _ticks[key];
                    int j = i - 1;
                    while (j >= 0 && _ticks[_sortIndices[j]] < keyTicks)
                    {
                        _sortIndices[j + 1] = _sortIndices[j];
                        j--;
                    }
                    _sortIndices[j + 1] = key;
                }

                _sb.Append(" | Buckets[");
                if (activeCount == 0)
                {
                    _sb.Append("<all <0.08ms>");
                }
                else
                {
                    for (int k = 0; k < activeCount; k++)
                    {
                        if (k > 0)
                            _sb.Append(", ");
                        int b = _sortIndices[k];
                        double ms = _ticks[b] * _ticksToMs;
                        int calls = _calls[b];
                        _sb.Append(_bucketNames[b]).Append('=').Append(ms.ToString("F2")).Append("ms(").Append(calls);
                        if (calls > 1)
                        {
                            double maxSingleMs = _maxTicks[b] * _ticksToMs;
                            if (maxSingleMs >= 0.25)
                                _sb.Append(",max:").Append(maxSingleMs.ToString("F2")).Append("ms");
                        }
                        _sb.Append(')');
                    }
                }
                _sb.Append(']');

                Plugin.Log.LogWarning(_sb.ToString());
            }
            catch
            {
            }
        }

        private static void EmitRollingSummaryLog(long curIl2CppUsed, long curMonoHeap, long curNetMem)
        {
            if (Plugin.Log == null || _winFrames <= 0)
                return;

            try
            {
                double avgDtMs = _winSumDtMs / _winFrames;
                double avgFps = avgDtMs > 0.01 ? (1000.0 / avgDtMs) : 0.0;
                double avgIl2CppKb = (_winIl2CppPosAllocBytes / 1024.0) / _winFrames;
                double peakIl2CppKb = _winPeakIl2CppFrameAllocBytes / 1024.0;
                double avgNetKb = (_winNetPosAllocBytes / 1024.0) / _winFrames;

                _sb.Clear();
                _sb.Append("[HitchSummary 15s] Mod:").Append(_bmModEnabled ? "ON" : "OFF")
                   .Append(" | Frames:").Append(_winFrames)
                   .Append(" AvgFPS:").Append(avgFps.ToString("F0"))
                   .Append(" (").Append(avgDtMs.ToString("F2")).Append("ms, Max:").Append(_winMaxDtMs.ToString("F1")).Append("ms)")
                   .Append(" | Hitches15s:").Append(_winHitches).Append(" (Total:").Append(_totalAutopsyHitches).Append(')')
                   .Append(" | IL2CPP[AvgAlloc:").Append(avgIl2CppKb.ToString("F1"))
                   .Append("KB/f, PeakFrame:").Append(peakIl2CppKb.ToString("F1"))
                   .Append("KB, GCs:").Append(_winIl2CppGcCount)
                   .Append(", Heap:").Append((curIl2CppUsed / (1024.0 * 1024.0)).ToString("F1"))
                   .Append('/').Append((curMonoHeap / (1024.0 * 1024.0)).ToString("F1")).Append("MB]")
                   .Append(" | Net6[AvgAlloc:").Append(avgNetKb.ToString("F2"))
                   .Append("KB/f, GCs:").Append(_winNetGcCount)
                   .Append(", Heap:").Append((curNetMem / (1024.0 * 1024.0)).ToString("F1")).Append("MB]");

                Plugin.Log.LogInfo(_sb.ToString());
            }
            catch
            {
            }
        }

        // =====================================================================
        // Vanilla Subsystem Dynamic Probes (Patched ONLY when EnableHitchFlightRecorder == true)
        // =====================================================================
        private static void PatchProbe(Type targetType, string targetMethod, string prefixName, string postfixName)
        {
            var target = AccessTools.Method(targetType, targetMethod);
            var pre = AccessTools.Method(typeof(HitchProfiler), prefixName);
            var post = AccessTools.Method(typeof(HitchProfiler), postfixName);
            if (target != null && pre != null && post != null)
            {
                _probeHarmony.Patch(target, prefix: new HarmonyMethod(pre), postfix: new HarmonyMethod(post));
            }
        }

        private static void ProbeBeginPrefix(out long __state)
        {
            __state = Begin();
        }

        private static void SoldierPathPostfix(long __state)
        {
            End(HitchBucket.VanSoldierPath, __state);
        }

        private static void VehiclePathPostfix(long __state)
        {
            End(HitchBucket.VanVehiclePath, __state);
        }

        private static void LuaPhasePostfix(long __state)
        {
            End(HitchBucket.VanLuaPhase, __state);
        }

        private static void RecalcObjsPostfix(long __state)
        {
            End(HitchBucket.VanRecalcObjs, __state);
        }
    }
}

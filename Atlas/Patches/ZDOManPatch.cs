﻿namespace Atlas;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection.Emit;

using ComfyLib;

using HarmonyLib;

using UnityEngine;

[HarmonyPatch(typeof(ZDOMan))]
static class ZDOManPatch {
  [HarmonyPostfix]
  [HarmonyPatch(nameof(ZDOMan.Load))]
  static void LoadPostfix(ZDOMan __instance) {
    int timeCreatedMissing = 0;
    int originalUidMissing = 0;

    foreach (ZDO zdo in __instance.m_objectsByID.Values) {
      if (!zdo.TryGetLong(Atlas.TimeCreatedHashCode, out _)) {
        ZDOExtraData.Set(zdo.m_uid, Atlas.TimeCreatedHashCode, 0L);
        ZDOExtraData.Set(zdo.m_uid, Atlas.EpochTimeCreatedHashCode, 0L);

        timeCreatedMissing++;
      }

      if (!zdo.TryGetZDOID(Atlas.OriginalUidHashPair, out _)) {
        ZDOExtraData.Set(zdo.m_uid, Atlas.OriginalUidHashPair.Key, 0L);
        ZDOExtraData.Set(zdo.m_uid, Atlas.OriginalUidHashPair.Value, 0U);

        originalUidMissing++;
      }
    }

    PluginLogger.LogInfo($"Found {timeCreatedMissing} ZDOs missing TimeCreated long value, now set to 0.");
    PluginLogger.LogInfo($"Found {originalUidMissing} ZDOs missing OriginalUid ZDOID value, now set to None.");
  }

  [HarmonyTranspiler]
  [HarmonyPatch(nameof(ZDOMan.Load))]
  static IEnumerable<CodeInstruction> LoadTranspiler(
      IEnumerable<CodeInstruction> instructions) {
    return new CodeMatcher(instructions)
        // Add exception handling for duplicate ZDOs saved.
        .MatchForward(
            useEnd: false,
            new CodeMatch(OpCodes.Ldarg_0),
            new CodeMatch(OpCodes.Ldfld, AccessTools.Field(typeof(ZDOMan), nameof(ZDOMan.m_objectsByID))),
            new CodeMatch(OpCodes.Ldloc_S),
            new CodeMatch(OpCodes.Ldfld, AccessTools.Field(typeof(ZDO), nameof(ZDO.m_uid))),
            new CodeMatch(OpCodes.Ldloc_S),
            new CodeMatch(
                OpCodes.Callvirt,
                AccessTools.Method(typeof(Dictionary<ZDOID, ZDO>), nameof(Dictionary<ZDOID, ZDO>.Add))))
        .ThrowIfInvalid("Could not patch ZDOMan.Load()! (AddObjectsByIdDelegate)")
        .Advance(offset: 5)
        .SetInstructionAndAdvance(Transpilers.EmitDelegate(AddObjectsByIdDelegate))
        .InstructionEnumeration();
  }

  static void AddObjectsByIdDelegate(Dictionary<ZDOID, ZDO> objectsById, ZDOID uid, ZDO zdo) {
    if (objectsById.ContainsKey(uid)) {
      PluginLogger.LogWarning($"Duplicate ZDO ({uid}) detected, overwriting.");
    }

    objectsById[uid] = zdo;
  }

  [HarmonyEmitIL]
  [HarmonyTranspiler]
  [HarmonyPatch(nameof(ZDOMan.RPC_ZDOData))]
  static IEnumerable<CodeInstruction> RPC_ZDODataTranspiler(IEnumerable<CodeInstruction> instructions) {
    return new CodeMatcher(instructions)
        .Start()
        .MatchStartForward(
            new CodeMatch(OpCodes.Ldloc_S),
            new CodeMatch(OpCodes.Ldloc_3),
            new CodeMatch(OpCodes.Callvirt, AccessTools.Method(typeof(ZDO), nameof(ZDO.Deserialize))))
        .ThrowIfInvalid("Could not patch ZDOMan.RPC_ZDOData()! (Deserialize)")
        .SaveInstruction(offset: 0, out CodeInstruction ldLocS12)
        .Advance(offset: 3)
        .InsertAndAdvance(
            ldLocS12,
            Transpilers.EmitDelegate(SetTimeCreatedDelegate))
        .InstructionEnumeration();
  }

  static void SetTimeCreatedDelegate(ZDO zdo) {
    if (!zdo.TryGetLong(Atlas.TimeCreatedHashCode, out _)) {
      zdo.Set(Atlas.TimeCreatedHashCode, (long) (ZNet.m_instance.m_netTime * TimeSpan.TicksPerSecond));
      zdo.Set(Atlas.EpochTimeCreatedHashCode, DateTimeOffset.Now.ToUnixTimeSeconds());
    }

    if (!zdo.TryGetZDOID(Atlas.OriginalUidHashPair, out _)) {
      zdo.Set(Atlas.OriginalUidHashPair, zdo.m_uid);
    }
  }

  [HarmonyPostfix]
  [HarmonyPatch(nameof(ZDOMan.CreateNewZDO), typeof(Vector3), typeof(int))]
  static void CreateNewZDOPostfix(ZDO __result) {
    ZDOID zdoid = __result.m_uid;

    ZDOExtraData.Set(zdoid, Atlas.TimeCreatedHashCode, (long) (ZNet.m_instance.m_netTime * TimeSpan.TicksPerSecond));
    ZDOExtraData.Set(zdoid, Atlas.EpochTimeCreatedHashCode, DateTimeOffset.Now.ToUnixTimeSeconds());

    ZDOExtraData.Set(zdoid, Atlas.OriginalUidHashPair.Key, zdoid.UserID);
    ZDOExtraData.Set(zdoid, Atlas.OriginalUidHashPair.Value, zdoid.ID);
  }

  [HarmonyPrefix]
  [HarmonyPatch(nameof(ZDOMan.ConnectSpawners))]
  static bool ConnectSpawnersPrefix(ref ZDOMan __instance) {
    PluginLogger.LogInfo($"Starting ConnectSpawners with caching.");
    Stopwatch stopwatch = Stopwatch.StartNew();

    Dictionary<ZDOID, ZDOConnectionHashData> spawned = new();
    Dictionary<int, ZDOID> targetsByHash = new();

    foreach (KeyValuePair<ZDOID, ZDOConnectionHashData> pair in ZDOExtraData.s_connectionsHashData) {
      if (pair.Value.m_type == ZDOExtraData.ConnectionType.Spawned) {
        spawned.Add(pair.Key, pair.Value);
      } else if (pair.Value.m_type ==
          (ZDOExtraData.ConnectionType.Portal
              | ZDOExtraData.ConnectionType.SyncTransform
              | ZDOExtraData.ConnectionType.Target)) {
        targetsByHash[pair.Value.m_hash] = pair.Key;
      }
    }

    PluginLogger.LogInfo($"Connecting {spawned.Count} spawners against {targetsByHash.Count} targets.");

    long sessionId = __instance.m_sessionID;
    int connectedCount = 0;
    int doneCount = 0;

    foreach (KeyValuePair<ZDOID, ZDOConnectionHashData> pair in spawned) {
      if (pair.Key.IsNone() || !__instance.m_objectsByID.TryGetValue(pair.Key, out ZDO zdo) || zdo == null) {
        continue;
      }

      zdo.SetOwner(sessionId);

      if (targetsByHash.TryGetValue(pair.Value.m_hash, out ZDOID targetZdoId) && pair.Key != targetZdoId) {
        connectedCount++;
        zdo.SetConnection(ZDOExtraData.ConnectionType.Spawned, targetZdoId);
      } else {
        doneCount++;
        zdo.SetConnection(ZDOExtraData.ConnectionType.Spawned, ZDOID.None);
      }
    }

    stopwatch.Stop();
    PluginLogger.LogInfo($"Connected {connectedCount} spawners ({doneCount} 'done'), time: {stopwatch.Elapsed}");

    return false;
  }

  static CodeMatcher SaveInstruction(this CodeMatcher matcher, int offset, out CodeInstruction instruction) {
    instruction = matcher.InstructionAt(offset);
    return matcher;
  }
}

﻿using ComfyLib;

using HarmonyLib;

namespace Transporter {
  [HarmonyPatch(typeof(Terminal))]
  static class TerminalPatch {
    [HarmonyPrefix]
    [HarmonyPatch(nameof(Terminal.InitTerminal))]
    static void InitTerminalPrefix(ref bool __state) {
      __state = Terminal.m_terminalInitialized;
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(Terminal.InitTerminal))]
    static void InitTerminalPostfix(bool __state) {
      if (!__state) {
        ComfyCommandUtils.ToggleCommands(true);
      }
    }
  }
}

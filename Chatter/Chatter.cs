﻿using BepInEx;

using HarmonyLib;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using UnityEngine;
using UnityEngine.UI;

using static Chatter.PluginConfig;

namespace Chatter {
  [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
  public class Chatter : BaseUnityPlugin {
    public const string PluginGuid = "redseiko.valheim.chatter";
    public const string PluginName = "Chatter";
    public const string PluginVersion = "1.0.0";

    Harmony _harmony;

    public void Awake() {
      BindConfig(Config);

      IsModEnabled.SettingChanged += (s, ea) => ToggleChatPanel(IsModEnabled.Value);

      _harmony = Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), harmonyInstanceId: PluginGuid);
    }

    public void OnDestroy() {
      _harmony?.UnpatchSelf();
    }

    static readonly List<ChatMessage> MessageHistory = new();
    static readonly CircularQueue<GameObject> MessageRows = new(50, row => Destroy(row));

    static void SetMessageFont(Font font, int fontSize) {
      Text textPrefabText = _chatPanel.TextPrefab.GetComponent<Text>();
      int fontSizeDelta = fontSize - textPrefabText.fontSize;

      IEnumerable<Text> texts =
          _chatPanel.Panel.GetComponentsInChildren<Text>()
              .SelectMany(row => row.GetComponentsInChildren<Text>())
                  .Append(textPrefabText);

      foreach (Text text in texts) {
        text.font = font;
        text.fontSize += fontSizeDelta;
      }
    }

    static ChatPanel _chatPanel;

    [HarmonyPatch(typeof(Menu))]
    class MenuPatch {
      [HarmonyPostfix]
      [HarmonyPatch(nameof(Menu.Show))]
      static void ShowPostfix() {
        if (IsModEnabled.Value && _chatPanel != null && _chatPanel.Panel.activeSelf) {
          _chatPanel.Grabber.SetActive(true);
          _chatPanel.Panel.GetComponent<RectTransform>().sizeDelta += new Vector2(0, 200f);
          Chat.m_instance.m_hideDelay = 600;
        }
      }

      [HarmonyPostfix]
      [HarmonyPatch(nameof(Menu.Hide))]
      static void HidePostfix() {
        if (IsModEnabled.Value && _chatPanel != null && _chatPanel.Panel.activeSelf) {
          _chatPanel.Grabber.SetActive(false);
          _chatPanel.Panel.GetComponent<RectTransform>().sizeDelta = ChatPanelSize.Value;
          _chatPanel.ScrollRect.verticalNormalizedPosition = 0f;
          Chat.m_instance.m_hideDelay = 8;
        }
      }
    }

    static void ToggleChatPanel(bool toggle) {
      if (!Chat.m_instance) {
        return;
      }

      Chat.m_instance.m_chatWindow.GetComponent<RectMask2D>().enabled = !toggle;
      Chat.m_instance.m_output.gameObject.SetActive(!toggle);
      Chat.m_instance.m_chatWindow.Find("bkg").gameObject.SetActive(!toggle);

      _chatPanel ??= new(Chat.m_instance.m_chatWindow.transform.parent, Chat.m_instance.m_output);
      _chatPanel.Panel.SetActive(toggle);

      if (toggle) {
        SetChatPanelSize(ChatPanelSize.Value);
        SetChatMessageRowWidth(ChatMessageWidthOffset.Value);
      }

      if (!_chatPanel.Grabber.TryGetComponent(out PanelDragger panelDragger)) {
        _chatPanel.Grabber.SetActive(false);
        panelDragger = _chatPanel.Grabber.AddComponent<PanelDragger>();
        panelDragger.TargetTransform = _chatPanel.Panel.GetComponent<RectTransform>();
        panelDragger.EndDragAction =
            () => ChatWindowPositionOffset.Value = panelDragger.TargetTransform.anchoredPosition;
      }

      SetChatWindowPositionOffset();
      Chat.m_instance.m_input = toggle ? _chatPanel.InputField : _vanillaInputField;
    }

    static void SetChatPanelSize(Vector2 sizeDelta) {
      RectTransform panelRectTransform = _chatPanel.Panel.GetComponent<RectTransform>();
      panelRectTransform.sizeDelta = sizeDelta;
      panelRectTransform.anchoredPosition = new(0, 30f);

      _chatPanel.Viewport.GetComponent<RectTransform>().sizeDelta = sizeDelta;
      SetChatMessageRowWidth(ChatMessageWidthOffset.Value);
    }

    static void SetChatMessageRowWidth(float widthoffset) {
      float preferredWidth = _chatPanel.Panel.GetComponent<RectTransform>().sizeDelta.x + widthoffset;

      foreach (
          LayoutElement layout
              in MessageRows
                  .SelectMany(row => row.GetComponentsInChildren<LayoutElement>())
                  .Where(layout => layout.name == "Message.Row.Text")) {
        layout.preferredWidth = preferredWidth;
      }
    }

    static void SetChatWindowPositionOffset() {
      _chatPanel.Panel.GetComponent<RectTransform>().anchoredPosition = ChatWindowPositionOffset.Value;             
    }

    static ChatMessage _lastMessage = null;
    static GameObject _lastMessageRow;
    static InputField _vanillaInputField;

    [HarmonyPatch(typeof(Chat))]
    class ChatPatch {
      [HarmonyPostfix]
      [HarmonyPatch(nameof(Chat.Awake))]
      static void AwakePostfix(ref Chat __instance) {
        if (!IsModEnabled.Value) {
          return;
        }

        _vanillaInputField = __instance.m_input;

        BindChatMessageFont(__instance.m_output.font);
        ChatMessageFont.SettingChanged += (s, ea) => SetMessageFont(MessageFont, MessageFontSize);
        ChatMessageFontSize.SettingChanged += (s, ea) => SetMessageFont(MessageFont, MessageFontSize);

        ChatPanelBackgroundColor.SettingChanged +=
            (s, ea) => _chatPanel.ViewportImage.color = ChatPanelBackgroundColor.Value;

        ChatPanelRectMaskSoftness.SettingChanged +=
            (s, ea) =>
                _chatPanel.Panel.GetComponent<RectMask2D>().softness =
                    Vector2Int.RoundToInt(ChatPanelRectMaskSoftness.Value);

        BindChatPanelSize(__instance.m_chatWindow);

        ChatPanelSize.SettingChanged += (s, ea) => SetChatPanelSize(ChatPanelSize.Value);
        ChatMessageWidthOffset.SettingChanged += (s, ea) => SetChatMessageRowWidth(ChatMessageWidthOffset.Value);
        ChatWindowPositionOffset.SettingChanged += (s, ea) => SetChatWindowPositionOffset();

        __instance.m_maxVisibleBufferLength = 80;
        __instance.m_hideDelay = 600;
        __instance.m_chatWindow.SetAsFirstSibling();

        ToggleChatPanel(IsModEnabled.Value);
      }

      [HarmonyPrefix]
      [HarmonyPatch(nameof(Chat.OnNewChatMessage))]
      static void ChatPrefix(Chat __instance, long senderID, Vector3 pos, Talker.Type type, string user, string text) {
        if (!IsModEnabled.Value) {
          return;
        }

        ChatMessage message = new() {
            Timestamp = DateTime.Now, SenderId = senderID, Position = pos, Type = type, User = user, Text = text };

        MessageHistory.Add(message);

        if (type == Talker.Type.Ping) {
          // Ignore pings.
          return;
        }

        if (_lastMessage == null
            || _lastMessage.SenderId != message.SenderId
            || _lastMessage.Type != message.Type
            || !_lastMessageRow) {
          GameObject divider = _chatPanel.CreateMessageDivider(_chatPanel.Content.transform);
          MessageRows.EnqueueItem(divider);

          GameObject row = _chatPanel.CreateChatMessageRow(_chatPanel.Content.transform);
          _chatPanel.CreateChatMessageRowHeader(row.transform, message);

          MessageRows.EnqueueItem(row);

          _lastMessageRow = row;
          _lastMessage = message;
        }

        _chatPanel.CreateChatMessageRowBody(_lastMessageRow.transform, ChatPanel.GetMessageText(message));
      }
    }

    [HarmonyPatch(typeof(Terminal))]
    class TerminalPatch {
      [HarmonyPostfix]
      [HarmonyPatch(nameof(Terminal.SendInput))]
      static void SendInputPostfix(ref Terminal __instance) {
        if (IsModEnabled.Value && __instance == Chat.m_instance && _chatPanel?.ScrollRect) {
          _chatPanel.ScrollRect.verticalNormalizedPosition = 0f;
        }
      }

      static bool _addingChatMessageText = false;

      [HarmonyPrefix]
      [HarmonyPatch(nameof(Terminal.AddString), typeof(string), typeof(string), typeof(Talker.Type), typeof(bool))]
      static void AddStringPrefix(ref Terminal __instance) {
        if (IsModEnabled.Value) {
          _addingChatMessageText = true;
        }
      }

      [HarmonyPostfix]
      [HarmonyPatch(nameof(Terminal.AddString), typeof(string), typeof(string), typeof(Talker.Type), typeof(bool))]
      static void AddStringPostfix(ref Terminal __instance) {
        if (IsModEnabled.Value) {
          _addingChatMessageText = false;
        }
      }

      [HarmonyPostfix]
      [HarmonyPatch(nameof(Terminal.AddString), typeof(string))]
      static void AddStringFinalPostfix(ref Terminal __instance, ref string text) {
        if (_addingChatMessageText || !IsModEnabled.Value || __instance != Chat.m_instance || _chatPanel == null) {
          return;
        }

        if (_lastMessage != null || !_lastMessageRow) {
          GameObject divider = _chatPanel.CreateMessageDivider(_chatPanel.Content.transform);
          MessageRows.EnqueueItem(divider);

          _lastMessageRow = _chatPanel.CreateChatMessageRow(_chatPanel.Content.transform);
          MessageRows.EnqueueItem(_lastMessageRow);
        }

        _lastMessage = null;
        _chatPanel.CreateChatMessageRowBody(_lastMessageRow.transform, text);
      }
    }
  }

  public class CircularQueue<T> : ConcurrentQueue<T> {
    // readonly ConcurrentQueue<T> _queue = new();
    readonly Action<T> _dequeueFunc;
    readonly int _capacity;

    public CircularQueue(int capacity, Action<T> dequeueFunc) {
      _capacity = capacity;
      _dequeueFunc = dequeueFunc;
    }

    public void EnqueueItem(T item) {
      while (Count + 1 > _capacity) {
        if (!TryDequeue(out T itemToDequeue)) {
          throw new Exception("Unable to dequeue!");
        }

        _dequeueFunc(itemToDequeue);
      }

      Enqueue(item);
    }

    public T DequeueItem() {
      return TryDequeue(out T result) ? result : default;
    }
  }
}
using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Mirror;
using Steamworks;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace StonewardsSessionVisibility
{
    [BepInPlugin(Guid, Name, Version)]
    public sealed class SessionVisibilityPlugin : BaseUnityPlugin
    {
        public const string Guid = "dev.stonewards.sessionvisibility";
        public const string Name = "Stonewards Session Visibility";
        public const string Version = "0.1.0";

        private enum Mode { Private = 0, FriendsOnly = 1, Public = 2 }

        private Harmony _harmony;
        private ConfigEntry<bool> _enableHotkey;
        private ConfigEntry<Key> _toggleKey;
        private float _nextUIRefresh;
        private int _lastDisplayedMode = -999;
        private bool _lastDisplayedLocked;

        private static SessionVisibilityPlugin _plugin;
        private static Type _steamLobbyType;
        private static FieldInfo _steamLobbyInstanceField;
        private static FieldInfo _lobbyIdField;
        private static PropertyInfo _hostedModeProperty;
        private static MethodInfo _setHostedModeMethod;

        private static VisualElement _modePanel;
        private static Label _statusLabel;
        private static Label _noticeLabel;
        private static Button _privateButton;
        private static Button _friendsButton;
        private static Button _publicButton;

        private static bool Hosting => NetworkServer.active && NetworkClient.active;

        private void Awake()
        {
            _plugin = this;
            _enableHotkey = Config.Bind("Controls", "EnableHotkey", true,
                "Allow switching between Public and Private with a keyboard shortcut.");
            _toggleKey = Config.Bind("Controls", "ToggleKey", Key.F7,
                "Key to switch between Public and Private. F7 by default; F8/F9 are used by other mods.");

            _steamLobbyType = AccessTools.TypeByName("SteamLobby");
            if (_steamLobbyType != null)
            {
                _steamLobbyInstanceField = AccessTools.Field(_steamLobbyType, "Instance");
                _lobbyIdField = AccessTools.Field(_steamLobbyType, "currentLobbyID");
            }

            var networkManagerType = typeof(StoneWardsNetworkManager);
            _hostedModeProperty = AccessTools.Property(networkManagerType, "HostedLobbyVisibility");
            _setHostedModeMethod = AccessTools.Method(networkManagerType, "SetHostedLobbyVisibility");

            _harmony = new Harmony(Guid);
            var menuType = AccessTools.TypeByName("UISessionSettings");
            var menuAwake = menuType == null ? null : AccessTools.Method(menuType, "Awake");
            var postfix = AccessTools.Method(typeof(SessionVisibilityPlugin), nameof(OnSessionSettingsAwake));
            if (menuAwake != null && postfix != null)
                _harmony.Patch(menuAwake, postfix: new HarmonyMethod(postfix));
            else
                Logger.LogWarning("Could not patch UISessionSettings.Awake. The F7 hotkey will still work.");

            if (_steamLobbyType == null || _lobbyIdField == null || _hostedModeProperty == null || _setHostedModeMethod == null)
                Logger.LogWarning("Lobby metadata differs from the inspected build. Switching will fail safely.");

            Logger.LogInfo("Stonewards Session Visibility v" + Version + " initialized. Host-only; F7 toggles Public/Private.");
        }

        private void Update()
        {
            if (_enableHotkey.Value && Hosting &&
                _toggleKey.Value != Key.None && Keyboard.current != null &&
                Keyboard.current[_toggleKey.Value].wasPressedThisFrame)
            {
                Mode mode;
                if (TryReadMode(out mode))
                    TrySetVisibility(mode == Mode.Public ? Mode.Private : Mode.Public);
            }

            if (Time.unscaledTime < _nextUIRefresh)
                return;
            _nextUIRefresh = Time.unscaledTime + 0.35f;
            RefreshUI();
        }

        private static bool TryGetLobby(out CSteamID lobbyId)
        {
            lobbyId = default(CSteamID);
            if (!Hosting || _steamLobbyInstanceField == null || _lobbyIdField == null)
                return false;
            try
            {
                var lobby = _steamLobbyInstanceField.GetValue(null);
                if (lobby == null) return false;
                ulong id = (ulong)_lobbyIdField.GetValue(lobby);
                if (id == 0UL) return false;
                lobbyId = new CSteamID(id);
                // Mirror hosting alone is not enough. Only the Steam lobby owner may edit it.
                return SteamMatchmaking.GetLobbyOwner(lobbyId) == SteamUser.GetSteamID();
            }
            catch (Exception ex)
            {
                _plugin?.Logger.LogWarning("Cannot resolve current Steam lobby: " + ex.Message);
                return false;
            }
        }

        private static bool TryReadMode(out Mode mode)
        {
            mode = Mode.Private;
            if (!Hosting || _hostedModeProperty == null) return false;
            var manager = StoneWardsNetworkManager.Instance;
            if (manager == null) return false;
            try
            {
                int n = Convert.ToInt32(_hostedModeProperty.GetValue(manager, null));
                if (n < 0 || n > 2) return false;
                mode = (Mode)n;
                return true;
            }
            catch (Exception ex)
            {
                _plugin?.Logger.LogWarning("Cannot read lobby visibility: " + ex.Message);
                return false;
            }
        }

        private static bool IsGameLocked(CSteamID lobbyId)
        {
            // Stonewards writes Joinable=false in its own SteamLobby.LockLobby().
            // Never undo that gameplay lock or silently allow joins during a protected phase.
            return string.Equals(SteamMatchmaking.GetLobbyData(lobbyId, "Joinable"),
                "false", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TrySetVisibility(Mode requestedMode)
        {
            if (_plugin == null) return false;
            CSteamID lobbyId;
            if (!TryGetLobby(out lobbyId))
            {
                _plugin.Logger.LogWarning("Cannot change visibility: not the owner of an active Steam lobby.");
                return false;
            }

            if (_setHostedModeMethod == null || _hostedModeProperty == null)
            {
                _plugin.Logger.LogError("Game visibility state is unavailable. No change made.");
                return false;
            }

            ELobbyType steamMode;
            switch (requestedMode)
            {
                case Mode.Private: steamMode = ELobbyType.k_ELobbyTypePrivate; break;
                case Mode.FriendsOnly: steamMode = ELobbyType.k_ELobbyTypeFriendsOnly; break;
                case Mode.Public: steamMode = ELobbyType.k_ELobbyTypePublic; break;
                default: return false;
            }

            try
            {
                // Do not call SteamLobby.UnlockLobby: the game rejects it in some
                // network states. Only update discovery/visibility; preserve Joinable.
                bool steamAccepted = SteamMatchmaking.SetLobbyType(lobbyId, steamMode);
                if (!steamAccepted)
                {
                    _plugin.Logger.LogWarning("SteamMatchmaking.SetLobbyType returned false.");
                    return false;
                }

                var manager = StoneWardsNetworkManager.Instance;
                if (manager == null) throw new InvalidOperationException("Network manager missing");
                object gameEnumValue = Enum.ToObject(_hostedModeProperty.PropertyType, (int)requestedMode);
                _setHostedModeMethod.Invoke(manager, new[] { gameEnumValue });

                _plugin._lastDisplayedMode = -1;
                bool locked = IsGameLocked(lobbyId);
                _plugin.Logger.LogInfo("Lobby visibility set to " + requestedMode +
                    (locked ? "; Stonewards currently blocks new joins." : "; joining rules unchanged."));
                RefreshUI();
                return true;
            }
            catch (Exception ex)
            {
                _plugin.Logger.LogError("Failed changing lobby visibility: " + ex);
                return false;
            }
        }

        // Patched after the native menu has resolved its UI Toolkit elements.
        private static void OnSessionSettingsAwake(object __instance)
        {
            if (_plugin == null) return;
            try
            {
                var component = __instance as Component;
                var document = component == null ? null : component.GetComponent<UIDocument>();
                var root = document == null ? null : document.rootVisualElement;
                var screen = root == null ? null : root.Q<VisualElement>("SessionSettings");
                if (screen == null)
                    return;
                if (screen.Q<VisualElement>("StonewardsSessionVisibilityPanel") != null)
                    return;

                var panel = new VisualElement { name = "StonewardsSessionVisibilityPanel" };
                panel.style.flexDirection = FlexDirection.Column;
                panel.style.marginTop = 8;
                panel.style.marginBottom = 8;
                panel.style.marginLeft = 12;
                panel.style.marginRight = 12;
                panel.style.paddingTop = 9;
                panel.style.paddingBottom = 9;
                panel.style.paddingLeft = 12;
                panel.style.paddingRight = 12;
                panel.style.backgroundColor = new Color(0.075f, 0.084f, 0.098f, 0.92f);
                Color border = new Color(0.59f, 0.50f, 0.34f, 0.8f);
                panel.style.borderBottomWidth = 1;
                panel.style.borderTopWidth = 1;
                panel.style.borderLeftWidth = 1;
                panel.style.borderRightWidth = 1;
                panel.style.borderBottomColor = border;
                panel.style.borderTopColor = border;
                panel.style.borderLeftColor = border;
                panel.style.borderRightColor = border;

                _statusLabel = new Label("LOBBY VISIBILITY");
                _statusLabel.style.fontSize = 15;
                _statusLabel.style.color = new Color(0.91f, 0.84f, 0.66f);
                _statusLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
                _statusLabel.style.marginBottom = 6;
                panel.Add(_statusLabel);

                var buttons = new VisualElement();
                buttons.style.flexDirection = FlexDirection.Row;
                buttons.style.flexWrap = Wrap.Wrap;
                var returnButton = screen.Q<Button>("SessionReturnButton");
                _privateButton = MakeButton("PRIVATE", Mode.Private, returnButton);
                _friendsButton = MakeButton("FRIENDS", Mode.FriendsOnly, returnButton);
                _publicButton = MakeButton("PUBLIC", Mode.Public, returnButton);
                buttons.Add(_privateButton);
                buttons.Add(_friendsButton);
                buttons.Add(_publicButton);
                panel.Add(buttons);

                _noticeLabel = new Label("Host only  •  F7: Public / Private");
                _noticeLabel.style.fontSize = 11;
                _noticeLabel.style.marginTop = 5;
                _noticeLabel.style.color = new Color(0.72f, 0.72f, 0.69f);
                panel.Add(_noticeLabel);

                // Put controls in the native Session Settings view, directly after
                // its title section, rather than creating a permanently visible HUD.
                var title = screen.Q<Label>("SessionTitle");
                VisualElement titleHolder = title == null ? null : title.parent;
                VisualElement hostContainer = titleHolder == null ? null : titleHolder.parent;
                if (hostContainer != null)
                {
                    int index = hostContainer.IndexOf(titleHolder);
                    hostContainer.Insert(index + 1, panel);
                }
                else
                    screen.Add(panel);
                _modePanel = panel;
                _plugin._lastDisplayedMode = -999;
                RefreshUI();
                _plugin.Logger.LogInfo("Lobby visibility buttons added to Session Settings.");
            }
            catch (Exception ex)
            {
                _plugin.Logger.LogError("Failed adding lobby controls to the native menu. Hotkey remains available: " + ex);
            }
        }

        private static Button MakeButton(string text, Mode target, Button nativeButton)
        {
            var button = new Button(() => TrySetVisibility(target)) { text = text };
            // Reuse class-based native USS styling where possible.
            if (nativeButton != null)
                foreach (var className in nativeButton.GetClasses())
                    button.AddToClassList(className);
            button.style.flexGrow = 1;
            button.style.minWidth = 86;
            button.style.height = 32;
            button.style.marginRight = 4;
            button.style.marginBottom = 3;
            button.style.paddingLeft = 6;
            button.style.paddingRight = 6;
            button.style.fontSize = 12;
            button.style.unityFontStyleAndWeight = FontStyle.Bold;
            return button;
        }

        private static void UpdateButton(Button button, bool selected, bool usable)
        {
            if (button == null) return;
            button.SetEnabled(usable);
            button.style.backgroundColor = selected
                ? new Color(0.43f, 0.36f, 0.24f, 1f)
                : new Color(0.17f, 0.18f, 0.19f, 1f);
            button.style.color = usable
                ? (selected ? new Color(1f, 0.92f, 0.70f) : Color.white)
                : new Color(0.48f, 0.48f, 0.48f);
        }

        private static void RefreshUI()
        {
            if (_modePanel == null || _modePanel.panel == null) return;
            CSteamID lobbyId;
            Mode current = Mode.Private;
            bool usable = TryGetLobby(out lobbyId) && TryReadMode(out current);
            bool locked = false;
            if (usable)
            {
                try { locked = IsGameLocked(lobbyId); }
                catch (Exception) { /* Some Steam states do not expose lobby data yet. */ }
            }
            else current = Mode.Private;

            int modeNumber = usable ? (int)current : -1;
            if (_plugin != null && _plugin._lastDisplayedMode == modeNumber &&
                _plugin._lastDisplayedLocked == locked) return;
            if (_plugin != null)
            {
                _plugin._lastDisplayedMode = modeNumber;
                _plugin._lastDisplayedLocked = locked;
            }

            if (_statusLabel != null)
                _statusLabel.text = usable ? "LOBBY VISIBILITY  ·  " + current.ToString().ToUpperInvariant() : "LOBBY VISIBILITY";
            UpdateButton(_privateButton, usable && current == Mode.Private, usable);
            UpdateButton(_friendsButton, usable && current == Mode.FriendsOnly, usable);
            UpdateButton(_publicButton, usable && current == Mode.Public, usable);
            if (_noticeLabel != null)
                _noticeLabel.text = !usable
                    ? "Host of an active Steam lobby only"
                    : locked
                        ? "Game locked new joins. Visibility changes do not override that lock."
                        : "F7: Public / Private  ·  Existing players stay connected";
        }

        private void OnDestroy()
        {
            if (_harmony != null) _harmony.UnpatchSelf();
            if (ReferenceEquals(_plugin, this)) _plugin = null;
            _modePanel = null;
            _statusLabel = null;
            _noticeLabel = null;
            _privateButton = null;
            _friendsButton = null;
            _publicButton = null;
        }
    }
}

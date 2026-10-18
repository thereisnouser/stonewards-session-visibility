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

        private static VisualElement _sessionScreen;
        private static VisualElement _playerListContainer;
        private static Button _visibilityButton;

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
            var awakePostfix = AccessTools.Method(typeof(SessionVisibilityPlugin), nameof(OnSessionSettingsAwake));
            var menuShow = menuType == null ? null : AccessTools.Method(menuType, "BaseShow");
            var showPostfix = AccessTools.Method(typeof(SessionVisibilityPlugin), nameof(OnSessionSettingsShown));
            if (menuAwake != null && awakePostfix != null)
                _harmony.Patch(menuAwake, postfix: new HarmonyMethod(awakePostfix));
            else
                Logger.LogWarning("Could not patch UISessionSettings.Awake; trying BaseShow instead.");
            if (menuShow != null && showPostfix != null)
                _harmony.Patch(menuShow, postfix: new HarmonyMethod(showPostfix));
            else
                Logger.LogWarning("Could not patch UISessionSettings.BaseShow; menu integration may be unavailable.");

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

        // The Awake hook mounts early, while BaseShow retries once Session opens.
        // This avoids depending on the title's hierarchy or a single UI event.
        private static void OnSessionSettingsAwake(object __instance)
        {
            MountVisibilityButton(__instance);
        }

        private static void OnSessionSettingsShown(object __instance, bool show)
        {
            if (!show) return;
            MountVisibilityButton(__instance);
            RefreshUI();
        }

        private static void MountVisibilityButton(object menu)
        {
            if (_plugin == null) return;
            try
            {
                var component = menu as Component;
                var document = component == null ? null : component.GetComponent<UIDocument>();
                var root = document == null ? null : document.rootVisualElement;
                var screen = root == null ? null : root.Q<VisualElement>("SessionSettings");
                if (screen == null) return;

                // UISessionSettings.Awake also locates this exact container,
                // then UISessionSettings.Init appends the player rows to it.
                // Inserting at index zero keeps the toggle immediately below
                // the native session title, above the entire player roster.
                var rows = screen.Q<VisualElement>("unity-content-container");
                var nativeReturn = screen.Q<Button>("SessionReturnButton");
                if (rows == null || nativeReturn == null)
                {
                    _plugin.Logger.LogWarning(
                        "Session player rows or Return button not found; F7 remains available.");
                    return;
                }

                var button = rows.Q<Button>("StonewardsSessionVisibilityToggle");
                if (button == null)
                {
                    button = new Button(ToggleFromMenu)
                    {
                        name = "StonewardsSessionVisibilityToggle",
                        text = "VISIBILITY: PUBLIC"
                    };
                    foreach (var className in nativeReturn.GetClasses())
                        button.AddToClassList(className);

                    // Flow layout: do not cover any player entries or Return.
                    button.style.position = Position.Relative;
                    button.style.flexGrow = 0;
                    button.style.flexShrink = 0;
                    button.style.alignSelf = Align.Stretch;
                    button.style.height = 38;
                    button.style.marginTop = 5;
                    button.style.marginBottom = 9;
                    button.style.marginLeft = 10;
                    button.style.marginRight = 10;
                    button.style.paddingLeft = 10;
                    button.style.paddingRight = 10;
                    button.style.fontSize = 14;
                    button.style.unityFontStyleAndWeight = FontStyle.Bold;
                }

                // Reattach safely if the game has rebuilt/reordered its roster.
                if (!ReferenceEquals(button.parent, rows) ||
                    rows.IndexOf(button) != 0)
                    rows.Insert(0, button);

                _sessionScreen = screen;
                _playerListContainer = rows;
                _visibilityButton = button;
                _plugin._lastDisplayedMode = -999;
                RefreshUI();
                _plugin.Logger.LogInfo(
                    "Session visibility toggle inserted below Session title, above player rows.");
            }
            catch (Exception ex)
            {
                _plugin.Logger.LogError("Could not mount Session visibility toggle: " + ex);
            }
        }

        private static void ToggleFromMenu()
        {
            Mode mode;
            if (TryReadMode(out mode))
                TrySetVisibility(mode == Mode.Public ? Mode.Private : Mode.Public);
        }

        private static void RefreshUI()
        {
            if (_visibilityButton == null || _visibilityButton.panel == null) return;
            CSteamID lobbyId;
            Mode current = Mode.Private;
            bool usable = TryGetLobby(out lobbyId) && TryReadMode(out current);
            // Host only: do not alter the Session menu on guests' clients.
            _visibilityButton.style.display = usable ? DisplayStyle.Flex : DisplayStyle.None;
            if (!usable) return;

            bool locked = false;
            try { locked = IsGameLocked(lobbyId); }
            catch (Exception) { /* Some Steam states do not expose lobby data yet. */ }

            int modeNumber = (int)current;
            if (_plugin != null && _plugin._lastDisplayedMode == modeNumber &&
                _plugin._lastDisplayedLocked == locked) return;
            if (_plugin != null)
            {
                _plugin._lastDisplayedMode = modeNumber;
                _plugin._lastDisplayedLocked = locked;
            }

            // Text shows the current mode; clicking toggles Public/Private.
            // Friends-only rooms become Public on the first click.
            _visibilityButton.text = current == Mode.FriendsOnly
                ? "VISIBILITY: FRIENDS"
                : "VISIBILITY: " + current.ToString().ToUpperInvariant();
            _visibilityButton.tooltip = locked
                ? "The game's join lock remains active after changing visibility."
                : "Click to switch Public / Private. F7 also toggles visibility.";
        }

        private void OnDestroy()
        {
            if (_harmony != null) _harmony.UnpatchSelf();
            if (ReferenceEquals(_plugin, this)) _plugin = null;
            _sessionScreen = null;
            _playerListContainer = null;
            _visibilityButton = null;
        }
    }
}

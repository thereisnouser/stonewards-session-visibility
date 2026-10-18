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

        // Stonewards offers Friend and Public. Private (0) is an internal lobby lock.
        private enum Mode { Friend = 1, Public = 2 }

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
        private static VisualElement _sessionHeader;
        private static Button _visibilityButton;
        private static bool _buttonHovered;

        private static bool Hosting => NetworkServer.active && NetworkClient.active;

        private void Awake()
        {
            _plugin = this;
            _enableHotkey = Config.Bind("Controls", "EnableHotkey", true,
                "Allow switching between Friend and Public with a keyboard shortcut.");
            _toggleKey = Config.Bind("Controls", "ToggleKey", Key.F7,
                "Key to switch between Friend and Public. F7 by default; F8/F9 are used by other mods.");

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

            Logger.LogInfo("Stonewards Session Visibility v" + Version + " initialized. Host-only; F7 toggles Friend/Public.");
        }

        private void Update()
        {
            if (_enableHotkey.Value && Hosting &&
                _toggleKey.Value != Key.None && Keyboard.current != null &&
                Keyboard.current[_toggleKey.Value].wasPressedThisFrame)
            {
                Mode mode;
                if (TryReadMode(out mode))
                    TrySetVisibility(mode == Mode.Public ? Mode.Friend : Mode.Public);
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
            mode = Mode.Friend;
            if (!Hosting || _hostedModeProperty == null) return false;
            var manager = StoneWardsNetworkManager.Instance;
            if (manager == null) return false;
            try
            {
                int n = Convert.ToInt32(_hostedModeProperty.GetValue(manager, null));
                if (n != 1 && n != 2) return false;
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
                case Mode.Friend: steamMode = ELobbyType.k_ELobbyTypeFriendsOnly; break;
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

        // Prefer the actual colored Session title bar. In the game's UXML
        // the SessionTitle label is nested in it. Walking through ancestors
        // avoids assuming that the title bar is exactly one parent deep.
        private static VisualElement FindSessionHeader(Label title, VisualElement screen)
        {
            VisualElement fallback = title.parent;
            for (var current = title.parent; current != null && current != screen;
                 current = current.parent)
            {
                Color background = current.resolvedStyle.backgroundColor;
                // The native Session heading has a distinctive burgundy fill.
                if (background.a > 0.45f &&
                    background.r > background.g * 1.12f &&
                    background.r > background.b * 1.07f)
                    return current;
            }
            return fallback;
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
                var title = screen == null ? null : screen.Q<Label>("SessionTitle");
                var nativeReturn = screen == null ? null : screen.Q<Button>("SessionReturnButton");
                if (screen == null || title == null || nativeReturn == null)
                {
                    _plugin.Logger.LogWarning(
                        "Native Session title or Return button not found; F7 remains available.");
                    return;
                }

                var header = FindSessionHeader(title, screen);
                if (header == null || header == screen) return;

                // Extend the game's existing burgundy heading rather than
                // creating a new panel or taking a slot in the player roster.
                header.style.height = StyleKeyword.Auto;
                header.style.minHeight = 118;
                header.style.flexDirection = FlexDirection.Column;
                header.style.alignItems = Align.Center;
                header.style.justifyContent = Justify.Center;
                header.style.flexShrink = 0;
                header.style.paddingTop = 8;
                header.style.paddingBottom = 10;

                // Reuse the native Return button USS classes for its in-game
                // font, colors, border and hover appearance.
                var button = screen.Q<Button>("StonewardsSessionVisibilityToggle");
                if (button == null)
                {
                    button = new Button(ToggleFromMenu)
                    {
                        name = "StonewardsSessionVisibilityToggle",
                        text = "VISIBILITY: PUBLIC"
                    };
                    foreach (var className in nativeReturn.GetClasses())
                        button.AddToClassList(className);
                    button.RegisterCallback<PointerEnterEvent>(_ =>
                    {
                        _buttonHovered = true;
                        ApplyGameButtonStyle();
                    });
                    button.RegisterCallback<PointerLeaveEvent>(_ =>
                    {
                        _buttonHovered = false;
                        ApplyGameButtonStyle();
                    });
                    button.RegisterCallback<FocusInEvent>(_ =>
                    {
                        _buttonHovered = true;
                        ApplyGameButtonStyle();
                    });
                    button.RegisterCallback<FocusOutEvent>(_ =>
                    {
                        _buttonHovered = false;
                        ApplyGameButtonStyle();
                    });
                }

                // UXML and GenericButton provide extra styling to native
                // buttons: copying only USS classes left this button white.
                var gameFont = nativeReturn.resolvedStyle.unityFont;
                if (gameFont != null)
                    button.style.unityFont = gameFont;

                button.style.position = Position.Relative;
                button.style.alignSelf = Align.Center;
                button.style.flexGrow = 0;
                button.style.flexShrink = 0;
                button.style.width = new Length(55, LengthUnit.Percent);
                button.style.minWidth = 220;
                button.style.maxWidth = 350;
                button.style.height = 39;
                button.style.marginTop = 7;
                button.style.marginBottom = 0;
                button.style.marginLeft = 0;
                button.style.marginRight = 0;
                button.style.paddingLeft = 8;
                button.style.paddingRight = 8;
                button.style.fontSize = 17;
                button.style.unityFontStyleAndWeight = FontStyle.Bold;
                button.style.unityTextAlign = TextAnchor.MiddleCenter;
                button.style.borderLeftWidth = 2;
                button.style.borderRightWidth = 2;
                button.style.borderTopWidth = 2;
                button.style.borderBottomWidth = 2;
                button.style.borderTopLeftRadius = 0;
                button.style.borderTopRightRadius = 0;
                button.style.borderBottomLeftRadius = 0;
                button.style.borderBottomRightRadius = 0;
                ApplyGameButtonStyle(button);

                // Insert immediately after the branch containing the title.
                // Native player rows are untouched and remain below the header.
                VisualElement titleBranch = title;
                while (titleBranch.parent != null && titleBranch.parent != header)
                    titleBranch = titleBranch.parent;
                int targetIndex = header.IndexOf(titleBranch) + 1;
                if (button.parent != header || header.IndexOf(button) != targetIndex)
                    header.Insert(targetIndex, button);

                _sessionScreen = screen;
                _sessionHeader = header;
                _visibilityButton = button;
                _buttonHovered = false;
                ApplyGameButtonStyle();
                _plugin._lastDisplayedMode = -999;
                RefreshUI();
                _plugin.Logger.LogInfo("Visibility toggle embedded in expanded Session title header.");
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
                TrySetVisibility(mode == Mode.Public ? Mode.Friend : Mode.Public);
        }

        private static void ApplyGameButtonStyle(Button button = null)
        {
            if (button == null) button = _visibilityButton;
            if (button == null) return;

            // Stonewards dark-blue buttons use light text and gold borders.
            // Inline styles override the white UI Toolkit default and USS hover.
            Color fill = _buttonHovered
                ? new Color(0.27f, 0.33f, 0.40f, 1f)
                : new Color(0.20f, 0.25f, 0.31f, 1f);
            Color gold = _buttonHovered
                ? new Color(0.97f, 0.85f, 0.60f, 1f)
                : new Color(0.75f, 0.64f, 0.45f, 1f);

            // Prevent the game's USS from supplying a white button image.
            button.style.backgroundImage = StyleKeyword.None;
            button.style.backgroundColor = fill;
            button.style.borderLeftColor = gold;
            button.style.borderRightColor = gold;
            button.style.borderTopColor = gold;
            button.style.borderBottomColor = gold;
            button.style.color = _buttonHovered
                ? new Color(1f, 0.97f, 0.83f, 1f)
                : new Color(0.96f, 0.94f, 0.88f, 1f);
        }

        private static void RefreshUI()
        {
            if (_visibilityButton == null || _visibilityButton.panel == null) return;
            CSteamID lobbyId;
            Mode current = Mode.Friend;
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

            _visibilityButton.text = current == Mode.Public
                ? "VISIBILITY: PUBLIC"
                : "VISIBILITY: FRIEND";
            ApplyGameButtonStyle();
            _visibilityButton.tooltip = locked
                ? "The game's join lock remains active after changing visibility."
                : "Click to switch Friend / Public. F7 also toggles visibility.";
        }

        private void OnDestroy()
        {
            if (_harmony != null) _harmony.UnpatchSelf();
            if (ReferenceEquals(_plugin, this)) _plugin = null;
            _sessionScreen = null;
            _sessionHeader = null;
            _visibilityButton = null;
            _buttonHovered = false;
        }
    }
}

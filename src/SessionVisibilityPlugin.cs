using System;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using Mirror;
using Steamworks;
using UnityEngine;
using UnityEngine.UIElements;

namespace StonewardsSessionVisibility
{
    [BepInPlugin(Guid, Name, Version)]
    public sealed class SessionVisibilityPlugin : BaseUnityPlugin
    {
        public const string Guid = "dev.stonewards.sessionvisibility";
        public const string Name = "Stonewards Session Visibility";
        public const string Version = "0.1.0";

        // Stonewards offers Friends and Public. Private (0) is an internal lobby lock.
        private enum Mode { Friends = 1, Public = 2 }

        private Harmony _harmony;
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
        private static Texture2D _normalButtonTexture;
        private static Texture2D _hoverButtonTexture;
        private static bool _sessionLayoutExpanded;

        // The original Session viewport fits four rows before we enlarge its
        // title bar. Give both the roster viewport and popup room to grow.
        private const float ExtraSessionHeight = 82f;
        private const int CornerTextureSize = 32;
        private const int CornerSlice = 12;

        private static bool Hosting => NetworkServer.active && NetworkClient.active;

        private void Awake()
        {
            _plugin = this;
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

            Logger.LogInfo("Stonewards Session Visibility v" + Version + " initialized. Host-only; use Esc > Session.");
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextUIRefresh)
                return;
            _nextUIRefresh = Time.unscaledTime + 0.35f;
            ExpandSessionLayout();
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
            mode = Mode.Friends;
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
                case Mode.Friends: steamMode = ELobbyType.k_ELobbyTypeFriendsOnly; break;
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
                        "Native Session title or Return button not found; visibility control unavailable.");
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
                // The border is drawn in a pixel-perfect nine-sliced texture.
                // UI Toolkit's ordinary border radius would smooth the corners.
                button.style.borderLeftWidth = 0;
                button.style.borderRightWidth = 0;
                button.style.borderTopWidth = 0;
                button.style.borderBottomWidth = 0;
                button.style.unitySliceLeft = CornerSlice;
                button.style.unitySliceRight = CornerSlice;
                button.style.unitySliceTop = CornerSlice;
                button.style.unitySliceBottom = CornerSlice;
                button.style.unityBackgroundImageTintColor = Color.white;
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

                if (!ReferenceEquals(_sessionScreen, screen))
                    _sessionLayoutExpanded = false;

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
                TrySetVisibility(mode == Mode.Public ? Mode.Friends : Mode.Public);
        }

        // Build a 32x32 raster border with genuine staircase corners.
        // A 9-slice preserves the 12-pixel corner regions on every resolution.
        private static int StairInset(int distanceFromEdge)
        {
            if (distanceFromEdge < 2) return 8;
            if (distanceFromEdge < 4) return 6;
            if (distanceFromEdge < 6) return 4;
            if (distanceFromEdge < 8) return 2;
            return 0;
        }

        private static bool InsideStaircase(int x, int y)
        {
            if (x < 0 || y < 0 ||
                x >= CornerTextureSize || y >= CornerTextureSize)
                return false;

            int edgeX = Mathf.Min(x, CornerTextureSize - 1 - x);
            int edgeY = Mathf.Min(y, CornerTextureSize - 1 - y);
            return edgeX >= StairInset(edgeY) &&
                   edgeY >= StairInset(edgeX);
        }

        private static Texture2D CreateButtonTexture(bool hovered)
        {
            var texture = new Texture2D(
                CornerTextureSize, CornerTextureSize, TextureFormat.RGBA32, false);
            texture.name = hovered
                ? "StonewardsSessionVisibility_HoverFrame"
                : "StonewardsSessionVisibility_NormalFrame";
            texture.filterMode = FilterMode.Point;
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.hideFlags = HideFlags.HideAndDontSave;

            Color fill = hovered
                ? new Color(0.27f, 0.33f, 0.40f, 1f)
                : new Color(0.20f, 0.25f, 0.31f, 1f);
            Color gold = hovered
                ? new Color(0.97f, 0.85f, 0.60f, 1f)
                : new Color(0.75f, 0.64f, 0.45f, 1f);

            var pixels = new Color[CornerTextureSize * CornerTextureSize];
            for (int y = 0; y < CornerTextureSize; ++y)
            {
                for (int x = 0; x < CornerTextureSize; ++x)
                {
                    if (!InsideStaircase(x, y))
                    {
                        pixels[y * CornerTextureSize + x] = Color.clear;
                        continue;
                    }

                    // Two crisp pixels form the outer gold line, including
                    // each step of the corner. The center uses a solid fill.
                    bool interior =
                        InsideStaircase(x - 2, y) &&
                        InsideStaircase(x + 2, y) &&
                        InsideStaircase(x, y - 2) &&
                        InsideStaircase(x, y + 2);
                    pixels[y * CornerTextureSize + x] =
                        interior ? fill : gold;
                }
            }
            texture.SetPixels(pixels);
            texture.Apply(false, true);
            return texture;
        }

        private static void ApplyGameButtonStyle(Button button = null)
        {
            if (button == null) button = _visibilityButton;
            if (button == null) return;

            if (_normalButtonTexture == null)
                _normalButtonTexture = CreateButtonTexture(false);
            if (_hoverButtonTexture == null)
                _hoverButtonTexture = CreateButtonTexture(true);

            button.style.backgroundColor = Color.clear;
            button.style.backgroundImage = new StyleBackground(
                _buttonHovered ? _hoverButtonTexture : _normalButtonTexture);
            button.style.color = _buttonHovered
                ? new Color(1f, 0.97f, 0.83f, 1f)
                : new Color(0.96f, 0.94f, 0.88f, 1f);
        }

        private static void ExpandSessionLayout()
        {
            if (_sessionLayoutExpanded || _sessionScreen == null ||
                _sessionScreen.panel == null || !Hosting)
                return;

            // The rows are added to a ScrollView's unity-content-container.
            // Increasing only the popup would still clip the fourth player.
            var rowContainer = _sessionScreen.Q<VisualElement>(
                "unity-content-container");
            ScrollView scroll = null;
            for (VisualElement element = rowContainer; element != null;
                 element = element.parent)
            {
                scroll = element as ScrollView;
                if (scroll != null) break;
            }

            float popupHeight = _sessionScreen.resolvedStyle.height;
            float viewportHeight = scroll == null
                ? 0f : scroll.resolvedStyle.height;
            if (popupHeight < 200f || viewportHeight < 100f)
                return; // Wait until UI Toolkit has laid out the popup.

            _sessionScreen.style.height = popupHeight + ExtraSessionHeight;
            _sessionScreen.style.minHeight = popupHeight + ExtraSessionHeight;
            scroll.style.height = viewportHeight + ExtraSessionHeight;
            scroll.style.minHeight = viewportHeight + ExtraSessionHeight;
            _sessionLayoutExpanded = true;
            _plugin?.Logger.LogInfo(
                "Expanded Session popup and player viewport by " +
                ExtraSessionHeight + " UI units.");
        }

        private static void RefreshUI()
        {
            if (_visibilityButton == null || _visibilityButton.panel == null) return;
            CSteamID lobbyId;
            Mode current = Mode.Friends;
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
                : "VISIBILITY: FRIENDS";
            ApplyGameButtonStyle();
            _visibilityButton.tooltip = locked
                ? "The game's join lock remains active after changing visibility."
                : "Click to switch Friends / Public.";
        }

        private void OnDestroy()
        {
            if (_harmony != null) _harmony.UnpatchSelf();
            if (ReferenceEquals(_plugin, this)) _plugin = null;
            _sessionScreen = null;
            _sessionHeader = null;
            _visibilityButton = null;
            _buttonHovered = false;
            _sessionLayoutExpanded = false;

            if (_normalButtonTexture != null)
                Destroy(_normalButtonTexture);
            if (_hoverButtonTexture != null)
                Destroy(_hoverButtonTexture);
            _normalButtonTexture = null;
            _hoverButtonTexture = null;
        }
    }
}

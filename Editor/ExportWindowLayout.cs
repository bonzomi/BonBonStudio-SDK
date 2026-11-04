using UnityEditor;
using UnityEngine;

namespace BonV.Editor
{
    /// <summary>
    /// The layout the scene and character export windows share: Local Export and Publish tabs, then a panel with the bundle's
    /// fields on the left and its thumbnail on the right.
    /// </summary>
    public static class ExportWindowLayout
    {
        private static readonly string[] TabLabels = { "Local Export", "Publish" };

        private static GUIStyle _tabStyle;
        private static GUIStyle TabStyle => _tabStyle ??= new GUIStyle(EditorStyles.toolbarButton)
        {
            fontSize = 13,
            fontStyle = FontStyle.Bold,
            fixedHeight = 28,
        };

        private static GUIStyle _panelStyle;
        /// <summary>The box around the fields and thumbnail.</summary>
        public static GUIStyle Panel => _panelStyle ??= new GUIStyle(EditorStyles.helpBox) { padding = new RectOffset(10, 10, 10, 10) };

        /// <summary>
        /// Draws the Local Export and Publish tabs and returns whether Publish is picked. Without a publisher there are no tabs
        /// and it's always Local Export. The pick is kept under prefsKey.
        /// </summary>
        public static bool PublishTab(string prefsKey, IBundleExportPublisher publisher)
        {
            if (publisher == null) return false;
            int tab = EditorPrefs.GetInt(prefsKey, 0);
            int picked = GUILayout.Toolbar(tab, TabLabels, TabStyle);
            if (picked != tab) EditorPrefs.SetInt(prefsKey, picked);
            return picked == 1;
        }

        /// <summary>
        /// The export windows' width: enough for the field column to fit the ID row (a UUID and New ID) beside a ~230 px
        /// thumbnail. Wider only adds empty field width.
        /// </summary>
        public const float WindowWidth = 600;
        private const float MinWindowHeight = 420;

        /// <summary>Keeps the window at <see cref="WindowWidth"/> while it floats; only its height can change. Docked windows take the dock's size.</summary>
        public static void FixWidth(EditorWindow window)
        {
            window.minSize = new Vector2(WindowWidth, MinWindowHeight);
            window.maxSize = new Vector2(WindowWidth, 4000);
        }

        /// <summary>The thumbnail's side for a window this wide, leaving the fields most of the room.</summary>
        public static float ImageSize(float windowWidth) => Mathf.Clamp((windowWidth - 50) * 0.42f, 140, 300);

        private static GUIStyle _placeholderStyle;
        private static GUIStyle PlaceholderStyle => _placeholderStyle ??= new GUIStyle(EditorStyles.label)
        {
            normal = { textColor = new Color(0.5f, 0.5f, 0.5f) },
            padding = new RectOffset(3, 3, 1, 0),
            alignment = TextAnchor.UpperLeft,
            clipping = TextClipping.Clip,
        };

        /// <summary>Grey text over the field just drawn while it's empty.</summary>
        public static void Placeholder(string value, string placeholder)
        {
            if (string.IsNullOrEmpty(value) && Event.current.type == EventType.Repaint)
                PlaceholderStyle.Draw(GUILayoutUtility.GetLastRect(), placeholder, false, false, false, false);
        }

        /// <summary>
        /// The ID, stacked like the panel's other fields so it gets the column's full width, with New ID once there is one.
        /// Returns whether New ID was clicked.
        /// </summary>
        public static bool IdField(string id, string tooltip)
        {
            GUILayout.Label(new GUIContent("ID", tooltip), EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (id.Length > 0)
                    EditorGUILayout.SelectableLabel(id, EditorStyles.miniLabel, GUILayout.Height(EditorGUIUtility.singleLineHeight), GUILayout.MinWidth(0));
                else
                    GUILayout.Label("Made on the first export", EditorStyles.miniLabel);
                return id.Length > 0 && GUILayout.Button("New ID", EditorStyles.miniButton, GUILayout.Width(52));
            }
        }
    }
}

using BonStudio.SDK.Characters.Runtime;
using UnityEditor;
using UnityEngine;

[CustomPropertyDrawer(typeof(Expression))]
public class ExpressionDrawer : PropertyDrawer
{
    private const float SideWidth = 0.3f;
    private static float Line => EditorGUIUtility.singleLineHeight;
    private static float Spacing => EditorGUIUtility.standardVerticalSpacing;

    public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
    {
        return Layout(default, property, label, false);
    }

    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        EditorGUI.BeginProperty(position, label, property);
        Layout(position, property, label, true);
        EditorGUI.EndProperty();
    }

    // Shared by height calculation and drawing so the two can never disagree
    private float Layout(Rect position, SerializedProperty property, GUIContent label, bool draw)
    {
        float y = position.y;

        Rect NextRow(float height)
        {
            var r = new Rect(position.x, y, position.width, height);
            y += height + Spacing;
            return r;
        }

        void Field(string name, string displayName = null)
        {
            var p = property.FindPropertyRelative(name);
            var content = new GUIContent(displayName ?? p.displayName);
            var r = NextRow(EditorGUI.GetPropertyHeight(p, content, true));
            if (draw) EditorGUI.PropertyField(r, p, content, true);
        }

        // Two fields on one row, the right one taking SideWidth of the space with no label
        void SplitRow(string left, string leftLabel, string right, string rightLabel = null)
        {
            var r = NextRow(Line);
            if (!draw) return;
            float rightWidth = r.width * SideWidth;
            var leftRect = new Rect(r.x, r.y, r.width - rightWidth - 4f, r.height);
            var rightRect = new Rect(leftRect.xMax + 4f, r.y, rightWidth, r.height);
            EditorGUI.PropertyField(leftRect, property.FindPropertyRelative(left), new GUIContent(leftLabel));

            using (new LabelWidthScope(rightLabel == null ? 0f : 40f))
                EditorGUI.PropertyField(rightRect, property.FindPropertyRelative(right),
                    rightLabel == null ? GUIContent.none : new GUIContent(rightLabel));
        }

        var nameProp = property.FindPropertyRelative(nameof(Expression.expressionName));
        var foldoutLabel = string.IsNullOrEmpty(nameProp.stringValue) ? label.text : nameProp.stringValue;
        var foldoutRect = NextRow(Line);
        if (draw) property.isExpanded = EditorGUI.Foldout(foldoutRect, property.isExpanded, foldoutLabel, true);
        if (!property.isExpanded) return y - position.y - Spacing;

        using (new EditorGUI.IndentLevelScope())
        {
            SplitRow(nameof(Expression.expressionName), "Name", nameof(Expression.icon));
            Field(nameof(Expression.parameterType));

            var paramType = (Expression.ParamType)property.FindPropertyRelative(nameof(Expression.parameterType)).enumValueIndex;
            switch (paramType)
            {
                case Expression.ParamType.Bool:
                    SplitRow(nameof(Expression.parameter), "Parameter", nameof(Expression.boolValue), "Value"); break;
                case Expression.ParamType.Int:
                    SplitRow(nameof(Expression.parameter), "Parameter", nameof(Expression.intValue), "Value"); break;
                case Expression.ParamType.Float:
                    SplitRow(nameof(Expression.parameter), "Parameter", nameof(Expression.floatValue), "Value"); break;
                default:
                    Field(nameof(Expression.parameter)); break;
            }

            Field(nameof(Expression.uiType), "UI Type");

            var uiType = (Expression.UiType)property.FindPropertyRelative(nameof(Expression.uiType)).enumValueIndex;
            switch (uiType)
            {
                case Expression.UiType.SubMenu:
                    Field(nameof(Expression.subMenu));
                    break;
                case Expression.UiType.FourAxisPuppet:
                    Field(nameof(Expression.upParam));
                    Field(nameof(Expression.downParam));
                    Field(nameof(Expression.leftParam));
                    Field(nameof(Expression.rightParam));
                    break;
                case Expression.UiType.TwoAxisPuppet:
                    Field(nameof(Expression.horizontalParam), "Horizontal");
                    Field(nameof(Expression.verticalParam), "Vertical");
                    break;
                case Expression.UiType.RadialPuppet:
                    Field(nameof(Expression.radialParam));
                    break;
            }

            if (uiType == Expression.UiType.TwoAxisPuppet || uiType == Expression.UiType.FourAxisPuppet)
            {
                y += Line * 0.5f;
                SplitRow(nameof(Expression.upLabel), "Up Label", nameof(Expression.upLabelIcon));
                SplitRow(nameof(Expression.downLabel), "Down Label", nameof(Expression.downLabelIcon));
                SplitRow(nameof(Expression.leftLabel), "Left Label", nameof(Expression.leftLabelIcon));
                SplitRow(nameof(Expression.rightLabel), "Right Label", nameof(Expression.rightLabelIcon));
            }
        }

        return y - position.y - Spacing;
    }

    private readonly struct LabelWidthScope : System.IDisposable
    {
        private readonly float previous;
        private readonly int previousIndent;

        public LabelWidthScope(float width)
        {
            previous = EditorGUIUtility.labelWidth;
            previousIndent = EditorGUI.indentLevel;
            EditorGUIUtility.labelWidth = width;
            EditorGUI.indentLevel = 0;
        }

        public void Dispose()
        {
            EditorGUIUtility.labelWidth = previous;
            EditorGUI.indentLevel = previousIndent;
        }
    }
}

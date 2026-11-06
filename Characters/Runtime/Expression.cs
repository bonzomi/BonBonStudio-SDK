using System;
using UnityEngine;

namespace BonBonStudio.SDK.Characters.Runtime
{
// Inspector layout is handled by ExpressionDrawer in BonBonStudio.SDK.Characters.Editor
[Serializable]
public class Expression
{
    public string expressionName;
    public Texture2D icon;
    public ParamType parameterType;
    public string parameter;
    public bool boolValue;
    public int intValue;
    public float floatValue;

    public UiType uiType;
    public ExpressionMenu subMenu;

    //Four Axis Puppet Fields
    public string upParam;
    public string downParam;
    public string leftParam;
    public string rightParam;

    //Two Axis Puppet Fields
    public string horizontalParam;
    public string verticalParam;

    //Radial Puppet Fields
    public string radialParam;

    //Shared Axis Puppet Fields
    public string upLabel;
    public Texture2D upLabelIcon;
    public string downLabel;
    public Texture2D downLabelIcon;
    public string leftLabel;
    public Texture2D leftLabelIcon;
    public string rightLabel;
    public Texture2D rightLabelIcon;

    public enum UiType { Button, Toggle, SubMenu, TwoAxisPuppet, FourAxisPuppet,  RadialPuppet}
    public enum ParamType {  Float, Int, Bool, Trigger }
}
}

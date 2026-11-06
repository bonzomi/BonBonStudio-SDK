using System.Collections.Generic;
using UnityEngine;

namespace BonBonStudio.SDK.Characters.Runtime
{
    [CreateAssetMenu(fileName = "DefaultExpressions", menuName = "BonBonStudio/ExpressionMenu", order = 1)]
    public class ExpressionMenu : ScriptableObject
    {
        public List<Expression> expressions;
    }
}
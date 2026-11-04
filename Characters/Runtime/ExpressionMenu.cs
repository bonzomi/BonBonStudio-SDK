using System.Collections.Generic;
using UnityEngine;

namespace BonStudio.SDK.Characters.Runtime
{
    [CreateAssetMenu(fileName = "DefaultExpressions", menuName = "BonStudio/ExpressionMenu", order = 1)]
    public class ExpressionMenu : ScriptableObject
    {
        public List<Expression> expressions;
    }
}
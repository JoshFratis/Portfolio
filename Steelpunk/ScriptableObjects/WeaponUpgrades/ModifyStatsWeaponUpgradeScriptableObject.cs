/* The entirety of this script was written by Joshua Fratis */

using Loot;
using UnityEngine;
using WebApp;

namespace ScriptableObjects
{
    [CreateAssetMenu(menuName =
        "ScriptableObjects/Item/WeaponUpgrade/ModifyStats")]
    public class
        ModifyStatsWeaponUpgradeScriptableObject :
            WeaponUpgradeScriptableObject
    {
        [SerializeField] private Inventory.WeaponItemInstance.Stats _statModifiers;

        public override Inventory.WeaponItemInstance.Stats GetStatModifiers()
        { 
            return _statModifiers; 
        } 
    }
}
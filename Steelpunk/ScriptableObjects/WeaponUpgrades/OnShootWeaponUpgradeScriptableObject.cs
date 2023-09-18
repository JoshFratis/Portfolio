/* The entirety of this script was written by Joshua Fratis */

using Loot;
using UnityEngine;
using Utility;

namespace ScriptableObjects
{
    [CreateAssetMenu(menuName =
        "ScriptableObjects/Item/WeaponUpgrade/OnShoot")]
    public class
        OnShootWeaponUpgradeScriptableObject :
            WeaponUpgradeScriptableObject
    {
        [SerializeField] private string _message;

        public override void OnShoot()
        {
            Debug.Log(_message);
        }
    }
}
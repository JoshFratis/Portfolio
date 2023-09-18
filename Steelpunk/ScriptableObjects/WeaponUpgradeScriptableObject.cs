/* The entirety of this script was written by Joshua Fratis */

using Game;
using Loot;
using UnityEditor;
using UnityEngine;
using Weapons;
using WebApp;

namespace ScriptableObjects
{
    public class WeaponUpgradeScriptableObject : ItemScriptableObject
    {
        private bool debugging = false;
        
        // Item
        public override ItemType GetItemType()
        {
            return ItemType.WeaponUpgrade;
        }
        
        public virtual Inventory.WeaponItemInstance.Stats GetStatModifiers()
        {
            return new Inventory.WeaponItemInstance.Stats();
        }

        public virtual DamagePayload ModifyDamage(DamagePayload payload)
        {
            return new DamagePayload();
        }

        // Weapon Use
        public virtual void OnEquip()
        {
            if (debugging) Debug.Log("WUSO "+description.name+" Equipped");
        }

        public virtual void OnShoot()
        {
            if (debugging) Debug.Log("WUSO "+description.name+" Shot");
        }

        public virtual void OnHit()
        {
            if (debugging) Debug.Log("WUSO "+description.name+" Hit");
        }

        public virtual void OnDequip()
        {
            Debug.Log("WUSO "+description.name+" Dequipped");
        }
        
        // Health
        public virtual void OnHealthChange(float val)
        {
            if (val < 0) OnHealed();
            else if (val > 0) OnDamaged();
        }

        public virtual void OnHealed()
        {
            if (debugging) Debug.Log("WUSO "+description.name+" Healed");
        }

        public virtual void OnDamaged()
        {
            if (debugging) Debug.Log("WUSO "+description.name+" Damaged");
        }
    }
} 
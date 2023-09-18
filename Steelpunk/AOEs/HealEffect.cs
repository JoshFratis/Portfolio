/* The entirety of this script was written by Joshua Fratis */

using Game;
using Player;
using UnityEngine;

namespace AOEs
{
    public class HealEffect : MonoBehaviour, IEffect
    {
        [SerializeField] private float healAmount = 1.0f;
        [SerializeField] private float healEffectTime = 1.0f;
    
        public void ApplyEffect(GameObject affected)
        {
            Damageable dmgAble = affected.GetComponent<Damageable>();
            PlayerController pc = affected.GetComponent<PlayerController>();
                
            // Server
            dmgAble.SetHealth(dmgAble.GetHealth() + healAmount);

            // Client
            pc.AddHealSource("AOE", healEffectTime);
        }
    }
}

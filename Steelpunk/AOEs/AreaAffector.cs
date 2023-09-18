/* The entirety of this script was written by Joshua Fratis */

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

namespace AOEs
{
    public class AreaAffector : MonoBehaviour
    {
        [SerializeField] private float radius = 5.0f;
        [SerializeField] private float timeBetweenEffects = 1.0f;
        [SerializeField] private List<string> affectedTags = new List<string>()
        {
            "Player"
        };
        [SerializeField] private float persistTime = -1f;

        private List<GameObject> _inArea;
        private IEffect[] _effects;

        private BubbleAnimation _animator;
        
        void Awake()
        {
            _inArea = new List<GameObject>();
           _effects = gameObject.GetComponents<IEffect>();
           _animator = GetComponent<BubbleAnimation>();
        }

        void Start()
        {
            _animator.targetScale = radius * 2;
            _animator.Expand();
            
            StartCoroutine(ApplyEffect());
            StartCoroutine(PersistRoutine());
            
            _animator.OnFinishedShrinking += () =>
            {
                Destroy(gameObject.transform.root.gameObject);
            };
        }

        private void Update()
        {
            if (_animator.Animstate == BubbleAnimation.AnimState.Shrunk) Destroy(gameObject);
        }

        private void OnTriggerEnter(Collider other)
        {
            foreach (var affectedTag in affectedTags)
            {
                if (other.CompareTag(affectedTag))
                {
                    _inArea.Add(other.gameObject);
                }
            }
        }

        private void OnTriggerExit(Collider other)
        {
            foreach (var affectedTag in affectedTags)
            {
                if (other.CompareTag(affectedTag) && (_inArea.Contains(other.gameObject)))
                {
                    _inArea.Remove(other.gameObject);
                }
            }
        }
    
        private IEnumerator ApplyEffect()
        {
            while (true)
            {
                yield return new WaitForSeconds(timeBetweenEffects);
                foreach (var effect in _effects)
                {
                    foreach (var affected in _inArea)
                    {
                        effect.ApplyEffect(affected);
                    }
                }
            }
        }

        private IEnumerator PersistRoutine()
        {
            if (persistTime < 0) yield break;
            yield return new WaitForSeconds(persistTime);
            _animator.Shrink();
        }
    }
}

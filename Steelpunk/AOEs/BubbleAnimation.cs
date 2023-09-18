/* The entirety of this script was written by Joshua Fratis */

using System;
using System.Collections;
using UnityEngine;

namespace AOEs
{
    public class BubbleAnimation : MonoBehaviour
    {
        [SerializeField] private float spawnSpeed = 20;
        [SerializeField] private float idleOscillationRange = 0.15f;
        [SerializeField] private float idleOscillationFrequency = 1.2f;
        [SerializeField] public float targetScale;
        [SerializeField] private Transform bubbleParent;


        [SerializeField] private bool _materialAnimated;
        [SerializeField] private float emissiveIntensityRange = 0.1f;

        public AnimState Animstate { get; private set;  }
        private Oscillator _oscillator;
        private Material _material;

        private float baseEmissiveIntensity;
        private Color baseEmissionColor;

        public enum AnimState
        {
            Expanding,
            Idling,
            Shrinking,
            Shrunk
        }
        
        public Action OnFinishedShrinking;

        void Awake()
        {
            _material = GetComponent<Renderer>().material;
        }
    
        void Start()
        {
            _oscillator = new Oscillator(this, idleOscillationFrequency);

            baseEmissiveIntensity = _material.GetFloat("_EmissiveIntensity");
            baseEmissionColor = _material.GetColor("_EmissiveColor");
            Debug.Log("Base Emissive Intensity: "+baseEmissiveIntensity);
            Debug.Log("Base Emissive Color: "+baseEmissionColor);
        }
    
        void Update()
        {
            if (Animstate == AnimState.Idling) IdleAnim();
            if (_materialAnimated) MatAnim();
        }

        public void Expand()
        {
            Animstate = AnimState.Expanding;
            StartCoroutine(ExpandAnim());
        }

        public void Shrink()
        {
            Animstate = AnimState.Shrinking;
            StartCoroutine(ShrinkAnim());
        }
        
        private IEnumerator ExpandAnim()
        {
            while (bubbleParent.localScale.x < targetScale)
            {
                bubbleParent.localScale += new Vector3(1, 1, 1) * (Time.deltaTime * spawnSpeed); // add configurable time to spawn, calculate growth
                yield return null;
            }

            bubbleParent.localScale = new Vector3(targetScale, targetScale, targetScale);
            
            Animstate = AnimState.Idling;
            _oscillator.Start();
        }

        private IEnumerator ShrinkAnim()
        {
            // Wait for idle anim to shrink
            while (_oscillator.Height > 1)
            {
                yield return null; 
            }
            
            while (bubbleParent.localScale.x > 0)
            {
                bubbleParent.localScale -= new Vector3(1, 1, 1) * (Time.deltaTime * spawnSpeed); // add configurable time to spawn, calculate growth
                yield return null;
            }

            bubbleParent.localScale = Vector3.zero;
            Animstate = AnimState.Shrunk;
            OnFinishedShrinking?.Invoke();
        }

        private void IdleAnim()     // turn this into a coroutine that goes while state = idling and returns otherwise
        {
            var idleAnimScale = idleOscillationRange * (float) _oscillator.Height;
            bubbleParent.localScale = new Vector3(targetScale + idleAnimScale, targetScale + idleAnimScale, targetScale + idleAnimScale);
            if ((bubbleParent.localScale.x > targetScale + idleOscillationRange) || (bubbleParent.localScale.x < targetScale - idleOscillationRange)) Debug.LogWarning("Oscillating animation outside range.");
        }

        private void MatAnim()
        {
            float newEmissiveIntensity =  baseEmissiveIntensity + (float) (_oscillator.Height * emissiveIntensityRange);
            _material.SetColor("_EmissiveColor",baseEmissionColor *  newEmissiveIntensity);
            
            Debug.Log("New Emissive Intensity: "+newEmissiveIntensity);
            Debug.Log("Emissive Intensity: "+_material.GetFloat("_EmissiveIntensity"));
            Debug.Log("Emissive Color: "+_material.GetColor("_EmissiveColor"));
        }
    } 
}

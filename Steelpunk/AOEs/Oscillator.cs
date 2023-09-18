/* The entirety of this script was written by Joshua Fratis */

using System;
using System.Collections;
using UnityEngine;

namespace AOEs
{
    public class Oscillator
    {
        private MonoBehaviour mono;
        public double Frequency { get; set; }
        public double Height { get; set; }

        public Oscillator(MonoBehaviour mono, double frequency)
        {
            this.mono = mono;
            this.Frequency = frequency;
        }
    
        public void Start()
        {
            mono.StartCoroutine(Oscillate());
        }

        public void Stop()
        {
            mono.StopCoroutine(Oscillate());
        }

        private IEnumerator Oscillate()
        {
            double time = 0;

            var period = Frequency * Math.PI * 2;

            while (true)
            {
                time = (time + (period * Time.deltaTime)) % (Math.PI * 2);
                Height = Math.Sin(time);
                yield return null;
            }
        }
    }
}

/* The entirety of this script was written by Joshua Fratis */

using System;
using System.Collections;
using System.Collections.Generic;
using GameState;
using UnityEngine;
using Utility;

namespace Enemies.Pathfinding
{
    public class Navigator : MonoBehaviour
    {
        [Header("Settings")] 
        [SerializeField] private bool showPath;
        
        [Header("Config")]
        [SerializeField] private float inertia = 6.0f;
        [SerializeField] private int lookAhead = 0;
        [SerializeField] private float leeway = 1.0f;
        
        [HideInInspector] public RaidRoomManager room;
        
        private bool _requestingPath;
        private Vector3 _goal;
        
        private Coroutine _pathfinderRoutine;
        public Coroutine AStarRoutine;

        // Debugging
        private static SteelpunkLogger.LoggerInstance logger =
            new (SteelpunkLogger.LogCategory.Navigation);
        
        // API
        [HideInInspector] public List<Vector3> path;
        [HideInInspector] public Vector3 vector;

        public bool NeedsPath => ((path == null) || (path.Count <= lookAhead));
        public Action ReachedNode;
        
        
        // Lifetime
        private void Start()
        {
            _goal = transform.position;
        }
        
        private void Update() 
        {
            if (NeedsPath) return;
            UpdatePath();
            
            if (showPath)
            {
                DebugPath(path);
            } 
        }
        
        private void FixedUpdate()
        {
            UpdateVector();
        }

        
        // Navigation
        private void UpdatePath()
        {
            if (Vector3.Distance(transform.position, path[lookAhead]) < leeway)
            {
                path.RemoveRange(0, Math.Max(1, Math.Min(lookAhead + 1, path.Count - 1)));
                if ((ReachedNode != null) && (ReachedNode.GetInvocationList().Length > 0))
                {
                    ReachedNode.Invoke();
                }
            }
        }

        private void UpdateVector()
        {
            Vector3 targetVelocity;
            
            // Normal Case - Following Path
            if (!NeedsPath && !_requestingPath)
            {
                targetVelocity = (path[lookAhead] - transform.position).normalized;
            }
            
            // Closing in on Goal
            else if (_requestingPath)
            {
                targetVelocity = _goal - transform.position;
                
                // Some Distance To Go
                if (targetVelocity.magnitude > inertia)
                {
                    targetVelocity = targetVelocity.normalized;
                }
                
                // Practically Arrived
                else if (targetVelocity.magnitude < 0.01)
                {
                    transform.position = _goal;
                    targetVelocity = Vector3.zero;
                }
            }

            // Arrived 
            else
            {
                targetVelocity = Vector3.zero;
            }

            targetVelocity *= inertia;
            vector = targetVelocity; // TODO: set to zero if calculated target isn't high enough? 
        }
        
        
        // Debugging
        private void DebugPath(List<Vector3> debugPath, float duration = 0.0f, bool nextLine = false, bool goalLine = false)
        {
            if (NeedsPath) return;

            // Line to Goal
            if (goalLine)
            {
                Color color = debugPath.Count > 1 ? Color.red : Color.green;
                Debug.DrawLine(transform.position, debugPath[^1], color, duration); 
            }
            
            // Defined Path
            for (int i = 0; i < debugPath.Count - 1; i++)
            {
                Debug.DrawLine(debugPath[i], debugPath[i+1], Color.cyan, duration);
            }
            
            // Trajectory to Next Node
            if (nextLine)
            {
                Debug.DrawLine(transform.position, debugPath[lookAhead], Color.magenta);
            }
        }
        
        
        // API
        public IEnumerator NavigateTo(Vector3 goal)
        {
            path = null;
            _requestingPath = true;

            Vector3 pos = transform.position;
            Vector3 start = new Vector3(Mathf.Round(pos.x), Mathf.Round(pos.y), Mathf.Round(pos.z)); 
            logger.Log("Navigating from " + start + " to " + goal);
            yield return _pathfinderRoutine = room.Pathfinder.StartCoroutine(
                room.Pathfinder.FindPath(
                    this, start, goal));

            if (path == null)  
            {
                logger.Log("Pathfinding Failed");
                yield break;
            }
            
            _goal = goal;
            _requestingPath = false;
        }
    
        public void ReturnPath(List<Vector3> returnedPath)
        {
            if (returnedPath == null)
            {
                return;
            }
            
            path = returnedPath;
            DebugPath(returnedPath, 3.0f);
        }

        public void EndNavigationRoutine()
        {
            if (_pathfinderRoutine != null)
            {
                room.Pathfinder.StopCoroutine(_pathfinderRoutine);
                _pathfinderRoutine = null;
            }
            
            if (AStarRoutine != null)
            {
                room.Pathfinder.StopCoroutine(AStarRoutine);
                AStarRoutine = null;
            }
        }
        
        public bool HasLineOfSightOn(Vector3 target, float duration = 0.0f, bool debug = false)
        {
            Vector3 pos = transform.position; 
            Vector3 dir = target - pos;
            float dist = dir.magnitude;
            RaycastHit hit;
            
            bool result = !Physics.Raycast(pos, dir, dist, 
                LayerMaskLibrary.Instance.environmentMask, QueryTriggerInteraction.Ignore);

            Color color = result ? Color.green : Color.red;
            Debug.DrawLine(pos, target, color, duration); 
            
            return result;
        }
    }
}

/* 
    The entierty of this script, 
    apart from the "Damage & Death", 
    "VFX", and "Serialization" sections, 
    were written by Joshua Fratis. 
*/

using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Castle.Core;
using Enemies.Pathfinding;
using Game;
using GameState;
using Palmmedia.ReportGenerator.Core;
using Serilog;
using Unity.Netcode;
using Unity.VisualScripting.Antlr3.Runtime.Collections;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.VFX;
using Utility;
using Random = UnityEngine.Random;
using VFXManager = GameState.VFXManager;

namespace Enemies
{
    public class HealerEnemyController : NetworkBehaviour
    {
        [Header("Config")]
        [SerializeField] private Transform effectParent;
        [SerializeField] private VisualEffect smokingEffect;

        [Header("Settings")]

        // Idle Settings
        private float _idleSpeed;
        private float _idleDistance;
        [SerializeField] private float idleSpeedBase = 2.0f;
        [SerializeField] private float idleSpeedVariance = 0.2f;
        [SerializeField] private float idleDistanceBase = 1.0f; 
        [SerializeField] private float idleVelocityVariance = 0.2f;
        [SerializeField] private float idleBaseHeight = 5.0f;
        
        // Healing Settings
        [SerializeField] public float healRange = 3.0f;
        [SerializeField] private float healAmount = 10.0f;
        public float MinHealthPrioritized { get; private set; } = 0.20f;
        public float MaxHealthPrioritized { get; private set; } = 0.80f;
        
        // General References & Components
        [HideInInspector]
        public Enemy enemy;
        private Rigidbody _rigidBody;
        
        // State Machine
        private enum State
        {
            Unassigned,     // Healer is NOT Assigned (no prioritized enemies in assignment)
            Navigating,     // Healer is Assigned and does NOT have LoS on Assignment Centroid
            Arrived,        // Healer is Assigned and has LoS on Assignment Centroid
        }
        private State _state = State.Unassigned;
        
        // Navigation
        [SerializeField] private float stallNavigationDuration;
        private Navigator _navigator;
        private Coroutine _controllerRoutine;
        private Coroutine _navigatorRoutine;
        private bool _hasLosOnAssignmentCentroid;
        
        // Damage
        private float _lastDamageTime;
        
        // Healing Behavior
        private readonly NetworkVariable<bool> _healingActive = new();
        private readonly NetworkVariable<NetworkedHealingAssignment> _netHealingAssignment = new();
        private readonly List<Pair<bool, Enemy>> _localAssignment = new();
        
        private Coroutine _healingRoutine;
        private VisualEffect[] _healingEffects;
        
        // Healing Assignment
        private RaidRoomManager.HealerEnemyManager _manager;
        public HealerAssignment Assignment;

        public class HealerAssignment
        {
            private HashSet<Enemy> _enemies;

            public IReadOnlyCollection<Enemy> Enemies => _enemies;
            private HealerEnemyController _controller;

            public HealerAssignment(HealerEnemyController controller)
            {
                _controller = controller;
                _enemies = new HashSet<Enemy>();
            }

            public void Add(Enemy enemy)
            {
                enemy.Damageable.onDeath.AddListener(((_, _) =>
                {
                    _enemies.Remove(enemy);
                }));
                _controller._manager.HealableEnemies[enemy].Assigned = true;
                _enemies.Add(enemy);
                _controller.OnUpdateAssignment();
            }

            public void Set(HashSet<Enemy> newAssignment)
            {
                if (newAssignment.SetEquals(_enemies)) return;
                
                foreach (var enemy in _enemies)
                {
                    _controller._manager.HealableEnemies[enemy].Assigned = false;
                }
                
                foreach (var enemy in newAssignment)
                {                
                    _controller._manager.HealableEnemies[enemy].Assigned = true;
                    enemy.Damageable.onDeath.AddListener(((_, _) =>
                    {
                        _enemies.Remove(enemy);
                    }));
                }

                _enemies = new HashSet<Enemy>(newAssignment);
                _controller.OnUpdateAssignment();
            }
        }
        
        // Serialization
        private struct NetworkedHealingAssignment : INetworkSerializable
        {
            public List<Pair<bool, NetworkBehaviourReference>> Assignment;
            
            public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
            {
                int length = 0;

                if (serializer.IsWriter)
                {
                    if (Assignment == null)
                    {
                        length = 0;
                    }
                    else
                    {
                        length = Assignment.Count;
                    }
                }
                
                serializer.SerializeValue(ref length);

                if (length == 0)
                {
                    return;
                }
                
                if (serializer.IsReader)
                {
                    Assignment = new List<Pair<bool, NetworkBehaviourReference>>();
                }

                for(int i = 0; i < length; i++)
                {
                    bool enemyPrioritized = false;
                    NetworkBehaviourReference enemy = default; 
                    
                    if (serializer.IsWriter)
                    {
                        if (Assignment != null)
                        {
                            enemyPrioritized = Assignment[i].First;
                            enemy = Assignment[i].Second;
                        }
                    }
                    
                    serializer.SerializeValue(ref enemyPrioritized);
                    serializer.SerializeValue(ref enemy);

                    if (serializer.IsReader)
                    {
                        if (Assignment != null)
                        {
                            Assignment.Add(new Pair<bool, NetworkBehaviourReference>(enemyPrioritized, enemy));
                        }
                    }
                }
            }
        }
        
        private bool Assigned => Assignment.Enemies.Any(e => _manager.HealableEnemies[e].Prioritized);
        
        private Vector3? AssignmentCentroid
        {
            get
            {
                if (!Assigned) return null;
            
                Vector3 result = Vector3.zero;
                int nPrioritizedTargets = 0;
            
                foreach (Enemy target in Assignment.Enemies)
                {
                    if (_manager.HealableEnemies[target].Prioritized)
                    {
                        result += target.transform.position;
                        nPrioritizedTargets++;
                    }
                }
                result /= nPrioritizedTargets;

                if (nPrioritizedTargets == 1)
                {
                    result += Vector3.left + Vector3.up; 
                }
            
                return enemy.room.Pathfinder.FindClosestFree(result); 
            }
        }

        // Debugging
        private static readonly SteelpunkLogger.LoggerInstance Logger = new (SteelpunkLogger.LogCategory.HealerNavigation);
        private bool _loggingAssignment;

        
        // Lifetime
        private void Awake()
        {
            _healingEffects = effectParent.GetComponentsInChildren<VisualEffect>();
            
            enemy = GetComponent<Enemy>();
            enemy.onActivate.AddListener(OnActivate);
            
            _rigidBody = GetComponent<Rigidbody>();
            _navigator = GetComponent<Navigator>();
            
            Assignment = new HealerAssignment(this);
        }

        private void Start()
        {
            if (IsServer)
            {
                enemy.Damageable.onTakeDamageServer.AddListener(OnTakeDamage);
                enemy.Damageable.onDeathServer.AddListener(((_, _, _) =>
                {
                    _healingActive.Value = false;
                    StopAllCoroutines();
                    StartCoroutine(DeathRoutine());
                }));
            }
            
            _healingActive.OnValueChanged += OnHealingActiveChanged;

            // References
            _manager = enemy.room.healerManager;
            _navigator.room = enemy.room;
        }

        public override void OnNetworkSpawn()
        {
            _netHealingAssignment.OnValueChanged += OnAssignmentUpdated;
        }

        private void OnActivate()
        {
            if (!IsOwner) return;
            
            _idleSpeed = Random.Range(idleSpeedBase - idleSpeedVariance, idleSpeedBase + idleSpeedVariance);
            _idleDistance = Random.Range(idleDistanceBase - idleVelocityVariance, idleDistanceBase + idleVelocityVariance);

            if (Physics.Raycast(transform.position, Vector3.down, out RaycastHit hit,
                    100.0f, LayerMaskLibrary.Instance.environmentMask))
            {
                transform.position = new Vector3(hit.point.x, hit.point.y + idleBaseHeight, hit.point.z);
            }

            _healingActive.Value = true;

            SetState(State.Unassigned); 
            StartCoroutine(GetStateRoutine());
        }
        
        private void Update()
        {
            if (!enemy.IsAwake) return;

            HashSet<Enemy> newAssignment = new HashSet<Enemy>(Assignment.Enemies);
            foreach (Enemy e in Assignment.Enemies)
            {
                if (!e)
                {
                    newAssignment.Remove(e);
                    continue;
                }
                
                // Re-Prioritize Enemies
                _manager.HealableEnemies[e].Prioritized = EnemyPrioritizable(e);
                
                // drop enemies that are de-prioritized and outside physical range
                if (!_manager.HealableEnemies[e].Prioritized && !EnemyInRange(e))
                {
                    newAssignment.Remove(e);
                }
            }
            Assignment.Set(newAssignment);

            // add enemies that are unassigned and within physical range
            foreach (var e in _manager.HealableEnemies)
            {
                if ((!e.Value.Assigned) && (EnemyInRange(e.Key)))
                {
                    Assignment.Add(e.Key);
                    e.Value.Prioritized = EnemyPrioritizable(e.Key);
                }
            }

            // VFX
            if (IsClient)
            {
                var prioritizedEnemies = _localAssignment.Where(pair => pair.First);
                int effectIndex = 0;
                foreach (var pair in prioritizedEnemies)
                {
                    if (!EnemyInRange(pair.Second)) continue;
                    if (effectIndex < _healingEffects.Length)
                    {
                        _healingEffects[effectIndex].SetVector3("HealingBeamStart", this.enemy.CenterOfMass);
                        _healingEffects[effectIndex].SetVector3("HealingBeamEnd", pair.Second.CenterOfMass);
                        _healingEffects[effectIndex].enabled = true;
                    }
                    effectIndex++;
                }
                
                var enemies = _localAssignment.Where(pair => !pair.First);
                foreach (var pair in enemies)
                {
                    if (!EnemyInRange(pair.Second)) continue;
                    if (effectIndex < _healingEffects.Length)
                    {
                        _healingEffects[effectIndex].SetVector3("HealingBeamStart", this.enemy.CenterOfMass);
                        _healingEffects[effectIndex].SetVector3("HealingBeamEnd", pair.Second.CenterOfMass);
                    _healingEffects[effectIndex].enabled = true;
                    }
                    effectIndex++;
                }
                
                for(int i = effectIndex; i < _healingEffects.Length; i++)
                {
                    _healingEffects[effectIndex].SetVector3("HealingBeamStart", this.enemy.CenterOfMass);
                    _healingEffects[effectIndex].SetVector3("HealingBeamEnd", this.enemy.CenterOfMass);
                    _healingEffects[effectIndex].enabled = false;
                }
            }

            if (IsServer)
            {
                foreach (var target in Assignment.Enemies)
                {
                    if (!EnemyInRange(target)) continue;
                    
                    var damageable = target.GetComponent<Damageable>();

                    damageable.SetHealth(Mathf.Min(
                        damageable.GetHealth() + healAmount * Time.deltaTime,
                        damageable.GetMaxHealth()));
                }
            }
        } 

        private void FixedUpdate()
        {
            if (!IsOwner) return;

            if (!enemy.Damageable.IsDead && Time.time - _lastDamageTime > 0.5f)
            {
                Vector3 targetVector = Vector3.zero;
                
                switch (_state)
                {
                    case State.Unassigned:
                        var idleVectorY = Mathf.Sin(Time.fixedTime * _idleSpeed) * _idleDistance;
                        targetVector = new Vector3(0.0f, idleVectorY, 0.0f);
                        break;
                    
                    case State.Navigating:
                        targetVector = _navigator.vector;
                        break;
                    
                    case State.Arrived:
                        var ac = AssignmentCentroid;
                        if (ac != null)
                        {
                            Logger.Log("Valid Assignment Centroid could not be found (arrived).");
                            targetVector = (Vector3)ac - transform.position;
                        }
                        break;
                }

                if (Vector3.Distance(_rigidBody.velocity, targetVector) > 1.0f)
                {
                    _rigidBody.velocity = Vector3.MoveTowards(_rigidBody.velocity, targetVector, 
                        15.0f * Time.fixedDeltaTime);
                }
            }
        }
        
        public override void OnDestroy()
        {
            if(_netHealingAssignment != null)
                _netHealingAssignment.OnValueChanged -= OnAssignmentUpdated;
        }

        
        // Get State
        private IEnumerator GetStateRoutine()
        {
            while (true)
            {
                yield return new WaitForSeconds(1.0f);
                var newState = GetState();
                
                if (newState == State.Unassigned)
                {
                    SetState(newState);
                }
                else if (newState != _state)
                { 
                    Logger.Log("New State: " + newState);
                    SetState(newState);
                }
            }
        }
        
        private State GetState()
        {
            // Do I have any prioritized enemies assigned to me to heal?
            if (!Assigned)
            {
                return State.Unassigned;
            }
            
            // Is there a location where I can carry out my assignment?
            var nullableAc = AssignmentCentroid;
            if (nullableAc == null)
            {
                Logger.Log("Valid Assignment Centroid could not be found (resetting state).");
                return State.Unassigned;
            }
            var ac = (Vector3)nullableAc;

                // Do I have a direct path to my assignment location?
            if (!_navigator.HasLineOfSightOn(ac))
            {
                return State.Navigating;
            }
            
            // Am I at my assignment location?
            if (Vector3.Distance(transform.position, ac) < 1.0f)
            {
                // And is there an assigned enemy here for me to heal? 
                var requestNewAssignment = true;
                foreach (var e in Assignment.Enemies)
                {
                    if ((_manager.HealableEnemies[e].Prioritized) && (EnemyInRange(e)))
                    {
                        requestNewAssignment = false;
                        break; 
                    }
                }

                if (requestNewAssignment)
                {
                    Logger.Log("No prioritized enemies in range.");
                    return State.Unassigned;
                }
            }

            return State.Arrived; 
        }

        private bool EnemyInRange(Enemy e)
        {
            if (!e) return false;
                
            var enemyPosition = e.transform.position;
            var healerPosition = transform.position;
            return (Vector3.Distance(
                        new Vector3(enemyPosition.x,0.0f, enemyPosition.z),
                        new Vector3(healerPosition.x, 0.0f, healerPosition.z))
                    <= healRange);
        }

        private bool EnemyPrioritizable(Enemy e)
        {
            return GetPotentialHealing(e) > 0;
        }
        
        private float GetPotentialHealing(Enemy e)
        {
            float memberEnemyMaxHealth = e.Damageable.GetMaxHealth();
            float memberEnemyCurrentHealth = e.Damageable.GetHealth();
            float memberEnemyPercentHealth = memberEnemyCurrentHealth / memberEnemyMaxHealth;
            if ((memberEnemyPercentHealth > MinHealthPrioritized) && (memberEnemyPercentHealth < MaxHealthPrioritized))
            {
                return memberEnemyMaxHealth - memberEnemyCurrentHealth;
            }
            return 0;
        }
        
        
        // Set State
        private void SetState(State newState) 
        {
            switch (newState)
            {
                case State.Unassigned:
                    _state = State.Unassigned;
                    StartCoroutine(NewAssignmentRoutine());
                    break;
                case State.Navigating:
                    _state = State.Navigating;
                    StartNavigationRoutine();
                    break;
                case State.Arrived:
                    _state = State.Arrived;
                    EndNavigationRoutine();
                    break;
            }
        }
        
        private IEnumerator NewAssignmentRoutine()
        {
            yield return new WaitUntil(() => _manager != null);
           Assignment.Set(_manager.GenerateAssignment(this));
           if (!_loggingAssignment) StartCoroutine(LogAssignmentRoutine());
        }

        private void StartNavigationRoutine(bool overrideRoutine = false)
        {
            if (_controllerRoutine != null)
            {
                if (overrideRoutine)
                {
                    StopCoroutine(_controllerRoutine);
                }
                else
                {
                    Logger.Log("Navigation Routine is Busy!");
                    return;
                }
            }

            _controllerRoutine = StartCoroutine(NavigationRoutine());
        }
        
        private IEnumerator NavigationRoutine()
        {
            yield return new WaitForSeconds(stallNavigationDuration); 
            
            var nac = AssignmentCentroid;
            if (nac == null)
            {
                Logger.Log("Valid Assignment Centroid could not be found (navigating).");
                yield break;
            }
            var ac = (Vector3)nac;
            
            yield return _navigatorRoutine =
                _navigator.StartCoroutine(
                    _navigator.NavigateTo(ac));
            EndNavigationRoutine();
            
            if (_navigator.path == null)
            {
                Logger.Log("No path returned.");
                SetState(State.Arrived); // This will naturally reevaluate if the healer needs a new assignment
            }

            Logger.Log("Navigation Routines Available");
        }

        private void EndNavigationRoutine()
        {
            if (_controllerRoutine != null)
            {
                Logger.Log("Ending Navigation Routines");
                StopCoroutine(_controllerRoutine);
                _controllerRoutine = null;
            }

            if (_navigatorRoutine != null)
            {
                _navigator.StopCoroutine(_navigatorRoutine);
                _navigatorRoutine = null;
            }

            _navigator.EndNavigationRoutine();
        }
        

        // Healing Behavior
        private void OnHealingActiveChanged(bool oldValue, bool newValue)
        {
            if (newValue != oldValue)
            {
                var msg = newValue ? "On" : "Off";
                Logger.Log("Healing " + msg);
            }

            foreach (var effect in _healingEffects)
            {
                effect.enabled = newValue;
            }
        }
        
        
        // Healing Assignment
        private void OnAssignmentUpdated(NetworkedHealingAssignment old, NetworkedHealingAssignment newAssignment)
        {
            _localAssignment.Clear();

            if (newAssignment.Assignment == null)
                return;
            
            foreach (var pair in newAssignment.Assignment)
            {
                if (pair.Second.TryGet(out var enemy))
                {
                    var newPair = new Pair<bool, Enemy>(pair.First, enemy.GetComponent<Enemy>());
                    _localAssignment.Add(newPair);
                    enemy.GetComponent<Damageable>().onDeath.AddListener(((_, _) =>
                    {
                        _localAssignment.Remove(newPair);
                    }));
                }
            }
        }

        private void OnUpdateAssignment()
        {
            if (!IsServer || !IsSpawned)
                return;
            
            _netHealingAssignment.Value =
                new NetworkedHealingAssignment()
                {
                    Assignment = Assignment.Enemies.Select(itEnemy => new Pair<bool, NetworkBehaviourReference>(
                        _manager.HealableEnemies[itEnemy].Prioritized,
                        new NetworkBehaviourReference(itEnemy))).ToList()
                };

            if (!_loggingAssignment) StartCoroutine(LogAssignmentRoutine());
        }
        
        // Damage & Death
        private void OnTakeDamage(DamagePayload payload, Ray ray, float amt)
        {
            _lastDamageTime = Time.time;
            _rigidBody.AddForceAtPosition(
                (ray.direction + Random.insideUnitSphere).normalized *
                payload.baseDamage,
                ray.origin, ForceMode.Impulse);

            if (enemy.Damageable.GetHealth() <
                0.3f * enemy.Damageable.GetMaxHealth())
                StartLowHpClientRpc();
        }
        
        [ClientRpc]
        private void StartLowHpClientRpc()
        {
            smokingEffect.Play();
        }

        private IEnumerator DeathRoutine()
        {
            foreach (var e in Assignment.Enemies)
            {
                _manager.HealableEnemies[e].Assigned = false;
            }
            
            _rigidBody.constraints = RigidbodyConstraints.None;
            _rigidBody.AddTorque(transform.forward * 100.0f, ForceMode.Impulse);
            _rigidBody.AddForce((Random.insideUnitSphere + Vector3.up) * 60.0f,
                ForceMode.Impulse);
            _rigidBody.useGravity = true;

            yield return new WaitForSeconds(1.0f);
            VFXManager.Instance.ExplosionEffectClientRpc(transform.position, 2.5f);
            Destroy(gameObject);
        }

        
        // Debugging
        private IEnumerator LogAssignmentRoutine()
        {
            _loggingAssignment = true;
            yield return new WaitForSeconds(0.1f);
            LogAssignment();
            _loggingAssignment = false;
        }

        private void LogAssignment()
        {
            var str = "Assignment ("+Assignment.Enemies.Count+"): ";
            foreach (var key in Assignment.Enemies)
            {
                str += (_manager.HealableEnemies[key].Prioritized ? 
                    key.gameObject.name.ToUpper() : 
                    key.gameObject.name) + ", ";
            }
            Logger.Log(str);
        }
    }
}
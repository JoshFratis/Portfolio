/* 
    The "Pathfinding & Navigation" and the "Healer Enemy Manager" sections
    were developed solely by Joshua Fratis. The other sections were developed
    jointly by Joshua Fratis and Thomas Carey. Sections have been organized
    in order to best illustrate ownership and highlight Joshua Fratis's contributions. 
*/

using System;
using System.Collections.Generic;
using System.Linq;
using Enemies;
using Enemies.Pathfinding;
using GameUI;
using Loot;
using Networking;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Serialization;
using Utility;

namespace GameState
{
    public class RaidRoomManager : NetworkBehaviour
    {
        // Pathfinding & Navigation - Joshua Fratis
        public PathfinderDebugger pathfinderDebugger;
        [SerializeField] private int pathfinderResolution;
        [SerializeField] private int pathfinderSpeed;

        private Pathfinder _pathfinder;

        public Pathfinder Pathfinder {
            get
            {
                if (_pathfinder == null)
                {
                    ConstructPathfinder();
                }
                return _pathfinder;
            }
        }

        private void ConstructPathfinder()
        {
            _pathfinder = gameObject.AddComponent<Pathfinder>();
            _pathfinder.Initialize(gameObject, 30f, 20f, 30f, Mathf.Max(1, pathfinderResolution), pathfinderSpeed);
            pathfinderDebugger = GetComponent<PathfinderDebugger>();
            
            if (pathfinderDebugger && pathfinderDebugger.enabled)
            {
                pathfinderDebugger.DemoMap(_pathfinder);
            }
        }


        // Healer Enemy Manager - Joshua Fratis
        private readonly List<Enemy> _currentEnemies = new();
        private Dictionary<Enemy.EnemyType, List<Enemy>> _enemyTypeMap = new();
        public HealerEnemyManager healerManager;

        public class HealerEnemyManager : MonoBehaviour
        {
            public HashSet<HealerEnemyController> Healers;
            public Dictionary<Enemy, HealableEnemy> HealableEnemies;
            
            private bool _collectingHealerGroup;
            private HashSet<HealerEnemyController> _healerGroup; 

            public class HealableEnemy
            {
                public bool Healable;   // Excludes healer enemies
                public bool Assigned;   // To maintain that each enemy is healed by at most on healer
                public bool Prioritized; // For assigned enemies at 20%-80% their maximum health, used to calculate assignment weight and centroid

                public HealableEnemy(
                    bool healable = true, 
                    bool assigned = false, 
                    bool prioritized = false)
                {
                    Healable = healable;
                    Assigned = assigned;
                    Prioritized = prioritized;
                }
            }

            
            // Lifetime
            public void Initialize()
            {
                Healers = new HashSet<HealerEnemyController>();
                HealableEnemies = new Dictionary<Enemy, HealableEnemy>();
            }
            
            public HashSet<Enemy> GenerateAssignment(HealerEnemyController healer)
            {
                HashSet<Enemy> currAssig = new HashSet<Enemy>();
                HashSet<Enemy> bestAssig = new HashSet<Enemy>();
                float bestAssigWeight = 0.0f;

                foreach (KeyValuePair<Enemy, HealableEnemy> repEnemyState in HealableEnemies)
                {
                    var currAssigWeight = 0.0f;
                    if ((repEnemyState.Value.Healable) || (repEnemyState.Key == healer.enemy))
                    {
                        foreach (KeyValuePair<Enemy, HealableEnemy> memberEnemyState in HealableEnemies)
                        {
                            HealableEnemies[memberEnemyState.Key].Prioritized = EnemyPrioritizable(healer, memberEnemyState.Key);
                            
                            if ((memberEnemyState.Value.Healable) &&
                                (Vector3.Distance(repEnemyState.Key.transform.position, 
                                    memberEnemyState.Key.transform.position) < healer.healRange))
                            {
                                currAssigWeight =
                                    AddEnemyToAssignment(memberEnemyState.Key, healer, currAssig, currAssigWeight);
                            }
                        }

                        if (currAssigWeight > bestAssigWeight)
                        {
                            bestAssig.Clear();
                            bestAssig.UnionWith(currAssig);
                            bestAssigWeight = currAssigWeight;
                        }

                        currAssig.Clear();
                    }
                }

                return bestAssig;
            }

            
            // Enemy Assessment
            private float GetPotentialHealing(HealerEnemyController healer, Enemy enemy)
            {
                float memberEnemyMaxHealth = enemy.Damageable.GetMaxHealth();
                float memberEnemyCurrentHealth = enemy.Damageable.GetHealth();
                float memberEnemyPercentHealth = memberEnemyCurrentHealth / memberEnemyMaxHealth;
                if ((memberEnemyPercentHealth > healer.MinHealthPrioritized) && (memberEnemyPercentHealth < healer.MaxHealthPrioritized))
                {
                    return memberEnemyMaxHealth - memberEnemyCurrentHealth;
                }
                return 0;
            }

            private bool EnemyPrioritizable(HealerEnemyController healer, Enemy enemy)
            {
                return GetPotentialHealing(healer, enemy) > 0;
            }
            
            private float AddEnemyToAssignment(Enemy enemy, HealerEnemyController healer, HashSet<Enemy> assig, float assigWeight)
            {
                var enemyWeight = GetPotentialHealing(healer, enemy);
                HealableEnemies[enemy].Prioritized = (enemyWeight > 0);
                assig.Add(enemy);
                return assigWeight + enemyWeight;
            }
        }


        // Enemies - Joshua Fratis & Thomas Carey
        public UnityEvent onEnemyDied;
        public List<Enemy> Enemies => _currentEnemies;

        public IEnumerable<Enemy> GetEnemies()
        {
            return _currentEnemies;
        }
        
        public void AddEnemies(List<Enemy> enemies)
        {
            foreach (var enemy in enemies)
            {
                if (_enemyTypeMap.TryGetValue(enemy.Type, out var list))
                {
                    list.Add(enemy);
                }
                else
                {
                    _enemyTypeMap.Add(enemy.Type, new List<Enemy> {enemy});
                }
                
                // This block is for EnemyTester
                if (healerManager == null)
                { 
                    healerManager = gameObject.AddComponent<HealerEnemyManager>();
                    healerManager.Initialize();
                }
                
                if (enemy.Type == Enemy.EnemyType.Healer)
                {
                    var healerEnemyController = enemy.GetComponent<HealerEnemyController>();
                    healerManager.Healers.Add(healerEnemyController);
                    healerManager.HealableEnemies.Add(enemy, new HealerEnemyManager.HealableEnemy(false));
                    healerEnemyController.Assignment.Set(healerManager.GenerateAssignment(healerEnemyController));
                    Logger.Log("Healer Added");
                } 
                else healerManager.HealableEnemies.Add(enemy, new HealerEnemyManager.HealableEnemy());
            }
            _currentEnemies.AddRange(enemies);
        }
        
        public void OnEnemyDied(Enemy enemy)
        {
            _enemyTypeMap[enemy.Type].Remove(enemy);
            
            _currentEnemies.Remove(enemy);
            healerManager.HealableEnemies.Remove(enemy);

            if (enemy.Type == Enemy.EnemyType.Healer)
            {
                healerManager.Healers.Remove(enemy.GetComponent<HealerEnemyController>());
            }
            
            onEnemyDied.Invoke();
        }


        // Lifetime - Joshua Fratis & Thomas Carey
        public enum RoomState
        {
            Unentered,
            Alarm,
            Cleared
        }
        public RoomState State
        {
            get => _roomState;
            set
            {
                _roomState = value;
                onRoomStateChange.Invoke();
                Logger.Log("Setting room state to " + value);
                
                if(IsSpawned)
                    OnRoomStateUpdateClientRpc(value);
                    
                foreach (var door in doors)
                {
                    door.UpdateRoomState();
                }
            }
        }
        private RoomState _roomState = RoomState.Unentered;

        [SerializeField] public Collider roomEntryCollider;
        public UnityEvent onRoomStateChange;
        public UnityEvent onRoomStateChangeClient;
        
        private int _currentFloor;
        private float _spawnCurrency;
        private float _remainingCurrency;

        private Coroutine _waveCoroutine;

        public List<RaidRoomDoorway> Doors => doors;
        public List<RaidRoomDoorway> doors;

        private void Awake()
        {
            if(!forceRender)
                SetRendered(false);

            ConstructPathfinder();
        }
        
        public void Initialize(bool isHidden)
        {
            doors = new List<RaidRoomDoorway>();
            
            _enemyTypeMap = new Dictionary<Enemy.EnemyType, List<Enemy>>();
            
            healerManager = gameObject.AddComponent<HealerEnemyManager>();
            healerManager.Initialize();
             

            InitializeClientRpc(isHidden);
        }

        [ClientRpc]
        private void InitializeClientRpc(bool isHidden)
        {
            // if (roomConfig.roomType == ERoomType.Entry)
            // {
            //     SetRendered(true);
            // }
            // else
            // {
            //     SetRendered(false);
            // }

            MinimapManager.Instance.AddRoom(transform, roomConfig, isHidden);
        }
        
        public override void OnNetworkSpawn()
        {
            MapManager.Instance.RegisterRoom(this);
            
            if (!roomEntryCollider.GetComponent<RaidRoomEntryCollider>())
                roomEntryCollider.gameObject
                    .AddComponent<RaidRoomEntryCollider>();

            if (!IsServer) return;

            //SetLightState(false);

            State = RoomState.Unentered;
        }

        public static void InitializeRooms()
        {
            _renderedRooms.Clear();
        }


        // Room - Thomas Carey
        [SerializeField] private SRoomConfig roomConfig;
        public SRoomConfig RoomConfig => roomConfig;
        
        [Serializable]
        public struct SRoomConfig
        {
            public string name;
            public bool selectable;
            public Vector2Int[] tiles; 
            public LevelGenConfigScriptableObject.DoorConfig[] doorConfigs;
            public ERoomType roomType;
            public Mesh minimapMesh;
            public Transform spawnPosition;
            
            // Spawning Specifications
            /*
             * Order of Operations:
             * Selectable
             * Depth
             * Instances
             */
            public int minInstances;
            public int maxInstances;
            public int minDepth;
            public int maxDepth;
            public float selectionWeight;
            [HideInInspector] public int index;
        }
        
        public enum ERoomType
        {
            Entry,
            Exit,
            Blank,
            Fight,
            Shop,
            Heal,
            Item
        }
        
        [ClientRpc]
        private void OnRoomStateUpdateClientRpc(RoomState newState)
        {
            onRoomStateChangeClient.Invoke();
            MinimapManager.Instance.OnRoomStateUpdate(this, newState);
        }
        
        public void ResetDoorState()
        {
            //Default behaviour when no controller is present
            foreach (var door in Doors.Where(door =>
                         door.rooms[1] != null))
            {
                // TODO handle this conundrum
                door.SetDoorStateServer(
                    door.rooms[1].GetComponent<RaidRoomController>()
                        ? door.rooms[1].GetComponent<RaidRoomController>()
                            .CanUnlockDoor()
                            ? RaidRoomDoorway.State.Unlocked
                            : RaidRoomDoorway.State.Locked
                        : RaidRoomDoorway.State.Unlocked);
            }
        }


        // Rendering - Thomas Carey
        public UnityEvent onFirstRender;
        public UnityEvent<bool> onRenderStateChange;
        private bool _hasFirstRendered;

        private static HashSet<RaidRoomManager> _renderedRooms = new();
        private bool _rendered = true;
        public bool Rendered => _rendered;
        
        public bool forceRender;
        public bool demo;

        [ServerRpc(RequireOwnership = false)]
        public void FirstRenderServerRpc()
        {
            if (!_hasFirstRendered)
            {
                onFirstRender?.Invoke();
                _hasFirstRendered = true;
            }
        }

        public void SetRendered(bool rendered)
        {
            if (_rendered == rendered || !this) return;

            if (!_hasFirstRendered && rendered)
            {
                if(!IsServer)
                    FirstRenderServerRpc();
                
                onFirstRender?.Invoke();
                 _hasFirstRendered = true;
            }
            
            _rendered = rendered;
            OnRenderStateChange(rendered);
            onRenderStateChange?.Invoke(rendered);

             foreach (var roomRenderer in GetComponentsInChildren<Renderer>())
             {
                 roomRenderer.enabled = rendered;
             }
            
             foreach (var roomLight in GetComponentsInChildren<Light>())
             {
                 roomLight.enabled = rendered;
             }
            
             foreach (var ches in _chests)
             {
                 foreach (var chestRenderer in ches.GetComponentsInChildren<Renderer>())
                 {
                     chestRenderer.enabled = rendered;
                 }
             }
            
             foreach (RaidRoomDoorway door in doors)
             {
                 door.UpdateRenderState();
             }
        }

        public bool IsInRoom(Vector3 position)
        {
            var bounds = roomEntryCollider.bounds;
            return (bounds.Contains(position));
        }
        
        private void OnRenderStateChange(bool rendered)
        {
            foreach (var enemy in _currentEnemies)
            {
                enemy.SetRendered(rendered);
            }
        }


        // Players - Thomas Carey
        public UnityEvent onPlayerEnter;
        public UnityEvent onPlayerExit;

        private readonly List<NetworkedPlayer> _players = new();
        public IReadOnlyList<NetworkedPlayer> Players => _players;
    
        public void OnPlayerEnter(NetworkedPlayer player)
        {
            if (NetworkManager.Singleton.IsServer)
            {
                if (!GetComponent<RaidRoomController>())
                {
                    //Default behaviour when no controller is present
                    ResetDoorState();
                }
            }

            onPlayerEnter.Invoke();
            _players.Add(player);

            //Handle room rendering

            HashSet<RaidRoomManager>
                nextRenderedRooms = new();

            if (player.IsOwner)
            {
                foreach (var adjacentRoom in doors.SelectMany(
                             door => door.rooms))
                {
                    nextRenderedRooms.Add(adjacentRoom);
                }

                foreach (var room in _renderedRooms.Where(room =>
                             !nextRenderedRooms.Contains(room)))
                {
                    room.SetRendered(false);
                }

                foreach (var room in nextRenderedRooms)
                {
                    if (!_renderedRooms.Contains(room))
                    {
                        room.SetRendered(true);
                    }
                }

                _renderedRooms = nextRenderedRooms;
            }
        }

        public void OnPlayerExit(NetworkedPlayer player)
        {
            _players.Remove(player);
        }


        // Chests - Thomas Carey
        public IReadOnlyList<Chest> Chests => _chests;
        private readonly List<Chest> _chests = new();
        
        public void RegisterChest(Chest chest)
        {
            _chests.Add(chest);
        }


        // Testing - Joshua Fratis & Thomas Carey
        private static readonly SteelpunkLogger.LoggerInstance Logger =
            new (SteelpunkLogger.LogCategory.Rooms);

        public Counter EnemyCounter = new Counter();

        public class Counter
        {
            // TODO: turn this into a static utility system used by room spawner to name rooms, enemy spawners to name enemies
            private int _count;
            public string Count {
                get
                {
                    var result = "";
                    for (int i = 0; i < 3 - _count.ToString().Length; i++)
                    {
                        result += "0";
                    }

                    result += _count.ToString();
                    _count++;
                    return result;
                }
            }
        }
    }
}
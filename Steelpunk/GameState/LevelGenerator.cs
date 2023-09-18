/* This script was written entirely by Joshua Fratis */

#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Game;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Events;
using Quaternion = UnityEngine.Quaternion;
using Random = UnityEngine.Random;
using Vector3 = UnityEngine.Vector3;

namespace GameState
{
    public class LevelGenerator : MonoBehaviour
    {
        [SerializeField] private Transform levelParent = null!;
        [SerializeField] private bool doGenerate = true;

        [SerializeField]
        private LevelGenConfigScriptableObject levelGenConfig = null!;

        private Vector2Int _mapCenter;

        private List<RaidRoomManager> rooms = new();
        private List<RaidRoomDoorway> doors = new();

        private readonly bool[,] _map = new bool[100, 100];
        private readonly Dictionary<Vector3, Room> _rooms = new();
        private readonly Dictionary<Vector3, Door> _doors = new();

        public UnityEvent onGenerationComplete = null!;

        /*
         * List of doors connected to one room, adjacent to which another room could be spawned.
         * Not actually instantiated game objects, only abstract representations of doors.
         * Instantiated when both rooms are spawned.
         * Once generation is complete, all free doors are turned into walls. 
         */
        private readonly List<Door> _freeDoors = new();

        /*
         * List of doors between two rooms, but which were not selected to be spawned by the algorithm.
         * Once generation is complete, a random subset of these doors are spawned, while the remainder are turned into walls.
         * Spawning a door essentially creates a cycle in the graph of rooms, which would otherwise be a tree.
         */
        private readonly List<Door> _possibleDoors = new();
        private List<Door> _selectableDoors = new();

        [SerializeField] private bool scripted;
        [SerializeField] private bool networked;
        [SerializeField] private bool debugging;
        [SerializeField] private bool writeToFile;
        [SerializeField] private float worldScale = 1f;
        [SerializeField] private float cycleProbability = 0.1f;
        [SerializeField] private float depthWeight;
        [SerializeField] private float timeBetweenSpawns;
        [SerializeField] private int spawnQuota;
        [SerializeField] private Plane plane;

        [SerializeField] private GameObject wallPrefab = null!;
        [SerializeField] private GameObject pathDoorPrefab = null!;

        private RoomConfig[] _allRoomConfigs = null!;
        private List<RoomConfig> _validRoomConfigs = null!;

        private List<RoomConfig>
            _selectableRoomConfigs =
                null!; // Valid Configs minus those that are not selectable at the depth of a selected free door

        private int[] _roomConfigInstances = null!;

        private int _roomsSpawned;

        private enum Plane
        {
            XY,
            XZ
        }

        public enum Face
        {
            North,
            East,
            South,
            West
        }

        private struct RoomConfig
        {
            public Vector2Int[] Tiles;
            public DoorConfig[] DoorConfigs;
            public LevelGenConfigScriptableObject.RoomConfig Data;
            public int Instances;
        }

        public struct DoorConfig
        {
            public Vector2Int LocalTile;
            public Face Face;
            public LevelGenConfigScriptableObject.DoorConfig Data;
        }

        private struct DoorLocal
        {
            public Vector2Int
                ToAdjacentRoom; // this is the location of this door's room relative to the adjacent room's location

            public Vector3 ToTile; // Direction from center of tile
            public Quaternion Rotation;
        }

        public struct DoorGlobal
        {
            public Vector3 Position;
            public Quaternion Rotation;
        }

        public class Room
        {
            public Vector2Int Coords;
            public float Rotation;
            public int Depth;
            public readonly List<Door> Doors;
            public readonly RaidRoomManager Script;

            public Room(Vector2Int coords, int depth, GameObject gameObject)
            {
                Coords = coords;
                Depth = depth;
                Doors = new List<Door>();
                Script = gameObject.GetComponent<RaidRoomManager>();
            }

            public void Print()
            {
                WriteToFile("\tRoom:");

                WriteToFile("\t\tCoords: " + Coords);

                WriteToFile("\t\tDoors: ");
                foreach (var door in Doors)
                {
                    door?.PrintIndented();
                }
            }

            public void PrintIndented()
            {
                WriteToFile("\t\t\tRoom:");
                WriteToFile("\t\t\t\tCoords: " + Coords);
            }
        }

        public class Door
        {
            public readonly Room?[] Rooms; // should always be size 2
            public readonly DoorConfig Config;
            public DoorGlobal Global;

            public Door(Room room, DoorConfig config,
                DoorGlobal global)
            {
                Rooms = new[] { room, null };
                Config = config;
                Global = global;
            }

            public void Print()
            {
                WriteToFile("\tDoor:");

                WriteToFile("\t\tRooms:");
                foreach (var room in Rooms)
                {
                    room?.PrintIndented();
                }
            }

            public void PrintIndented()
            {
                WriteToFile("\t\t\tDoor:");
            }
        }

        private void Awake()
        {
            _mapCenter = new Vector2Int(_map.GetLength(0) / 2,
                _map.GetLength(1) / 2);

            _roomConfigInstances = new int[levelGenConfig.roomConfigs.Length];
            _allRoomConfigs = new RoomConfig[levelGenConfig.roomConfigs.Length];
            _selectableRoomConfigs =
                new List<RoomConfig>();
            _validRoomConfigs = new List<RoomConfig>();

            // Construct copy of room configs
            for (var i = 0; i < levelGenConfig.roomConfigs.Length; i++)
            {
                var roomConfig = levelGenConfig.roomConfigs[i];

                // Copy Tiles
                var tilesCopy = new Vector2Int[roomConfig.tiles.Length];
                for (var ii = 0; ii < roomConfig.tiles.Length; ii++)
                {
                    tilesCopy[ii] = new Vector2Int(roomConfig.tiles[ii].x,
                        roomConfig.tiles[ii].y);
                }

                // Copy Doors
                var doorConfigsCopy =
                    new DoorConfig[roomConfig.doorConfigs.Length];
                for (var ii = 0; ii < roomConfig.doorConfigs.Length; ii++)
                {
                    var doorConfigCopy = new DoorConfig
                    {
                        Data = roomConfig.doorConfigs[ii],
                        LocalTile = roomConfig.doorConfigs[ii].localTile,
                        Face = (Face)roomConfig.doorConfigs[ii].face
                    };
                    doorConfigsCopy[ii] = (doorConfigCopy);
                }

                // Construct Copy
                var roomConfigCopy = new RoomConfig
                {
                    Data = roomConfig,
                    Tiles = tilesCopy,
                    DoorConfigs = doorConfigsCopy
                };

                // Check for errors in spawning specifications
                if (roomConfigCopy.Data.selectable)
                {
                    if (roomConfigCopy.Data.selectionWeight <= 0)
                    {
                        Debug.LogWarning("Room config " +
                                         roomConfigCopy.Data.name +
                                         " has selectionWeight <= 0.");
                        roomConfigCopy.Data.selectable = false;
                    }

                    if ((roomConfigCopy.Data.minInstances > 0) &&
                        (roomConfigCopy.Data.maxInstances > 0) &&
                        (roomConfigCopy.Data.minInstances >
                         roomConfigCopy.Data.maxInstances))
                    {
                        Debug.LogWarning("Room config " +
                                         roomConfigCopy.Data.name +
                                         " has minInstances > maxInstances.");
                        roomConfigCopy.Data.selectable = false;
                    }

                    if (roomConfigCopy.Data.minDepth >
                        roomConfigCopy.Data.maxDepth)
                    {
                        Debug.LogWarning("Room config " +
                                         roomConfigCopy.Data.name +
                                         " has minDepth > maxDepth.");
                        roomConfigCopy.Data.selectable = false;
                    }
                }

                // Put copy in level generator's array
                _allRoomConfigs[i] = roomConfigCopy;
            }
        }

        // Publicly Accessible Functions =========================================================================================
        private void SpawnFirstRoom()
        {
            var firstRoomConfig = _allRoomConfigs[0];
            var firstRoom = SpawnRoom(firstRoomConfig, Vector2Int.zero, 0f, 0);
            FindDoors(firstRoom, firstRoomConfig, null);
        }

        public void DoGeneration()
        {
            if (!doGenerate) return;

            // GenerateLevel();
            StartCoroutine(GenerateLevel());
        }

        // Main Level Generation Routine =========================================================================================
        //private void GenerateLevel()

        private IEnumerator GenerateLevel()
        {
            RaidRoomManager.InitializeRooms();

            SpawnFirstRoom();
            // Main Generation Routine
            var spawns = 0;
            var safety = 0;
            var safetyQuota = spawnQuota * 10;
            while ((spawns < spawnQuota) && (safety < safetyQuota))
            {
                safety++;
                _validRoomConfigs = GetValidRoomConfigsFrom(_allRoomConfigs);

                if (GenerateRoom() == null)
                {
                    break; // completing generation is impossible
                }

                spawns++;
                yield return new WaitForSeconds(timeBetweenSpawns);
            }

            if (safety >= 1000) Debug.LogWarning("Main Generation timed out");

            Debug.Log(spawns >= spawnQuota
                ? "Spawn quota met"
                : "Spawn quota could not be met.");

            _roomsSpawned = spawns;


            MeetMinInstances();
            SelectPossibleDoors();
            // StartCoroutine(SelectPossibleDoorsRoutine());
            ReplaceDeadDoors();

            // Write Data Structures to File
            if (writeToFile) PrintDataStructures();

            if (debugging)
                Debug.Log("Finished Generating.");

            onGenerationComplete.Invoke();

            foreach (RaidRoomDoorway doorway in doors)
            {
                doorway.ReceiveRoomsClientRpc(doorway.rooms
                    .Select(room => new NetworkBehaviourReference(room))
                    .ToArray());
            }

            foreach (RaidRoomManager room in rooms)
            {
                room.ReceiveDoorsClientRpc(room.doors
                    .Select(door => new NetworkBehaviourReference(door))
                    .ToArray());
            }
        }

        private RoomConfig? GenerateRoom()
        {
            var spawned = false;

            ShuffleList(_freeDoors);
            SortFreeDoorsByDepth();
            var doorSafety = 0;
            while ((_selectableDoors.Count > 0) && (doorSafety < 1000))
            {
                doorSafety++;
                var freeDoor = SelectDoorByDepth();

                // Validate Room Configs
                var freeRoom = freeDoor.Rooms[0];
                if (freeRoom == null)
                {
                    Debug.LogWarning("Selected free door has no room");
                    continue;
                }

                var freeDoorDepth = freeRoom.Depth;

                _selectableRoomConfigs =
                    GetValidRoomConfigsAtDepth(_validRoomConfigs,
                        freeDoorDepth + 1);

                var roomSafety = 0;
                while ((_selectableRoomConfigs.Count > 0) &&
                       (roomSafety < 1000))
                {
                    roomSafety++;

                    // Select Room Config
                    ShuffleList(_selectableRoomConfigs);
                    var selectedRoomConfig = SelectRoomConfigByWeight();
                    if (selectedRoomConfig != null)
                    {
                        var roomConfig = (RoomConfig)selectedRoomConfig;

                        ShuffleList(roomConfig.DoorConfigs);
                        for (var d = 0;
                             d < roomConfig.DoorConfigs.Length;
                             d++)
                        {
                            var doorConfig = roomConfig.DoorConfigs[d];

                            // Determine rotations required to match doors
                            var rotations =
                                GetRotationsToMatchDoors(freeDoor,
                                    doorConfig);

                            // Rotate Room Config
                            for (var ii = 0; ii < rotations; ii++)
                                RotateRoom(roomConfig);
                            doorConfig =
                                roomConfig.DoorConfigs
                                    [d]; // reacquire rotated door config

                            // Calculate position
                            var doorLocal = GetDoorLocal(doorConfig);
                            var newRoomOriginTile = freeRoom.Coords +
                                                    freeDoor.Config.LocalTile -
                                                    doorLocal.ToAdjacentRoom -
                                                    doorConfig.LocalTile;

                            // Check for occlusion
                            var occluded = CheckForOcclusion(roomConfig,
                                newRoomOriginTile);

                            if (!occluded)
                            {
                                // Spawn Room & Doors
                                var newRoom = SpawnRoom(roomConfig,
                                    newRoomOriginTile, rotations * 90,
                                    freeDoorDepth + 1);

                                newRoom.Doors.Add(freeDoor);

                                freeDoor.Rooms[1] = newRoom;
                                var doorScript = SpawnDoor(freeDoor, true);
                                if (scripted)
                                    newRoom.Script.Doors.Add(doorScript);
                                _freeDoors.Remove(freeDoor);

                                FindDoors(newRoom, roomConfig, freeDoor);

                                spawned = true;
                            }

                            // Rotate Room Config Back
                            // required to re-match config to un-rotated prefab
                            for (var ii = rotations; ii < 4; ii++)
                                RotateRoom(roomConfig);

                            if (spawned) return roomConfig;
                        }

                        _selectableRoomConfigs
                            .Remove(
                                roomConfig); // remove config from pool to be selected by weight to avoid infinite loop
                    }
                }

                if (roomSafety >= 1000)
                    Debug.LogWarning("Room Selection timed out");

                if (debugging)
                    Debug.Log("Could not spawn door at " +
                              freeDoor.Global.Position + " (depth: " +
                              freeDoorDepth +
                              ". Removing from Selectable Doors.");
                _selectableDoors.Remove(freeDoor);
            }

            if (doorSafety >= 1000)
                Debug.LogWarning("Door Selection timed out");

            var reason = "";
            if (_freeDoors.Count <= 0) reason += "No free doors left; ";
            if (_validRoomConfigs.Count <= 0)
                reason += "No valid room configs left; ";
            if (_selectableRoomConfigs.Count <= 0)
                reason += "No room configs valid at depths of free doors; ";
            if (reason == "") reason = "Unknown";
            Debug.LogWarning("Generation failed. Reason(s): " + reason);
            return null;
        }

        // Cleanup Routines ====================================================================================================
        // Minimum Instances
        private void MeetMinInstances()
        {
            /* This method will only spawn extra rooms as leafs on the tree.
             Either it will only spawn rooms that have no depth specification 
             or happen to be specified for the height of the tree. 
             It also violates the spawn quota. 
             A better method may be to replace existing rooms. 
             
             Replacing existing rooms:
                 Traverse to a room that satisfies all conditions. 
                 Check that removing room would not violate that room's conditions (min instances). 
                 Check that room is not an articulation point. 
                    An exception may exist if the room-to-spawn is the same size as the room-to-remove
                    and has matching door configs. 
                    Or if another room could be spawned to fill the gap.
                    In order for this to work, this method would need to be called after cycle selection.
                Then replace the room. 
                    Destroy the room-to-remove. 
                    Instantiate the room-to-spawn. 
                    Update the data structures, including the doors' references to the rooms. 
             */

            _selectableRoomConfigs = GetValidRoomConfigsFrom(
                GetRoomConfigsUnderMinInstances(_selectableRoomConfigs));

            var safety = 0;
            while ((_selectableRoomConfigs.Count > 0) && (safety < 100))
            {
                if (debugging)
                    Debug.Log("Spawning min instances. Iteration " + safety +
                              ".");
                safety++;

                var generatedRoom = GenerateRoom();
                if (generatedRoom == null) break;

                _selectableRoomConfigs = GetValidRoomConfigsFrom(
                    GetRoomConfigsUnderMinInstances(_selectableRoomConfigs));
            }

            if (safety >= 100)
                Debug.LogWarning("Spawning min instances timed out");

            if (debugging)
            {
                foreach (var roomConfig in _allRoomConfigs)
                {
                    if (!roomConfig.Data.selectable) continue;
                    var validity = true;

                    if ((roomConfig.Data.minInstances > 0) &&
                        (_roomConfigInstances[roomConfig.Data.index] <
                         roomConfig.Data.minInstances)) validity = false;
                    if ((roomConfig.Data.maxInstances > 0) &&
                        (_roomConfigInstances[roomConfig.Data.index] >
                         roomConfig.Data.maxInstances)) validity = false;

                    var validityString =
                        validity ? " (Correct) " : " (Incorrect) ";
                    Debug.Log("Instances of " + roomConfig.Data.name + ": " +
                              _roomConfigInstances[roomConfig.Data.index]
                              + " (" +
                              ((float)_roomConfigInstances[
                                  roomConfig.Data.index] / _roomsSpawned) *
                              100 + "%)"
                              + validityString);
                }
            }
        }

        // Cycle Selection
        private IEnumerator SelectPossibleDoorsRoutine()
        {
            foreach (var possibleDoor in _possibleDoors)
            {
                SelectPossibleDoors(possibleDoor);
                yield return new WaitForSeconds(timeBetweenSpawns);
            }

            _possibleDoors.Clear();
            ReplaceDeadDoors();
        }

        private void SelectPossibleDoors()
        {
            foreach (var possibleDoor in _possibleDoors)
            {
                SelectPossibleDoors(possibleDoor);
            }

            _possibleDoors.Clear();
        }

        private void SelectPossibleDoors(Door possibleDoor)
        {
            // random chance of selecting door to spawn
            if (Random.Range(0f, 1f) <= cycleProbability)
            {
                SpawnDoor(possibleDoor, false);
                /* now the depth of the room and all its children may be wrong
                we don't need it anymore, but it's worth remembering in case we ever do */
            }
            else
            {
                var rot = possibleDoor.Global.Rotation;
                SpawnWall(possibleDoor.Global.Position, rot);

                rot *= plane switch
                {
                    Plane.XY => Quaternion.Euler(Vector3.forward * 180),
                    Plane.XZ => Quaternion.Euler(Vector3.up * 180),
                    _ => throw new ArgumentOutOfRangeException()
                };

                SpawnWall(possibleDoor.Global.Position, rot);
                _doors.Remove(possibleDoor.Global.Position);
            }
        }

        // Replace Dead Doors
        private void ReplaceDeadDoors()
        {
            // Replace One-Roomed Doors with Walls
            if (wallPrefab == null) return;
            foreach (var door in _freeDoors)
            {
                if (debugging)
                    Debug.Log("Replacing door at " + door.Global.Position);

                // Double check that door only has one room
                if (door.Rooms[1] != null) continue;

                // Remove door from its one room's list of doors
                var roomDoors = door.Rooms[0].Doors;
                for (var i = 0; i < roomDoors.Count; i++)
                    if (roomDoors[i] == door)
                        roomDoors[i] = null;

                // Remove door from LevelGen's dictionary of doors
                var doorGlobal = door.Global;
                _doors.Remove(doorGlobal.Position);

                // Replace door with wall
                SpawnWall(doorGlobal.Position, doorGlobal.Rotation);
            }

            _freeDoors.Clear();
        }

        // Private Helper Functions for Generation Algorithm =========================================================================================
        // Validate Room Configs
        private List<RoomConfig> GetValidRoomConfigsFrom(
            ICollection<RoomConfig> roomConfigs)
        {
            var tempRoomConfigs = new List<RoomConfig>();
            foreach (var roomConfig in roomConfigs)
            {
                var valid = roomConfig.Data.selectable;

                if ((roomConfig.Data.maxInstances > 0) &&
                    (_roomConfigInstances[roomConfig.Data.index] >=
                     roomConfig.Data.maxInstances)) valid = false;

                if (valid) tempRoomConfigs.Add(roomConfig);
            }

            return tempRoomConfigs;
        }

        private List<RoomConfig> GetValidRoomConfigsAtDepth(
            List<RoomConfig> roomConfigs, int depth)
        {
            var validRoomConfigs = new List<RoomConfig>();
            foreach (var roomConfig in roomConfigs)
            {
                if (((roomConfig.Data.minDepth > 0) &&
                     (depth < roomConfig.Data.minDepth)) ||
                    (roomConfig.Data.maxDepth > 0) &&
                    (depth > roomConfig.Data.maxDepth))
                {
                    if (debugging)
                        Debug.Log("Room Config " + roomConfig.Data.name + " (" +
                                  roomConfig.Data.minDepth + ", " +
                                  roomConfig.Data.maxDepth +
                                  ") NOT valid at depth " + depth + ".");
                    continue;
                }

                if (debugging)
                    Debug.Log("Room Config " + roomConfig.Data.name + " (" +
                              roomConfig.Data.minDepth + ", " +
                              roomConfig.Data.maxDepth +
                              ") IS valid at depth " + depth + ".");
                validRoomConfigs.Add(roomConfig);
            }

            return validRoomConfigs;
        }

        private List<RoomConfig> GetRoomConfigsUnderMinInstances(
            List<RoomConfig> roomConfigs)
        {
            var unmetRoomConfigs = new List<RoomConfig>();
            foreach (var roomConfig in roomConfigs)
            {
                if (_roomConfigInstances[roomConfig.Data.index] <
                    roomConfig.Data.minInstances)
                {
                    unmetRoomConfigs.Add(roomConfig);
                }
            }

            return unmetRoomConfigs;
        }

        // Selection Weight
        private float SumSelectionWeights()
        {
            float sum = 0;
            foreach (var roomConfig in _selectableRoomConfigs)
            {
                sum += roomConfig.Data.selectionWeight;
            }

            if (debugging) Debug.Log("Sum of Selection Weights: " + sum + ".");
            return sum;
        }

        private RoomConfig? SelectRoomConfigByWeight()
        {
            float selectedWeight = Random.Range(0, SumSelectionWeights());
            float runningSum = 0;

            if (debugging) Debug.Log("Selected Weight: " + selectedWeight);

            foreach (var roomConfig in _selectableRoomConfigs)
            {
                runningSum += roomConfig.Data.selectionWeight;
                if (debugging)
                {
                    Debug.Log("Room Config: " + roomConfig.Data.name +
                              ". Weight: " + roomConfig.Data.selectionWeight);
                    Debug.Log("Running Sum: " + runningSum);
                }

                if (runningSum > selectedWeight)
                {
                    if (debugging)
                        Debug.Log("Selected " + roomConfig.Data.name);
                    return roomConfig;
                }
            }

            return null;
        }

        // Selection by Depth
        private void SortFreeDoorsByDepth()
        {
            _selectableDoors = new List<Door>(_freeDoors);
            _selectableDoors.Sort((x, y) =>
                x.Rooms[0].Depth.CompareTo(y.Rooms[0].Depth));
        }

        private Door SelectDoorByDepth()
        {
            int i = SampleParetoDistribution(0, _selectableDoors.Count);
            int depth = _selectableDoors[i].Rooms[0].Depth;
            if (debugging)
                Debug.Log("Selecting door at index " + i + " with depth " +
                          depth);
            return _selectableDoors[i];
        }

        // Min inclusive, Max exclusive
        private int SampleParetoDistribution(int min, int max)
        {
            double x = Random.Range(0f, 1f);
            double y = 1 - Math.Pow((1 / (x + 1)), depthWeight);
            int range = max - min;
            int i = (int)Math.Floor(y * range) + min;

            if (i >= max) i = max - 1;
            else if (i <= min) i = min;
            if (debugging)
            {
                Debug.Log("Random x (0, 1): " + x);
                Debug.Log("Distributed y: " + y);
                Debug.Log("Range: " + range + " (" + min + ", " + max + ")");
                Debug.Log("Returning index " + i);
            }

            return i;
        }

        // Shuffle Lists
        public static void ShuffleList<T>(IList<T> list)
        {
            for (var i = 0; i < list.Count; i++)
            {
                var j = Random.Range(0, list.Count);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        private static void ShuffleList<T>(T[] list)
        {
            for (var i = 0; i < list.Length; i++)
            {
                var j = Random.Range(0, list.Length);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        // Rotation
        // Returns the number of rotations (clockwise 90 degrees) required to match a given door config to a given free door
        private int GetRotationsToMatchDoors(Door freeDoor,
            DoorConfig doorConfig)
        {
            var matchingFace = (Face)(((int)freeDoor.Config.Face + 2) % 4);
            var rotations = (((int)matchingFace - (int)doorConfig.Face) % 4);
            if (rotations < 0) rotations += 4;
            return rotations;
        }

        // Rotates a room configuration clockwise around its origin by 90 degrees
        private void RotateRoom(RoomConfig roomConfig)
        {
            // Rotate the room config's tiles
            for (var i = 0; i < roomConfig.Tiles.Length; i++)
            {
                roomConfig.Tiles[i] = new Vector2Int(roomConfig.Tiles[i].y,
                    -roomConfig.Tiles[i].x);
            }

            // Rotate the local tiles and faces of each of the room config's door configs
            for (var i = 0; i < roomConfig.DoorConfigs.Length; i++)
            {
                roomConfig.DoorConfigs[i].LocalTile = new Vector2Int(
                    roomConfig.DoorConfigs[i].LocalTile.y,
                    -roomConfig.DoorConfigs[i].LocalTile.x);
                roomConfig.DoorConfigs[i].Face =
                    (Face)(((int)roomConfig
                        .DoorConfigs[i].Face + 1) % 4);
            }
        }

        // Positioning
        private DoorLocal GetDoorLocal(DoorConfig doorConfig)
        {
            var doorLocal = new DoorLocal();

            switch (plane)
            {
                case Plane.XY:
                    switch (doorConfig.Face)
                    {
                        case Face.North:
                            doorLocal.ToAdjacentRoom = Vector2Int.up;
                            doorLocal.ToTile = Vector3.up;
                            doorLocal.Rotation =
                                Quaternion.Euler(new Vector3(0, 0, 0));
                            break;
                        case Face.East:
                            doorLocal.ToAdjacentRoom = Vector2Int.right;
                            doorLocal.ToTile = Vector3.right;
                            doorLocal.Rotation =
                                Quaternion.Euler(new Vector3(0, 0, 90));
                            break;
                        case Face.South:
                            doorLocal.ToAdjacentRoom = Vector2Int.down;
                            doorLocal.ToTile = Vector3.down;
                            doorLocal.Rotation =
                                Quaternion.Euler(new Vector3(0, 0, 180));
                            break;
                        case Face.West:
                            doorLocal.ToAdjacentRoom = Vector2Int.left;
                            doorLocal.ToTile = Vector3.left;
                            doorLocal.Rotation =
                                Quaternion.Euler(new Vector3(0, 0, 270));
                            break;
                        default:
                            doorLocal.ToAdjacentRoom = Vector2Int.zero;
                            doorLocal.ToTile = Vector3.zero;
                            doorLocal.Rotation =
                                Quaternion.Euler(Vector3.zero);
                            throw new ArgumentOutOfRangeException();
                    }

                    break;

                case Plane.XZ:
                    switch (doorConfig.Face)
                    {
                        case Face.North:
                            doorLocal.ToAdjacentRoom = Vector2Int.up;
                            doorLocal.ToTile = Vector3.forward;
                            doorLocal.Rotation =
                                Quaternion.Euler(new Vector3(0, -90, 0));
                            break;
                        case Face.East:
                            doorLocal.ToAdjacentRoom = Vector2Int.right;
                            doorLocal.ToTile = Vector3.right;
                            doorLocal.Rotation =
                                Quaternion.Euler(new Vector3(0, 0, 0));
                            break;
                        case Face.South:
                            doorLocal.ToAdjacentRoom = Vector2Int.down;
                            doorLocal.ToTile = Vector3.back;
                            doorLocal.Rotation =
                                Quaternion.Euler(new Vector3(0, 180, 0));
                            break;
                        case Face.West:
                            doorLocal.ToAdjacentRoom = Vector2Int.left;
                            doorLocal.ToTile = Vector3.left;
                            doorLocal.Rotation =
                                Quaternion.Euler(new Vector3(0, 270, 0));
                            break;
                        default:
                            doorLocal.ToAdjacentRoom = Vector2Int.zero;
                            doorLocal.ToTile = Vector3.zero;
                            doorLocal.Rotation =
                                Quaternion.Euler(Vector3.zero);
                            throw new ArgumentOutOfRangeException();
                    }

                    break;

                default:
                    doorLocal.ToAdjacentRoom = Vector2Int.zero;
                    doorLocal.ToTile = Vector3.zero;
                    doorLocal.Rotation = Quaternion.Euler(Vector3.zero);
                    throw new ArgumentOutOfRangeException();
            }

            return doorLocal;
        }

        private DoorGlobal GetDoorGlobal(Room room, DoorConfig doorConfig)
        {
            var doorGlobal = new DoorGlobal();
            var doorLocal = GetDoorLocal(doorConfig);

            var globalTile = room.Coords + doorConfig.LocalTile;
            var worldTilePos = GetWorldPos(globalTile);
            var worldOffsetToTile = doorLocal.ToTile * worldScale / 2;
            doorGlobal.Position = worldTilePos + worldOffsetToTile;

            doorGlobal.Rotation = plane switch
            {
                Plane.XY => Quaternion.Euler(0, 0,
                    ((int)doorConfig.Face * 90) + room.Rotation),
                Plane.XZ => Quaternion.Euler(0,
                    (((int)doorConfig.Face + 1) * 90) + room.Rotation, 0),
                _ => Quaternion.identity
            };

            return doorGlobal;
        }

        private Vector3 GetWorldPos(Vector2Int globalTile)
        {
            var worldPos = plane switch
            {
                Plane.XY => new Vector3(globalTile.x, globalTile.y, 0) *
                            worldScale,
                Plane.XZ => new Vector3(globalTile.x, 0, globalTile.y) *
                            worldScale,
                _ => Vector3.zero
            };

            return worldPos;
        }

        private Quaternion GetWorldRotation(float rotation)
        {
            Quaternion worldRotation = plane switch
            {
                Plane.XY => Quaternion.Euler(0, 0, -rotation),
                Plane.XZ => Quaternion.Euler(0, rotation, 0),
                _ => Quaternion.identity
            };

            return worldRotation;
        }

        // Occlusion
        private bool CheckForOcclusion(RoomConfig roomConfig,
            Vector2Int newRoomOriginTile)
        {
            var occluded = false;
            foreach (var tile in roomConfig.Tiles)
            {
                if (!_map[
                        newRoomOriginTile.x + _mapCenter.x +
                        tile.x,
                        newRoomOriginTile.y + _mapCenter.y +
                        tile.y]) continue;

                occluded = true;
            }

            return occluded;
        }

        // Instantiation
        private GameObject InstantiateLocal(GameObject prefab, Vector3 position,
            Quaternion rotation)
        {
            var newObject = Instantiate(prefab,
                levelParent);

            newObject.transform.localPosition = position;
            newObject.transform.localRotation = rotation;

            if (networked) newObject.GetComponent<NetworkObject>().Spawn();

            return newObject;
        }

        private Room SpawnRoom(RoomConfig roomConfig,
            Vector2Int originTile, float rotation, int depth)
        {
            // Get Position
            var worldPos = GetWorldPos(originTile);
            var mapCoords = new Vector2Int(originTile.x + _mapCenter.x,
                originTile.y + _mapCenter.y);

            // Instantiate & Transform
            var worldRotation = GetWorldRotation(rotation);
            var newRoomGameObject = InstantiateLocal(roomConfig.Data.prefab,
                worldPos, worldRotation);

            // Initialize
            RaidRoomManager newRoomScript;
            newRoomScript =
                newRoomGameObject.GetComponent<RaidRoomManager>();
            if ((scripted) && (newRoomScript != null))
            {
                newRoomScript.Initialize(this, depth == 0);
                rooms.Add(newRoomScript);
            }


            // Update Map
            foreach (var tile in roomConfig.Tiles)
            {
                _map[mapCoords.x + tile.x, mapCoords.y + tile.y] = true;
            }

            // Track Instance
            _roomConfigInstances[roomConfig.Data.index]++;

            // Log New Room
            var newRoom = new Room(originTile, depth,
                newRoomGameObject);
            _rooms.Add(worldPos, newRoom);
            return newRoom;
        }

        private void FindDoors(Room room, RoomConfig roomConfig,
            Door? entryDoor)
        {
            foreach (var doorConfig in roomConfig.DoorConfigs)
            {
                var doorGlobal = GetDoorGlobal(room, doorConfig);
                if (entryDoor != null &&
                    doorGlobal.Position == entryDoor.Global.Position) continue;

                // Door exists at position
                if (_doors.ContainsKey(doorGlobal.Position))
                {
                    _freeDoors.Remove(_doors[doorGlobal.Position]);
                    _possibleDoors.Add(_doors[doorGlobal.Position]);

                    _doors[doorGlobal.Position].Rooms[1] = room;
                }

                // Position is empty
                else
                {
                    var newDoor = new Door(room, doorConfig, doorGlobal);
                    room.Doors.Add(newDoor);
                    _doors.Add(doorGlobal.Position, newDoor);
                    _freeDoors.Add(newDoor);
                }
            }
        }

        private RaidRoomDoorway SpawnDoor(Door door, bool isEntryDoor)
        {
            var room = door.Rooms[0];

            if (room is null)
            {
                throw new Exception("Doorway has no room in first index.");
            }

            var roomScript = room.Script;
            var doorConfig = door.Config;
            var doorGlobal = door.Global;
            var prefab = doorConfig.Data.prefab;
            if (isEntryDoor) prefab = pathDoorPrefab;

            // Spawn Door Object
            var newDoorGameObject =
                InstantiateLocal(prefab, doorGlobal.Position,
                    doorGlobal.Rotation);

            // Track Door Class
            room.Doors.Add(door);

            // Track Door Script
            var newDoorScript =
                newDoorGameObject.GetComponent<RaidRoomDoorway>();

            if (newDoorScript is null)
            {
                if (scripted)
                    throw new Exception(
                        "Doorway prefab does not have a RaidRoomDoorway script attached.");
            }
            else
            {
                doors.Add(newDoorScript);
                newDoorScript.rooms[0] = roomScript;
                newDoorScript.rooms[1] = door.Rooms[1]?.Script;
                if ((scripted) && (roomScript != null))
                    roomScript.doors.Add(newDoorScript);
            }

            return newDoorScript;
        }

        private void SpawnWall(Vector3 globalPos, Quaternion globalRotation)
        {
            InstantiateLocal(wallPrefab, globalPos, globalRotation);
        }

        // Functions for Printing to Output File =========================================================================================
        private void PrintDataStructures()
        {
            ClearFile();
            PrintMap();
            PrintRooms();
            PrintDoors();
        }

        private void PrintMap()
        {
            var writeRow = false;

            WriteToFile("MAP:");
            for (var y = 0; y < _map.GetLength(1); y++)
            {
                var line = "";
                for (var x = 0; x < _map.GetLength(0); x++)
                {
                    if (_map[x, y])
                    {
                        writeRow = true;
                        line += "+";
                    }
                    else line += " ";
                }

                if (writeRow) WriteToFile(line);
                writeRow = false;
            }

            WriteToFile("");
        }

        private void PrintRooms()
        {
            WriteToFile("ROOMS:");
            foreach (var room in _rooms)
            {
                room.Value.Print();
                WriteToFile("");
            }
        }

        private void PrintDoors()
        {
            WriteToFile("\nDOORS:");
            foreach (var door in _doors)
            {
                door.Value.Print();
                WriteToFile("");
            }
        }

        private static void ClearFile()
        {
            string path = Application.dataPath + "../LevelGeneratorOutput.txt";
            File.WriteAllText(path, "");
        }

        private static void WriteToFile(string str)
        {
            var path = Application.dataPath + "../LevelGeneratorOutput.txt";
            if (!File.Exists(path))
            {
                File.WriteAllText(path, "");
            }
            else
            {
                using (var writer = new StreamWriter(path, true))
                {
                    writer.WriteLine(str);
                }
            }
        }
    }
}
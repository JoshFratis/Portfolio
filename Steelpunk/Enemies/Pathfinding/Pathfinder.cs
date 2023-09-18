/* The entirety of this script was written by Joshua Fratis */

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Utility;
using Random = UnityEngine.Random;

namespace Enemies.Pathfinding
{
    public class Pathfinder: MonoBehaviour
    {
        // World Coordinates
        private int _xLower;
        private int _yLower;
        private int _zLower;
        private int _xUpper;
        private int _yUpper;
        private int _zUpper;
        public int resolution;
        
        // Map Coordinates
        public bool[,,] Map;
        public int xRange;
        public int yRange;
        public int zRange;
        
        // Settings
        private int _speed;
        
        // Debugging
        private static readonly SteelpunkLogger.LoggerInstance Logger =
            new (SteelpunkLogger.LogCategory.Navigation);
        
        private void DebugPoint(Vector3 point)
        {
            float x = point.x;
            float y = point.y;
            float z = point.z;
            
            Debug.DrawLine(new Vector3(x, -100000, z), new Vector3(x, 100000, z), Color.white, 3.0f);
            Debug.DrawLine(new Vector3(x, y, -100000), new Vector3(x, y, 100000), Color.white, 3.0f);
            Debug.DrawLine(new Vector3(-100000, y, z), new Vector3(100000, y, z), Color.white, 3.0f);
        }
        
        private IEnumerator TestFindClosestFreeRoutine()
        {
            while (true)
            {
                yield return new WaitForSeconds(3.0f);
                Vector3 randomWorld = GenerateWorldCoordinate(false);
                Vector3Int randomMap = WorldToMap(randomWorld);
                
                int i = 0;
                while (!Map[randomMap.x, randomMap.y, randomMap.z] && i < 10000)
                {
                    i++;
                    randomWorld = GenerateWorldCoordinate();
                    randomMap = WorldToMap(randomWorld);
                }

                if (i < 10000)
                {
                    Logger.Log("Finding the closest free to " + randomMap); 
                    FindClosestFree(randomWorld);
                }
                else
                {
                    Logger.Log("Error");
                }
            }
        }
        

        // Helpers (Coordinate System)
        public Vector3 MapToWorld(Vector3Int map)
        {
            int worldX = (map.x * resolution) + _xLower;
            int worldY = (map.y * resolution) + _yLower;
            int worldZ = (map.z * resolution) + _zLower;
            
            return new Vector3(worldX, worldY, worldZ);
        }

        public Vector3Int WorldToMap(Vector3 world)
        {
            int mapX = Math.Clamp(((int)world.x - _xLower) / resolution, 0, xRange);
            int mapY = Math.Clamp(((int)world.y - _yLower) / resolution, 0, yRange);
            int mapZ = Math.Clamp(((int)world.z - _zLower) / resolution, 0, zRange);

            return new Vector3Int(mapX, mapY, mapZ);
        }
        
        public bool IsInBounds(Vector3Int cell)
        {
            return ((cell.x >= 0) && (cell.y >= 0) && (cell.z >= 0) &&
                    (cell.x < xRange) && (cell.y < yRange) && (cell.z < zRange));
        }

        private bool IsObstructed(Vector3Int map)
        {
            if (!IsInBounds(map))
            {
                return true;
            }
            
            return Map[map.x, map.y, map.z];
        }
        
        public Vector3 GenerateWorldCoordinate(bool isOpen = true)
        {
            return MapToWorld(GenerateMapCoordinates(isOpen));
        }

        public Vector3Int GenerateMapCoordinates(bool isOpen = true)
        {
            if (isOpen)
            {
                Vector3Int coord = new Vector3Int(-1, -1, -1);
                while (IsObstructed(coord))
                {
                    coord = new Vector3Int(Random.Range(0, xRange), Random.Range(0, yRange), Random.Range(0, zRange));
                }
                return coord;
            }
            else
            {
                Vector3Int coord = GenerateMapCoordinates();
                while (!IsObstructed(coord))
                {
                    coord = new Vector3Int(Random.Range(0, xRange), Random.Range(0, yRange), Random.Range(0, zRange));
                }
                return coord;
            }
        }
        
        public Vector3? FindClosestFree(Vector3 world)
        {
            Vector3Int start = WorldToMap(world);

            Queue<Vector3Int> queue = new Queue<Vector3Int>();
            bool[,,] visited = new bool[xRange, yRange, zRange];

            queue.Enqueue(start);
            visited[start.x, start.y, start.z] = true;

            int[] dx = { 1, -1, 0, 0, 0, 0 };
            int[] dy = { 0, 0, 1, -1, 0, 0 };
            int[] dz = { 0, 0, 0, 0, 1, -1 };

            int s = 0;
            while ((queue.Count > 0) && (s < 10000))
            {
                s++;
                Vector3Int current = queue.Dequeue();

                if (!IsObstructed(current))
                {
                    return MapToWorld(current);
                }

                for (int i = 0; i < 6; i++)
                {
                    int newX = current.x + dx[i];
                    int newY = current.y + dy[i];
                    int newZ = current.z + dz[i];

                    Vector3Int coord = new Vector3Int(newX, newY, newZ);
                    if (!IsInBounds(coord)) continue;
                    
                    if (!visited[newX, newY, newZ])
                    {
                        queue.Enqueue(coord);
                        visited[newX, newY, newZ] = true;
                    }
                }
            }

            Logger.Log("No closest free found.");
            if (s >= 10000) Logger.Log("FindClosestFree timed out");
            return null; 
        }
        

        // Lifetime
        public void Initialize(GameObject room, float width, float height, float depth,
            int res, int speed)
        {
            resolution = res;
            _speed = speed; 
            
            // Requires room position root be at bottom center
            Vector3 roomPos = room.transform.position;
            _xLower = (int)(roomPos.x - (width / 2f)); 
            _yLower = (int)roomPos.y;
            _zLower = (int)(roomPos.z - (depth / 2f));
            _xUpper = (int)(roomPos.x + (width / 2f));
            _yUpper = (int)(roomPos.y + height);
            _zUpper = (int)(roomPos.z + (depth / 2f));
            
            xRange = (_xUpper - _xLower) / resolution;
            yRange = (_yUpper - _yLower) / resolution;
            zRange = (_zUpper - _zLower) / resolution;
            
            ConstructMap();
        }

        private void ConstructMap()
        {
            Map = new bool[xRange, yRange, zRange];
        
            Collider[] colliderBuffer = new Collider[1];
        
            for (int x = 0; x < xRange; x++)
            {
                for (int y = 0; y < yRange; y++)
                {
                    for (int z = 0; z < zRange; z++)
                    {
                        Vector3 pos = MapToWorld(new Vector3Int(x, y, z));
                        if (Physics.OverlapSphereNonAlloc(pos, resolution / 2.0f,
                                colliderBuffer, LayerMaskLibrary.Instance.environmentMask, QueryTriggerInteraction.Ignore) > 0)
                        {
                            Map[x, y, z] = true;
                        }
                        else
                        {
                            Map[x, y, z] = false;
                        }
                    }
                }
            }
        }
        
        
        // Pathfinding
        public IEnumerator FindPath(Navigator navigator, Vector3 start, Vector3 goal)
        {
            Vector3Int mapStart = WorldToMap(start);
            Vector3Int mapGoal = WorldToMap(goal);
            yield return navigator.AStarRoutine = StartCoroutine(AStar.FindPath(Map,
                mapStart, mapGoal, _speed,
                (nodePath) =>
                {
                    if (nodePath != null)
                    {
                        navigator.ReturnPath(TransformPath(nodePath));
                    }
                }));
        }

        public IEnumerator FindPath(DemoNavigator navigator, Vector3 start, Vector3 goal)
        {
            Vector3Int mapStart = WorldToMap(start);
            Vector3Int mapGoal = WorldToMap(goal);
            yield return StartCoroutine(AStar.FindPath(Map,
                mapStart, mapGoal, _speed,
                (nodePath) =>
                {
                    if (nodePath != null)
                    {
                        navigator.ReturnPath(TransformPath(nodePath));
                    }
                }));
        }

        private List<Vector3> TransformPath(List<Node> nodePath)
        {
            if (nodePath == null)
            {
                return null;
            } 

            List<Vector3> result = new List<Vector3>();
            foreach (Node node in nodePath)
            {
                result.Add(MapToWorld(node.Coord));
            }
            return result;
        }
        

        // A*
        class Node
        {
            public Vector3Int Coord;
            public bool IsPassable;
            public int G = Int32.MaxValue / 2; // Cost from start node to current node
            public float H; // Heuristic estimate of cost from current node to goal node
            public float F => G + H; // Total cost
            public Node Parent;
            public bool Open;
            public bool Closed;
            
            public Node(Vector3Int coord, bool isPassable)
            {
                Coord = coord;
                IsPassable = isPassable;
            }
        }

        class AStar : MonoBehaviour
        {
            public static IEnumerator FindPath(bool[,,] grid,
                Vector3Int start, Vector3Int goal,
                int speed, 
                Action<List<Node>> callback)
            {
                int sizeX = grid.GetLength(0);
                int sizeY = grid.GetLength(1);
                int sizeZ = grid.GetLength(2);
            
                Node startNode = new Node(start, true);
                Node goalNode = new Node(goal, true);

                Dictionary<Vector3Int, Node> nodes = new Dictionary<Vector3Int, Node>();
                PriorityQueue<Node> openPq = new PriorityQueue<Node>();

                startNode.G = 0;
                startNode.H = ManhattanDistance(startNode, goalNode);
                
                openPq.Enqueue(startNode, startNode.F);
                startNode.Open = true;
                nodes.Add(startNode.Coord, startNode);
                
                int iter = 0;
                while (openPq.Count > 0 && iter < 4000)
                {
                    iter++; 
                    if (openPq.Count > 8000) break;
                    if (iter % speed == 0) yield return null;
                
                    Node currentNode = openPq.Dequeue();
                    currentNode.Open = false;
                    currentNode.Closed = true;
                    
                    if (currentNode.Coord == goalNode.Coord)
                    {
                        callback(ConstructPath(currentNode));
                        yield break;
                    }

                    List<Node> neighbors = GetNeighbors(currentNode, sizeX, sizeY, sizeZ, grid, nodes); 

                    foreach (Node neighbor in neighbors)
                    {
                        if (neighbor.Closed || !neighbor.IsPassable) continue; 
                        
                        int tentativeG = currentNode.G + 1;
                    
                        if (!neighbor.Open)
                        {
                            neighbor.Parent = currentNode;
                            neighbor.G = tentativeG;
                            neighbor.H = ManhattanDistance(neighbor, goalNode);
                            openPq.Enqueue(neighbor, neighbor.F);
                            neighbor.Open = true;
                        } 
                        else if (tentativeG < neighbor.G)
                        {
                            neighbor.Parent = currentNode;
                            neighbor.G = tentativeG;
                            neighbor.H = ManhattanDistance(neighbor, goalNode);
                        }
                    }
                }

                callback(null); // No path found
            }

            private static List<Node> GetNeighbors(Node node, int sizeX, int sizeY, int sizeZ, bool[,,] grid, Dictionary<Vector3Int, Node> nodes)
            {
                List<Node> neighbors = new List<Node>();

                int[] dx = { -1, 0, 1, 0, 0, 0 };
                int[] dy = { 0, -1, 0, 1, 0, 0 };
                int[] dz = { 0, 0, 0, 0, -1, 1 };

                for (int i = 0; i < 6; i++)
                {
                    Vector3Int newCoord = new Vector3Int(
                        node.Coord.x + dx[i],
                        node.Coord.y + dy[i],
                        node.Coord.z + dz[i]
                    );
                    
                    if ((newCoord.x >= 0) && (newCoord.x < sizeX) && 
                        (newCoord.y >= 0) && (newCoord.y < sizeY) &&
                        (newCoord.z >= 0) && (newCoord.z < sizeZ))
                    {
                        if (nodes.TryGetValue(newCoord, out Node existingNode))
                        {
                            neighbors.Add(existingNode);
                        }
                        else
                        {
                            Node newNode = new Node(newCoord, !grid[newCoord.x, newCoord.y, newCoord.z]);
                            neighbors.Add(newNode);
                        }
                    } 
                }

                return neighbors;
            }

            private static List<Node> ConstructPath(Node node)
            {
                List<Node> path = new List<Node>();
                while (node != null)
                {
                    path.Add(node);
                    node = node.Parent;
                }
                path.Reverse();
                return path;
            }

            private static float ManhattanDistance(Node a, Node b)
            {
                return (Math.Abs(a.Coord.x - b.Coord.x) 
                        + Math.Abs(a.Coord.y - b.Coord.y) 
                        + Math.Abs(a.Coord.z - b.Coord.z)) 
                       - Random.Range(0, 10f);
            }
        }
    }
}

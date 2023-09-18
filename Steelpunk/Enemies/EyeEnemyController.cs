/* 
    The entierty of this script, apart from the "Attacking" 
    and "Damage & Death" sections were written by Joshua Fratis. 
*/

using System.Collections;
using System.Collections.Generic;
using Enemies.Pathfinding;
using FMOD.Studio;
using FMODUnity;
using Game;
using GameState;
using Networking;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.VFX;
using Utility;
using Random = UnityEngine.Random;
using STOP_MODE = FMOD.Studio.STOP_MODE;
using VFXManager = GameState.VFXManager;

namespace Enemies
{
    [RequireComponent(typeof(Enemy))]
    public class EyeEnemyController : NetworkBehaviour
    {
        [Header("Setup")] 
        [SerializeField] private GameObject projectile;
        [SerializeField] private Transform projectileSpawnPoint;
        [SerializeField] private float fireTime;
        [SerializeField] private bool onlyFiresWhileIdling;
        [SerializeField] private DamagePayload explodePayload;

        [SerializeField] private SphereCollider mainCollider;
        [SerializeField] private Animator animator;
        [SerializeField] private VisualEffect warmupEffect;
        [SerializeField] private VisualEffect smokingEffect;
        [SerializeField] private float eyeColorTransitionDuration = 10.0f;

        [SerializeField] private SkinnedMeshRenderer renderer;
        [SerializeField] private int eyeMaterialIndex;
        private static readonly int EmissionColor = Shader.PropertyToID("_EmissiveColor");
        
        [Header("Audio")] 
        [SerializeField] private EventReference warmupSound; 
        [SerializeField] private EventReference fireSound;

        private EventInstance _warmupSoundInstance;
        private EventInstance _fireSoundInstance;

        [Header("Config")] 
        [Header("Movement")]
        [SerializeField] private float movementSpeed = 15.0f;
        [SerializeField] private float maxHeight = 10.0f;
        [SerializeField] private float minHeight = 2.0f;

        [Header("Idle")] 
        [SerializeField] private float idleRate;
        [SerializeField] private float idleRateBase = 2.0f;
        [SerializeField] private float idleRateVariance = 0.2f;
        [SerializeField] private float idleVelocity;
        [SerializeField] private float idleVelocityBase = 1.0f;
        [SerializeField] private float idleVelocityVariance = 0.2f;
        
        private Vector3 _idleVector;
        private float _idleOffset;
        
        [Header("Circling")]
        [SerializeField] private float circlingSpeed = 1.0f;
        
        private Vector3 _circlingVelocity;

        [Header("Tracking")] 
        [SerializeField] private float trackingVelocity = 1.0f;
        [SerializeField] private float idlingLosAssessmentInterval = 3.0f;
        [SerializeField] private float trackingLosAssessmentInterval = 3.0f;
        [SerializeField] private float stallNavigationDuration = 0.2f;
        
        private float _losAssessmentInterval;
        private bool _hasLosOnTarget;
        
        [Header("Avoidance")]
        [SerializeField] private float avoidanceVelocity = 6.0f;
        [SerializeField] private float avoidanceDuration = 0.8f;
        [SerializeField] private float avoidanceLookAngleRange = 12.5f;
        [SerializeField] private float avoidanceReactionTime = 0.2f;

        private Vector3 _avoidanceVector;
        private NetworkedPlayer _playerAvoiding;
        
        [Header("Correction")]
        [SerializeField] private float correctionInertia = 6.0f;
        [SerializeField] private float correctionDuration = 0.8f;
        private Vector3 _correctionVector;
        private float _relativeHeight;
        
        // Movement
        private enum MovementState
        {
            Idling,
            Circling,
            Tracking,
            Avoiding,
            Correcting
        }

        private MovementState _movementState = MovementState.Idling;
        private bool _movementStateLocked;
        
        private readonly Color _idlingColor = new Color(231f/255f, 33f/255f, 0, 1);
        private readonly Color _trackingColor = new Color(0, 231f/255f, 130f/255f, 1);
        private readonly Color _avoidingColor = new Color(130f/255f, 0, 231f/255f, 1);
        /* More Colors
        private Color _tomatoColor = new Color(231f/255f, 33f/255f, 0, 1);
        private Color _yellowColor = new Color(231f/255f, 205f/255f, 0, 1);
        private Color _greenColor = new Color(0, 231f/255f, 130f/255f, 1);
        private Color _lightBlueColor = new Color(0, 170f/255f, 231f/255f, 1);
        private Color _raspberryColor = new Color(231f/255f, 0, 110f/255f, 1);
        private Color _violetColor = new Color(130f/255f, 0, 231f/255f, 1); */

        private Color _emissiveIdlingColor;
        private Color _emissiveTrackingColor;
        private Color _emissiveAvoidingColor;

        private Coroutine _changeEyeColorRoutine;
        private Coroutine _changeEyeEmissiveColorRoutine;
        
        // Navigation
        private Navigator _navigator;
        private Coroutine _controllerRoutine;
        private Coroutine _navigatorRoutine;
        
        // Attacking
        private readonly Collider[] _explosionHitColliders = new Collider[20];
        private static readonly int Firing = Animator.StringToHash("Firing");
        private readonly NetworkVariable<bool> _isAttacking = new();

        // Damage
        private static readonly int TakeDamage =
            Animator.StringToHash("TakeDamage");

        private float _lastDamageTime = -Mathf.Infinity;

        // Components
        private Enemy _enemy;
        private Damageable _damageable;
        private NetworkObject _netObject;
        private Rigidbody _rigidBody;
        private Transform _transform;

        private bool _hasControl;

        // Debugging
        private static readonly SteelpunkLogger.LoggerInstance Logger =
            new(SteelpunkLogger.LogCategory.Navigation);


        // Lifetime
        public override void OnNetworkSpawn()
        {
            _isAttacking.OnValueChanged += OnAttackingUpdate;

            if (!IsServer) return;

            _enemy.onActivate.AddListener(OnActivate);
            _damageable.onDeathServer.AddListener(((_, _, _) =>
            {
                StopAllCoroutines();
                _isAttacking.Value = false;
                StartCoroutine(DeathRoutine());
            }));
            _damageable.onTakeDamageServer.AddListener(OnTakeDamage);
        }

        private void Awake()
        {
            _enemy = GetComponent<Enemy>();
            _damageable = GetComponent<Damageable>();
            _rigidBody = GetComponent<Rigidbody>();
            _transform = GetComponent<Transform>();
            _navigator = GetComponent<Navigator>();

            _warmupSoundInstance = RuntimeManager.CreateInstance(warmupSound);
            _warmupSoundInstance.set3DAttributes(_transform.To3DAttributes());
            _fireSoundInstance = RuntimeManager.CreateInstance(fireSound);
            _fireSoundInstance.set3DAttributes(_transform.To3DAttributes());
        }

        private void Start()
        {
            // Emissive Colors for Movement States
            _emissiveAvoidingColor = GenerateEmissiveColor(_avoidingColor);
            _emissiveIdlingColor = GenerateEmissiveColor(_idlingColor);
            _emissiveTrackingColor = GenerateEmissiveColor(_trackingColor);
            
            // Idle Parameters
            _idleOffset = Random.Range(0.0f, Mathf.PI * 2);
            var idleSpeedVarianceRange = idleRateBase * idleRateVariance;
            idleRate = idleRateBase + Random.Range(-idleSpeedVarianceRange,
                idleSpeedVarianceRange);
            var idleVelocityVarianceRange =
                idleRateBase * idleVelocityVariance;
            idleVelocity = idleVelocityBase + Random.Range(-idleVelocityVarianceRange,
                idleVelocityVarianceRange);

            // References
            _navigator.room = _enemy.room;
            
            // Subscriptions
            _navigator.ReachedNode += AssessLos;
        }

        private void OnActivate()
        {
            StartCoroutine(AssessLosRoutine());
            StartCoroutine(FiringAction());
            StartCoroutine(AvoidanceRoutine());
        }

        private void Update()
        {
            if (IsClient)
            {
                _warmupSoundInstance.set3DAttributes(
                    _transform.To3DAttributes());
            }

            if (!IsServer) return;

            if (!_enemy.GetTargetPlayer()) return;

            if (_damageable.IsFrozen) return;

            if (_damageable.IsDead) return;

            if (_enemy.room.State == RaidRoomManager.RoomState.Unentered)
                return;

            // Movement State Machine
            var newMovementState = GetMovementState();
            if (newMovementState != _movementState)
                SetMovementState(newMovementState);
        }

        private void FixedUpdate()
        {
            if (!IsOwner) return;

            if (!_enemy.GetTargetPlayer())
            {
                UpdateIdleVector();
                SetVelocity(_idleVector);
                return;
            };

            if (_damageable.IsFrozen) return;

            if (_damageable.IsDead) return;

            if (Time.time - _lastDamageTime > 0.5f)
            {
                switch (_movementState)
                {
                    case MovementState.Idling:
                        FacePlayer();
                        UpdateIdleVector();
                        SetVelocity(_idleVector);
                        break;
                    case MovementState.Circling:
                        FacePlayer();
                        UpdateIdleVector();
                        UpdateCirclingVector();
                        SetVelocity(_circlingVelocity + _idleVector);
                        break;
                    case MovementState.Tracking:
                        FacePlayer();
                        UpdateIdleVector();
                        SetVelocity((_navigator.vector * trackingVelocity) + _idleVector);
                        break;
                    case MovementState.Avoiding:
                        FacePlayer();
                        UpdateIdleVector();
                        SetVelocity(_avoidanceVector + _idleVector);
                        break;
                    case MovementState.Correcting:
                        FacePlayer();
                        UpdateCorrectionVector();
                        SetVelocity(_correctionVector);
                        break;
                }
            }
        }


        // Get Movement State
        private MovementState GetMovementState()
        {
            if (_movementStateLocked) return _movementState;
            
            // Am I out of bounds?
            _relativeHeight = transform.position.y - _enemy.room.transform.position.y;
            if ((_relativeHeight < minHeight) || (_relativeHeight > maxHeight))
            {
                Logger.Log("Out of Bounds");
                if (_movementState != MovementState.Tracking)
                {
                    return MovementState.Correcting;
                }
                else
                {
                    Logger.Log("Currently navigating, cannot correct");
                }
            }
            
            // Am I being targeted? 
            foreach (var player in _enemy.room.Players)
            {
                var position = _transform.position;
                var playerLook = player.CurrentLookRay;
                var lineToPlayer = position - playerLook.origin;

                var vectorToPlayer = player.GetCenterOfMass() - position;
                var normalizedVectorToPlayer =
                    Vector3.Normalize(vectorToPlayer);
                var rayOriginOffset = mainCollider.radius * 100 *
                                      normalizedVectorToPlayer;

                if (Vector3.Angle(playerLook.direction, lineToPlayer) <
                    avoidanceLookAngleRange)
                {
                    if (_hasLosOnTarget)
                    {
                        _playerAvoiding = player;
                        return MovementState.Avoiding;
                    }
                }
            }

            // Do I have LoS on the target player?
            if (!_hasLosOnTarget)
            {
                return MovementState.Tracking;
            }
            
            return MovementState.Idling;
        }
        
        private Vector3 GetTargetFlat()
        {
            if (_enemy.GetTargetPlayer())
            {
                return new Vector3(
                    _enemy.GetTargetPlayer().transform.position.x,
                    transform.position.y,
                    _enemy.GetTargetPlayer().transform.position.z);
            }
            
            var position = transform.position + transform.forward;
            position.y = transform.position.y;
            return position;
        }
        
        private IEnumerator AssessLosRoutine()
        {
            while (true)
            {
                yield return new WaitForSeconds(_losAssessmentInterval);
                AssessLos();
            }
        }

        private void AssessLos()
        {
            var targetPlayer = _enemy.GetTargetPlayer();
            if (targetPlayer == null) return;
            var targetPosition = targetPlayer.GetCenterOfMass();
            AssessLos(targetPosition);
        }
        
        private void AssessLos(Vector3 target)
        {
            var newLos = _navigator.HasLineOfSightOn(target,
                _losAssessmentInterval,
                _losAssessmentInterval == trackingLosAssessmentInterval);
            if (newLos != _hasLosOnTarget)
            {
                if (newLos) Logger.Log("Line of Sight REGAINED");
                else Logger.Log("Line of Sight LOST");
            }

            _hasLosOnTarget = newLos;
        }
        
        
        // Set Movement State
        private void SetMovementState(MovementState newMovementState)
        {
            if (newMovementState == _movementState) return;
            Logger.Log("New Movement State: " + newMovementState);

            switch (newMovementState)
            {
                case MovementState.Avoiding:
                    StartCoroutine(LockMovementState(avoidanceDuration));
                    SetEyeColor(MovementState.Avoiding);
                    switch (_movementState)
                    {
                        case MovementState.Tracking:
                            EndNavigationRoutine();
                            break;
                    }

                    break;

                case MovementState.Idling:
                    _losAssessmentInterval = idlingLosAssessmentInterval;
                    SetEyeColor(MovementState.Idling);
                    switch (_movementState)
                    {
                        case MovementState.Tracking:
                            EndNavigationRoutine();
                            break;
                    }

                    break;

                case MovementState.Tracking:
                    _losAssessmentInterval = trackingLosAssessmentInterval;
                    SetEyeColor(MovementState.Tracking);
                    StartNavigationRoutine();
                    break;
                
                case MovementState.Correcting:
                    StartCoroutine(LockMovementState(correctionDuration));
                    switch (_movementState)
                    {
                        case MovementState.Tracking:
                            EndNavigationRoutine();
                            break;
                    }
                    break;
                    
            }

            _movementState = newMovementState; 
        }

        private void SetEyeColor(MovementState movementState)
        {
            Color newColor;
            Color newEmissiveColor;
            renderer.materials[eyeMaterialIndex].EnableKeyword("_EMISSION");
            
            switch (movementState)
            {
                case MovementState.Avoiding:
                    newColor = _avoidingColor;
                    newEmissiveColor = _emissiveAvoidingColor;
                    break;
                case MovementState.Idling:
                    newColor = _idlingColor;
                    newEmissiveColor = _emissiveIdlingColor;
                    break;
                case MovementState.Tracking: 
                    newColor = _trackingColor;
                    newEmissiveColor = _emissiveTrackingColor;
                    break;
                default:
                    return;
            }

            if (_changeEyeColorRoutine != null)
            {
                StopCoroutine(_changeEyeColorRoutine);
            }
            _changeEyeColorRoutine = StartCoroutine(ChangeEyeColor(newColor));
            
            if (_changeEyeEmissiveColorRoutine != null)
            {
                StopCoroutine(_changeEyeEmissiveColorRoutine);
            }
            _changeEyeEmissiveColorRoutine = StartCoroutine(ChangeEyeColor(newEmissiveColor, true));
        }

        private Color GenerateEmissiveColor(Color color)
        {
            // Quadratic
            return new Color(
                CalculateEmissiveColorComponent(color.r), 
                CalculateEmissiveColorComponent(color.g),
                CalculateEmissiveColorComponent(color.b),
                color.a
            );
        }

        private float CalculateEmissiveColorComponent(float colorComponent)
        {
            return Mathf.Max(0, (22.6301f * Mathf.Pow(colorComponent, 2.0f) - 2.3804f));
        }

        private IEnumerator LockMovementState(float duration)
        {
            _movementStateLocked = true;
            Logger.Log("Movement State Locked");
            yield return new WaitForSeconds(duration);
            Logger.Log("Movement State Unlocked");
            _movementStateLocked = false;
        }


        // Enact Movement State
        private void SetVelocity(Vector3 targetVelocity)
        {
            _rigidBody.velocity =
                Vector3.MoveTowards(_rigidBody.velocity, targetVelocity,
                    movementSpeed * Time.fixedDeltaTime);
        }

        private void FacePlayer()
        {
            var localTransform = transform;
            var targetPlayer = _enemy.GetTargetPlayer();

            if (targetPlayer)
            {
                transform.rotation = Quaternion.Lerp(localTransform.rotation,
                    Quaternion.LookRotation(
                        targetPlayer.transform.position -
                        localTransform.position),
                    Time.deltaTime * 5.0f);
            }
        }

        private void UpdateIdleVector()
        {
            _idleVector = new Vector3(0.0f,
                Mathf.Sin((Time.fixedTime * idleRate) + _idleOffset) *
                idleVelocity, 0.0f);
        }

        private void UpdateCirclingVector()
        {
            Vector3 position = transform.position;
            Vector3 targetFlat = GetTargetFlat();
            Vector3 toPositionFromTarget = position - targetFlat;
            Vector3 toCirclePointFromTarget =
                Quaternion.AngleAxis(15.0f, Vector3.up) * toPositionFromTarget;
            Vector3 circlePoint = targetFlat + toCirclePointFromTarget;
            Vector3 toCirclePointFromPosition =
                circlePoint - position;
            _circlingVelocity =
                (toCirclePointFromPosition).normalized * circlingSpeed;
        }

        private void UpdateCorrectionVector()
        {
            _correctionVector = (Vector3.up * (Mathf.Clamp((_relativeHeight), minHeight, maxHeight) - _relativeHeight)).normalized * correctionInertia;
        }

        private IEnumerator AvoidanceRoutine()
        {
            while (!_damageable.IsDead)
            {
                while (_damageable.IsFrozen)
                {
                    yield return new WaitForSeconds(0.5f);
                }

                var avoiding = (_movementState == MovementState.Avoiding);
                yield return new WaitForSeconds(avoidanceReactionTime);
                while (avoiding)
                {
                    var avoidanceSpeedScaler = new Vector3(avoidanceVelocity,
                        avoidanceVelocity, avoidanceVelocity);

                    var position = _transform.position;
                    var playerLook = _playerAvoiding.CurrentLookRay;
                    var lineToPlayer = position - playerLook.origin;
                    var heightDiff = lineToPlayer.y;

                    // When x and z are set to 0, the bot only moves on the z axis
                    // when only y is set to 0, the bot mostly moves on the y axis

                    // idea: check the final product's y component and optionally negate it, depending on the bot's relative height, so it tends to come back to mid-level
                    // idea: experiment with switching the lhs / rhs -- cross product is not communicative! 
                    // idea: give the movement more variation by randomly (or dynamically?) altering the rhs component ranges and the speed scaler

                    // Repeat until unblocked path is found
                    var validDir = false;
                    var i = 0;
                    var finalDir = Vector3.zero;
                    while ((!validDir) && (i < 100))
                    {
                        // Calculate Random Scaled Orthogonal Vector 
                        var rhs = new Vector3(Random.Range(-0.75f, 0.75f),
                            Random.Range(-1f, 1f),
                            Random.Range(-0.75f, 0.75f));
                        var lhs = Vector3.Normalize(lineToPlayer);
                        finalDir = Vector3.Scale(
                            Vector3.Normalize(Vector3.Cross(lhs, rhs)),
                            avoidanceSpeedScaler);

                        // Enforce Height Bounds 
                        if (heightDiff > maxHeight)
                        {
                            finalDir.y = Mathf.Min(-Mathf.Abs(finalDir.y),
                                -0.75f * avoidanceVelocity);
                        }
                        else if (heightDiff < minHeight)
                        {
                            finalDir.y = Mathf.Max(Mathf.Abs(finalDir.y),
                                0.75f * avoidanceVelocity);
                        }

                        // Predict Collision
                        var predictedDistanceTravelled =
                            Vector3.Distance(position, position + finalDir);
                        validDir = !Physics.SphereCast(position,
                            mainCollider.radius, finalDir, out RaycastHit hit,
                            predictedDistanceTravelled);
                        i++;
                    }

                    if (i >= 100)
                        Debug.LogWarning(
                            "Eye drone avoidance pathing timed out.");

                    // Avoidance Movement
                    _avoidanceVector = finalDir;
                    yield return new WaitForSeconds(avoidanceDuration);
                    avoiding = (_movementState == MovementState.Avoiding);
                    _avoidanceVector = Vector3.zero;
                }
            }
        }

        private IEnumerator ChangeEyeColor(Color newColor, bool emissive = false)
        {
            var lerpedColor = emissive ? 
                renderer.materials[eyeMaterialIndex].GetColor(EmissionColor) :
                renderer.materials[eyeMaterialIndex].color;
            
            while (!((lerpedColor.r - newColor.r < 0.001f) && 
                     (lerpedColor.g - newColor.g < 0.001f) &&
                     (lerpedColor.b - newColor.b < 0.001f)))
            {
                lerpedColor = Color.Lerp(renderer.materials[eyeMaterialIndex].color, newColor, 1 / eyeColorTransitionDuration);
                
                if (emissive)
                {
                    renderer.materials[eyeMaterialIndex].SetColor(EmissionColor, lerpedColor);
                }
                else
                {
                    renderer.materials[eyeMaterialIndex].color = lerpedColor;
                }
                
                yield return null; 
            }
        }


        // Navigation
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

            Logger.Log("Starting Navigation Routines");
            _controllerRoutine = StartCoroutine(NavigationRoutine());
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

            _losAssessmentInterval = idlingLosAssessmentInterval;
        }

        private IEnumerator NavigationRoutine()
        {
            /* idea: If navigation is ever used for purposes beyond LoS,
             NavigationRoutine could accept a specification of which navigation routine to run.
             Currently this is used as a good safety against over-calling.
             */
            yield return new WaitForSeconds(stallNavigationDuration);
            yield return StartCoroutine(RegainLosNavigationRoutine());
            _losAssessmentInterval = trackingLosAssessmentInterval;
            EndNavigationRoutine();
            
            if (_navigator.path == null) _movementState = MovementState.Idling;

            Logger.Log("Navigation Routines Available");
        }

        private IEnumerator RegainLosNavigationRoutine()
        {
            if (!_enemy.GetTargetPlayer())
                yield break;
                
            var target = _enemy.GetTargetPlayer().GetCenterOfMass();
            var goal = new Vector3(target.x, transform.position.y, target.z);
            yield return _navigatorRoutine =
                _navigator.StartCoroutine(_navigator.NavigateTo(goal));
        }


        // Attack
        [ClientRpc]
        private void StartAttackClientRpc(float time)
        {
            warmupEffect.SetFloat("Lifetime", time);
        }

        private void OnAttackingUpdate(bool prev, bool next)
        {
            if (next)
            {
                _warmupSoundInstance.start();
                warmupEffect.Play();
            }
            else
            {
                _warmupSoundInstance.stop(STOP_MODE.IMMEDIATE);
                warmupEffect.Stop();
            }

            animator.SetBool(Firing, next);
        }

        private IEnumerator FiringAction()
        {
            while (!_damageable.IsDead)
            {
                if (_enemy.room.State == RaidRoomManager.RoomState.Unentered)
                {
                    yield return new WaitForEndOfFrame();
                    continue;
                }
                
                while (_damageable.IsFrozen)
                {
                    yield return new WaitForSeconds(0.5f);
                }

                if (onlyFiresWhileIdling)
                {
                    while (_movementState != MovementState.Idling)
                    {
                        yield return new WaitForSeconds(0.5f);
                    }   
                }
                
                _enemy.SetTargetPlayer(
                    _enemy.GetClosestPlayerInAngle(transform.forward,
                        _enemy.CenterOfMass));

                if (_enemy.GetTargetPlayer() == null)
                {
                    yield return new WaitForEndOfFrame();
                    continue;
                };

                yield return new WaitForSeconds(Random.Range(0, 1.5f));

                var currentFireTime = fireTime;

                yield return new WaitForSeconds(currentFireTime / 2.0f);

                _isAttacking.Value = true;
                StartAttackClientRpc(currentFireTime / 2.0f);

                yield return new WaitForSeconds(currentFireTime / 2.0f);
                
                if (_enemy.GetTargetPlayer() == null)
                {
                    yield return new WaitForEndOfFrame();
                    continue;
                };
                
                var bullet = Instantiate(projectile,
                        projectileSpawnPoint.position,
                        Quaternion.LookRotation(
                            _enemy.GetTargetPlayer().GetCenterOfMass() -
                            _transform.position,
                            Vector3.up))
                    .GetComponent<SimpleEnemyBullet>();

                OnFireClientRpc();

                _isAttacking.Value = false;
                bullet.GetComponent<NetworkObject>().Spawn();

                bullet.damage = _enemy.EnemyConfig.damage;
                bullet.speed = 15.0f;
                bullet.SetTarget(_enemy.GetTargetPlayer().GetCenterOfMass());
            }
        }

        [ClientRpc]
        private void OnFireClientRpc()
        {
            _fireSoundInstance.set3DAttributes(_transform.To3DAttributes());

            _fireSoundInstance.start();
        }

        
        // Damage & Death
        private void OnTakeDamage(DamagePayload payload, Ray ray, float amt) 
        {
            _lastDamageTime = Time.time;
            animator.SetTrigger(TakeDamage);
            _rigidBody.AddForceAtPosition(
                (ray.direction + Random.insideUnitSphere).normalized *
                payload.baseDamage,
                ray.origin, ForceMode.Impulse);

            if (_damageable.GetHealth() < 0.3f * _damageable.GetMaxHealth())
                StartLowHpClientRpc();
        }

        [ClientRpc]
        private void StartLowHpClientRpc()
        {
            smokingEffect.Play();
        }

        private IEnumerator DeathRoutine()
        {
            _rigidBody.constraints = RigidbodyConstraints.None;
            _rigidBody.AddTorque(_transform.forward * 100.0f,
                ForceMode.Impulse);
            _rigidBody.AddForce((Random.insideUnitSphere + Vector3.up) * 60.0f,
                ForceMode.Impulse);
            _rigidBody.useGravity = true;

            yield return new WaitForSeconds(1.0f);
            VFXManager.Instance.ExplosionEffectClientRpc(_transform.position,
                2.5f);
            Destroy(gameObject);
        }

        private void Explode()
        {
            var n = Physics.OverlapSphereNonAlloc(_transform.position, 5.0f,
                _explosionHitColliders,
                LayerMaskLibrary.Instance.enemyMask |
                LayerMaskLibrary.Instance.playerMask);

            HashSet<Damageable> damageables = new HashSet<Damageable>();

            for (int i = 0; i < n; i++)
            {
                Collider col = _explosionHitColliders[i];
                Damageable damageable = col.GetComponentInParent<Damageable>();
                if (damageable && !damageables.Contains(damageable))
                {
                    damageables.Add(damageable);

                    DamagePayload payload = explodePayload;
                    Vector3 closestPoint =
                        col.ClosestPoint(_transform.position);
                    float distance = Vector3.Distance(closestPoint,
                        transform.position);
                    payload.baseDamage *= (5.0f - distance) / 5.0f;

                    damageable.TakeDamage(payload,
                        new Ray(closestPoint,
                            closestPoint - transform.position));
                }
            }
        }
    }
}
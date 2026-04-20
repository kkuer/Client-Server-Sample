using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Collections;
using Unity.Netcode;
using TMPro;
using System.Collections;
using System.Collections.Generic;

public class PlayerController : NetworkBehaviour
{
    [Header("Movement")]
    [SerializeField] private float _walkSpeed = 5f;
    [SerializeField] private float _acceleration = 10f;
    [SerializeField] private float _deceleration = 10f;
    [SerializeField] private float _jumpForce = 5f;
    [SerializeField] private float _gravity = 20f;

    [Header("Dash")]
    [SerializeField] private float _dashForce = 15f;
    [SerializeField] private float _dashCooldown = 1.5f;
    [SerializeField] private float _dashDuration = 0.2f;

    [Header("Hook")]
    [SerializeField] private float _hookRange = 30f;
    [SerializeField] private float _hookTravelSpeed = 60f;
    [SerializeField] private float _hookReelSpeed = 15f;
    [SerializeField] private float _hookCooldown = 2f;
    [SerializeField] private LayerMask _hookLayerMask;
    [SerializeField] private GameObject _hookVisualPrefab;
    [SerializeField] private Material _hookLineMaterial;
    [SerializeField] private Color _hookLineColor = Color.white;
    [SerializeField] private float _hookLineWidth = 0.05f;

    [Header("Melee")]
    [SerializeField] private float _meleeRange = 2f;
    [SerializeField] private float _meleeDamage = 30f;
    [SerializeField] private float _meleeCooldown = 0.5f;
    [SerializeField] private LayerMask _meleeLayerMask = ~0;

    [Header("Health")]
    [SerializeField] private float _maxHealth = 100f;
    [SerializeField] private float _respawnTime = 5f;
    [SerializeField] private AudioClip _damageSound;
    [SerializeField] private AudioClip _deathSound;
    [SerializeField] private AudioClip _respawnSound;

    [Header("UI")]
    [SerializeField] private GameObject _healthBarPrefab;
    [SerializeField] private GameObject _playerUIElementPrefab;

    [Header("References")]
    [SerializeField] private TextMeshPro _nameLabel;
    [SerializeField] private GameObject _cameraHolder;

    // Network
    private NetworkVariable<int> _colorIndex = new NetworkVariable<int>(0,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private NetworkVariable<int> _playerNumber = new NetworkVariable<int>(0,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private NetworkVariable<FixedString32Bytes> _playerName = new NetworkVariable<FixedString32Bytes>(
        new FixedString32Bytes("Player"),
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    // Health and Stats
    private NetworkVariable<float> _currentHealth = new NetworkVariable<float>(
        100f,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );
    private NetworkVariable<int> _eliminations = new NetworkVariable<int>(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );
    private NetworkVariable<int> _deaths = new NetworkVariable<int>(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );
    private NetworkVariable<bool> _isDead = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    public static string LocalPlayerName = "Player";
    private static readonly Color[] PlayerColors = new Color[]
    {
        Color.red, Color.blue, Color.green, Color.yellow,
        Color.cyan, Color.magenta, new Color(1f, 0.5f, 0f), new Color(0.5f, 0f, 1f)
    };

    // Components
    private CharacterController _controller;
    private Camera _playerCamera;
    private LineRenderer _hookLineRenderer;
    private AudioSource _audioSource;
    private GameObject _healthBarInstance;
    private UnityEngine.UI.Slider _healthSlider;

    // Input
    private InputAction _moveAction;
    private InputAction _jumpAction;
    private InputAction _dashAction;
    private InputAction _attackAction;
    private InputAction _hookBringToAction;
    private InputAction _hookGoToAction;

    // State
    private Vector3 _moveDirection;
    private Vector3 _currentVelocity;
    private bool _isGrounded;
    private bool _isDashing;
    private float _dashTimer;
    private float _dashCooldownTimer;
    private float _meleeTimer;

    // Hook State
    private float _hookCooldownTimer;
    private bool _isHooking;
    private bool _isReeling;
    private HookType _activeHookType;
    private NetworkObject _hookedPlayer;
    private Vector3 _hookedPosition;
    private GameObject _activeHookVisual;
    private Vector3 _hookEndPoint;

    private enum HookType
    {
        None,
        BringTo,
        GoTo
    }

    public override void OnNetworkSpawn()
    {
        _controller = GetComponent<CharacterController>();
        _audioSource = GetComponent<AudioSource>();
        if (_audioSource == null)
            _audioSource = gameObject.AddComponent<AudioSource>();

        if (Camera.main != null)
            _playerCamera = Camera.main;

        if (IsServer)
        {
            _colorIndex.Value = (int)(OwnerClientId % (ulong)PlayerColors.Length);
            _playerNumber.Value = NetworkManager.ConnectedClients.Count;
            _currentHealth.Value = _maxHealth;
            transform.position = new Vector3((_playerNumber.Value - 1) * 1f, 1f, 0f);
        }

        _colorIndex.OnValueChanged += OnColorChanged;
        _playerNumber.OnValueChanged += OnPlayerNumberChanged;
        _playerName.OnValueChanged += OnPlayerNameChanged;
        _currentHealth.OnValueChanged += OnHealthChanged;
        _isDead.OnValueChanged += OnDeathStateChanged;
        _eliminations.OnValueChanged += OnEliminationsChanged;

        ApplyColor(_colorIndex.Value);
        ApplyPlayerNumber(_playerNumber.Value);
        ApplyPlayerName(_playerName.Value.ToString());
        UpdateHealthDisplay(_currentHealth.Value);

        if (!IsOwner)
        {
            CreateHealthBar();
            return;
        }

        SetPlayerNameServerRpc(LocalPlayerName);

        SetupInput();
        SetupLineRenderer();
        SetupPlayerUI();

        if (_playerCamera == null)
        {
            _playerCamera = Camera.main;
            if (_playerCamera != null)
                _playerCamera.gameObject.SetActive(true);
        }

        CameraController cam = Camera.main?.GetComponent<CameraController>();
        if (cam != null)
            cam.SetTarget(transform);
    }

    private void CreateHealthBar()
    {
        if (_healthBarPrefab != null)
        {
            _healthBarInstance = Instantiate(_healthBarPrefab, transform);
            _healthBarInstance.transform.localPosition = new Vector3(0, 2.5f, 0);
            _healthSlider = _healthBarInstance.GetComponentInChildren<UnityEngine.UI.Slider>();
            if (_healthSlider != null)
            {
                _healthSlider.maxValue = _maxHealth;
                _healthSlider.value = _currentHealth.Value;
            }
        }
    }

    private void SetupPlayerUI()
    {
        if (GameUIManager.Instance != null)
        {
            GameUIManager.Instance.RegisterPlayer(this);
        }
    }

    private void SetupInput()
    {
        _moveAction = InputSystem.actions.FindAction("Move");
        _jumpAction = InputSystem.actions.FindAction("Jump");
        _dashAction = InputSystem.actions.FindAction("Sprint");
        _attackAction = InputSystem.actions.FindAction("Attack");
        _hookBringToAction = InputSystem.actions.FindAction("HookBringTo");
        _hookGoToAction = InputSystem.actions.FindAction("HookGoTo");

        _moveAction?.Enable();
        _jumpAction?.Enable();
        _dashAction?.Enable();
        _attackAction?.Enable();
        _hookBringToAction?.Enable();
        _hookGoToAction?.Enable();
    }

    private void SetupLineRenderer()
    {
        _hookLineRenderer = GetComponent<LineRenderer>();
        if (_hookLineRenderer == null)
            _hookLineRenderer = gameObject.AddComponent<LineRenderer>();

        _hookLineRenderer.positionCount = 2;
        _hookLineRenderer.startWidth = _hookLineWidth;
        _hookLineRenderer.endWidth = _hookLineWidth;
        _hookLineRenderer.material = _hookLineMaterial != null ? _hookLineMaterial : new Material(Shader.Find("Sprites/Default"));
        _hookLineRenderer.startColor = _hookLineColor;
        _hookLineRenderer.endColor = _hookLineColor;
        _hookLineRenderer.enabled = false;
    }

    public override void OnNetworkDespawn()
    {
        _colorIndex.OnValueChanged -= OnColorChanged;
        _playerNumber.OnValueChanged -= OnPlayerNumberChanged;
        _playerName.OnValueChanged -= OnPlayerNameChanged;
        _currentHealth.OnValueChanged -= OnHealthChanged;
        _isDead.OnValueChanged -= OnDeathStateChanged;
        _eliminations.OnValueChanged -= OnEliminationsChanged;

        if (GameUIManager.Instance != null)
        {
            GameUIManager.Instance.UnregisterPlayer(this);
        }
    }

    private void Update()
    {
        if (!IsOwner || !Application.isFocused) return;

        // Simple cursor toggle with Escape
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            if (Cursor.lockState == CursorLockMode.Locked)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            else
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }

        if (GameManager.Instance == null || !GameManager.Instance.GameStarted.Value) return;

        if (_isDead.Value) return;

        _hookCooldownTimer -= Time.deltaTime;
        _meleeTimer -= Time.deltaTime;

        if (!_isReeling)
        {
            HandleMelee();
            HandleDash();
            HandleMovement();
            HandleHook();
        }
        else
        {
            HandleReeling();
        }
    }

    private void HandleMovement()
    {
        _isGrounded = _controller.isGrounded;

        if (_isGrounded && _moveDirection.y < 0)
            _moveDirection.y = -2f;

        Vector2 input = _moveAction?.ReadValue<Vector2>() ?? Vector2.zero;
        Vector3 targetVelocity = (transform.right * input.x + transform.forward * input.y) * _walkSpeed;

        if (!_isDashing)
        {
            float acceleration = input.magnitude > 0.1f ? _acceleration : _deceleration;
            _currentVelocity = Vector3.Lerp(_currentVelocity, targetVelocity, acceleration * Time.deltaTime);

            _moveDirection.x = _currentVelocity.x;
            _moveDirection.z = _currentVelocity.z;
        }

        if (_jumpAction?.triggered ?? false)
        {
            if (_isGrounded)
                _moveDirection.y = _jumpForce;
        }

        _moveDirection.y -= _gravity * Time.deltaTime;
        _controller.Move(_moveDirection * Time.deltaTime);
    }

    private void HandleDash()
    {
        if (_dashAction == null) return;

        _dashCooldownTimer -= Time.deltaTime;

        if (_isDashing)
        {
            _dashTimer -= Time.deltaTime;
            if (_dashTimer <= 0f)
            {
                _isDashing = false;
                _currentVelocity = new Vector3(_moveDirection.x, 0, _moveDirection.z);
            }
            return;
        }

        if (_dashAction.triggered && _dashCooldownTimer <= 0f)
        {
            Vector2 input = _moveAction?.ReadValue<Vector2>() ?? Vector2.zero;
            Vector3 dashDirection = transform.forward;

            if (input.magnitude > 0.1f)
                dashDirection = (transform.right * input.x + transform.forward * input.y).normalized;

            _moveDirection = dashDirection * _dashForce;
            _moveDirection.y = 0f;
            _isDashing = true;
            _dashTimer = _dashDuration;
            _dashCooldownTimer = _dashCooldown;
        }
    }

    private void HandleHook()
    {
        if (_hookCooldownTimer > 0f) return;
        if (_isHooking || _isReeling) return;

        bool hookBringTo = _hookBringToAction?.triggered ?? false;
        bool hookGoTo = _hookGoToAction?.triggered ?? false;

        if (hookBringTo)
        {
            StartHook(HookType.BringTo);
        }
        else if (hookGoTo)
        {
            StartHook(HookType.GoTo);
        }
    }

    private void StartHook(HookType type)
    {
        if (_playerCamera == null) return;

        _isHooking = true;
        _activeHookType = type;

        Vector3 hookOrigin = _playerCamera.transform.position;
        Vector3 hookDirection = _playerCamera.transform.forward;

        if (_hookLineRenderer != null)
        {
            _hookLineRenderer.enabled = true;
            _hookEndPoint = hookOrigin + hookDirection * _hookRange;
        }

        StartCoroutine(TravelHook(hookOrigin, hookDirection));
    }

    private IEnumerator TravelHook(Vector3 origin, Vector3 direction)
    {
        float distanceTraveled = 0f;
        Vector3 currentPos = origin;

        if (_hookVisualPrefab != null)
        {
            _activeHookVisual = Instantiate(_hookVisualPrefab, origin, Quaternion.LookRotation(direction));
        }

        float startOffset = 1f;
        distanceTraveled = startOffset;
        currentPos += direction * startOffset;
        origin = currentPos;

        while (distanceTraveled < _hookRange)
        {
            float step = _hookTravelSpeed * Time.deltaTime;
            distanceTraveled += step;
            currentPos += direction * step;

            _hookEndPoint = currentPos;

            if (_activeHookVisual != null)
            {
                _activeHookVisual.transform.position = currentPos;
            }

            RaycastHit hit;
            if (Physics.Raycast(origin, direction, out hit, step, _hookLayerMask))
            {
                if (hit.collider.gameObject != gameObject)
                {
                    _hookEndPoint = hit.point;
                    OnHookHit(hit);
                    yield break;
                }
            }

            origin = currentPos;
            yield return null;
        }

        CleanupHook();
    }

    private void OnHookHit(RaycastHit hit)
    {
        bool validHit = false;

        PlayerController hitPlayer = hit.collider.GetComponent<PlayerController>();

        if (hitPlayer != null)
        {
            if (_activeHookType == HookType.BringTo)
            {
                _hookedPlayer = hitPlayer.NetworkObject;
                validHit = true;
            }
            else if (_activeHookType == HookType.GoTo)
            {
                _hookedPlayer = hitPlayer.NetworkObject;
                validHit = true;
            }
        }
        else if (_activeHookType == HookType.GoTo)
        {
            _hookedPosition = hit.point;
            validHit = true;
        }

        if (validHit)
        {
            StartReeling();
        }
        else
        {
            CleanupHook();
        }
    }

    private void StartReeling()
    {
        _isHooking = false;
        _isReeling = true;

        if (_activeHookVisual != null)
        {
            Destroy(_activeHookVisual);
            _activeHookVisual = null;
        }
    }

    private void HandleReeling()
    {
        if (_activeHookType == HookType.BringTo)
        {
            if (_hookedPlayer != null && _hookedPlayer.IsSpawned)
            {
                Vector3 direction = (transform.position - _hookedPlayer.transform.position).normalized;
                Vector3 newPos = _hookedPlayer.transform.position + direction * _hookReelSpeed * Time.deltaTime;

                if (Vector3.Distance(transform.position, _hookedPlayer.transform.position) < 2f)
                {
                    StopReeling();
                }
                else
                {
                    RequestReelPositionServerRpc(_hookedPlayer.NetworkObjectId, newPos);
                }
            }
            else
            {
                StopReeling();
            }
        }
        else if (_activeHookType == HookType.GoTo)
        {
            Vector3 targetPos;

            if (_hookedPlayer != null && _hookedPlayer.IsSpawned)
            {
                targetPos = _hookedPlayer.transform.position;
            }
            else
            {
                targetPos = _hookedPosition;
            }

            Vector3 direction = (targetPos - transform.position).normalized;
            Vector3 movement = direction * _hookReelSpeed;

            _controller.Move(movement * Time.deltaTime);

            if (Vector3.Distance(transform.position, targetPos) < 2f)
            {
                StopReeling();
                _moveDirection.y = 0f;
            }
        }
    }

    [ServerRpc(RequireOwnership = false)]
    private void RequestReelPositionServerRpc(ulong targetNetworkId, Vector3 newPosition)
    {
        if (NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(targetNetworkId, out NetworkObject target))
        {
            PlayerController targetPlayer = target.GetComponent<PlayerController>();
            if (targetPlayer != null && targetPlayer._controller != null)
            {
                Vector3 moveDelta = newPosition - target.transform.position;
                targetPlayer._controller.Move(moveDelta);
            }
        }
    }

    private void StopReeling()
    {
        _isReeling = false;
        _hookCooldownTimer = _hookCooldown;
        _hookedPlayer = null;
        _activeHookType = HookType.None;

        if (_hookLineRenderer != null)
        {
            _hookLineRenderer.enabled = false;
        }
    }

    private void CleanupHook()
    {
        _isHooking = false;
        _hookCooldownTimer = _hookCooldown;
        _activeHookType = HookType.None;

        if (_activeHookVisual != null)
        {
            Destroy(_activeHookVisual);
            _activeHookVisual = null;
        }

        if (_hookLineRenderer != null)
        {
            _hookLineRenderer.enabled = false;
        }
    }

    private void HandleMelee()
    {
        if (_attackAction?.triggered ?? false)
        {
            if (_meleeTimer <= 0f)
            {
                PerformMelee();
                _meleeTimer = _meleeCooldown;
            }
        }
    }

    private void PerformMelee()
    {
        if (_playerCamera == null) return;

        RaycastHit hit;
        if (Physics.Raycast(_playerCamera.transform.position, _playerCamera.transform.forward,
            out hit, _meleeRange, _meleeLayerMask))
        {
            PlayerController hitPlayer = hit.collider.GetComponent<PlayerController>();
            if (hitPlayer != null && hitPlayer != this && !hitPlayer._isDead.Value)
            {
                DamagePlayerServerRpc(hitPlayer.OwnerClientId, _meleeDamage, OwnerClientId);
            }
        }
    }

    [ServerRpc]
    private void DamagePlayerServerRpc(ulong targetClientId, float damage, ulong attackerClientId)
    {
        if (NetworkManager.SpawnManager.GetPlayerNetworkObject(targetClientId) != null)
        {
            PlayerController targetPlayer = NetworkManager.SpawnManager.GetPlayerNetworkObject(targetClientId).GetComponent<PlayerController>();
            if (targetPlayer != null && !targetPlayer._isDead.Value)
            {
                targetPlayer.TakeDamage(damage, attackerClientId);
            }
        }
    }

    public void TakeDamage(float damage, ulong attackerClientId)
    {
        if (!IsServer) return;
        if (_isDead.Value) return;

        _currentHealth.Value = Mathf.Max(0, _currentHealth.Value - damage);

        if (_currentHealth.Value <= 0)
        {
            Die(attackerClientId);
        }
    }

    private void Die(ulong killerClientId)
    {
        if (!IsServer) return;

        _isDead.Value = true;
        _deaths.Value++;

        if (killerClientId != OwnerClientId && NetworkManager.SpawnManager.GetPlayerNetworkObject(killerClientId) != null)
        {
            PlayerController killer = NetworkManager.SpawnManager.GetPlayerNetworkObject(killerClientId).GetComponent<PlayerController>();
            if (killer != null)
            {
                killer._eliminations.Value++;
            }
        }

        StartCoroutine(RespawnCoroutine());
    }

    private IEnumerator RespawnCoroutine()
    {
        yield return new WaitForSeconds(_respawnTime);

        if (IsServer)
        {
            Respawn();
        }
    }

    private void Respawn()
    {
        if (!IsServer) return;

        _currentHealth.Value = _maxHealth;
        _isDead.Value = false;

        SpawnPoint[] spawnPoints = FindObjectsByType<SpawnPoint>(FindObjectsSortMode.None);
        Vector3 spawnPosition;

        if (spawnPoints.Length > 0)
        {
            SpawnPoint spawn = spawnPoints[Random.Range(0, spawnPoints.Length)];
            spawnPosition = spawn.transform.position;
        }
        else
        {
            spawnPosition = new Vector3((_playerNumber.Value - 1) * 1f, 1f, 0f);
        }

        transform.position = spawnPosition;
        _moveDirection = Vector3.zero;
        _currentVelocity = Vector3.zero;
    }

    private void OnHealthChanged(float previous, float current)
    {
        UpdateHealthDisplay(current);

        if (IsOwner)
        {
            GameUIManager.Instance?.UpdateHealth(current, _maxHealth);

            if (current < previous)
            {
                PlayDamageSound();
            }
        }
        else
        {
            if (_healthSlider != null)
            {
                _healthSlider.value = current;
            }
        }
    }

    private void OnDeathStateChanged(bool previous, bool current)
    {
        if (current)
        {
            PlayDeathSound();
            if (IsOwner)
            {
                GameUIManager.Instance?.ShowDeathScreen();
            }

            Renderer rend = GetComponent<Renderer>();
            if (rend != null)
                rend.enabled = false;

            _controller.enabled = false;
        }
        else
        {
            PlayRespawnSound();
            if (IsOwner)
            {
                GameUIManager.Instance?.HideDeathScreen();
            }

            Renderer rend = GetComponent<Renderer>();
            if (rend != null)
                rend.enabled = true;

            _controller.enabled = true;
        }
    }

    private void OnEliminationsChanged(int previous, int current)
    {
        if (IsOwner)
        {
            GameUIManager.Instance?.UpdateEliminations(current);
        }
    }

    private void UpdateHealthDisplay(float health)
    {
        if (_healthSlider != null)
        {
            _healthSlider.value = health;
        }
    }

    private void PlayDamageSound()
    {
        if (_audioSource != null && _damageSound != null)
        {
            _audioSource.PlayOneShot(_damageSound);
        }
    }

    private void PlayDeathSound()
    {
        if (_audioSource != null && _deathSound != null)
        {
            _audioSource.PlayOneShot(_deathSound);
        }
    }

    private void PlayRespawnSound()
    {
        if (_audioSource != null && _respawnSound != null)
        {
            _audioSource.PlayOneShot(_respawnSound);
        }
    }

    [ServerRpc(RequireOwnership = false)]
    private void SetPlayerNameServerRpc(FixedString32Bytes name, ServerRpcParams rpcParams = default)
    {
        if (rpcParams.Receive.SenderClientId != OwnerClientId) return;
        _playerName.Value = name;
    }

    private void UpdateLineRenderer()
    {
        if (_hookLineRenderer == null || _playerCamera == null) return;

        Vector3 startPoint = _playerCamera.transform.position;
        Vector3 endPoint = _isHooking ? _hookEndPoint : (_isReeling ? GetCurrentHookTarget() : startPoint);

        _hookLineRenderer.SetPosition(0, startPoint);
        _hookLineRenderer.SetPosition(1, endPoint);
    }

    private Vector3 GetCurrentHookTarget()
    {
        if (_hookedPlayer != null && _hookedPlayer.IsSpawned)
        {
            return _hookedPlayer.transform.position;
        }
        return _hookedPosition;
    }

    private void OnColorChanged(int previous, int current)
    {
        ApplyColor(current);
    }

    private void OnPlayerNumberChanged(int previous, int current)
    {
        ApplyPlayerNumber(current);
    }

    private void OnPlayerNameChanged(FixedString32Bytes previous, FixedString32Bytes current)
    {
        ApplyPlayerName(current.ToString());
    }

    private void ApplyPlayerName(string name)
    {
        if (_nameLabel != null && !string.IsNullOrEmpty(name))
            _nameLabel.text = name;
    }

    private void ApplyPlayerNumber(int number)
    {
        if (_nameLabel != null && number > 0 && _playerName.Value.IsEmpty)
            _nameLabel.text = $"P{number}";
    }

    private void ApplyColor(int index)
    {
        Renderer rend = GetComponent<Renderer>();
        if (rend != null)
            rend.material.color = PlayerColors[index];
    }

    public Color GetPlayerColor()
    {
        return PlayerColors[_colorIndex.Value];
    }

    private void LateUpdate()
    {
        if (_nameLabel != null && Camera.main != null)
        {
            _nameLabel.transform.LookAt(
                _nameLabel.transform.position + Camera.main.transform.rotation * Vector3.forward,
                Camera.main.transform.rotation * Vector3.up
            );
        }

        if (_isHooking || _isReeling)
        {
            UpdateLineRenderer();
        }

        if (_healthBarInstance != null && Camera.main != null)
        {
            _healthBarInstance.transform.LookAt(
                _healthBarInstance.transform.position + Camera.main.transform.rotation * Vector3.forward,
                Camera.main.transform.rotation * Vector3.up
            );
        }
    }

    private void OnDestroy()
    {
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    public string GetPlayerName() => _playerName.Value.ToString();
    public int GetEliminations() => _eliminations.Value;
    public int GetDeaths() => _deaths.Value;
    public float GetHealth() => _currentHealth.Value;
    public float GetMaxHealth() => _maxHealth;
    public bool IsDead() => _isDead.Value;
}
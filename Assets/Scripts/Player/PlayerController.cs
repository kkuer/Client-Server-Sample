using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Collections;
using Unity.Netcode;
using TMPro;

// PlayerController handles movement, color assignment, and the player number label.
// It inherits from NetworkBehaviour instead of MonoBehaviour, which gives it
// access to NGO properties like IsOwner, IsServer, and IsClient, as well as
// network lifecycle callbacks like OnNetworkSpawn.
public class PlayerController : NetworkBehaviour
{
    // [SerializeField] exposes a private field in the Unity Inspector.
    // Adjust move speed per-prefab without changing code.
    [SerializeField] private float _moveSpeed = 5f;

    // Assign the TextMeshPro child object in the prefab Inspector.
    // This label floats above the capsule and shows the player name.
    [SerializeField] private TextMeshPro _nameLabel;

    // Drag the Coin prefab here in the Player prefab Inspector.
    // The server will instantiate and spawn a copy of this prefab when the player fires.
    [SerializeField] private GameObject _coinPrefab;

    // Static bridge: NetworkManagerUI sets this before the player prefab spawns
    // so the spawned PlayerController can read the local player's chosen name
    // without needing a direct reference to the UI.
    public static string LocalPlayerName = "Player";

    // Eight distinct colors, one per player slot (indexed by OwnerClientId % 8).
    private static readonly Color[] PlayerColors = new Color[]
    {
        Color.red,
        Color.blue,
        Color.green,
        Color.yellow,
        Color.cyan,
        Color.magenta,
        new Color(1f, 0.5f, 0f),    // orange
        new Color(0.5f, 0f, 1f),    // purple
    };

    // NetworkVariable<int> is replicated automatically by NGO to all clients.
    // The server writes the value; all clients (including the server) can read it.
    // When the value changes on any client, OnValueChanged fires on that client.
    private NetworkVariable<int> _colorIndex = new NetworkVariable<int>(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    // The server assigns a 1-based player number using ConnectedClients.Count at spawn time.
    // Storing it in a NetworkVariable replicates it to every client so every instance
    // of the label (the one floating above the remote player's head) shows the right number.
    private NetworkVariable<int> _playerNumber = new NetworkVariable<int>(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    // FixedString32Bytes is a value type (no heap allocation) safe for use in NetworkVariable.
    // It stores up to 32 bytes of UTF-8 text — sufficient for an adjective+noun name.
    private NetworkVariable<FixedString32Bytes> _playerName = new NetworkVariable<FixedString32Bytes>(
        new FixedString32Bytes("Player"),
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private InputAction _moveAction;
    private InputAction _fireAction;

    // Cached reference to this object's CapsuleCollider, used for the pre-move
    // overlap check. Cached once at spawn to avoid calling GetComponent every frame.
    private CapsuleCollider _collider;

    // Reusable buffer for Physics.OverlapCapsuleNonAlloc — avoids a heap allocation
    // every frame. Size 8 is enough for any realistic player count in this sample.
    private readonly Collider[] _overlapBuffer = new Collider[8];

    // OnNetworkSpawn() is called by NGO after this object is spawned on the network
    // and ownership has been assigned. Use this instead of Start() for any setup
    // that depends on knowing IsOwner, IsServer, or IsClient.
    public override void OnNetworkSpawn()
    {
        // The server assigns each player a color index based on their client ID.
        // OwnerClientId is 0 for the host, 1 for the first client, etc.
        // Modulo 8 keeps the index within the PlayerColors array bounds.
        if (IsServer)
        {
            _colorIndex.Value = (int)(OwnerClientId % (ulong)PlayerColors.Length);

            // ConnectedClients only contains real game clients — the dedicated server
            // (client ID 0) is NOT included. So Count is 1 when the first client spawns,
            // 2 for the second, and so on. This avoids the N-1 adjustment that would
            // be needed if we used OwnerClientId directly.
            _playerNumber.Value = NetworkManager.ConnectedClients.Count;

            // Spread players along the X axis so they don't spawn on top of each other.
            // _playerNumber is 1-based, so subtract 1 to put the first player at the origin.
            // The server sets this initial position before the owner's NetworkTransform
            // takes over, so it becomes the client's starting location.
            // Y is 1 to raise the capsule above the floor plane — the default capsule is
            // 2 units tall, so its center must be 1 unit up to sit flush on the ground.
            transform.position = new Vector3((_playerNumber.Value - 1) * 1f, 1f, 0f);
        }

        // Subscribe to future color changes so all clients update visuals
        // if the value arrives after OnNetworkSpawn (common on late-joining clients).
        _colorIndex.OnValueChanged += OnColorChanged;

        // Apply the color immediately in case the value was already set.
        ApplyColor(_colorIndex.Value);

        // Subscribe so late-joining clients update the label if the value arrives after spawn.
        _playerNumber.OnValueChanged += OnPlayerNumberChanged;

        // Apply the player number label immediately in case the value is already set.
        ApplyPlayerNumber(_playerNumber.Value);

        // Subscribe to name changes so all clients update the label when it replicates.
        _playerName.OnValueChanged += OnPlayerNameChanged;

        // Apply immediately in case the value already arrived.
        ApplyPlayerName(_playerName.Value.ToString());

        // Cache the collider on every machine so the overlap check (owner) and
        // any future server checks can reference it without a per-frame GetComponent.
        _collider = GetComponent<CapsuleCollider>();

        // IsOwner is true only on the instance that owns this object.
        // Without this guard, every client would set up input for every player.
        if (!IsOwner)
            return;

        // Send our chosen name to the server so it can store it in the NetworkVariable
        // and replicate it to all clients (including ourselves).
        SetPlayerNameServerRpc(LocalPlayerName);

        // Find the "Move" action defined in the InputSystem_Actions asset.
        // This maps to WASD keys and left gamepad stick by default.
        _moveAction = InputSystem.actions.FindAction("Move");
        if (_moveAction == null)
        {
            Debug.LogError("[PlayerController] Move action not found!");
            return;
        }

        // Actions must be explicitly enabled before they produce input values.
        _moveAction.Enable();

        // The Attack action (left mouse button / gamepad West) spawns a coin.
        // It reuses an existing action from the Input Actions asset rather than
        // adding a new one, keeping the asset tidy for this teaching sample.
        _fireAction = InputSystem.actions.FindAction("Attack");
        if (_fireAction == null)
        {
            Debug.LogError("[PlayerController] Attack action not found!");
            return;
        }
        _fireAction.Enable();

        // Tell the camera to follow this player.
        // Camera.main finds the scene camera tagged "MainCamera".
        // Only the owning client reaches this code, so each client's camera
        // follows only their own player.
        CameraController cam = Camera.main?.GetComponent<CameraController>();
        if (cam != null)
            cam.SetTarget(transform);
    }

    // OnNetworkDespawn() is called when this object is removed from the network.
    // Always unsubscribe from NetworkVariable callbacks here to avoid memory leaks.
    public override void OnNetworkDespawn()
    {
        _colorIndex.OnValueChanged -= OnColorChanged;
        _playerNumber.OnValueChanged -= OnPlayerNumberChanged;
        _playerName.OnValueChanged -= OnPlayerNameChanged;
    }

    private void OnColorChanged(int previous, int current)
    {
        ApplyColor(current);
    }

    private void OnPlayerNumberChanged(int previous, int current)
    {
        ApplyPlayerNumber(current);
    }

    // ServerRpc: the owning client calls this to tell the server their chosen name.
    // RequireOwnership = false allows the server itself (host) to call it too.
    // ServerRpcParams lets us read the actual sender's client ID on the server side.
    [ServerRpc(RequireOwnership = false)]
    private void SetPlayerNameServerRpc(FixedString32Bytes name, ServerRpcParams rpcParams = default)
    {
        // Only accept the name from the player who owns this object.
        if (rpcParams.Receive.SenderClientId != OwnerClientId)
            return;

        _playerName.Value = name;
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
        // Player number is now shadowed by the name once it arrives.
        // We only use it as a fallback while the name NetworkVariable hasn't replicated yet.
        if (_nameLabel != null && number > 0 && _playerName.Value.IsEmpty)
            _nameLabel.text = $"P{number}";
    }

    private void ApplyColor(int index)
    {
        // GetComponent<Renderer>() gets the MeshRenderer on this capsule.
        // .material creates a unique material instance per object so changing
        // one player's color doesn't affect the others.
        Renderer rend = GetComponent<Renderer>();
        if (rend != null)
            rend.material.color = PlayerColors[index];
    }

    private void Update()
    {
        // Only the owning client should move this player.
        // Non-owners receive position updates automatically via NetworkTransform.
        if (!IsOwner)
            return;

        // Ignore input when this window doesn't have OS focus.
        // Keyboard input is naturally focus-gated by Windows, but gamepad input is
        // read directly from the hardware and reaches all running instances simultaneously.
        // This check makes controller behaviour consistent with keyboard behaviour.
        if (!Application.isFocused)
            return;

        // Block movement until the lobby leader has started the game.
        if (GameManager.Instance == null || !GameManager.Instance.GameStarted.Value)
            return;

        // triggered is true for exactly one frame when a button action is performed.
        // We check _fireAction before movement so a blocked move doesn't prevent firing.
        if (_fireAction != null && _fireAction.triggered)
            SpawnCoinServerRpc(transform.position);

        // ReadValue<Vector2>() returns the current WASD or stick input this frame:
        // x = horizontal (-1 left, +1 right), y = vertical (-1 back, +1 forward)
        Vector2 input = _moveAction.ReadValue<Vector2>();

        // Map the 2D input onto the XZ plane (Y=0 keeps the player on the ground).
        // Multiply by speed and Time.deltaTime to make movement frame-rate independent.
        Vector3 move = new Vector3(input.x, 0, input.y) * _moveSpeed * Time.deltaTime;

        // Before moving, check whether the proposed position would overlap another player.
        // This runs entirely on the owning client — no network round-trip, no latency.
        if (WouldOverlapPlayer(transform.position + move))
            return;

        // Translate moves the transform in world space.
        // NetworkTransform will replicate this position change to all other clients.
        transform.Translate(move, Space.World);
    }

    // Returns true if placing this capsule at proposedPosition would overlap
    // another player's collider. Uses NonAlloc to avoid per-frame heap allocations.
    private bool WouldOverlapPlayer(Vector3 proposedPosition)
    {
        if (_collider == null)
            return false;

        // Compute the two sphere centres of the capsule at the proposed position.
        // The capsule stands upright (Y axis), height 2, radius 0.5.
        // The sphere centres are offset by (halfHeight - radius) along Y from the
        // capsule's centre, keeping them inside the end caps.
        float halfHeight = _collider.height / 2f;
        Vector3 up = Vector3.up * (halfHeight - _collider.radius);
        Vector3 point1 = proposedPosition + up;
        Vector3 point2 = proposedPosition - up;

        int hitCount = Physics.OverlapCapsuleNonAlloc(
            point1, point2, _collider.radius, _overlapBuffer);

        for (int i = 0; i < hitCount; i++)
        {
            Collider hit = _overlapBuffer[i];

            // Skip self — our own collider will always be in the results.
            if (hit == _collider)
                continue;

            // Only block movement for other players, not the floor or other geometry.
            if (hit.GetComponent<PlayerController>() != null)
                return true;
        }

        return false;
    }

    // The owning client sends this RPC to ask the server to spawn a coin.
    // Spawning always happens on the server — clients never call Spawn() directly.
    // We accept the position as a parameter because the server cannot reliably read
    // the client's transform; passing the value explicitly makes the data flow clear.
    [ServerRpc]
    private void SpawnCoinServerRpc(Vector3 position)
    {
        if (_coinPrefab == null)
        {
            Debug.LogError("[PlayerController] Coin Prefab is not assigned on the Player prefab.");
            return;
        }

        GameObject coin = Instantiate(_coinPrefab, position, Quaternion.identity);

        // Spawn() registers the object with NGO and replicates it to all clients.
        // destroyWithScene: true ensures it's cleaned up if the scene is unloaded.
        coin.GetComponent<NetworkObject>().Spawn(destroyWithScene: true);
    }

    private void LateUpdate()
    {
        // Billboard the label so it always faces the camera regardless of player rotation.
        // This uses the camera's own rotation axes to orient the label correctly.
        if (_nameLabel != null && Camera.main != null)
        {
            _nameLabel.transform.LookAt(
                _nameLabel.transform.position + Camera.main.transform.rotation * Vector3.forward,
                Camera.main.transform.rotation * Vector3.up
            );
        }
    }
}

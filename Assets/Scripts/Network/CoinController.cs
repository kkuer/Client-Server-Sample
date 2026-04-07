using System.Collections;
using UnityEngine;
using Unity.Netcode;

// CoinController manages the lifetime of a spawned coin network object.
// It lives on the Coin prefab alongside NetworkObject.
//
// NGO ownership model for spawned objects:
//   - The server instantiates and calls Spawn(), so the server owns the object.
//   - Only the server runs the despawn countdown — clients just wait for the
//     Despawn message to arrive, which destroys the object automatically.
public class CoinController : NetworkBehaviour
{
    // How long (in seconds) the coin stays active before the server removes it.
    // Exposed in the Inspector so you can tweak it per-prefab without changing code.
    [SerializeField] private float _lifetime = 5f;

    // Degrees per second for the visual spin around the Y axis.
    // Pure client-side — no NetworkTransform needed because the coin never moves.
    [SerializeField] private float _spinSpeed = 180f;

    // OnNetworkSpawn() is called on every machine (server + all clients) after
    // the object is spawned on the network. We gate the countdown behind IsServer
    // so only the server manages lifetime — the authoritative source of truth.
    public override void OnNetworkSpawn()
    {
        if (IsServer)
            StartCoroutine(DespawnAfterDelay());
    }

    private IEnumerator DespawnAfterDelay()
    {
        yield return new WaitForSeconds(_lifetime);

        // Despawn(true) tells NGO to remove this object from the network and
        // destroy it on the server and every connected client simultaneously.
        // Passing true means "also destroy the underlying GameObject."
        NetworkObject.Despawn(true);
    }

    private void Update()
    {
        // Rotate around the world Y axis every frame. This runs on every machine
        // independently — no networking required for a purely visual effect.
        transform.Rotate(0f, _spinSpeed * Time.deltaTime, 0f, Space.World);
    }
}

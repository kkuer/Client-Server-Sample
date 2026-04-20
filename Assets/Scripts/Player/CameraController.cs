using UnityEngine;

// CameraController follows the local player's head position and handles look rotation.
// Attach this script to the Main Camera in the scene.
// PlayerController calls SetTarget() on spawn to assign the correct player.
public class CameraController : MonoBehaviour
{
    [SerializeField] private Vector3 _offset = new Vector3(0, 0.7f, 0); // Eye height
    [SerializeField] private float _lookSensitivity = 2f;
    [SerializeField] private float _maxLookAngle = 80f;

    private Transform _target;
    private float _verticalRotation;

    // Called by PlayerController.OnNetworkSpawn() on the owning client only.
    public void SetTarget(Transform target)
    {
        _target = target;
        transform.SetParent(target);
        transform.localPosition = _offset;
        transform.localRotation = Quaternion.identity;

        //Cursor.lockState = CursorLockMode.Locked;
        //Cursor.visible = false;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    private void Update()
    {
        if (_target == null || !Application.isFocused) return;

        if (GameManager.Instance == null || !GameManager.Instance.GameStarted.Value) return;

        Vector2 lookInput = UnityEngine.InputSystem.InputSystem.actions.FindAction("Look")?.ReadValue<Vector2>() ?? Vector2.zero;
        Vector2 rotation = lookInput * _lookSensitivity * Time.deltaTime;

        _target.Rotate(Vector3.up * rotation.x);

        _verticalRotation -= rotation.y;
        _verticalRotation = Mathf.Clamp(_verticalRotation, -_maxLookAngle, _maxLookAngle);
        transform.localRotation = Quaternion.Euler(_verticalRotation, 0f, 0f);
    }

    private void OnDestroy()
    {
        //Cursor.lockState = CursorLockMode.None;
        //Cursor.visible = true;
    }
}
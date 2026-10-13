using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace OsrsPort
{
    /// <summary>
    /// First-person WASD controller. Mouse to look, Shift to run, Space to jump,
    /// N to toggle noclip (fly through walls), Esc to free the cursor, click to recapture.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class FirstPersonController : MonoBehaviour
    {
        public float walkSpeed = 2.5f;
        public float runSpeed = 4.5f;
        public float flySpeed = 8f;
        public float jumpHeight = 0.6f;
        public float gravity = -20f;
        public float lookSensitivity = 0.12f;
        public float eyeHeight = 1.35f;

        private CharacterController body;
        private Transform cameraPivot;
        private float pitch;
        private float verticalVelocity;
        private bool noclip;

        private void Awake()
        {
            body = GetComponent<CharacterController>();
            body.height = 1.5f;
            body.radius = 0.3f;
            body.center = new Vector3(0f, 0.75f, 0f);
            body.stepOffset = 0.45f;
            body.slopeLimit = 55f;

            var camGo = new GameObject("PlayerCamera");
            camGo.tag = "MainCamera";
            cameraPivot = camGo.transform;
            cameraPivot.SetParent(transform, false);
            cameraPivot.localPosition = new Vector3(0f, eyeHeight, 0f);
            var cam = camGo.AddComponent<Camera>();
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 300f;
            cam.fieldOfView = 75f;
            camGo.AddComponent<AudioListener>();

            LockCursor(true);
        }

        private void Update()
        {
            ReadInput(out var move, out var look, out bool run, out bool jump, out bool up, out bool down,
                out bool toggleNoclip, out bool escape, out bool click);

            if (escape) LockCursor(false);
            if (click && Cursor.lockState != CursorLockMode.Locked) LockCursor(true);
            if (toggleNoclip)
            {
                noclip = !noclip;
                body.enabled = !noclip;
                verticalVelocity = 0f;
            }

            if (Cursor.lockState == CursorLockMode.Locked)
            {
                transform.Rotate(0f, look.x * lookSensitivity, 0f);
                pitch = Mathf.Clamp(pitch - look.y * lookSensitivity, -89f, 89f);
                cameraPivot.localEulerAngles = new Vector3(pitch, 0f, 0f);
            }

            var loader = OsrsWorldLoader.Instance;
            if (!noclip && loader != null && !loader.GroundReadyAt(transform.position))
            {
                verticalVelocity = 0f;
                return;
            }

            if (noclip)
            {
                Vector3 dir = cameraPivot.forward * move.y + cameraPivot.right * move.x;
                if (up) dir += Vector3.up;
                if (down) dir += Vector3.down;
                transform.position += dir * (flySpeed * (run ? 2f : 1f) * Time.deltaTime);
                return;
            }

            Vector3 planar = (transform.forward * move.y + transform.right * move.x);
            if (planar.sqrMagnitude > 1f) planar.Normalize();
            planar *= run ? runSpeed : walkSpeed;

            if (body.isGrounded)
            {
                verticalVelocity = -1f;
                if (jump) verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
            }
            verticalVelocity += gravity * Time.deltaTime;

            body.Move((planar + Vector3.up * verticalVelocity) * Time.deltaTime);

            if (transform.position.y < -50f)
                transform.position = new Vector3(transform.position.x, 30f, transform.position.z);
        }

        private static void LockCursor(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }

        private static void ReadInput(out Vector2 move, out Vector2 look, out bool run, out bool jump,
            out bool up, out bool down, out bool toggleNoclip, out bool escape, out bool click)
        {
            move = Vector2.zero;
            look = Vector2.zero;
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            if (kb != null)
            {
                if (kb.wKey.isPressed) move.y += 1;
                if (kb.sKey.isPressed) move.y -= 1;
                if (kb.dKey.isPressed) move.x += 1;
                if (kb.aKey.isPressed) move.x -= 1;
            }
            run = kb != null && kb.leftShiftKey.isPressed;
            jump = kb != null && kb.spaceKey.wasPressedThisFrame;
            up = kb != null && kb.spaceKey.isPressed;
            down = kb != null && kb.leftCtrlKey.isPressed;
            toggleNoclip = kb != null && kb.nKey.wasPressedThisFrame;
            escape = kb != null && kb.escapeKey.wasPressedThisFrame;
            click = mouse != null && mouse.leftButton.wasPressedThisFrame;
            if (mouse != null) look = mouse.delta.ReadValue();
#else
            move = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
            look = new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y")) * 10f;
            run = Input.GetKey(KeyCode.LeftShift);
            jump = Input.GetKeyDown(KeyCode.Space);
            up = Input.GetKey(KeyCode.Space);
            down = Input.GetKey(KeyCode.LeftControl);
            toggleNoclip = Input.GetKeyDown(KeyCode.N);
            escape = Input.GetKeyDown(KeyCode.Escape);
            click = Input.GetMouseButtonDown(0);
#endif
        }
    }
}

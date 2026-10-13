using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace OsrsPort
{
    /// <summary>
    /// First-person WASD controller. Mouse to look, Shift to run, Space to jump,
    /// N to toggle noclip (fly through walls), V to toggle third person, Left Alt to dodge roll,
    /// Esc to free the cursor, click to recapture.
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
        /// <summary>Extra camera rotation (pitch, yaw, roll) other scripts add for swings and blocking.</summary>
        public Vector3 cameraOffsetEuler;
        public float thirdPersonDistance = 3.5f;
        public float dodgeDistance = 2.5f;
        public float dodgeDuration = 0.35f;
        public float dodgeCooldown = 1.2f;
        /// <summary>NPC id whose model stands in for the player when seen from behind (3106 = Man).</summary>
        public int avatarNpcId = 3106;

        public bool ThirdPerson { get; private set; }
        public bool Dodging => Time.time < dodgeEnd;
        private float dodgeEnd = -10f, dodgeStart = -10f;
        private Vector3 dodgeDir;
        private Transform cameraBoom;
        private GameObject avatar;

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

            var pivotGo = new GameObject("CameraPivot");
            cameraPivot = pivotGo.transform;
            cameraPivot.SetParent(transform, false);
            cameraPivot.localPosition = new Vector3(0f, eyeHeight, 0f);
            var camGo = new GameObject("PlayerCamera");
            camGo.tag = "MainCamera";
            cameraBoom = camGo.transform;
            cameraBoom.SetParent(cameraPivot, false);
            var cam = camGo.AddComponent<Camera>();
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 300f;
            cam.fieldOfView = 75f;
            camGo.AddComponent<AudioListener>();

            LockCursor(true);
        }

        private void Update()
        {
            if (OsrsInteraction.TextInputActive) return;
            ReadInput(out var move, out var look, out bool run, out bool jump, out bool up, out bool down,
                out bool toggleNoclip, out bool escape, out bool click, out bool toggleView, out bool dodge);

            if (toggleView) SetThirdPerson(!ThirdPerson);
            UpdateCameraBoom();
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
                cameraPivot.localEulerAngles = new Vector3(pitch, 0f, 0f) + cameraOffsetEuler;
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
            if (dodge && !Dodging && Time.time - dodgeStart > dodgeCooldown && body.isGrounded)
            {
                dodgeStart = Time.time;
                dodgeEnd = Time.time + dodgeDuration;
                dodgeDir = planar.sqrMagnitude > 0.01f ? planar.normalized : -transform.forward;
                OsrsServerLink.Instance?.Send("DODGE");
            }
            if (Dodging)
                planar = dodgeDir * (dodgeDistance / dodgeDuration);
            else
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

        /// <summary>Turns the body and camera toward a world point; t = 1 snaps, smaller values ease.</summary>
        public void LookToward(Vector3 point, float t)
        {
            var eye = cameraPivot.position;
            var dir = point - eye;
            var flat = new Vector3(dir.x, 0f, dir.z);
            if (flat.sqrMagnitude < 0.0001f) return;
            float yaw = Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg;
            float wantPitch = -Mathf.Atan2(dir.y, flat.magnitude) * Mathf.Rad2Deg;
            float curYaw = transform.eulerAngles.y;
            transform.rotation = Quaternion.Euler(0f, Mathf.LerpAngle(curYaw, yaw, t), 0f);
            pitch = Mathf.Clamp(Mathf.Lerp(pitch, wantPitch, t), -89f, 89f);
            cameraPivot.localEulerAngles = new Vector3(pitch, 0f, 0f) + cameraOffsetEuler;
        }

        public void SetThirdPerson(bool on)
        {
            ThirdPerson = on;
            if (on && avatar == null) avatar = BuildAvatar();
            if (avatar != null) avatar.SetActive(on);
        }

        /// <summary>Pulls the camera back behind the shoulder in third person, stopping short of walls.</summary>
        private void UpdateCameraBoom()
        {
            if (!ThirdPerson) { cameraBoom.localPosition = Vector3.zero; return; }
            var origin = cameraPivot.position;
            var back = -cameraPivot.forward;
            float dist = thirdPersonDistance;
            if (Physics.SphereCast(origin, 0.25f, back, out var hit, thirdPersonDistance, OsrsNpcManager.WorldMask))
                dist = Mathf.Max(0.3f, hit.distance - 0.1f);
            cameraBoom.localPosition = new Vector3(0.35f, 0.25f, -dist);
            float roll = Dodging ? Mathf.Sin(Mathf.Clamp01((Time.time - dodgeStart) / dodgeDuration) * Mathf.PI) * 20f : 0f;
            if (avatar != null) avatar.transform.localRotation = Quaternion.Euler(roll, 0f, 0f);
        }

        private GameObject BuildAvatar()
        {
            var loader = OsrsWorldLoader.Instance;
            if (loader == null || loader.Data == null) return null;
            var vm = loader.Data.GetNpcMesh(avatarNpcId);
            if (vm.faceCount == 0) return null;
            int nv = vm.faceCount * 3;
            var verts = new Vector3[nv]; var normals = new Vector3[nv]; var idx = new int[nv];
            for (int i = 0; i < nv; i++) { verts[i] = new Vector3(vm.positions[i * 3], vm.positions[i * 3 + 1], vm.positions[i * 3 + 2]) / 128f; idx[i] = i; }
            for (int i = 0; i < nv; i += 3) { var n = Vector3.Cross(verts[i + 1] - verts[i], verts[i + 2] - verts[i]).normalized; normals[i] = normals[i + 1] = normals[i + 2] = n; }
            var mesh = new Mesh { name = "player_avatar", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(verts); mesh.SetNormals(normals); mesh.SetColors(vm.colors); mesh.SetTriangles(idx, 0, false); mesh.RecalculateBounds();
            var go = new GameObject("Avatar") { layer = OsrsNpcManager.PlayerLayer };
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            var npcs = FindFirstObjectByType<OsrsNpcManager>();
            r.sharedMaterial = npcs != null ? npcs.material : new Material(Shader.Find("OsrsPort/VertexColorLit"));
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return go;
        }

        private static void LockCursor(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }

        private static void ReadInput(out Vector2 move, out Vector2 look, out bool run, out bool jump,
            out bool up, out bool down, out bool toggleNoclip, out bool escape, out bool click,
            out bool toggleView, out bool dodge)
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
            toggleView = kb != null && kb.vKey.wasPressedThisFrame;
            dodge = kb != null && kb.leftAltKey.wasPressedThisFrame;
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
            toggleView = Input.GetKeyDown(KeyCode.V);
            dodge = Input.GetKeyDown(KeyCode.LeftAlt);
#endif
        }
    }
}

using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace OsrsPort
{
    /// <summary>
    /// First-person WASD controller. Mouse to look, Shift to run, Space to jump,
    /// N to toggle noclip (fly through walls), V or F5 to toggle third person, Left Alt to dodge roll,
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
        public float eyeHeight = 1.42f;
        /// <summary>How far ahead of the body's centre the first-person eye sits, so the head isn't in view.</summary>
        public float eyeForward = 0.22f;
        /// <summary>Arms, hands, weapon and shield on the first-person camera, in front of the eye.</summary>
        private static readonly Vector3 FP_ARMS_POS = new Vector3(0.02f, -0.8f, 0.7f);
        private static readonly Vector3 FP_ARMS_EULER = new Vector3(25f, 180f, 0f);
        /// <summary>Extra camera rotation (pitch, yaw, roll) other scripts add for swings and blocking.</summary>
        public Vector3 cameraOffsetEuler;
        public float thirdPersonDistance = 3.5f;
        public float dodgeDistance = 2.5f;
        public float dodgeDuration = 0.35f;
        public float dodgeCooldown = 1.2f;
        /// <summary>NPC id whose model stands in for the player until the server sends our real body (PLOOK).</summary>
        public int avatarNpcId = 3106;

        public bool ThirdPerson { get; private set; }
        public bool Dodging => Time.time < dodgeEnd;
        private float dodgeEnd = -10f, dodgeStart = -10f;
        private Vector3 dodgeDir;
        private Transform cameraBoom;
        private GameObject avatar;
        private GameObject fpArms;
        private string avatarLookKey;
        private int[] avatarLookRef;

        private CharacterController body;
        private Transform cameraPivot;
        private float pitch;
        private float verticalVelocity;
        private bool noclip;

        private void Awake()
        {
            body = GetComponent<CharacterController>();
            body.height = 1.5f;
            body.radius = 0.22f;
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
            cam.nearClipPlane = 0.03f;
            cam.farClipPlane = 300f;
            cam.fieldOfView = 75f;
            camGo.AddComponent<AudioListener>();

            LockCursor(true);
        }

        private void Update()
        {
            if (OsrsInteraction.TextInputActive || OsrsPanels.UiOpen) return;
            ReadInput(out var move, out var look, out bool run, out bool jump, out bool up, out bool down,
                out bool toggleNoclip, out bool escape, out bool click, out bool toggleView, out bool dodge);

            if (toggleView) SetThirdPerson(!ThirdPerson);
            RefreshAvatar();
            UpdateCameraBoom();
            if (escape) LockCursor(false);
            if (click && Cursor.lockState != CursorLockMode.Locked && !OsrsInteraction.CursorMode) LockCursor(true);
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
            // Stunned (failed pickpocket) or bound: no walking, jumping or dodging until the server says FROZEN 0.
            var srv = OsrsServerLink.Instance;
            if (srv != null && srv.Frozen) { planar = Vector3.zero; jump = false; dodge = false; }
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
            RefreshAvatar();
        }

        /// <summary>Rebuilds the avatar when our look (identkits, colours, worn items) changed.</summary>
        private void RefreshAvatar()
        {
            var look = OsrsServerLink.Instance != null ? OsrsServerLink.Instance.SelfLook : null;
            if (avatar != null && !string.IsNullOrEmpty(avatarLookKey) && ReferenceEquals(look, avatarLookRef)) return;
            avatarLookRef = look;
            string key = look != null && OsrsPlayerModels.GetMesh(look) != null ? OsrsPlayerModels.Key(look) : "";
            if (avatar != null && key == avatarLookKey) return;
            if (avatar != null) Destroy(avatar);
            avatar = BuildAvatar();
            avatarLookKey = key;
        }

        /// <summary>Pulls the camera back behind the shoulder in third person, stopping short of walls.</summary>
        private void UpdateCameraBoom()
        {
            float roll = Dodging ? Mathf.Sin(Mathf.Clamp01((Time.time - dodgeStart) / dodgeDuration) * Mathf.PI) * 20f : 0f;
            if (avatar != null) avatar.transform.localRotation = Quaternion.Euler(-roll, 180f, 0f);
            if (!ThirdPerson)
            {
                cameraPivot.localPosition = new Vector3(0f, eyeHeight, eyeForward);
                cameraBoom.localPosition = Vector3.zero;
                return;
            }
            cameraPivot.localPosition = new Vector3(0f, eyeHeight, 0f);
            var origin = cameraPivot.position;
            var back = -cameraPivot.forward;
            float dist = thirdPersonDistance;
            if (Physics.SphereCast(origin, 0.25f, back, out var hit, thirdPersonDistance, OsrsNpcManager.WorldMask))
                dist = Mathf.Max(0.3f, hit.distance - 0.1f);
            cameraBoom.localPosition = new Vector3(0.35f, 0.25f, -dist);
        }

        private GameObject BuildAvatar()
        {
            var loader = OsrsWorldLoader.Instance;
            if (loader == null || loader.Data == null) return null;
            // Our real OSRS body when the server sent it (PLOOK me); otherwise the stand-in NPC.
            var look = OsrsServerLink.Instance != null ? OsrsServerLink.Instance.SelfLook : null;
            var mesh = OsrsPlayerModels.GetMesh(look);
            if (mesh == null)
            {
                var vm = loader.Data.GetNpcMesh(avatarNpcId);
                if (vm.faceCount == 0) return null;
                mesh = OsrsPlayerModels.ToMesh(vm, "player_avatar");
            }
            var go = new GameObject("Avatar") { layer = OsrsNpcManager.PlayerLayer };
            go.transform.SetParent(transform, false);
            var animData = loader.Data.anim;
            if (animData != null && animData.Loaded)
            {
                // Animated body: stand / walk / run from our BAS, actions from the server's ANIM.
                bool real = OsrsPlayerModels.GetMesh(look) != null;
                var ad = real ? loader.Data.BuildPlayerAnimData(look) : loader.Data.BuildNpcAnimData(avatarNpcId);
                if (ad != null)
                {
                    var an = go.AddComponent<OsrsAnimator>();
                    mesh = an.Init(animData, ad, "player_avatar_anim");
                    if (!real && loader.Data.npcs.TryGetValue(avatarNpcId, out var stand))
                    { an.standSeq = stand.standSeq; an.walkSeq = stand.walkSeq; an.runSeq = stand.runSeq; }
                    OsrsAnimator.Local = an;
                }
            }
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            var npcs = FindAnyObjectByType<OsrsNpcManager>();
            r.sharedMaterial = npcs != null ? npcs.material : new Material(Shader.Find("OsrsPort/VertexColorLit"));
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return go;
        }

        /// <summary>Drives the avatar's base animation from our actual movement and the server's BAS.</summary>
        private void LateUpdate()
        {
            UpdateFpArms();
            if (avatar == null) return;
            var an = avatar.GetComponent<OsrsAnimator>();
            if (an == null) return;
            var v = body.enabled ? body.velocity : Vector3.zero;
            v.y = 0f;
            float speed = v.magnitude;
            an.moving = speed > 0.3f;
            an.running = speed > (walkSpeed + runSpeed) * 0.5f;
            var bas = OsrsServerLink.Instance != null ? OsrsServerLink.Instance.SelfBas : null;
            if (bas != null) OsrsNpcManager.SetBas(an, bas);
        }

        private void UpdateFpArms()
        {
            var bodyRenderer = avatar != null ? avatar.GetComponent<MeshRenderer>() : null;
            if (bodyRenderer != null) bodyRenderer.enabled = ThirdPerson;
            if (ThirdPerson || avatar == null || cameraBoom == null)
            {
                if (fpArms != null) fpArms.SetActive(false);
                return;
            }
            var look = OsrsServerLink.Instance != null ? OsrsServerLink.Instance.SelfLook : null;
            var mesh = OsrsPlayerModels.GetArmsMesh(look);
            if (mesh == null) return;
            if (fpArms == null)
            {
                fpArms = new GameObject("FpArms");
                fpArms.transform.SetParent(cameraBoom, false);
                fpArms.transform.localPosition = FP_ARMS_POS;
                fpArms.transform.localEulerAngles = FP_ARMS_EULER;
                fpArms.AddComponent<MeshFilter>();
                var rend = fpArms.AddComponent<MeshRenderer>();
                rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                var npcs = FindAnyObjectByType<OsrsNpcManager>();
                rend.sharedMaterial = npcs != null ? npcs.material
                    : bodyRenderer != null ? bodyRenderer.sharedMaterial : null;
            }
            fpArms.SetActive(true);
            fpArms.GetComponent<MeshFilter>().sharedMesh = mesh;
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
            toggleView = kb != null && (kb.vKey.wasPressedThisFrame || kb.f5Key.wasPressedThisFrame);
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
            toggleView = Input.GetKeyDown(KeyCode.V) || Input.GetKeyDown(KeyCode.F5);
            dodge = Input.GetKeyDown(KeyCode.LeftAlt);
#endif
        }
    }
}

using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Builds ready-to-play enemy prefabs for the ZombieKit zombies, each holding the kit Rifle:
///  - marks the FBX Idle/Walk clips as looping and gives every model an Avatar,
///  - analyses Rifle.fbx (barrel axis, up axis, grip / handguard / muzzle points),
///  - bakes per zombie: Shoot (rifle aimed + recoil), IdleRifle and WalkRifle (rifle held low-ready)
///    on top of the zombie's own Idle / Walk, with two-bone IK putting both hands on the rifle,
///  - creates an Animator Controller (Idle / Chase / Shoot, same parameters and transitions as EnemyAnimator),
///  - creates one prefab per zombie with NavMeshAgent, collider, Enemy, Health, health bar, rifle and a muzzle firePoint,
///  - renders a preview image per zombie to Temp/ZombiePreview_*.png.
/// Run it from Tools > Zombies > Build Zombie Enemies (safe to run again; it overwrites its own output).
/// </summary>
[InitializeOnLoad]
public static class ZombieEnemyBuilder
{
    const string Log = "[ZombieEnemyBuilder] ";
    const string RequestFile = "Temp/ZombieEnemyBuilder.request";

    const string OutDir = "Assets/Prefabs/Zombies";
    const string AnimDir = OutDir + "/Animations";
    const string BulletPath = "Assets/Prefabs/BulletEnemy.prefab";
    const string HealthBarPath = "Assets/Health/EnemyHealthBar.prefab";
    const string RiflePath = "Assets/ZombieKit/Weapons/Models/Rifle.fbx";
    const int EnemyLayer = 7; // same layer as Enemy.prefab / EnemyAnimated.prefab

    // Clips
    const float ShootLength = 1f;    // matches Enemy.timeBetweenAttacks so the recoil lines up with each bullet
    const float FrameRate = 30f;
    const float KickPeak = 0.07f;    // time the recoil reaches its peak
    const float KickRecover = 0.45f; // time the rifle is back on target
    const float RifleKickDeg = 14f;  // muzzle climb
    const float TorsoKickDeg = 6f;
    const float HeadKickDeg = 5f;
    const float LowReadyDeg = 28f;   // rifle pitched down while idle / chasing

    // Size
    const float TargetHeight = 1.5f; // outliers (Zombie_Basic is ~1m in its FBX) are scaled to the kit's size

    static readonly string[] LoopClips = { "Idle", "Walk", "Run", "Run_Arms", "Crawl", "Jump_Idle" };

    class ZombieDef
    {
        public string Name;
        public string Fbx;
        public float Speed;
        public float Health;
        public float Radius;
    }

    static readonly ZombieDef[] Zombies =
    {
        new ZombieDef { Name = "Zombie_Basic",  Fbx = "Assets/ZombieKit/Characters/Models/Zombie_Basic.fbx",  Speed = 2.0f, Health = 100f, Radius = 0.35f },
        new ZombieDef { Name = "Zombie_Arm",    Fbx = "Assets/ZombieKit/Characters/Models/Zombie_Arm.fbx",    Speed = 2.3f, Health = 80f,  Radius = 0.35f },
        new ZombieDef { Name = "Zombie_Chubby", Fbx = "Assets/ZombieKit/Characters/Models/Zombie_Chubby.fbx", Speed = 1.6f, Health = 160f, Radius = 0.45f },
    };

    class RifleInfo
    {
        public GameObject Asset;
        public Quaternion AxisRot; // LookRotation(barrel, up) in rifle-local space
        public Vector3 Grip, Support, Muzzle; // rifle-local points
        public float Length;       // rifle-local length
        public float WorldLength;  // length we want in the game world
    }

    class ZombieResult
    {
        public AnimationClip Shoot, IdleRifle, WalkRifle;
        public Vector3 Forward;      // model-space facing direction
        public float Height;         // model-space height
        public float Scale;          // model scale used in the prefab
        public Vector3 Muzzle;       // model-space muzzle position in the aim pose
        public string RifleBonePath; // path (from model root) of the bone holding the rifle
        public Vector3 RifleLocalPos;
        public Quaternion RifleLocalRot;
        public Vector3 RifleLocalScale;
    }

    static ZombieEnemyBuilder()
    {
        // One-shot auto run, requested by creating Temp/ZombieEnemyBuilder.request
        if (File.Exists(RequestFile))
        {
            File.Delete(RequestFile);
            EditorApplication.delayCall += Build;
        }
    }

    [MenuItem("Tools/Zombies/Build Zombie Enemies")]
    public static void Build()
    {
        Scene scene = default;
        try
        {
            EnsureFolder(OutDir);
            EnsureFolder(AnimDir);

            foreach (var z in Zombies) ConfigureImporter(z.Fbx);

            scene = EditorSceneManager.NewPreviewScene();
            var rifle = AnalyzeRifle(scene);

            AnimatorController baseController = null;
            AnimationClip baseIdle = null, baseWalk = null, baseShoot = null;

            foreach (var z in Zombies)
            {
                var assets = AssetDatabase.LoadAllAssetsAtPath(z.Fbx);
                var clips = assets.OfType<AnimationClip>().Where(c => !c.name.StartsWith("__preview__")).ToArray();
                var avatar = assets.OfType<Avatar>().FirstOrDefault();
                var idle = FindClip(clips, "Idle", z);
                var walk = FindClip(clips, "Walk", z);

                var r = BuildClips(z, idle, walk, rifle, scene);

                RuntimeAnimatorController controller;
                if (baseController == null)
                {
                    baseController = CreateController($"{AnimDir}/ZombieEnemy.controller", r.IdleRifle, r.WalkRifle, r.Shoot);
                    baseIdle = r.IdleRifle; baseWalk = r.WalkRifle; baseShoot = r.Shoot;
                    controller = baseController;
                }
                else
                {
                    controller = CreateOverride($"{AnimDir}/{z.Name}.overrideController", baseController,
                        new Dictionary<AnimationClip, AnimationClip> { { baseIdle, r.IdleRifle }, { baseWalk, r.WalkRifle }, { baseShoot, r.Shoot } });
                }

                BuildPrefab(z, controller, avatar, r, rifle, scene);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log(Log + "DONE: prefabs created in " + OutDir);
        }
        catch (System.Exception e)
        {
            Debug.LogError(Log + "FAILED: " + e);
        }
        finally
        {
            if (scene.IsValid()) EditorSceneManager.ClosePreviewScene(scene);
        }
    }

    // ---------------------------------------------------------------- import settings

    static void ConfigureImporter(string path)
    {
        var imp = (ModelImporter)AssetImporter.GetAtPath(path);
        if (imp == null) throw new System.Exception("Model not found: " + path);

        bool changed = false;
        if (imp.animationType != ModelImporterAnimationType.Generic)
        {
            imp.animationType = ModelImporterAnimationType.Generic; changed = true;
        }
        if (imp.avatarSetup != ModelImporterAvatarSetup.CreateFromThisModel)
        {
            imp.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel; changed = true;
        }

        var clips = imp.clipAnimations;
        if (clips == null || clips.Length == 0) clips = imp.defaultClipAnimations;
        bool clipsChanged = false;
        foreach (var c in clips)
        {
            bool loop = LoopClips.Any(n => ClipMatches(c.name, n));
            if (loop && !c.loopTime)
            {
                c.loopTime = true;
                clipsChanged = true;
            }
        }
        if (clipsChanged) { imp.clipAnimations = clips; changed = true; }

        if (changed)
        {
            imp.SaveAndReimport();
            Debug.Log(Log + "Reimported " + path + " (looping clips / avatar)");
        }
    }

    static bool ClipMatches(string clipName, string wanted) =>
        clipName == wanted || clipName.EndsWith("|" + wanted);

    static AnimationClip FindClip(AnimationClip[] clips, string wanted, ZombieDef z)
    {
        var clip = clips.FirstOrDefault(c => ClipMatches(c.name, wanted));
        if (clip == null)
            throw new System.Exception($"{z.Name}: clip '{wanted}' not found. Clips: {string.Join(", ", clips.Select(c => c.name))}");
        return clip;
    }

    // ---------------------------------------------------------------- rifle

    static RifleInfo AnalyzeRifle(Scene scene)
    {
        var asset = AssetDatabase.LoadAssetAtPath<GameObject>(RiflePath);
        if (asset == null) throw new System.Exception("Missing " + RiflePath);
        var go = (GameObject)Object.Instantiate(asset);
        SceneManager.MoveGameObjectToScene(go, scene);
        go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        go.transform.localScale = Vector3.one;
        try
        {
            var verts = new List<Vector3>();
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var m = mf.transform.localToWorldMatrix;
                verts.AddRange(mf.sharedMesh.vertices.Select(v => m.MultiplyPoint3x4(v)));
            }
            foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh == null) continue;
                var m = smr.transform.localToWorldMatrix;
                verts.AddRange(smr.sharedMesh.vertices.Select(v => m.MultiplyPoint3x4(v)));
            }
            if (verts.Count == 0) throw new System.Exception("Rifle has no mesh");

            var b = new Bounds(verts[0], Vector3.zero);
            foreach (var v in verts) b.Encapsulate(v);
            var ext = b.size;

            // Longest axis = barrel, next = vertical (magazine/grip/stock make rifles taller than wide)
            int[] order = new[] { 0, 1, 2 }.OrderByDescending(i => ext[i]).ToArray();
            int L = order[0], V = order[1];
            float uMin = b.min[L], uMax = b.max[L], len = uMax - uMin;

            float Thickness(bool high)
            {
                var sel = verts.Where(p => high ? p[L] > uMax - 0.15f * len : p[L] < uMin + 0.15f * len).ToList();
                return sel.Max(p => p[V]) - sel.Min(p => p[V]);
            }
            // The stock end is thicker than the muzzle end
            float thickHigh = Thickness(true), thickLow = Thickness(false);
            bool muzzleHigh = thickHigh < thickLow;
            Vector3 axL = Vector3.zero; axL[L] = 1f;
            Vector3 axV = Vector3.zero; axV[V] = 1f;
            Vector3 barrel = muzzleHigh ? axL : -axL;

            // Near the muzzle there is only the barrel, which sits above the rifle's centre (grip/magazine hang below)
            var front = verts.Where(p => muzzleHigh ? p[L] > uMax - 0.15f * len : p[L] < uMin + 0.15f * len).ToList();
            float vFront = front.Average(p => p[V]);
            bool upHigh = vFront >= b.center[V];
            Vector3 up = upHigh ? axV : -axV;
            float vBarrel = (vFront - b.center[V]) * (upHigh ? 1f : -1f); // barrel line height above the centre

            Vector3 P(float u, float v) => b.center + barrel * (u - len * 0.5f) + up * v;

            var info = new RifleInfo
            {
                Asset = asset,
                AxisRot = Quaternion.LookRotation(barrel, up),
                Grip = P(0.36f * len, vBarrel - 0.07f * len),    // pistol grip, right hand
                Support = P(0.66f * len, vBarrel - 0.04f * len), // handguard, left hand
                Muzzle = P(len, vBarrel),
                Length = len,
                WorldLength = (len >= 0.5f && len <= 1.6f) ? len : 1f,
            };
            Debug.Log(Log + $"Rifle: size={ext}, barrel={barrel}, up={up}, thickness muzzle/stock={Mathf.Min(thickHigh, thickLow):F3}/{Mathf.Max(thickHigh, thickLow):F3}, length={len:F3} -> world {info.WorldLength:F2}");
            return info;
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }

    // ---------------------------------------------------------------- clips

    static ZombieResult BuildClips(ZombieDef z, AnimationClip idle, AnimationClip walk, RifleInfo rifle, Scene scene)
    {
        var modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(z.Fbx);
        var go = (GameObject)Object.Instantiate(modelAsset);
        SceneManager.MoveGameObjectToScene(go, scene);
        go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        go.transform.localScale = Vector3.one;

        try
        {
            var bones = new Dictionary<string, Transform>();
            foreach (var t in go.GetComponentsInChildren<Transform>(true))
                if (!bones.ContainsKey(t.name)) bones[t.name] = t;
            Transform B(string n) => bones.TryGetValue(n, out var t) ? t : null;

            idle.SampleAnimation(go, 0f);

            // Height (skinned renderer bounds are stale in the editor: use the highest bone plus a margin)
            float height = bones.Values.Max(t => t.position.y) * 1.08f;
            float scale = (height < 1.2f || height > 2.6f) ? TargetHeight / height : 1f;

            // Facing direction from the shoulders (character's right x up = forward)
            var shL = B("Shoulder.L") ?? B("UpperArm.L");
            var shR = B("Shoulder.R") ?? B("UpperArm.R");
            Vector3 right = Vector3.right;
            if (shL != null && shR != null)
            {
                right = shR.position - shL.position;
                right.y = 0f;
                right = right.sqrMagnitude > 1e-8f ? right.normalized : Vector3.right;
            }
            Vector3 fwd = Vector3.Cross(right, Vector3.up).normalized;
            Vector3 up = Vector3.up;

            var torso = B("Torso") ?? B("Abdomen");
            var head = B("Head");
            var upR = B("UpperArm.R"); var loR = B("LowerArm.R"); var effR = B("Middle1.R");
            var upL = B("UpperArm.L"); var loL = B("LowerArm.L"); var effL = B("Middle1.L");
            if (upR == null || loR == null || effR == null || torso == null)
                throw new System.Exception(z.Name + ": right arm / torso bones not found");
            bool hasLeft = upL != null && loL != null && effL != null;

            float armLen = (loR.position - upR.position).magnitude + (effR.position - loR.position).magnitude;
            float shoulderWidth = shL != null && shR != null ? (shR.position - shL.position).magnitude : armLen * 0.6f;

            // ---- Reference aim pose: rifle level, butt near the right shoulder
            float rifleScale = rifle.WorldLength / rifle.Length / scale; // rifle size in model space
            Quaternion rifleRot = Quaternion.LookRotation(fwd, up) * Quaternion.Inverse(rifle.AxisRot);
            Vector3 gripTarget = upR.position + fwd * (0.45f * armLen) - up * (0.2f * armLen) - right * (0.25f * shoulderWidth);
            Vector3 riflePos = gripTarget - rifleRot * (rifle.Grip * rifleScale);
            Matrix4x4 rifleRef = Matrix4x4.TRS(riflePos, rifleRot, Vector3.one * rifleScale);

            Vector3 poleR = -up + right * 0.6f - fwd * 0.2f;
            Vector3 poleL = -up - right * 0.6f - fwd * 0.2f;

            TwoBoneIK(upR, loR, effR, gripTarget, poleR);
            Matrix4x4 rifleInForearm = loR.worldToLocalMatrix * rifleRef; // rifle rigidly held by the right forearm/hand
            Matrix4x4 torsoRefInv = torso.worldToLocalMatrix;
            Vector3 pivot = upR.position; // rotating around the shoulder keeps the elbow reachable

            void HoldRifle(float pitchDeg)
            {
                // Desired rifle: reference rotated around the shoulder, then carried by the torso's motion
                Matrix4x4 pitch = Matrix4x4.Translate(pivot) * Matrix4x4.Rotate(Quaternion.AngleAxis(pitchDeg, right)) * Matrix4x4.Translate(-pivot);
                Matrix4x4 rifleNow = torso.localToWorldMatrix * torsoRefInv * pitch * rifleRef;
                Matrix4x4 forearm = rifleNow * rifleInForearm.inverse;
                Vector3 elbow = forearm.GetColumn(3);
                Align(upR, loR.position - upR.position, elbow - upR.position);
                loR.rotation = forearm.rotation;

                if (hasLeft)
                {
                    Matrix4x4 rifleActual = loR.localToWorldMatrix * rifleInForearm;
                    TwoBoneIK(upL, loL, effL, rifleActual.MultiplyPoint3x4(rifle.Support), poleL);
                }
            }

            var posed = new List<Transform> { torso, head, upR, loR, upL, loL };

            // ---- Shoot: static aim on Idle frame 0 + recoil
            var shoot = Bake(go, $"{z.Name}_Shoot", idle, ShootLength, false, posed, t =>
            {
                idle.SampleAnimation(go, 0f);
                float k = Kick(t);
                Rotate(torso, right, -TorsoKickDeg * k); // negative around right = lean back
                Rotate(head, right, -HeadKickDeg * k);
                HoldRifle(-RifleKickDeg * k);            // negative = muzzle climbs
            });

            // Muzzle position in the aim pose (for the firePoint)
            idle.SampleAnimation(go, 0f);
            HoldRifle(0f);
            Vector3 muzzle = (loR.localToWorldMatrix * rifleInForearm).MultiplyPoint3x4(rifle.Muzzle);

            // ---- Idle / Walk holding the rifle low-ready
            var idleRifle = Bake(go, $"{z.Name}_IdleRifle", idle, idle.length, true, posed, t =>
            {
                idle.SampleAnimation(go, t);
                HoldRifle(LowReadyDeg);
            });
            var walkRifle = Bake(go, $"{z.Name}_WalkRifle", walk, walk.length, true, posed, t =>
            {
                walk.SampleAnimation(go, t);
                HoldRifle(LowReadyDeg);
            });

            Matrix4x4 local = rifleInForearm;
            Debug.Log(Log + $"{z.Name}: height={height:F2} scale={scale:F2} armLen={armLen:F3} muzzle={muzzle} forward={fwd}");
            return new ZombieResult
            {
                Shoot = shoot,
                IdleRifle = idleRifle,
                WalkRifle = walkRifle,
                Forward = fwd,
                Height = height,
                Scale = scale,
                Muzzle = muzzle,
                RifleBonePath = AnimationUtility.CalculateTransformPath(loR, go.transform),
                RifleLocalPos = local.GetColumn(3),
                RifleLocalRot = local.rotation,
                RifleLocalScale = local.lossyScale,
            };
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }

    static AnimationClip Bake(GameObject go, string name, AnimationClip baseClip, float length, bool loop,
        IEnumerable<Transform> posed, System.Action<float> pose)
    {
        // Curves: everything the base clip animates, plus rotation for the bones we pose
        var bindings = AnimationUtility.GetCurveBindings(baseClip).Where(b => b.type == typeof(Transform)).ToList();
        foreach (var t in posed.Where(t => t != null).Distinct())
        {
            string path = AnimationUtility.CalculateTransformPath(t, go.transform);
            bool hasRot = bindings.Any(b => b.path == path &&
                (b.propertyName.StartsWith("m_LocalRotation") || b.propertyName.StartsWith("localEulerAngles")));
            if (!hasRot)
                foreach (var c in new[] { "x", "y", "z", "w" })
                    bindings.Add(EditorCurveBinding.FloatCurve(path, typeof(Transform), "m_LocalRotation." + c));
        }

        var targets = bindings.Select(b => b.path == "" ? go.transform : go.transform.Find(b.path)).ToArray();
        var curves = bindings.Select(_ => new AnimationCurve()).ToArray();

        int frames = Mathf.Max(1, Mathf.RoundToInt(length * FrameRate));
        for (int f = 0; f <= frames; f++)
        {
            float time = length * f / frames;
            pose(time);
            for (int i = 0; i < bindings.Count; i++)
                if (targets[i] != null)
                    curves[i].AddKey(new Keyframe(time, Read(targets[i], bindings[i].propertyName)));
        }

        var clip = new AnimationClip { name = name, frameRate = FrameRate };
        for (int i = 0; i < bindings.Count; i++)
            if (targets[i] != null) AnimationUtility.SetEditorCurve(clip, bindings[i], curves[i]);
        clip.EnsureQuaternionContinuity();
        var settings = AnimationUtility.GetAnimationClipSettings(clip);
        settings.loopTime = loop;
        AnimationUtility.SetAnimationClipSettings(clip, settings);

        string path2 = $"{AnimDir}/{name}.anim";
        var existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(path2);
        if (existing != null)
        {
            EditorUtility.CopySerialized(clip, existing);
            Object.DestroyImmediate(clip);
            EditorUtility.SetDirty(existing);
            return existing;
        }
        AssetDatabase.CreateAsset(clip, path2);
        return clip;
    }

    static void Rotate(Transform t, Vector3 axis, float deg)
    {
        if (t != null) t.rotation = Quaternion.AngleAxis(deg, axis) * t.rotation;
    }

    static void Align(Transform t, Vector3 currentDir, Vector3 targetDir)
    {
        if (t != null && currentDir.sqrMagnitude > 1e-12f && targetDir.sqrMagnitude > 1e-12f)
            t.rotation = Quaternion.FromToRotation(currentDir.normalized, targetDir.normalized) * t.rotation;
    }

    static void TwoBoneIK(Transform upper, Transform lower, Transform effector, Vector3 target, Vector3 pole)
    {
        Vector3 a = upper.position;
        float la = (lower.position - a).magnitude;
        float lb = (effector.position - lower.position).magnitude;
        Vector3 toT = target - a;
        float d = Mathf.Clamp(toT.magnitude, Mathf.Abs(la - lb) + 1e-5f, (la + lb) * 0.999f);
        Vector3 dir = toT.normalized;
        float cosA = (la * la + d * d - lb * lb) / (2f * la * d);
        float angA = Mathf.Acos(Mathf.Clamp(cosA, -1f, 1f));
        Vector3 bend = Vector3.ProjectOnPlane(pole, dir);
        if (bend.sqrMagnitude < 1e-10f) bend = Vector3.ProjectOnPlane(Vector3.down, dir);
        bend.Normalize();
        Vector3 elbow = a + (dir * Mathf.Cos(angA) + bend * Mathf.Sin(angA)) * la;
        Align(upper, lower.position - a, elbow - a);
        Align(lower, effector.position - lower.position, target - lower.position);
    }

    static float Kick(float t)
    {
        if (t <= KickPeak)
        {
            float x = t / KickPeak;
            return 1f - (1f - x) * (1f - x); // fast ease-out
        }
        if (t <= KickRecover)
            return 1f - Mathf.SmoothStep(0f, 1f, (t - KickPeak) / (KickRecover - KickPeak));
        return 0f;
    }

    static float Read(Transform t, string prop)
    {
        switch (prop)
        {
            case "m_LocalPosition.x": return t.localPosition.x;
            case "m_LocalPosition.y": return t.localPosition.y;
            case "m_LocalPosition.z": return t.localPosition.z;
            case "m_LocalRotation.x": return t.localRotation.x;
            case "m_LocalRotation.y": return t.localRotation.y;
            case "m_LocalRotation.z": return t.localRotation.z;
            case "m_LocalRotation.w": return t.localRotation.w;
            case "m_LocalScale.x": return t.localScale.x;
            case "m_LocalScale.y": return t.localScale.y;
            case "m_LocalScale.z": return t.localScale.z;
            case "localEulerAnglesRaw.x": return t.localEulerAngles.x;
            case "localEulerAnglesRaw.y": return t.localEulerAngles.y;
            case "localEulerAnglesRaw.z": return t.localEulerAngles.z;
            default: return 0f;
        }
    }

    // ---------------------------------------------------------------- animator

    static AnimatorController CreateController(string path, AnimationClip idle, AnimationClip walk, AnimationClip shoot)
    {
        if (AssetDatabase.LoadAssetAtPath<Object>(path) != null) AssetDatabase.DeleteAsset(path);
        var c = AnimatorController.CreateAnimatorControllerAtPath(path);
        c.AddParameter("isChasing", AnimatorControllerParameterType.Bool);
        c.AddParameter("isShooting", AnimatorControllerParameterType.Bool);

        var sm = c.layers[0].stateMachine;
        var idleS = sm.AddState("Idle", new Vector3(290, 80, 0));
        var chaseS = sm.AddState("Chase", new Vector3(290, 190, 0));
        var shootS = sm.AddState("Shoot", new Vector3(620, 125, 0));
        idleS.motion = idle;
        chaseS.motion = walk;
        shootS.motion = shoot;
        sm.defaultState = idleS;

        const AnimatorConditionMode If = AnimatorConditionMode.If, IfNot = AnimatorConditionMode.IfNot;
        Transition(idleS, shootS, 0.15f, (If, "isShooting"));
        Transition(idleS, chaseS, 0.2f, (If, "isChasing"));
        Transition(chaseS, shootS, 0.15f, (If, "isShooting"));
        Transition(chaseS, idleS, 0.2f, (IfNot, "isChasing"));
        Transition(shootS, chaseS, 0.2f, (IfNot, "isShooting"), (If, "isChasing"));
        Transition(shootS, idleS, 0.2f, (IfNot, "isShooting"), (IfNot, "isChasing"));
        var again = Transition(shootS, shootS, 0.05f, (If, "isShooting"));
        again.hasExitTime = true;
        again.exitTime = 0.95f;
        again.canTransitionToSelf = true;

        EditorUtility.SetDirty(c);
        return c;
    }

    static AnimatorStateTransition Transition(AnimatorState from, AnimatorState to, float duration,
        params (AnimatorConditionMode mode, string param)[] conditions)
    {
        var t = from.AddTransition(to);
        t.hasExitTime = false;
        t.exitTime = 0.75f;
        t.hasFixedDuration = true;
        t.duration = duration;
        t.canTransitionToSelf = false;
        foreach (var c in conditions) t.AddCondition(c.mode, 0f, c.param);
        return t;
    }

    static AnimatorOverrideController CreateOverride(string path, AnimatorController baseController,
        Dictionary<AnimationClip, AnimationClip> map)
    {
        if (AssetDatabase.LoadAssetAtPath<Object>(path) != null) AssetDatabase.DeleteAsset(path);
        var oc = new AnimatorOverrideController(baseController);
        var list = new List<KeyValuePair<AnimationClip, AnimationClip>>();
        oc.GetOverrides(list);
        for (int i = 0; i < list.Count; i++)
            if (map.TryGetValue(list[i].Key, out var replacement))
                list[i] = new KeyValuePair<AnimationClip, AnimationClip>(list[i].Key, replacement);
        oc.ApplyOverrides(list);
        AssetDatabase.CreateAsset(oc, path);
        return oc;
    }

    // ---------------------------------------------------------------- prefab

    static void BuildPrefab(ZombieDef z, RuntimeAnimatorController controller, Avatar avatar, ZombieResult r, RifleInfo rifle, Scene scene)
    {
        var bullet = AssetDatabase.LoadAssetAtPath<GameObject>(BulletPath);
        var healthBarAsset = AssetDatabase.LoadAssetAtPath<GameObject>(HealthBarPath);
        var modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(z.Fbx);
        if (bullet == null) throw new System.Exception("Missing " + BulletPath);
        if (healthBarAsset == null) throw new System.Exception("Missing " + HealthBarPath);

        var root = new GameObject("Enemy_" + z.Name);
        SceneManager.MoveGameObjectToScene(root, scene);
        try
        {
            // Model (nested prefab of the FBX), turned to face +Z and scaled to the kit's size if needed
            var model = (GameObject)PrefabUtility.InstantiatePrefab(modelAsset, scene);
            model.name = "Model";
            model.transform.SetParent(root.transform, false);
            Vector3 fwd = r.Forward; fwd.y = 0f;
            Quaternion modelRot = Quaternion.FromToRotation(fwd.normalized, Vector3.forward);
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = modelRot;
            model.transform.localScale = Vector3.one * r.Scale;

            var animator = model.GetComponent<Animator>();
            if (animator == null) animator = model.AddComponent<Animator>();
            animator.runtimeAnimatorController = controller;
            if (avatar != null) animator.avatar = avatar;
            animator.applyRootMotion = false;
            animator.updateMode = AnimatorUpdateMode.Normal;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            // Rifle held by the right forearm/hand
            var hand = model.transform.Find(r.RifleBonePath);
            if (hand == null) throw new System.Exception(z.Name + ": bone not found " + r.RifleBonePath);
            var gun = (GameObject)PrefabUtility.InstantiatePrefab(rifle.Asset, scene);
            gun.name = "Rifle";
            gun.transform.SetParent(hand, false);
            gun.transform.localPosition = r.RifleLocalPos;
            gun.transform.localRotation = r.RifleLocalRot;
            gun.transform.localScale = r.RifleLocalScale;
            foreach (var col in gun.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(col);

            float height = r.Height * r.Scale;
            Vector3 muzzle = modelRot * (r.Muzzle * r.Scale);

            // Fire point at the muzzle (aim pose), pointing along the enemy's forward so bullets fly straight at the player
            var firePoint = new GameObject("firePoint");
            firePoint.transform.SetParent(root.transform, false);
            firePoint.transform.localPosition = new Vector3(muzzle.x, muzzle.y, Mathf.Max(muzzle.z + 0.05f, z.Radius + 0.3f));
            firePoint.transform.localRotation = Quaternion.identity;

            var capsule = root.AddComponent<CapsuleCollider>();
            capsule.direction = 1;
            capsule.radius = z.Radius;
            capsule.height = height;
            capsule.center = new Vector3(0f, height * 0.5f, 0f);

            var agent = root.AddComponent<NavMeshAgent>();
            agent.radius = z.Radius + 0.05f;
            agent.height = height;
            agent.baseOffset = 0f;
            agent.speed = z.Speed;
            agent.acceleration = 8f;
            agent.angularSpeed = 300f;
            agent.stoppingDistance = 0f;
            agent.autoBraking = true;
            agent.obstacleAvoidanceType = ObstacleAvoidanceType.HighQualityObstacleAvoidance;

            // Health bar above the head (same setup as EnemyAnimated)
            var bar = (GameObject)PrefabUtility.InstantiatePrefab(healthBarAsset, scene);
            bar.transform.SetParent(root.transform, false);
            var rt = (RectTransform)bar.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.zero;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(80f, 10f);
            rt.localRotation = Quaternion.identity;
            rt.anchoredPosition3D = new Vector3(0f, height + 0.3f, 0f);
            var fill = bar.GetComponentsInChildren<Image>(true).FirstOrDefault(i => i.type == Image.Type.Filled)
                       ?? bar.GetComponentsInChildren<Image>(true).LastOrDefault();

            var enemy = root.AddComponent<Enemy>();
            var es = new SerializedObject(enemy);
            es.FindProperty("agent").objectReferenceValue = agent;
            es.FindProperty("animator").objectReferenceValue = animator;
            es.FindProperty("bullet").objectReferenceValue = bullet;
            es.FindProperty("firePoint").objectReferenceValue = firePoint.transform;
            es.FindProperty("chaseRange").floatValue = 15f;
            es.FindProperty("attackRange").floatValue = 10f;
            es.FindProperty("timeBetweenAttacks").floatValue = ShootLength;
            es.ApplyModifiedPropertiesWithoutUndo();

            var health = root.AddComponent<Health>();
            var hs = new SerializedObject(health);
            hs.FindProperty("maxHealth").floatValue = z.Health;
            hs.FindProperty("healthBarFill").objectReferenceValue = fill;
            hs.ApplyModifiedPropertiesWithoutUndo();

            SetLayer(root.transform, EnemyLayer, bar.transform);

            string path = $"{OutDir}/Enemy_{z.Name}.prefab";
            PrefabUtility.SaveAsPrefabAsset(root, path, out bool ok);
            if (!ok) throw new System.Exception("Could not save " + path);
            Debug.Log(Log + $"Saved {path} (height {height:F2}, scale {r.Scale:F2}, firePoint {firePoint.transform.localPosition}, model rot {modelRot.eulerAngles})");

            // Preview images (after saving, because sampling poses the bones)
            bar.SetActive(false);
            RenderPreview(z, model, r, height, scene);
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    static void RenderPreview(ZombieDef z, GameObject model, ZombieResult r, float height, Scene scene)
    {
        const int S = 420;
        GameObject camGo = null, lightGo = null;
        RenderTexture rtex = null;
        Texture2D tex = null;
        try
        {
            lightGo = new GameObject("PreviewLight");
            SceneManager.MoveGameObjectToScene(lightGo, scene);
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.3f;
            lightGo.transform.rotation = Quaternion.Euler(35f, -40f, 0f);

            camGo = new GameObject("PreviewCam");
            SceneManager.MoveGameObjectToScene(camGo, scene);
            var cam = camGo.AddComponent<Camera>();
            cam.enabled = false;
            cam.scene = scene;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.55f, 0.6f, 0.66f);
            cam.fieldOfView = 30f;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 100f;
            rtex = new RenderTexture(S, S, 24);
            cam.targetTexture = rtex;

            var shots = new (AnimationClip clip, float t, Vector3 dir)[]
            {
                (r.Shoot, ShootLength, new Vector3(1.1f, 0.35f, 1.6f)),  // aim, front 3/4
                (r.Shoot, ShootLength, new Vector3(1f, 0.1f, -0.05f)),   // aim, side
                (r.Shoot, ShootLength, new Vector3(0.25f, 1f, -0.35f)),  // aim, from above/behind
                (r.Shoot, KickPeak, new Vector3(1f, 0.1f, -0.05f)),      // recoil peak, side
                (r.WalkRifle, r.WalkRifle.length * 0.25f, new Vector3(1.1f, 0.35f, 1.6f)), // walking low-ready
            };
            tex = new Texture2D(S * shots.Length, S, TextureFormat.RGB24, false);
            Vector3 center = model.transform.parent.position + Vector3.up * height * 0.55f + Vector3.forward * height * 0.15f;
            for (int i = 0; i < shots.Length; i++)
            {
                shots[i].clip.SampleAnimation(model, shots[i].t);
                camGo.transform.position = center + shots[i].dir.normalized * height * 3f;
                camGo.transform.LookAt(center);
                cam.Render();
                RenderTexture.active = rtex;
                tex.ReadPixels(new Rect(0, 0, S, S), i * S, 0);
                RenderTexture.active = null;
            }
            tex.Apply();
            string file = $"Temp/ZombiePreview_{z.Name}.png";
            File.WriteAllBytes(file, tex.EncodeToPNG());
            Debug.Log(Log + "Preview " + Path.GetFullPath(file));
        }
        catch (System.Exception e)
        {
            Debug.LogWarning(Log + "Preview failed for " + z.Name + ": " + e.Message);
        }
        finally
        {
            RenderTexture.active = null;
            if (camGo != null) Object.DestroyImmediate(camGo);
            if (lightGo != null) Object.DestroyImmediate(lightGo);
            if (rtex != null) Object.DestroyImmediate(rtex);
            if (tex != null) Object.DestroyImmediate(tex);
        }
    }

    static void SetLayer(Transform t, int layer, Transform skip)
    {
        if (t == skip) return; // keep the UI health bar on its own layer
        t.gameObject.layer = layer;
        foreach (Transform c in t) SetLayer(c, layer, skip);
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
    }
}

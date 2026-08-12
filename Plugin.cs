using System;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using System.Collections.Generic;
using System.Collections;
using BepInEx.Configuration;
using BepInEx.Logging;
namespace TLS
{
    [BepInPlugin("neutral.tactical.laser", "Tactical Laser System (TLS)", "2.4")]
    public class Plugin : BaseUnityPlugin
    {
        public static Plugin Instance;
        public static Dictionary<string, ConfigEntry<bool>> UnitConfigs = new Dictionary<string, ConfigEntry<bool>>();
        public static ConfigEntry<float> MinRange;
        public static ConfigEntry<bool> AntiEverything;
        public static ManualLogSource _log;
        private void Awake()
        {
            Instance = this;
            _log = Logger;
            Logger.LogInfo("Tactical Laser System (TLS) starting...");
            MinRange = Config.Bind("General", "Minimum Range", 50f, "Minimum distance from the muzzle before damage starts (prevents self-damage).");
            AntiEverything = Config.Bind("General", "Anti-Everything", true, "If enabled, TLS lasers expand their UnitDefinition.roleIdentity to target Air, Surface, Missile, and Radar. Side effect: enemy AI also rates the unit as a proportionally bigger threat, since ThreatTracker/ThreatVector read the same field. Applied once at Encyclopedia.AfterLoad from the embedded tls.commands.qol manifest, so changing this takes effect on the next game start, not mid-mission.");
            var harmony = new Harmony("neutral.tactical.laser");
            try
            {
                harmony.PatchAll();
                Logger.LogInfo("TLS Fully Active.");
                StartCoroutine(ScannerRoutine());
            }
            catch (Exception ex)
            {
                Logger.LogError($"TLS Hook failed: {ex}");
            }
        }
        private IEnumerator ScannerRoutine()
        {
            while (true)
            {
                ScanForUnits();
                yield return new WaitForSeconds(10f);
            }
        }
        public void ScanForUnits()
        {
            foreach (var l in Resources.FindObjectsOfTypeAll<Laser>())
            {
                UnitDefinition def = ResolveDefinition(l);
                if (def == null || string.IsNullOrEmpty(def.unitName)) continue;
                if (UnitConfigs.ContainsKey(def.unitName)) continue;
                UnitConfigs[def.unitName] = Config.Bind("TLS Targets", def.unitName, true, $"Enable TLS overdrive for {def.unitName}");
                Logger.LogInfo($"Scanner: Found laser-capable unit '{def.unitName}' ({def.jsonKey}). Added toggle to ConfigManager.");
            }
        }
        private static Dictionary<GameObject, UnitDefinition> _prefabDefs;
        public static UnitDefinition ResolveDefinition(Component c)
        {
            if (c == null) return null;
            Unit unit = c.GetComponentInParent<Unit>();
            if (unit != null && unit.definition != null) return unit.definition;
            if (_prefabDefs == null)
            {
                _prefabDefs = new Dictionary<GameObject, UnitDefinition>();
                foreach (var d in Resources.FindObjectsOfTypeAll<UnitDefinition>())
                    if (d != null && d.unitPrefab != null) _prefabDefs[d.unitPrefab] = d;
            }
            _prefabDefs.TryGetValue(c.transform.root.gameObject, out var def);
            return def;
        }
        public static string GetUnitName(Component c)
        {
            UnitDefinition def = ResolveDefinition(c);
            return (def != null && !string.IsNullOrEmpty(def.unitName)) ? def.unitName : "Unknown";
        }
    } 
    [HarmonyPatch(typeof(Laser), "FixedUpdate")]
    public static class Laser_Injector_Patch
    {
        private static bool _playerTriggered = false;
        private static BepInEx.Logging.ManualLogSource _log =
            BepInEx.Logging.Logger.CreateLogSource("TLS");
        public static void Prefix(Laser __instance)
        {
            try {
                var traverse = Traverse.Create(__instance);
                string uName = Plugin.GetUnitName(__instance);
                var db = __instance.GetComponent<TLSBeam>();
                if (db == null)
                {
                    db = __instance.gameObject.AddComponent<TLSBeam>();
                    db.laserInst = __instance;
                    db.dirT = traverse.Field("directionTransform").GetValue<Transform>();
                    db.CaptureOriginals(traverse);
                }
                bool tlsEnabled = !Plugin.UnitConfigs.TryGetValue(uName, out var entry) || entry.Value;
                if (!tlsEnabled)
                {
                    db.RestoreOriginals(traverse);
                    return;
                }
                if (traverse.Field("ammo").FieldExists())
                    traverse.Field("ammo").SetValue(1000);
                if (traverse.Field("fireCommanded").FieldExists() &&
                    traverse.Field("fireCommanded").GetValue<bool>())
                {
                    if (traverse.Field("fireDamage").FieldExists())  traverse.Field("fireDamage").SetValue(1000000f);
                    if (traverse.Field("blastDamage").FieldExists()) traverse.Field("blastDamage").SetValue(1000000f);
                }
                if (traverse.Field("fireCommanded").FieldExists())
                    _playerTriggered = traverse.Field("fireCommanded").GetValue<bool>();
                if (__instance is MonoBehaviour mb)
                {
                    var dirT = traverse.Field("directionTransform").GetValue<Transform>();
                    var crosshairComp = mb.gameObject.GetComponent<LaserCrosshairUI>();
                    string rootName = mb.transform.root.name.ToLower();
                    if (crosshairComp == null &&
                        (rootName.Contains("coin") || rootName.Contains("helo") || rootName.Contains("vtol")))
                    {
                        crosshairComp = mb.gameObject.AddComponent<LaserCrosshairUI>();
                        crosshairComp.laser = traverse;
                        crosshairComp.dirT  = dirT;
                    }
                }
            } catch (Exception ex) { BepInEx.Logging.Logger.CreateLogSource("PrefixErr").LogError(ex.ToString()); }
        }
        public static void Postfix(Laser __instance)
        {
        }
    } 
    public class TLSBeam : MonoBehaviour
    {
        public Transform  dirT;
        public Laser      laserInst;
        private Unit _ownerUnit;
        private float         _lastDmgTime;
        private Collider[]    _capsuleBuffer = new Collider[256];
        private float _origFire, _origBlast;
        private bool  _captured = false;
        private static readonly int _terrainMask = (1 << 6) | (1 << 4);
        private static readonly int _damageMask  = ~((1 << 2) | (1 << 6) | (1 << 4));
        private const float BEAM_RADIUS = 0.5f; 
        private static BepInEx.Logging.ManualLogSource _log =
            BepInEx.Logging.Logger.CreateLogSource("TLSBeam");
        public void CaptureOriginals(Traverse tr)
        {
            if (_captured) return;
            if (tr.Field("fireDamage").FieldExists())   _origFire   = tr.Field("fireDamage").GetValue<float>();
            if (tr.Field("blastDamage").FieldExists())  _origBlast  = tr.Field("blastDamage").GetValue<float>();
            _captured = true;
        }
        public void RestoreOriginals(Traverse tr)
        {
            if (!_captured) return;
            if (tr.Field("fireDamage").FieldExists())   tr.Field("fireDamage").SetValue(_origFire);
            if (tr.Field("blastDamage").FieldExists())  tr.Field("blastDamage").SetValue(_origBlast);
        }
        private void Start()
        {
            _ownerUnit = GetComponentInParent<Unit>();
            _log.LogInfo($"[TLS] Started on {name}  owner={_ownerUnit?.name ?? "none"} (Resolved: {Plugin.GetUnitName(this)})");
        }
        private void Update()
        {
            if (dirT == null) return;
            bool firing = laserInst != null &&
                          Traverse.Create(laserInst).Field("fireCommanded").GetValue<bool>();
            if (!firing) return;
            string uName = Plugin.GetUnitName(this);
            if (Plugin.UnitConfigs.TryGetValue(uName, out var entry))
            {
                if (!entry.Value) return;
            }
            else
            {
                if (Time.frameCount % 1000 == 0)
                    _log.LogDebug($"[TLS] Config missing for '{uName}', defaulting to enabled.");
            }
            if (Time.time - _lastDmgTime < 0.1f) return;
            _lastDmgTime = Time.time;
            Vector3 origin = dirT.position;
            Vector3 dir    = dirT.forward;
            float beamLen = 15000f;
            if (Physics.Raycast(origin, dir, out RaycastHit terrainHit, 15000f, _terrainMask))
                beamLen = terrainHit.distance;
            Vector3 capsuleStart = origin + dir * Plugin.MinRange.Value;
            Vector3 beamEnd      = origin + dir * beamLen;
            int count = Physics.OverlapCapsuleNonAlloc(
                capsuleStart, beamEnd, BEAM_RADIUS, _capsuleBuffer, _damageMask);
            var seen    = new HashSet<int>();
            int pierced = 0;
            for (int i = 0; i < count; i++)
            {
                Collider col = _capsuleBuffer[i];
                if (col == null) continue;
                if (!seen.Add(col.GetInstanceID())) continue;
                var damageable = col.GetComponent<IDamageable>() ?? col.GetComponentInParent<IDamageable>();
                if (damageable != null)
                {
                    Unit hitUnit = damageable.GetUnit();
                    if (hitUnit != null && hitUnit == _ownerUnit) continue;
                    try 
                    { 
                        damageable.TakeDamage(500000f, 0f, 1f, 0f, 0f, _ownerUnit != null ? _ownerUnit.persistentID : default(PersistentID)); 
                        pierced++; 
                    }
                    catch (Exception ex) { _log.LogError($"[DB] TakeDamage: {ex.Message}"); }
                }
            }
            if (pierced > 0)
                _log.LogInfo($"[TLS] Pierced {pierced}/{count} colliders  beamLen={beamLen:F0}m (Unit: {Plugin.GetUnitName(this)})");
        }
    }
    [HarmonyPatch(typeof(Laser), "SetTarget")]
    public static class LaserCenterMassPatch
    {
        public static bool Prefix(Laser __instance, Unit target)
        {
            ((Behaviour)(object)__instance).enabled = true;
            var tr = Traverse.Create((object)__instance);
            tr.Field("currentTargetTransform").SetValue(
                (object)((target != null) ? ((Component)(object)target).transform : null));
            tr.Field("currentTarget").SetValue((object)target);
            return false;
        }
    }
    public class LaserCrosshairUI : MonoBehaviour
    {
        public Traverse laser;
        public Transform dirT;
        private Texture2D _crosshairTex;
        private void Start()
        {
            _crosshairTex = new Texture2D(32, 32);
            for (int x=0; x< 32; x++) {
                for (int y=0; y<32; y++) {
                    float dist = Vector2.Distance(new Vector2(x,y), new Vector2(16,16));
                    if (dist > 14 && dist < 16) _crosshairTex.SetPixel(x, y, Color.red);
                    else _crosshairTex.SetPixel(x, y, Color.clear);
                }
            }
            _crosshairTex.Apply();
        }
        private void OnGUI()
        {
            if (Camera.main != null && dirT != null)
            {
                string uName = Plugin.GetUnitName(this);
                if (Plugin.UnitConfigs.TryGetValue(uName, out var entry) && !entry.Value)
                    return;
                Vector3 targetPoint = dirT.position + (dirT.forward * 15000f);
                if (Physics.Raycast(dirT.position, dirT.forward, out RaycastHit hit, 15000f))
                {
                    targetPoint = hit.point;
                }
                Vector3 screenPos = Camera.main.WorldToScreenPoint(targetPoint);
                if (screenPos.z > 0) 
                {
                    float size = 32f;
                    Rect rect = new Rect(screenPos.x - size/2, Screen.height - screenPos.y - size/2, size, size);
                    GUI.DrawTexture(rect, _crosshairTex);
                }
            }
        }
    }
}
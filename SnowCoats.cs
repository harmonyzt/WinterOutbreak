using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using UnityEngine;
using UnityEngine.Rendering;

namespace WinterOutbreak
{
    /// <summary>
    /// Fresh snow settling on corpses and loose loot lying outdoors. Each covered object is drawn a second time with
    /// the snow coat shader (WinterStorms/SnowCoat, in snowprints.bundle) via Graphics.DrawMesh, which also works with
    /// the game's unreadable item meshes. Only surfaces facing up keep snow; coverage grows from a patchy dusting to a
    /// cap with the storm's snowfall. Loot stops at a partial cover so it stays findable. Corpses keep their snow
    /// grains (CorpseSnowCover) underneath.
    /// </summary>
    public sealed partial class WinterOutbreakPlugin
    {
        private const float ItemCoverSeconds = 240f;
        private const float ItemMaxCover = 0.6f;
        private const float CorpseCoatSeconds = 120f;
        private const float CoatRange = 45f;
        private const int MaxItemCoats = 48;

        private const float HollowRefillSeconds = 300f;
        private const float HollowRestampSeconds = 60f;
        private const int MaxHollowsPerScan = 64;

        // Items settle into the snow by weight, at most as deep as a boot print.
        private const float ItemMaxPress = 0.105f;
        private const float ItemGroundTolerance = 0.12f;

        // Map props whose mesh the raised snow cannot read (VoxelSnowArea.TakeUncappedProps) get a coat instead: only
        // small ones under open sky, since a coat cannot tell a covered floor inside a big mesh from its roof.
        private const int MaxPropCoats = 128;
        private const float PropCoatRange = 55f;
        private const float PropMaxWidth = 4.5f;
        private const float PropMaxHeight = 3.5f;
        private readonly List<Collider> _propPending = new List<Collider>();
        private float _nextPropScan;
        private int _propCoatsAdded;
        private int _propCoatsTooBig;
        private int _propCoatsCovered;

        private SnowCoatSet _snowCoats;
        private float _nextItemScan;
        private float _nextHollowScan;
        private GameWorld _snowCoatsFailedRaid;
        private readonly Dictionary<int, ItemRest> _itemRests = new Dictionary<int, ItemRest>();

        private sealed class ItemRest
        {
            public Vector3 Position;
            public float GroundY;
            public float Next;
            public string Outcome;
        }

        // Outcomes compare by their first word, so a growing depth does not log again every minute.
        private static bool SameOutcomeKind(string a, string b)
        {
            int end = a.IndexOf(' ');
            return end > 0 && string.CompareOrdinal(a, 0, b, 0, end + 1) == 0;
        }

        /// <summary>
        /// Coats and item seating run before the snow shading update. An error here must never stop that update, so it
        /// is caught, logged once and the coats stay off for the rest of the raid.
        /// </summary>
        private void UpdateSnowCoatsSafely()
        {
            if (_snowCoatsFailedRaid != null && ReferenceEquals(_snowCoatsFailedRaid, _raid))
            {
                return;
            }

            try
            {
                UpdateSnowCoats();
            }
            catch (System.Exception exception)
            {
                _snowCoatsFailedRaid = _raid;
                Logger.LogError($"Snow coats and item seating turned off for this raid after an error: {exception}");

                try
                {
                    ClearSnowCoats();
                }
                catch (System.Exception)
                {
                    _snowCoats = null;
                }
            }
        }

        private void UpdateSnowCoats()
        {
            if (_voxelSnow == null || _preset == null)
            {
                ClearSnowCoats();
                return;
            }

            var camera = Camera.main;
            if (camera == null)
            {
                return;
            }

            if (_snowCoats == null)
            {
                Shader shader = SnowPrintTexture.LoadCoatShader();
                if (shader != null)
                {
                    _snowCoats = new SnowCoatSet(shader);
                }
            }

            // Items are seated in the snow soon after the game ends their physics; coats can wait longer.
            float now = Time.time;
            if (now >= _nextHollowScan)
            {
                _nextHollowScan = now + 0.5f;
                ScanItemHollows(camera.transform.position, now);
            }

            if (now >= _nextItemScan && _snowCoats != null)
            {
                _nextItemScan = now + 2f;
                ScanLooseLoot(camera.transform.position, now);
            }

            if (now >= _nextPropScan && _snowCoats != null)
            {
                _nextPropScan = now + 1.5f;
                ScanUncappedProps(camera.transform.position);
            }

            float propCoverage = Mathf.Clamp01(GetStormSnowDepth(out _) / 0.12f) * 0.9f;
            _snowCoats?.Draw(Singleton<GameWorld>.Instance, camera.transform.position, CoatRange, now, _preset.Snowfall,
                propCoverage);
        }

        /// <summary>
        /// Loose items rest on the ground collider under the snow and would vanish into it. Once an item's physics has
        /// finished, it is raised to the snow surface, settled a few centimetres in, and presses a shallow hollow the
        /// shape of its footprint. Rechecked every minute (sooner if it moves) so it follows the snow depth; the hollow
        /// refills over 5 minutes after the item is picked up.
        /// </summary>
        private void ScanItemHollows(Vector3 eye, float now)
        {
            var loot = Singleton<GameWorld>.Instance?.LootItems;
            if (loot == null)
            {
                return;
            }

            int stamped = 0;
            for (int i = 0; i < loot.Count && stamped < MaxHollowsPerScan; i++)
            {
                LootItem item = loot.GetByIndex(i);
                if (item == null || item is Corpse || !item.gameObject.activeInHierarchy)
                {
                    continue;
                }

                Vector3 position = item.transform.position;
                if ((position - eye).sqrMagnitude > CoatRange * CoatRange)
                {
                    continue;
                }

                // The game removes the rigidbody once the item has settled. Moving it before then would only wake it
                // and let it fall back through the snow.
                if (item.RigidBody != null)
                {
                    continue;
                }

                int id = item.GetInstanceID();
                bool known = _itemRests.TryGetValue(id, out var rest)
                    && (rest.Position - position).sqrMagnitude < 0.05f * 0.05f;
                if (known && now < rest.Next)
                {
                    continue;
                }

                if (!TryItemFootprint(item, out Vector3 start, out Vector3 end, out float halfWidth)
                    || !TryItemBottom(item, out float bottom))
                {
                    continue;
                }

                // A new resting place: the item lies on the ground under the snow.
                float groundY = known ? rest.GroundY : bottom;

                Vector3 middle = (start + end) * 0.5f;
                middle.y = groundY;
                float press = 0f;
                float target = groundY;
                float retry = HollowRestampSeconds;
                string outcome;
                if (!_voxelSnow.TryGetGroundSnow(middle, out float snowGround, out float depth))
                {
                    // The snow here may not be built yet.
                    outcome = "no ground snow here yet";
                    retry = 2f;
                }
                else if (Mathf.Abs(groundY - snowGround) > ItemGroundTolerance)
                {
                    outcome = $"not on the ground snow (rests {groundY - snowGround:+0.00;-0.00} m from it)";
                }
                else if (depth < 0.02f)
                {
                    outcome = $"snow too thin ({depth * 100f:F1} cm)";
                }
                else
                {
                    press = ItemPress(item, depth);
                    target = Mathf.Max(groundY, snowGround + depth - press);
                    outcome = $"seated {press * 100f:F1} cm into {depth * 100f:F1} cm of snow";
                }

                bool changed = !known || rest.Outcome == null || !SameOutcomeKind(rest.Outcome, outcome);

                float lift = target - bottom;
                if (Mathf.Abs(lift) > 0.005f)
                {
                    item.transform.position += Vector3.up * lift;
                    position = item.transform.position;
                }

                if (press > 0f && _voxelSnow.GpuPrints)
                {
                    start.y = groundY;
                    end.y = groundY;
                    if (_voxelSnow.StampItemHollow(start, end, halfWidth, press, HollowRefillSeconds))
                    {
                        stamped++;
                    }
                }

                _itemRests[id] = new ItemRest
                {
                    Position = position,
                    GroundY = groundY,
                    Next = now + retry,
                    Outcome = outcome
                };
            }
        }

        /// <summary>
        /// How far an item settles into fresh snow. Heavier items sink further: a grenade or pistol about 3 cm, a rifle
        /// about 5 cm, a full backpack as deep as a boot print (10.5 cm). Never more than 60 % of the snow.
        /// </summary>
        private static float ItemPress(LootItem item, float depth)
        {
            float weight = item.Item != null ? item.Item.TotalWeight : 1f;
            float press = Mathf.Clamp(0.02f + 0.01f * weight, 0.02f, ItemMaxPress);
            return Mathf.Min(press, depth * 0.6f);
        }

        /// <summary>
        /// The lowest point of the item's shown meshes. Disabled renderers (a switched-off light beam, a hidden LOD)
        /// report empty bounds at the world origin and are skipped. False while the item is hidden.
        /// </summary>
        private static bool TryItemBottom(LootItem item, out float bottom)
        {
            bottom = float.MaxValue;
            foreach (var renderer in item.GetComponentsInChildren<MeshRenderer>())
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy)
                {
                    continue;
                }

                Bounds bounds = renderer.bounds;
                if (bounds.size.sqrMagnitude < 1e-6f)
                {
                    continue;
                }

                bottom = Mathf.Min(bottom, bounds.min.y);
            }

            return bottom < float.MaxValue;
        }

        /// <summary>
        /// The item's footprint on the ground: its long horizontal axis and half its width.
        /// </summary>
        private static bool TryItemFootprint(LootItem item, out Vector3 start, out Vector3 end, out float halfWidth)
        {
            start = end = default;
            halfWidth = 0f;
            Vector3 center;
            Vector3 axisA, axisB, axisC;
            var box = item.GetComponentInChildren<BoxCollider>();
            if (box != null)
            {
                Transform t = box.transform;
                Vector3 size = Vector3.Scale(box.size, t.lossyScale);
                center = t.TransformPoint(box.center);
                axisA = t.right * size.x;
                axisB = t.up * size.y;
                axisC = t.forward * size.z;
            }
            else
            {
                var renderer = item.GetComponentInChildren<Renderer>();
                if (renderer == null)
                {
                    return false;
                }

                Bounds bounds = renderer.bounds;
                center = bounds.center;
                axisA = Vector3.right * bounds.size.x;
                axisB = Vector3.up * bounds.size.y;
                axisC = Vector3.forward * bounds.size.z;
            }

            // The two box axes that spread furthest over the ground give the footprint's length and width.
            Vector3[] axes = { Flat(axisA), Flat(axisB), Flat(axisC) };
            System.Array.Sort(axes, (x, y) => y.sqrMagnitude.CompareTo(x.sqrMagnitude));
            float length = axes[0].magnitude;
            float width = axes[1].magnitude;
            if (length < 0.02f)
            {
                return false;
            }

            halfWidth = Mathf.Clamp(width * 0.5f + 0.02f, 0.04f, 0.4f);
            Vector3 direction = axes[0] / length;
            float reach = Mathf.Max(0f, length * 0.5f - halfWidth * 0.6f);
            start = center - direction * reach;
            end = center + direction * reach;
            // A very short groove still needs a direction for the stamp.
            if (reach < 0.005f)
            {
                end = center + direction * 0.01f;
            }

            return true;
        }

        private static Vector3 Flat(Vector3 axis)
        {
            return new Vector3(axis.x, 0f, axis.z);
        }

        /// <summary>
        /// Called when a corpse gets its snow grains; adds the coat on top.
        /// </summary>
        private void AddCorpseCoat(Corpse corpse, float since)
        {
            if (_snowCoats == null || corpse == null || _snowCoats.Has(corpse))
            {
                return;
            }

            var renderers = new List<Renderer>();
            foreach (var renderer in corpse.GetComponentsInChildren<Renderer>())
            {
                if (renderer is SkinnedMeshRenderer && renderer.enabled && renderer.gameObject.activeInHierarchy)
                {
                    renderers.Add(renderer);
                }
            }

            // Baking a pose costs a little per renderer: coat the largest few (body, armour, backpack, helmet).
            renderers.Sort((x, y) => y.bounds.size.sqrMagnitude.CompareTo(x.bounds.size.sqrMagnitude));
            if (renderers.Count > 8)
            {
                renderers.RemoveRange(8, renderers.Count - 8);
            }

            _snowCoats.Add(corpse, renderers, since, CorpseCoatSeconds, 1f);
        }

        private void ScanLooseLoot(Vector3 eye, float now)
        {
            var world = Singleton<GameWorld>.Instance;
            var loot = world?.LootItems;
            if (loot == null || _snowCoats.Count >= MaxItemCoats)
            {
                return;
            }

            const QueryTriggerInteraction triggers = QueryTriggerInteraction.Ignore;
            int roofMask = LayersMaskController.HighPolyCollider | LayersMaskController.LowPolyColliderLayerMask;
            for (int i = 0; i < loot.Count && _snowCoats.Count < MaxItemCoats; i++)
            {
                LootItem item = loot.GetByIndex(i);
                if (item == null || item is Corpse || !item.gameObject.activeInHierarchy
                    || _snowCoats.Has(item) || _snowCoats.Rejected(item))
                {
                    continue;
                }

                Vector3 position = item.transform.position;
                if ((position - eye).sqrMagnitude > CoatRange * CoatRange)
                {
                    continue;
                }

                // Still falling or rolling: try again on a later scan.
                var body = item.RigidBody;
                if (body != null && !body.isKinematic && !body.IsSleeping() && body.velocity.sqrMagnitude > 0.01f)
                {
                    continue;
                }

                // Outdoors under open sky only.
                if (SnowSurface.IsInIndoorVolume(position + Vector3.up * 0.3f)
                    || Physics.Raycast(position + Vector3.up * 0.3f, Vector3.up, 30f, roofMask, triggers))
                {
                    _snowCoats.Reject(item);
                    continue;
                }

                var renderers = new List<Renderer>();
                foreach (var renderer in item.GetComponentsInChildren<MeshRenderer>())
                {
                    if (renderer.enabled && renderer.GetComponent<MeshFilter>()?.sharedMesh != null)
                    {
                        renderers.Add(renderer);
                    }
                }

                if (renderers.Count == 0)
                {
                    _snowCoats.Reject(item);
                    continue;
                }

                _snowCoats.Add(item, renderers, now, ItemCoverSeconds, ItemMaxCover);
            }
        }

        private void ScanUncappedProps(Vector3 eye)
        {
            _voxelSnow.TakeUncappedProps(_propPending);
            _snowCoats.TakeReleasedProps(_propPending);
            for (int i = _propPending.Count - 1; i >= 0; i--)
            {
                Collider collider = _propPending[i];
                if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy)
                {
                    _propPending.RemoveAt(i);
                    continue;
                }

                if (_snowCoats.PropCount >= MaxPropCoats
                    || (collider.bounds.center - eye).sqrMagnitude > PropCoatRange * PropCoatRange)
                {
                    continue;
                }

                _propPending.RemoveAt(i);
                var renderers = FindPropCoatRenderers(collider, out bool covered);
                if (renderers != null)
                {
                    _snowCoats.AddProp(collider, renderers);
                    _propCoatsAdded++;
                }
                else if (covered)
                {
                    _propCoatsCovered++;
                }
                else
                {
                    _propCoatsTooBig++;
                }
            }
        }

        // The prop's renderers, every LOD of them so the coat follows whichever is drawn, or null when the prop is too
        // big or not under open sky.
        private static List<Renderer> FindPropCoatRenderers(Collider collider, out bool covered)
        {
            covered = false;
            const int maxNodes = 96;
            Bounds support = collider.bounds;
            support.Expand(0.25f);
            var renderers = new List<Renderer>();
            var nodes = new List<Transform>();
            Transform part = collider.transform;
            for (int level = 0; level < 4 && part != null && renderers.Count == 0; level++, part = part.parent)
            {
                nodes.Clear();
                nodes.Add(part);
                for (int i = 0; i < nodes.Count && nodes.Count <= maxNodes; i++)
                {
                    Transform node = nodes[i];
                    if (node.TryGetComponent<MeshRenderer>(out var renderer) && renderer.enabled
                        && renderer.shadowCastingMode != ShadowCastingMode.ShadowsOnly
                        && node.name.IndexOf("shadow", System.StringComparison.OrdinalIgnoreCase) < 0
                        && node.TryGetComponent<MeshFilter>(out var filter) && filter.sharedMesh != null
                        && renderer.bounds.Intersects(support))
                    {
                        renderers.Add(renderer);
                    }

                    for (int child = 0; child < node.childCount; child++)
                    {
                        nodes.Add(node.GetChild(child));
                    }
                }

                if (nodes.Count > maxNodes)
                {
                    // A search up into a whole map group: give up rather than coat its neighbours.
                    return null;
                }
            }

            if (renderers.Count == 0)
            {
                return null;
            }

            Bounds bounds = renderers[0].bounds;
            foreach (var renderer in renderers)
            {
                bounds.Encapsulate(renderer.bounds);
            }

            if (bounds.size.x > PropMaxWidth || bounds.size.z > PropMaxWidth || bounds.size.y > PropMaxHeight)
            {
                return null;
            }

            // Open sky above the top, as for loose loot, except that tree canopy does not count: the ground snow under
            // the Woods pines is full depth too.
            const QueryTriggerInteraction triggers = QueryTriggerInteraction.Ignore;
            int roofMask = LayersMaskController.HighPolyCollider | LayersMaskController.LowPolyColliderLayerMask;
            Vector3 top = new Vector3(bounds.center.x, bounds.max.y + 0.2f, bounds.center.z);
            if (SnowSurface.IsInIndoorVolume(top)
                || (Physics.Raycast(top, Vector3.up, out var hit, 30f, roofMask, triggers) && !IsTreeCollider(hit.collider)))
            {
                covered = true;
                return null;
            }

            return renderers;
        }

        private static bool IsTreeCollider(Collider collider)
        {
            for (Transform part = collider.transform; part != null; part = part.parent)
            {
                string name = part.name;
                if (name.IndexOf("tree", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("pine", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("spruce", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("birch", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private void ClearSnowCoats()
        {
            if (_propCoatsAdded + _propCoatsTooBig + _propCoatsCovered > 0)
            {
                _propCoatsAdded = _propCoatsTooBig = _propCoatsCovered = 0;
            }

            _propPending.Clear();
            _nextPropScan = 0f;
            _itemRests.Clear();
            _snowCoats?.Dispose();
            _snowCoats = null;
            _nextItemScan = 0f;
            _nextHollowScan = 0f;
        }
    }

    internal sealed class SnowCoatSet
    {
        private static readonly int CoverageId = Shader.PropertyToID("_Coverage");
        private readonly Material _material;
        private readonly MaterialPropertyBlock _block = new MaterialPropertyBlock();
        private readonly List<Coat> _coats = new List<Coat>();
        private readonly HashSet<int> _owners = new HashSet<int>();
        private readonly Dictionary<int, Vector3> _rejected = new Dictionary<int, Vector3>();

        // Map props are dropped again past this distance and handed back to the plugin's pending list.
        private const float PropKeepRange = 80f;
        private readonly HashSet<int> _propOwners = new HashSet<int>();
        private readonly List<Collider> _releasedProps = new List<Collider>();

        private sealed class Coat
        {
            public Component Owner;
            public int OwnerId;
            public Renderer Source;
            public Mesh Mesh;
            public bool Baked;
            public float Since;
            public float Seconds;
            public float Max;

            public bool Prop;
        }

        public SnowCoatSet(Shader shader)
        {
            _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, enableInstancing = false };
        }

        public int Count => _owners.Count - _propOwners.Count;

        public int PropCount => _propOwners.Count;

        public void AddProp(Collider owner, List<Renderer> renderers)
        {
            if (_owners.Contains(owner.GetInstanceID()))
            {
                return;
            }

            Add(owner, renderers, 0f, 0f, 0f, true);
            if (_owners.Contains(owner.GetInstanceID()))
            {
                _propOwners.Add(owner.GetInstanceID());
            }
        }

        public void TakeReleasedProps(List<Collider> into)
        {
            into.AddRange(_releasedProps);
            _releasedProps.Clear();
        }

        public bool Has(Component owner) => _owners.Contains(owner.GetInstanceID());

        public bool Rejected(Component owner)
        {
            return _rejected.TryGetValue(owner.GetInstanceID(), out Vector3 position)
                && (owner.transform.position - position).sqrMagnitude < 0.1f * 0.1f;
        }

        public void Reject(Component owner) => _rejected[owner.GetInstanceID()] = owner.transform.position;

        public void Add(Component owner, List<Renderer> renderers, float since, float seconds, float max,
            bool prop = false)
        {
            int id = owner.GetInstanceID();
            if (!_owners.Add(id))
            {
                return;
            }

            bool any = false;
            foreach (var source in renderers)
            {
                Mesh mesh;
                bool baked = false;
                if (source is SkinnedMeshRenderer skin)
                {
                    // A settled body does not move: bake its pose once.
                    mesh = new Mesh { name = "Winter Outbreak snow coat pose" };
                    skin.BakeMesh(mesh);
                    baked = true;
                }
                else
                {
                    mesh = source.GetComponent<MeshFilter>()?.sharedMesh;
                }

                if (mesh == null)
                {
                    continue;
                }

                // A statically batched renderer reports the whole combined mesh, and its own slice of it is not public
                // in this Unity version: drawing it would coat every object in the batch.
                if (!baked && source.isPartOfStaticBatch)
                {
                    continue;
                }

                _coats.Add(new Coat
                {
                    Owner = owner, OwnerId = id, Source = source, Mesh = mesh, Baked = baked,
                    Since = since, Seconds = seconds, Max = max, Prop = prop
                });
                any = true;
            }

            if (!any)
            {
                _owners.Remove(id);
            }
        }

        public void Draw(GameWorld world, Vector3 eye, float range, float now, float snowfall, float propCoverage)
        {
            for (int i = _coats.Count - 1; i >= 0; i--)
            {
                var coat = _coats[i];
                if (coat.Owner == null || coat.Source == null)
                {
                    Remove(i);
                    continue;
                }

                if (coat.Prop)
                {
                    if ((coat.Source.transform.position - eye).sqrMagnitude > PropKeepRange * PropKeepRange)
                    {
                        Remove(i);
                    }
                }
                else if (!coat.Source.transform.IsChildOf(coat.Owner.transform) || !IsStillLoot(world, coat.Owner))
                {
                    Remove(i);
                    continue;
                }

                if (i >= _coats.Count || _coats[i] != coat)
                {
                    continue;
                }

                // isVisible also skips LOD levels that are not drawn, so only the shown LOD gets a coat.
                var source = coat.Source;
                if (!source.enabled || !source.isVisible || !source.gameObject.activeInHierarchy
                    || (source.transform.position - eye).sqrMagnitude > range * range)
                {
                    continue;
                }

                float coverage = coat.Prop ? propCoverage
                    : Mathf.Min(coat.Max, (now - coat.Since) * snowfall / coat.Seconds);
                if (coverage <= 0.02f)
                {
                    continue;
                }

                _block.SetFloat(CoverageId, coverage);
                Transform transform = source.transform;
                Matrix4x4 matrix = coat.Baked
                    ? Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one)
                    : transform.localToWorldMatrix;
                for (int sub = 0; sub < coat.Mesh.subMeshCount; sub++)
                {
                    Graphics.DrawMesh(coat.Mesh, matrix, _material, source.gameObject.layer, null, sub, _block,
                        ShadowCastingMode.Off, true);
                }
            }
        }

        /// <summary>
        /// The LootItem component sits on the item's own pooled model. Picking the item up removes it from the world's
        /// loot list, but the same object (component included) is reused, for example as the weapon in the player's
        /// hands, so only the loot list tells whether it still lies on the ground. Corpses are coated only once and
        /// keep theirs.
        /// </summary>
        private static bool IsStillLoot(GameWorld world, Component owner)
        {
            if (owner is Corpse)
            {
                return true;
            }

            return owner is LootItem loot && loot.Item != null && loot.gameObject.activeInHierarchy && world != null
                && world.LootItems.TryGetByKey(loot.GetNetId(), out LootItem live) && ReferenceEquals(live, loot);
        }

        private void Remove(int index)
        {
            var coat = _coats[index];
            if (coat.Baked)
            {
                Object.Destroy(coat.Mesh);
            }

            _coats.RemoveAt(index);
            if (!_coats.Exists(other => other.OwnerId == coat.OwnerId))
            {
                _owners.Remove(coat.OwnerId);
                if (_propOwners.Remove(coat.OwnerId) && coat.Owner is Collider collider && collider != null)
                {
                    // Out of range: the plugin may coat it again when the player comes back.
                    _releasedProps.Add(collider);
                }
            }
        }

        public void Dispose()
        {
            foreach (var coat in _coats)
            {
                if (coat.Baked)
                {
                    Object.Destroy(coat.Mesh);
                }
            }

            _coats.Clear();
            _owners.Clear();
            _propOwners.Clear();
            _releasedProps.Clear();
            _rejected.Clear();
            Object.Destroy(_material);
        }
    }
}

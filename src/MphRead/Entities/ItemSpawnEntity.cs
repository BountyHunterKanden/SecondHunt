using MphRead.Formats;
using MphRead.Formats.Culling;
using MphRead.Sound;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    public class ItemSpawnEntity : EntityBase
    {
        private readonly ItemSpawnEntityData _data;
        private bool _playKeySfx = false;
        private ushort _spawnCount = 0;
        private ushort _spawnCooldown = 0;
        private bool _linkDone = false;
        private EntityBase? _parent = null;
        private Vector3 _invPos;
        private EntityBase? _pickupNotifyEntity = null;

        public ItemSpawnEntityData Data => _data;
        public new bool Active { get; set; }
        public bool AlwaysActive { get; set; }
        public ItemInstanceEntity? Item { get; set; }
        public NodeData3? ClosestNode { get; set; } = null;
        // recomp (online host migration, MphRecomp Net/HostHandoff): the spawner's counters as the match host has them, so
        // a match client promoted to host carries on with them
        public (ushort Cooldown, ushort Count) HostSpawnState
        {
            get => (_spawnCooldown, _spawnCount);
            set => (_spawnCooldown, _spawnCount) = value;
        }

        // used if there is no base model
        protected override Vector4? OverrideColor { get; } = new ColorRgb(0xC8, 0x00, 0xC8).AsVector4();

        public ItemSpawnEntity(ItemSpawnEntityData data, string nodeName, Scene scene)
            : base(EntityType.ItemSpawn, nodeName, scene)
        {
            _data = data;
            Id = data.Header.EntityId;
            Position = data.Header.Position.ToFloatVector(); // vecs from header are not used
            AlwaysActive = data.AlwaysActive != 0;
            if (GameState.Mode == GameMode.SinglePlayer)
            {
                int state = GameState.StorySave.InitRoomState(_scene.RoomId, Id, active: data.Enabled != 0);
                if (AlwaysActive)
                {
                    Active = data.Enabled != 0;
                }
                else
                {
                    Active = state != 0;
                }
            }
            else
            {
                Active = data.Enabled != 0;
            }
            _spawnCooldown = (ushort)(data.SpawnDelay * 2); // todo: FPS stuff
            if (data.HasBase != 0)
            {
                SetUpModel("items_base");
            }
            else
            {
                AddPlaceholderModel();
            }
        }

        public override void Initialize()
        {
            base.Initialize();
            _scene.TryGetEntity(_data.NotifyEntityId, out _pickupNotifyEntity);
        }

        public override bool Process()
        {
            if (!_linkDone && _data.ParentId != -1)
            {
                if (_scene.TryGetEntity(_data.ParentId, out EntityBase? parent))
                {
                    _parent = parent;
                }
                if (_parent != null)
                {
                    _invPos = Matrix.Vec3MultMtx4(Position, DetMatrix.Inverted(_parent.CollisionTransform));
                }
                _linkDone = true;
            }
            if (_linkDone && _parent != null)
            {
                Position = Matrix.Vec3MultMtx4(_invPos, _parent.CollisionTransform);
            }
            if (!Active)
            {
                return true;
            }
            if (Item == null && _spawnCooldown > 0)
            {
                _spawnCooldown--;
            }
            if (Item == null && _spawnCooldown == 0 && (_data.MaxSpawnCount == 0 || _spawnCount < _data.MaxSpawnCount)
                && !PlayerEntity.NetClient) // network play: a match client's items come from the host
            {
                Item = SpawnItem(_data.ItemType, Position.AddY(0.65f), NodeRef, _scene);
                if (Item != null)
                {
                    _spawnCooldown = (ushort)(_data.SpawnInterval * 2); // todo: FPS stuff
                    _spawnCount++;
                    Item.Owner = this;
                    Item.ParentId = _data.ParentId;
                    if (_data.ItemType != ItemType.ArtifactKey)
                    {
                        _soundSource.Update(Position, rangeIndex: 7);
                        UpdateNodeRefVolume();
                        _soundSource.PlaySfx(SfxId.ITEM_SPAWN1);
                    }
                    else if (_playKeySfx)
                    {
                        _soundSource.PlayFreeSfx(SfxId.KEY_APPEAR);
                    }
                    _playKeySfx = false;
                }
            }
            return base.Process();
        }

        public void OnItemPickedUp()
        {
            if (_data.CollectedMessage != Message.None)
            {
                _scene.SendMessage(_data.CollectedMessage, this, _pickupNotifyEntity, _data.CollectedMsgParam1, _data.CollectedMsgParam2);
            }
        }

        public override void HandleMessage(MessageInfo info)
        {
            if (info.Message == Message.Activate || (info.Message == Message.SetActive && (int)info.Param1 != 0))
            {
                Active = true;
                _playKeySfx = true;
                if (GameState.Mode == GameMode.SinglePlayer)
                {
                    GameState.StorySave.SetRoomState(_scene.RoomId, Id, state: 3);
                }
            }
            else if (info.Message == Message.SetActive && (int)info.Param1 == 0)
            {
                Active = false;
                if (GameState.Mode == GameMode.SinglePlayer)
                {
                    GameState.StorySave.SetRoomState(_scene.RoomId, Id, state: 1);
                }
                if (Item != null)
                {
                    Item.DespawnTimer = 0;
                    _spawnCount--;
                }
            }
            else if (info.Message == Message.MoveItemSpawner && info.Sender != null)
            {
                if (info.Sender.Type == EntityType.EnemySpawn && ((EnemySpawnEntity)info.Sender).Data.EnemyType == EnemyType.Hunter)
                {
                    foreach (PlayerEntity player in _scene.GetPlayerEntities())
                    {
                        if (player.EnemySpawner == info.Sender)
                        {
                            player.GetPosition(out Vector3 position);
                            Position = position;
                            break;
                        }
                    }
                }
                else
                {
                    info.Sender.GetPosition(out Vector3 position);
                    Position = position;
                }
                if (Item != null)
                {
                    Item.Position = Position.AddY(1);
                }
            }
        }

        public override bool ScanVisible()
        {
            // vanilla default ScanVisible (rev 1 0x2048DBC) returns true when the entity has no node ref, which is the case for this type
            return true;
        }

        public override void GetDrawInfo()
        {
            if (IsVisible(NodeRef))
            {
                base.GetDrawInfo();
            }
        }

        public static ItemInstanceEntity? SpawnItemDrop(ItemType type, Vector3 position,
            NodeRef nodeRef, uint chance, Scene scene)
        {
            if (PlayerEntity.NetClient) // recomp: a match client's drops come from the host (as its items: ItemSync)
            {
                return null;
            }
            return SpawnItem(type, position, nodeRef, scene, chance, despawnTime: 450 * 2); // todo: FPS stuff
        }

        public static ItemInstanceEntity? SpawnItem(ItemType type, Vector3 position,
            NodeRef nodeRef, int despawnTime, Scene scene)
        {
            return SpawnItem(type, position, nodeRef, scene, chance: null, despawnTime);
        }

        private static ItemInstanceEntity? SpawnItem(ItemType type, Vector3 position, NodeRef nodeRef,
            Scene scene, uint? chance = null, int despawnTime = 0)
        {
            ItemInstanceEntity? item = null;
            if (type != ItemType.None && (!chance.HasValue || Rng.GetRandomInt2(100) < chance.Value))
            {
                item = new ItemInstanceEntity(new ItemInstanceEntityData(position, type, despawnTime), nodeRef, scene);
                scene.AddEntity(item);
            }
            return item;
        }
    }

    public class FhItemSpawnEntity : EntityBase
    {
        private readonly FhItemSpawnEntityData _data;
        private bool _spawn = true;

        protected override Vector4? OverrideColor { get; } = new ColorRgb(0xC8, 0x00, 0xC8).AsVector4();

        public FhItemSpawnEntity(FhItemSpawnEntityData data, Scene scene) : base(EntityType.FhItemSpawn, scene)
        {
            _data = data;
            Id = data.Header.EntityId;
            SetTransform(data.Header.FacingVector, data.Header.UpVector, data.Header.Position);
            AddPlaceholderModel();
        }

        // recomp: First Hunt's spawning (0x2020fb4; docs/FIRST_HUNT_PORT.md P1) while a First Hunt level runs
        // (PlayerEntity.HostFirstHunt; otherwise one item at once, as before): the first item after CooldownTime frames
        // (0 = at once), the timer paused while the item is out, so the next comes CooldownTime after a pickup;
        // SpawnLimit items in all (0 = no limit)
        private ushort _fhTimer;
        private ushort _fhCount;
        public FhItemEntity? Item { get; private set; }

        public override bool Process()
        {
            if (!PlayerEntity.HostFirstHunt)
            {
                if (_spawn)
                {
                    FhItemEntity item = SpawnItem(Position, _data.ItemType, _scene);
                    _scene.AddEntity(item);
                    _spawn = false;
                }
                return base.Process();
            }
            if (_spawn)
            {
                _fhTimer = (ushort)(_data.CooldownTime * 2); // todo: FPS stuff
                _spawn = false;
            }
            if (Item != null && Item.PickedUp)
            {
                Item = null;
            }
            if (PlayerEntity.NetClient)
            {
                return base.Process(); // network play: the host's items come through ItemSync (NetSpawn)
            }
            if (Item == null)
            {
                if (_fhTimer > 0)
                {
                    _fhTimer--;
                }
                if (_fhTimer == 0 && (_data.SpawnLimit == 0 || _fhCount < _data.SpawnLimit))
                {
                    Item = SpawnItem(Position, _data.ItemType, _scene);
                    _scene.AddEntity(Item);
                    _fhTimer = (ushort)(_data.CooldownTime * 2); // todo: FPS stuff
                    _fhCount++;
                    _soundSource.Update(Position, rangeIndex: 7);
                    _soundSource.PlaySfx(SfxId.ITEM_SPAWN1); // (First Hunt's 0x18 with its sounds, P4)
                }
            }
            return base.Process();
        }

        // network play, a match client: the host spawned this spawner's next item (ItemSync); the timer and count as the
        // host's, for a client promoted to host
        public FhItemEntity NetSpawn()
        {
            if (Item != null)
            {
                Item.PickedUp = true;
            }
            Item = SpawnItem(Position, _data.ItemType, _scene);
            _scene.AddEntity(Item);
            _fhTimer = (ushort)(_data.CooldownTime * 2); // todo: FPS stuff
            _fhCount++;
            _soundSource.Update(Position, rangeIndex: 7);
            _soundSource.PlaySfx(SfxId.ITEM_SPAWN1);
            return Item;
        }

        // todo: FH entity node ref
        public static FhItemEntity SpawnItem(Vector3 position, FhItemType itemType, Scene scene)
        {
            return new FhItemEntity(new FhItemInstanceEntityData(position, itemType), scene);
        }
    }
}

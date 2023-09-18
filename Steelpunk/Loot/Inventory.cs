/*  This script was written jointly by Joshua Fratis and Thomas Carey. 
    Joshua Fratis's contributions specifically include 
    the WeaponItemInstance class and the UpdateMagCount function */

using System;
using System.Collections.Generic;
using System.Linq;
using Game;
using Networking;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ScriptableObjects;
using ScriptableObjects.StatusEffects;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Serialization;
using Utility;
using WebApp;
using Debug = UnityEngine.Debug;

namespace Loot
{
    [JsonObject(MemberSerialization.OptIn)]
    public class Inventory : INetworkSerializable
    {
        // Item Types
        public enum UpdateType
        {
            FullSwap,
            Modify
        }

        public enum TransferType
        {
            Default,
            Split
        }

        public enum SlotType
        {
            Inventory,
            Utility,
            WeaponUpgrade
        }

        public enum StackType
        {
            Stackable,
            Weapon,
            SpecialWeapon,
            Armor,
            Pure
        }

        public Action<ItemSlot, UpdateType> SlotUpdated;
        public Action<ItemSlot, UpdateType> SlotUpdatedServerOnly;
        public NetworkInventory AttachedNetworkInventory;

        private static SteelpunkLogger.LoggerInstance logger =
            new(SteelpunkLogger.LogCategory
                .Inventory);

        public void RebuildInventoryPointers()
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                var instance = _slots[i].ItemInstance;
                _slots[i] = new ItemSlot(this, i);
                _slots[i].SetItemInstance(instance);

                if (instance is IUpgradeableItemInstance
                    upgradeableItemInstance)
                {
                    for (int j = 0;
                         j < upgradeableItemInstance.GetUpgradeSlots().Length;
                         j++)
                    {
                        var upgradeInstance =
                            upgradeableItemInstance.GetUpgradeSlots()[j]
                                .ItemInstance;
                        upgradeableItemInstance.GetUpgradeSlots()[j] =
                            new UpgradeSlot(upgradeableItemInstance,
                                upgradeableItemInstance.GetUpgradeSlots()[j]
                                    .ItemType, j);
                        upgradeableItemInstance.GetUpgradeSlots()[j]
                            .SetItemInstance(upgradeInstance);
                    }
                }
            }

            for (int i = 0; i < _utilitySlots.Length; i++)
            {
                var instance = _utilitySlots[i].ItemInstance;
                _utilitySlots[i] =
                    new UtilityItemSlot(this, _utilitySlots[i].ItemType, i);
                _utilitySlots[i].SetItemInstance(instance);

                if (instance is IUpgradeableItemInstance
                    upgradeableItemInstance)
                {
                    for (int j = 0;
                         j < upgradeableItemInstance.GetUpgradeSlots().Length;
                         j++)
                    {
                        var upgradeInstance =
                            upgradeableItemInstance.GetUpgradeSlots()[j]
                                .ItemInstance;
                        upgradeableItemInstance.GetUpgradeSlots()[j] =
                            new UpgradeSlot(upgradeableItemInstance,
                                upgradeableItemInstance.GetUpgradeSlots()[j]
                                    .ItemType, j);
                        upgradeableItemInstance.GetUpgradeSlots()[j]
                            .SetItemInstance(upgradeInstance);
                    }
                }
            }
        }

        // Item Slots
        [JsonObject(MemberSerialization.OptIn)]
        public class ItemSlot : INetworkSerializable
        {
            public int Index { get; private set; }

            [JsonProperty("item")]
            public ItemInstance ItemInstance { get; private set; }

            private Inventory _inventory;

            public virtual Inventory GetInventory()
            {
                return _inventory;
            }

            public virtual void SetItemInstance(ItemInstance itemInstance)
            {
                if (ItemInstance != ItemInstance.Empty)
                    (ItemInstance as IItemInstanceRestrictedSlot).SetItemSlot(
                        null);

                ItemInstance = itemInstance;

                if (ItemInstance != ItemInstance.Empty)
                    (ItemInstance as IItemInstanceRestrictedSlot).SetItemSlot(
                        this);
            }

            public ItemSlot(Inventory inventory, int index)
            {
                this._inventory = inventory;
                this.ItemInstance = ItemInstance.Empty;
                this.Index = index;
            }

            public virtual void NetworkSerialize<T>(
                BufferSerializer<T> serializer) where T : IReaderWriter
            {
                var index = Index;

                serializer.SerializeValue(ref index);

                if (serializer.IsReader)
                {
                    Index = index;
                }

                bool hasItem = false;

                if (serializer.IsWriter)
                {
                    hasItem = ItemInstance != ItemInstance.Empty;
                }

                serializer.SerializeValue(ref hasItem);

                if (hasItem)
                {
                    ItemInstanceReference itemInstanceReference =
                        new ItemInstanceReference
                        {
                            ItemInstance = ItemInstance
                        };

                    serializer.SerializeValue(ref itemInstanceReference);

                    if (serializer.IsReader)
                        ItemInstance = itemInstanceReference.ItemInstance;
                }
                else
                {
                    if (serializer.IsReader)
                        ItemInstance = ItemInstance.Empty;
                }
            }
        }

        [JsonObject(MemberSerialization.OptIn)]
        public class UtilityItemSlot : ItemSlot
        {
            [JsonProperty("itemType")]
            public ItemType ItemType { get; private set; }

            public override void NetworkSerialize<T>(
                BufferSerializer<T> serializer)
            {
                base.NetworkSerialize(serializer);

                var type = ItemType;

                serializer.SerializeValue(ref type);

                if (serializer.IsReader)
                {
                    ItemType = type;
                }
            }

            public override string ToString()
            {
                return "Utility Slot, Index: " + Index + ", ItemStack: " +
                       ItemInstance + ", Type: " + ItemType;
            }

            public UtilityItemSlot(Inventory inventory, ItemType type,
                int index) : base(inventory, index)
            {
                this.ItemType = type;
            }
        }

        [JsonObject(MemberSerialization.OptIn)]
        [Serializable]
        public class UpgradeSlot : UtilityItemSlot
        {
            public IUpgradeableItemInstance ParentItem { get; private set; }

            public UpgradeSlot(IUpgradeableItemInstance parent,
                ItemType itemType, int index) : base(
                parent?.GetInstance().ItemSlot?.GetInventory(), itemType, index)
            {
                ParentItem = parent;
            }

            public override void SetItemInstance(ItemInstance itemInstance)
            {
                base.SetItemInstance(itemInstance);

                ParentItem.UpdateSlots();
            }

            public override Inventory GetInventory()
            {
                return ParentItem.GetInstance().ItemSlot.GetInventory();
            }

            public override void NetworkSerialize<T>(
                BufferSerializer<T> serializer)
            {
                base.NetworkSerialize(serializer);

                var parentPointer =
                    new NetworkInventory.ItemSlotReference(null);

                if (serializer.IsWriter)
                {
                    parentPointer =
                        new NetworkInventory.ItemSlotReference(ParentItem
                            .GetInstance().ItemSlot);
                }

                serializer.SerializeValue(ref parentPointer);

                ParentItem =
                    parentPointer.ItemSlot.ItemInstance as
                        IUpgradeableItemInstance;
            }

            public override string ToString()
            {
                return "Weapon Upgrade Slot, Index: " + Index +
                       ", ItemStack: " +
                       ItemInstance + ", Type: " + ItemType +
                       ", Weapon Slot: " +
                       ParentItem.GetInstance().ItemSlot;
            }
        }

        [JsonProperty("inventorySlots")] private ItemSlot[] _slots;

        [JsonProperty("utilitySlots")] private UtilityItemSlot[] _utilitySlots;

        [JsonProperty("id")] private string _id;

        public string Id => _id;

        public ItemSlot[] Slots => _slots;
        public UtilityItemSlot[] UtilitySlots => _utilitySlots;

        // Item Instances
        [JsonObject(MemberSerialization.OptIn)]
        public class ItemInstance : INetworkSerializable,
            IItemInstanceRestrictedSlot
        {
            public ItemScriptableObject ScriptableObject =>
                Registry.GetItem(ID);

            public ItemSlot ItemSlot { get; private set; }

            public static readonly ItemInstance Empty;

            [JsonProperty("id")] public uint ID;

            public virtual void Initialize(Inventory inventory)
            {
            }

            public virtual void NetworkSerialize<T>(
                BufferSerializer<T> serializer) where T : IReaderWriter
            {
                serializer.SerializeValue(ref ID);
            }

            public override string ToString()
            {
                return ID.ToString();
            }

            void IItemInstanceRestrictedSlot.SetItemSlot(ItemSlot itemSlot)
            {
                this.ItemSlot = itemSlot;
            }
        }

        [JsonObject(MemberSerialization.OptIn)]
        public class StackableItemInstance : ItemInstance
        {
            [JsonProperty("count")] public int Count;

            public new ItemScriptableObject ScriptableObject =>
                Registry.GetItem(ID);

            public override void NetworkSerialize<T>(
                BufferSerializer<T> serializer)
            {
                base.NetworkSerialize(serializer);
                serializer.SerializeValue(ref Count);
            }

            public override string ToString()
            {
                return base.ToString() + " x" + Count;
            }
        }

        [JsonObject(MemberSerialization.OptIn)]
        public class ArmorItemInstance : ItemInstance, IUpgradeableItemInstance
        {
            [JsonProperty("seed")] public int Seed;

            [JsonProperty("upgradeSlots")]
            public UpgradeSlot[] UpgradeSlots { get; private set; }

            public UpgradeSlot[] GetUpgradeSlots()
            {
                return UpgradeSlots;
            }

            public void SetUpgradeSlots(UpgradeSlot[] upgradeSlots)
            {
                UpgradeSlots = upgradeSlots;
                UpdateSlots();
            }

            public void UpdateSlots()
            {
            }

            public ItemInstance GetInstance()
            {
                return this;
            }

            public new WearableScriptableObject ScriptableObject =>
                Registry.GetItem(ID) as WearableScriptableObject;

            public override void NetworkSerialize<T>(
                BufferSerializer<T> serializer)
            {
                base.NetworkSerialize(serializer);
                serializer.SerializeValue(ref Seed);

                SerializeUpgradeable(this, serializer);
            }
        }

        [JsonObject(MemberSerialization.OptIn)]
        public class SpecialWeaponItemInstance : ItemInstance
        {
            [JsonProperty("seed")] public int Seed;

            public new SpecialWeaponScriptableObject ScriptableObject =>
                Registry.GetItem(ID) as SpecialWeaponScriptableObject;

            public override void NetworkSerialize<T>(
                BufferSerializer<T> serializer)
            {
                base.NetworkSerialize(serializer);
                serializer.SerializeValue(ref Seed);
            }
        }

        public int GetItemCount(int id)
        {
            int count = 0;
            foreach (var slot in _slots)
            {
                if (slot.ItemInstance != null && slot.ItemInstance.ID ==
                    id &&
                    slot.ItemInstance is Inventory.StackableItemInstance
                        simpleItemStack)
                {
                    count += simpleItemStack.Count;
                }
            }

            return count;
        }

        public interface IUpgradeableItemInstance
        {
            public UpgradeSlot[] GetUpgradeSlots();
            public void SetUpgradeSlots(UpgradeSlot[] slots);
            public void UpdateSlots();

            public ItemInstance GetInstance();
        }

        public interface IItemInstanceRestrictedSlot
        {
            public void SetItemSlot(ItemSlot itemSlot);
        }

        // Weapon Item Instances - Joshua Fratis
        [JsonObject(MemberSerialization.OptIn)]
        public class WeaponItemInstance : ItemInstance, IUpgradeableItemInstance
        {
            [JsonProperty("upgradeSlots")]
            public UpgradeSlot[] UpgradeSlots { get; private set; }

            [JsonProperty("magCount")] public int MagCount;
            [JsonProperty("seed")] public int Seed;

            public WeaponItemInstance()
            {
                _statsInitialized = false;
                _statsCalculated = false;
            }

            public void UpdateSlots()
            {
                GetStats();

                var newCappedMagCount =
                    Math.Min(MagCount, _finalStats.capacity);
                if (newCappedMagCount < MagCount && ItemSlot != null)
                {
                    ItemSlot.GetInventory()
                        .UpdateMagCount(ItemSlot, newCappedMagCount);
                }
            }

            public new WeaponScriptableObject ScriptableObject =>
                Registry.GetItem(ID) as WeaponScriptableObject;

            public UpgradeSlot[] GetUpgradeSlots()
            {
                return UpgradeSlots;
            }

            public void SetUpgradeSlots(UpgradeSlot[] slots)
            {
                UpgradeSlots = slots;
                UpdateSlots();
            }

            public ItemInstance GetInstance()
            {
                return this;
            }

            [Serializable]
            public struct Stats
            {
                [NonSerialized] public Rarity Rarity;

                [FormerlySerializedAs("Capacity")] public int capacity;

                [FormerlySerializedAs("Rate")] public float rate;
                public float timeBetweenShots;

                [FormerlySerializedAs("DamagePayload")]
                public DamagePayload damagePayload;

                /* TODO: could turn the following three functions into two
                 by putting addition logic in AddCollection
                 and just calling it from AddNonNegative,
                 passing in an array with one Stats object in it */
                public void AddNonNegative(Stats rhs)
                {
                    Add(rhs);
                    NonNegative();
                }

                public void AddCollection(IEnumerable<Stats> addends)
                {
                    var result = new Stats();
                    result.Clear();

                    foreach (var addend in addends)
                    {
                        result.Add(addend);
                    }

                    AddNonNegative(result);
                }

                private void Add(Stats rhs)
                {
                    capacity += rhs.capacity;
                    rate += rhs.rate;
                    if (damagePayload != null)
                        damagePayload.Add(rhs.damagePayload);
                    else damagePayload = rhs.damagePayload;
                }

                private void NonNegative()
                {
                    capacity = Math.Max(0, capacity);
                    rate = Math.Max(0, rate);
                    damagePayload.PreventNegatives();
                }

                public void Subtract(Stats rhs)
                {
                    capacity -= rhs.capacity;
                    rate -= rhs.rate;
                    if (damagePayload != null)
                        damagePayload.Subtract(rhs.damagePayload);
                    else damagePayload = new DamagePayload();
                }

                public void Clear()
                {
                    capacity = 0;
                    rate = 0;
                    damagePayload = new DamagePayload();
                }

                public Stats Copy()
                {
                    var result = new Stats();
                    result.capacity = capacity;
                    result.rate = rate;
                    result.Rarity = Rarity;
                    result.damagePayload = damagePayload.Copy();
                    return result;
                }

                public override string ToString()
                {
                    var result = "Rarity: " + Rarity + ", Capacity: " +
                                 capacity + ", Rate: " + rate;
                    if (damagePayload != null)
                    {
                        result += ", Damage: " +
                                  damagePayload.baseDamage;
                        foreach (var statusEffect in
                                 damagePayload.statusEffects)
                        {
                            result += ", Status Effect: " +
                                      statusEffect.effect + "(" +
                                      statusEffect.quantity + ")";
                        }
                    }

                    return result;
                }
            }

            private Stats _generatedStats;
            private bool _statsInitialized;

            private List<WeaponUpgradeScriptableObject> _weaponUpgrades =
                new List<WeaponUpgradeScriptableObject>();

            private List<Stats> _statModifiers = new List<Stats>();
            private bool _statModifiersUpdated;

            private Stats _finalStats;
            private bool _statsCalculated;

            /*
             * WII needs to be sent between machines,
             * so it needs to be rendered as pure data (no pointers to other areas of memory)
             * (this is serializaiton)
             * when a WII arrive on a new machine, the first thing that needs to happen is it needs to be allocated into that machines memory
             * obviously this needs to happen first before anything can actually be done with it.
             * and what happens when you create an object / allocate it into memory (and it doesn't exist yet)?
             * the constructor runs!
             * but bc this process is entirely autonomous / blind, we can't pass anything into the constructor
             * so the constructor that runs is the default constructor
             * so obviously we don't want to (re-)generate the seed in the constructor
             * and we cant do anything that requires the seed
             * so instead of generating the stats in the constructor
             * and instead of requiring the user to call an INitialize function after the WII is sent over the network
             * we have a getStats() function that returns the stats
             * AND computes them using the seed if they're not already initialized ( the first time they're accessed / getStats() is called )
             */

            public IEnumerable<WeaponUpgradeScriptableObject> GetUpgrades()
            {
                return UpgradeSlots.Select((slot) =>
                        slot.ItemInstance == Empty
                            ? null
                            : slot.ItemInstance.ScriptableObject as
                                WeaponUpgradeScriptableObject)
                    .Where((obj) => obj != null);
            }

            // Stats
            public Stats GetStats()
            {
                if (!_statsInitialized)
                {
                    GenerateStats();
                }

                if (!_statsCalculated)
                {
                    _finalStats = _generatedStats.Copy();
                    _finalStats.AddCollection(_statModifiers);
                    _statsCalculated = true;
                }

                return _finalStats;
            }

            private void GenerateStats()
            {
                var config = ScriptableObject;

                var sample = (float)Seed / RandomUtils.MaxSeed;

                _generatedStats.Rarity =
                    Registry.Instance.ResolveRarity(sample);

                var capacityScale = config.maxCapacity - config.minCapacity;
                var capacitySample = config.capacityDist.Evaluate(sample);
                var finalCapacity = config.minCapacity +
                                    (int)(capacitySample * capacityScale);
                _generatedStats.capacity = (int)(finalCapacity * _generatedStats.Rarity.statMultiplier);

                var rateScale = config.maxRate - config.minRate;
                var rateSample = config.rateDist.Evaluate(sample);
                var finalRate = config.minRate + (int)(rateSample * rateScale);
                _generatedStats.rate = finalRate * _generatedStats.Rarity.statMultiplier;
                _generatedStats.timeBetweenShots = 1 / _generatedStats.rate;

                var baseDamageScale =
                    config.maxBaseDamage - config.maxBaseDamage;
                var baseDamageSample = config.rateDist.Evaluate(sample);
                var baseDamage = config.minBaseDamage +
                                 (int)(baseDamageSample * baseDamageScale * _generatedStats.Rarity.statMultiplier);
                _generatedStats.damagePayload = new DamagePayload(baseDamage);

                _statsInitialized = true;
                _statsCalculated = false;
            }

            public override void NetworkSerialize<T>(
                BufferSerializer<T> serializer)
            {
                base.NetworkSerialize(serializer);

                serializer.SerializeValue(ref Seed);
                serializer.SerializeValue(ref MagCount);

                SerializeUpgradeable(this, serializer);
            }
        }

        private static void SerializeUpgradeable<T>(
            IUpgradeableItemInstance upgradeable,
            BufferSerializer<T> serializer) where T : IReaderWriter
        {
            var length = 0;

            if (serializer.IsWriter)
            {
                length = upgradeable.GetUpgradeSlots().Length;
            }

            serializer.SerializeValue(ref length);

            UpgradeSlot[] slots = serializer.IsReader
                ? new UpgradeSlot[length]
                : upgradeable.GetUpgradeSlots();

            for (int i = 0; i < length; i++)
            {
                var slotType = ItemType.WeaponUpgrade;
                if (serializer.IsWriter)
                {
                    slotType = slots[i].ItemType;
                }

                serializer.SerializeValue(ref slotType);
                if (serializer.IsReader)
                {
                    slots[i] = new UpgradeSlot(upgradeable, slotType, i);
                }
            }

            for (int i = 0; i < length; i++)
            {
                bool isEmpty = false;
                if (serializer.IsWriter)
                {
                    isEmpty = slots[i].ItemInstance == ItemInstance.Empty;
                }

                serializer.SerializeValue(ref isEmpty);

                if (!isEmpty)
                {
                    var itemRef =
                        new ItemInstanceReference(slots[i].ItemInstance);

                    itemRef.NetworkSerialize(serializer);

                    if (serializer.IsReader)
                    {
                        slots[i].SetItemInstance(itemRef.ItemInstance);
                    }
                }
            }

            if (serializer.IsReader)
            {
                upgradeable.SetUpgradeSlots(slots);
            }
        }

        public class ItemInstanceConverter : JsonConverter
        {
            public override bool CanConvert(Type objectType)
            {
                return typeof(ItemInstance).IsAssignableFrom(objectType);
            }

            public override object ReadJson(JsonReader reader, Type objectType,
                object existingValue, JsonSerializer serializer)
            {
                if (reader.TokenType == JsonToken.Null)
                {
                    return null;
                }

                JObject jsonObject = JObject.Load(reader);
                ItemInstance itemInstance;

                if (!jsonObject.HasValues)
                {
                    return ItemInstance.Empty;
                }

                var jsonId = jsonObject["id"];

                if (jsonId == null)
                {
                    return ItemInstance.Empty;
                }

                var id = jsonId.Value<uint>();

                var item = Registry.GetItem(id);

                if (item == null)
                {
                    return ItemInstance.Empty;
                }

                itemInstance = ItemTypeToStackType(item.GetItemType()) switch
                {
                    StackType.Stackable => new StackableItemInstance(),
                    StackType.Armor => new ArmorItemInstance(),
                    StackType.Pure => new ItemInstance(),
                    StackType.SpecialWeapon => new SpecialWeaponItemInstance(),
                    StackType.Weapon => new WeaponItemInstance(),
                    _ => throw new InvalidOperationException(
                        "Invalid item type")
                };

                // Populate the object's properties from the JSON data
                serializer.Populate(jsonObject.CreateReader(), itemInstance);

                return itemInstance;
            }

            public override void WriteJson(JsonWriter writer, object value,
                JsonSerializer serializer)
            {
                if (value is ItemInstance itemInstance)
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("id");
                    writer.WriteValue(itemInstance.ID);

                    switch (itemInstance)
                    {
                        case StackableItemInstance stackableItemInstance:
                            writer.WritePropertyName("count");
                            writer.WriteValue(stackableItemInstance.Count);
                            // Serialize stackable item properties
                            // ...
                            break;
                        case ArmorItemInstance armorItemInstance:
                            writer.WritePropertyName("seed");
                            writer.WriteValue(armorItemInstance.Seed);
                            // Serialize armor item properties
                            // ...
                            writer.WritePropertyName("upgradeSlots");
                            writer.WriteStartArray();
                            foreach (var upgradeSlot in armorItemInstance
                                         .UpgradeSlots)
                            {
                                serializer.Serialize(writer, upgradeSlot);
                            }

                            writer.WriteEndArray();
                            break;
                        case SpecialWeaponItemInstance specialWeaponItemInstance
                            :
                            writer.WritePropertyName("seed");
                            writer.WriteValue(specialWeaponItemInstance.Seed);
                            // Serialize special weapon item properties
                            // ...
                            break;
                        case WeaponItemInstance weaponItemInstance:
                            writer.WritePropertyName("seed");
                            writer.WriteValue(weaponItemInstance.Seed);
                            writer.WritePropertyName("magCount");
                            writer.WriteValue(weaponItemInstance.MagCount);

                            writer.WritePropertyName("upgradeSlots");
                            writer.WriteStartArray();
                            foreach (var upgradeSlot in weaponItemInstance
                                         .UpgradeSlots)
                            {
                                serializer.Serialize(writer, upgradeSlot);
                            }

                            writer.WriteEndArray();
                            // Serialize weapon item properties
                            // ...
                            break;
                        case { } defaultItemInstance:
                            // Serialize default item properties
                            // ...
                            break;
                        default:
                            throw new InvalidOperationException(
                                "Invalid item instance type");
                    }

                    writer.WriteEndObject();
                }
                else
                {
                    serializer.Serialize(writer, value);
                }
            }
        }

        // Constructors
        public Inventory()
        {
        }

        public Inventory(
            string id,
            int itemSlots,
            ItemType[] utilitySlots
        )
        {
            _id = id;
            _slots = new ItemSlot[itemSlots];

            for (int i = 0;
                 i < itemSlots;
                 i++)
            {
                _slots[i] = new ItemSlot(this, i);
            }

            this._utilitySlots = utilitySlots
                .Select((type, idx) => new UtilityItemSlot(this, type, idx))
                .ToArray();
        }

        public struct ItemInstanceReference : INetworkSerializable
        {
            public ItemInstance ItemInstance;

            public ItemInstanceReference(ItemInstance instance)
            {
                ItemInstance = instance;
            }

            public void NetworkSerialize<T>(BufferSerializer<T> serializer)
                where T : IReaderWriter
            {
                bool isNullOrEmpty = false;

                if (serializer.IsWriter)
                {
                    isNullOrEmpty = ItemInstance == null ||
                                    ItemInstance == ItemInstance.Empty;
                }

                serializer.SerializeValue(ref isNullOrEmpty);

                if (serializer.IsReader)
                {
                    if (isNullOrEmpty)
                    {
                        ItemInstance = ItemInstance.Empty;
                        return;
                    }
                }
                else if (isNullOrEmpty)
                {
                    return;
                }

                var stackType = StackType.Stackable;

                if (serializer.IsWriter)
                {
                    stackType = GetStackType(ItemInstance);
                }

                serializer.SerializeValue(ref stackType);

                if (serializer.IsReader)
                {
                    ItemInstance = stackType switch
                    {
                        StackType.Weapon => new WeaponItemInstance(),
                        StackType.Stackable => new StackableItemInstance(),
                        StackType.Armor => new ArmorItemInstance(),
                        StackType.SpecialWeapon =>
                            new SpecialWeaponItemInstance(),
                        StackType.Pure => new ItemInstance(),
                        _ => throw new ArgumentOutOfRangeException()
                    };
                }

                serializer.SerializeValue(ref ItemInstance);
            }
        }

        public static ItemInstance CreateItemInstance(uint id, int count, Rarity overrideRarity = null)
        {
            var fullItem = Registry.GetItem(id);

            if (fullItem == null)
                return ItemInstance.Empty;

            switch (ItemTypeToStackType(fullItem.GetItemType()))
            {
                case StackType.Weapon:
                    var weapon = new WeaponItemInstance()
                    {
                        ID = id,
                        MagCount = 0,
                        Seed = overrideRarity ? Registry.Instance.GetRaritySeed(overrideRarity) : RandomUtils.GetEquipmentSeed()
                    };

                    var upgrades = new UpgradeSlot[3];
                    for (var i = 0; i < 3; i++)
                    {
                        upgrades[i] = new UpgradeSlot(weapon,
                            ItemType.WeaponUpgrade, i);
                    }

                    weapon.SetUpgradeSlots(upgrades);
                    return weapon;
                case StackType.Armor:
                    var armor = new ArmorItemInstance()
                    {
                        ID = id,
                        Seed = RandomUtils.GetEquipmentSeed(),
                    };

                    var armorUpgrades = new UpgradeSlot[3];
                    for (var i = 0; i < 3; i++)
                    {
                        armorUpgrades[i] = new UpgradeSlot(armor,
                            ItemType.ArmorUpgrade, i);
                    }

                    armor.SetUpgradeSlots(armorUpgrades);
                    return armor;
                case StackType.Stackable:
                    return new StackableItemInstance()
                    {
                        ID = id,
                        Count = Math.Max(1, count)
                    };
                case StackType.SpecialWeapon:
                    return new SpecialWeaponItemInstance()
                    {
                        ID = id,
                        Seed = RandomUtils.GetEquipmentSeed()
                    };
                case StackType.Pure:
                    return new ItemInstance()
                    {
                        ID = id,
                    };
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        public static SlotType GetSlotType(ItemSlot slot)
        {
            if (slot is UpgradeSlot) return SlotType.WeaponUpgrade;
            return slot is UtilityItemSlot
                ? SlotType.Utility
                : SlotType.Inventory;
        }

        public static StackType ItemTypeToStackType(ItemType type)
        {
            switch (type)
            {
                case ItemType.Weapon:
                    return StackType.Weapon;
                case ItemType.ChestWearable:
                case ItemType.HeadWearable:
                case ItemType.LegsWearable:
                    return StackType.Armor;
                case ItemType.SpecialWeapon:
                    return StackType.SpecialWeapon;
                case ItemType.WeaponUpgrade:
                    return StackType.Pure;
                default:
                    return StackType.Stackable;
            }
        }

        private static StackType GetStackType(ItemInstance instance)
        {
            return instance switch
            {
                WeaponItemInstance => StackType.Weapon,
                SpecialWeaponItemInstance => StackType.SpecialWeapon,
                ArmorItemInstance => StackType.Armor,
                StackableItemInstance => StackType.Stackable,
                _ => StackType.Pure
            };
        }

        public ItemSlot GetSlot(ItemInstance itemInstance)
        {
            foreach (var slot in _slots)
            {
                if ((slot.ItemInstance != null) &&
                    (slot.ItemInstance == itemInstance)) return slot;
            }

            foreach (var slot in _utilitySlots)
            {
                if ((slot.ItemInstance != null) &&
                    (slot.ItemInstance == itemInstance)) return slot;
            }

            Debug.LogError("Item Instance is not in the Inventory!");
            return null;
        }

        /* TODO - make this generic rather than only stackable */
        public int RequestConsumeItemStack(
            StackableItemInstance stackableItemInstance)
        {
            var consumedSoFar = 0;

            foreach (var slot in _slots)
            {
                if (slot.ItemInstance == null ||
                    slot.ItemInstance.ID != stackableItemInstance.ID) continue;

                if (slot.ItemInstance is StackableItemInstance
                    currentSimpleStack)
                {
                    var amountToConsume = Mathf.Min(
                        stackableItemInstance.Count - consumedSoFar,
                        currentSimpleStack.Count);

                    currentSimpleStack.Count -= amountToConsume;

                    if (currentSimpleStack.Count == 0)
                    {
                        slot.SetItemInstance(ItemInstance.Empty);
                        SlotUpdatedServerOnly?.Invoke(slot,
                            UpdateType.FullSwap);
                    }
                    else
                    {
                        SlotUpdatedServerOnly?.Invoke(slot,
                            UpdateType.Modify);
                    }

                    consumedSoFar += amountToConsume;

                    if (consumedSoFar == stackableItemInstance.Count)
                        break;
                }
            }

            return consumedSoFar;
        }

        public int RequestConsumeItemStack(
            ItemScriptableObject itemScriptableObject, int quantity)
        {
            Debug.Log(
                "[DEBUG CS] Converting Scriptable Object into StackableItemInstance...");
            if (ItemTypeToStackType(itemScriptableObject.GetItemType()) !=
                StackType.Stackable) return 0;
            return RequestConsumeItemStack(
                (StackableItemInstance)CreateItemInstance(
                    itemScriptableObject.itemRegistryID, quantity));
        }

        public bool HasItemStack(ItemScriptableObject itemScriptableObject,
            int quantity)
        {
            Debug.Log(
                "[DEBUG CS] Converting Scriptable Object into StackableItemInstance...");
            if (ItemTypeToStackType(itemScriptableObject.GetItemType()) !=
                StackType.Stackable) return false;
            return HasItemStack(
                (StackableItemInstance)CreateItemInstance(
                    itemScriptableObject.itemRegistryID, quantity));
        }

        public bool HasItemStack(StackableItemInstance stackableItemInstance)
        {
            var quantityUnaccounted = stackableItemInstance.Count;

            Debug.Log("[DEBUG CS] Checking Inventory for " +
                      quantityUnaccounted + " " + stackableItemInstance
                          .ScriptableObject.description.name);

            foreach (var slot in _slots)
            {
                if (slot.ItemInstance == null ||
                    slot.ItemInstance.ID != stackableItemInstance.ID) continue;

                if (slot.ItemInstance is StackableItemInstance
                    currentSimpleStack)
                {
                    quantityUnaccounted -=
                        Mathf.Max(0, currentSimpleStack.Count);

                    Debug.Log("[DEBUG CS] " + currentSimpleStack.Count +
                              " found, " + quantityUnaccounted + " remaining.");

                    if (quantityUnaccounted <= 0)
                    {
                        Debug.Log("[DEBUG CS] " + quantityUnaccounted + " " +
                                  stackableItemInstance.ScriptableObject
                                      .description.name + " found.");
                        return true;
                    }
                }
            }

            Debug.Log("[DEBUG CS] " + quantityUnaccounted + " " +
                      stackableItemInstance.ScriptableObject.description.name +
                      " could not be found.");
            return false;
        }

        public void ConsumeSlot(ItemSlot slot)
        {
            if (slot.ItemInstance is StackableItemInstance simpleItemStack)
            {
                simpleItemStack.Count--;
                if (simpleItemStack.Count == 0)
                {
                    slot.SetItemInstance(ItemInstance.Empty);
                    SlotUpdatedServerOnly.Invoke(slot, UpdateType.FullSwap);
                }
                else
                {
                    SlotUpdatedServerOnly.Invoke(slot, UpdateType.Modify);
                }
            }
            else
            {
                throw new Exception("Tried to consume a non-simple item stack");
            }
        }

        public void DoSlotTransfer(ItemSlot fromSlot,
            ItemSlot toSlot,
            TransferType transferType)
        {
            if (fromSlot == toSlot) return;

            var fromItemStack = fromSlot.ItemInstance;
            var toItemStack = toSlot.ItemInstance;

            var toFullItem = toSlot.ItemInstance != ItemInstance.Empty
                ? Registry.GetItem(toItemStack.ID)
                : null;

            if (fromItemStack == null) return;

            if (toSlot is UtilityItemSlot utilityToSlot)
            {
                var item = Registry.GetItem(fromItemStack.ID);

                if (!item) throw new Exception("Invalid item");

                if (item.GetItemType() !=
                    utilityToSlot.ItemType)
                    return;
            }

            if (fromSlot is UtilityItemSlot utilityFromSlot &&
                toItemStack != ItemInstance.Empty)
            {
                if (utilityFromSlot.ItemType !=
                    toFullItem.GetItemType())
                    return;
            }

            if (transferType == TransferType.Split)
            {
                if (fromItemStack is not StackableItemInstance
                    {
                        Count: > 1
                    } fromStackableInstance)
                    return;

                if (toItemStack == ItemInstance.Empty)
                {
                    var toTransfer = fromStackableInstance.Count / 2;

                    fromStackableInstance.Count -= toTransfer;

                    toSlot.SetItemInstance(new StackableItemInstance()
                    {
                        ID = fromStackableInstance.ID,
                        Count = toTransfer
                    });
                }
                else if (toItemStack.ID == fromItemStack.ID)
                {
                    if (toItemStack is not StackableItemInstance
                        toStackableInstance)
                    {
                        throw new Exception("Invalid item");
                    }

                    var toTransfer = fromStackableInstance.Count / 2;

                    if (toTransfer + toStackableInstance.Count >
                        toFullItem.stackSize)
                    {
                        toTransfer = toFullItem.stackSize -
                                     toStackableInstance.Count;
                    }

                    fromStackableInstance.Count -= toTransfer;
                    toStackableInstance.Count += toTransfer;
                }
            }
            else
            {
                if (fromItemStack is StackableItemInstance
                        fromSimpleItemStack &&
                    toItemStack is StackableItemInstance toSimpleItemStack
                    && fromItemStack.ID == toItemStack.ID)
                {
                    var total = fromSimpleItemStack.Count +
                                toSimpleItemStack.Count;
                    var max = Registry.GetItem(fromItemStack.ID)
                        .stackSize;

                    if (total <= max)
                    {
                        fromSlot.SetItemInstance(ItemInstance.Empty);
                        toSlot.SetItemInstance(new StackableItemInstance
                        {
                            ID = fromItemStack.ID,
                            Count = total
                        });
                    }
                    else
                    {
                        fromSlot.SetItemInstance(new StackableItemInstance()
                        {
                            ID = fromItemStack.ID,
                            Count = total - max
                        });
                        toSlot.SetItemInstance(new StackableItemInstance()
                        {
                            ID = fromItemStack.ID,
                            Count = max
                        });
                    }
                }
                else
                {
                    fromSlot.SetItemInstance(toItemStack);
                    toSlot.SetItemInstance(fromItemStack);
                }
            }

            SlotUpdated?.Invoke(fromSlot, UpdateType.FullSwap);
            toSlot.GetInventory().SlotUpdated
                ?.Invoke(toSlot, UpdateType.FullSwap);
        }

        public ItemInstance AddItem(ItemInstance inputItemInstance)
        {
            if (inputItemInstance == ItemInstance.Empty)
                return null;

            var item = Registry.GetItem(inputItemInstance.ID);

            if (!item)
                throw new Exception("Item not found in registry");

            foreach (var utilitySlot in UtilitySlots)
            {
                if (item.GetItemType() != utilitySlot.ItemType) continue;

                if (utilitySlot.ItemInstance != null &&
                    utilitySlot.ItemInstance.ID != item.itemRegistryID)
                    continue;

                if (utilitySlot.ItemInstance == ItemInstance.Empty)
                {
                    utilitySlot.SetItemInstance(inputItemInstance);

                    SlotUpdatedServerOnly?.Invoke(utilitySlot,
                        UpdateType.FullSwap);
                    return null;
                }

                if (utilitySlot.ItemInstance is not StackableItemInstance
                        utilitySimpleStack ||
                    inputItemInstance is not StackableItemInstance
                        inputSimpleItemStack)
                    continue;

                var amountToAdd = Mathf.Min(
                    item.stackSize - utilitySimpleStack.Count,
                    inputSimpleItemStack.Count);

                utilitySimpleStack.Count += amountToAdd;

                SlotUpdatedServerOnly?.Invoke(utilitySlot,
                    UpdateType.Modify);

                inputSimpleItemStack.Count -= amountToAdd;

                if (inputSimpleItemStack.Count == 0) return ItemInstance.Empty;
            }

            // fill existing stacks which aren't full
            foreach (var inventorySlot in Slots)
            {
                if (inventorySlot.ItemInstance == ItemInstance.Empty ||
                    inventorySlot.ItemInstance.ID != inputItemInstance.ID)
                    continue;

                if (inventorySlot.ItemInstance is not StackableItemInstance
                        inventorySimpleStack ||
                    inputItemInstance is not StackableItemInstance
                        inputSimpleItemStack)
                    continue;

                var amountAdded = Mathf.Min(inputSimpleItemStack.Count,
                    item.stackSize -
                    inventorySimpleStack.Count);

                inputSimpleItemStack.Count -= amountAdded;

                inventorySimpleStack.Count += amountAdded;

                SlotUpdatedServerOnly?.Invoke(inventorySlot,
                    UpdateType.Modify);
                if (inputSimpleItemStack.Count == 0) return ItemInstance.Empty;
            }

            // then fill empty stacks, this pattern keeps inventory organized
            foreach (var inventorySlot in Slots)
            {
                if (inventorySlot.ItemInstance != null)
                    continue;

                if (inputItemInstance is not StackableItemInstance
                    inputSimpleItemStack)
                {
                    inventorySlot.SetItemInstance(inputItemInstance);

                    SlotUpdatedServerOnly?.Invoke(inventorySlot,
                        UpdateType.FullSwap);
                    return null;
                }

                var amountAdded = Mathf.Min(inputSimpleItemStack.Count,
                    item.stackSize);

                inputSimpleItemStack.Count -= amountAdded;

                inventorySlot.SetItemInstance(new StackableItemInstance()
                {
                    Count = amountAdded,
                    ID = inputItemInstance.ID
                });

                SlotUpdatedServerOnly?.Invoke(inventorySlot,
                    UpdateType.FullSwap);

                if (inputSimpleItemStack.Count == 0) return null;
            }

            // Returns leftovers if any
            return inputItemInstance;
        }

        public ItemInstance DropItem(ItemSlot slot, int count)
        {
            if (slot.ItemInstance == null) return null;

            if (slot.ItemInstance is not StackableItemInstance
                    simpleItemStack ||
                simpleItemStack.Count == count)
            {
                var itemStack = slot.ItemInstance;
                slot.SetItemInstance(ItemInstance.Empty);
                SlotUpdated?.Invoke(slot, UpdateType.FullSwap);

                return itemStack;
            }

            if (simpleItemStack.Count < count)
            {
                Debug.LogError("Tried to drop more than available");
                return null;
            }


            simpleItemStack.Count -= count;

            SlotUpdated?.Invoke(slot, UpdateType.Modify);

            return new StackableItemInstance()
            {
                Count = count,
                ID = slot.ItemInstance.ID
            };
        }

        public void UpdateMagCount(ItemSlot slot, int count)
        {
            if (slot.ItemInstance is WeaponItemInstance weaponItemStack)
            {
                weaponItemStack.MagCount = count;
                SlotUpdatedServerOnly?.Invoke(slot, UpdateType.Modify);
            }
        }


        // Networking
        public void NetworkSerialize<T>(BufferSerializer<T> serializer)
            where T : IReaderWriter
        {
            serializer.SerializeValue(ref _id);

            int length = 0;
            if (serializer.IsWriter)
            {
                length = Slots.Length;
            }

            serializer.SerializeValue(ref length);

            if (serializer.IsReader)
            {
                _slots = new ItemSlot[length];
            }

            for (int i = 0; i < length; i++)
            {
                if (serializer.IsReader)
                {
                    _slots[i] = new ItemSlot(this, i);
                }

                _slots[i].NetworkSerialize(serializer);
            }

            var utilityLength = 0;

            if (serializer.IsWriter)
            {
                utilityLength = UtilitySlots.Length;
            }

            serializer.SerializeValue(ref utilityLength);

            if (serializer.IsReader)
            {
                _utilitySlots = new UtilityItemSlot[utilityLength];
            }

            for (var i = 0; i < utilityLength; i++)
            {
                ItemType itemType = ItemType.Ammo;
                if (serializer.IsWriter)
                {
                    itemType = _utilitySlots[i].ItemType;
                }

                serializer.SerializeValue(ref itemType);

                if (serializer.IsReader)
                {
                    _utilitySlots[i] = new UtilityItemSlot(this, itemType, i);
                }

                UtilitySlots[i].NetworkSerialize(serializer);
            }
        }

        public class InventoryConverter : JsonConverter
        {
            public override bool CanConvert(Type objectType)
            {
                return typeof(Inventory).IsAssignableFrom(objectType);
            }

            public override object ReadJson(JsonReader reader, Type objectType,
                object existingValue, JsonSerializer serializer)
            {
                var tempSerializer = new JsonSerializer()
                {
                    Converters = { new ItemInstanceConverter() }
                };
                var inventory = tempSerializer.Deserialize<Inventory>(reader);
                inventory.RebuildInventoryPointers();
                return inventory;
            }

            public override void WriteJson(JsonWriter writer, object value,
                JsonSerializer serializer)
            {
                var tempSerializer = new JsonSerializer()
                {
                    Converters = { new ItemInstanceConverter() }
                };
                tempSerializer.Serialize(writer, (Inventory)value);
            }
        }
    }
}
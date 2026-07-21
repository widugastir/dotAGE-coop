using System;
using System.IO;
using System.Text;

namespace DotAgeCoop.Net
{
    public enum CoopMessageType : byte
    {
        Hello = 1,
        Welcome = 2,
        Chat = 3,
        PassTurnRequest = 10,
        PassTurnStarted = 11,
        PassTurnCompleted = 12,

        PassTurnReadyStatus = 13,

        PassTurnPeerDone = 14,

        PassTurnMorningAllReady = 15,

        PassTurnMorningRoster = 16,

        PassTurnRosterAck = 17,
        StateDelta = 40,
        HostGameStarted = 50,
        HostGameRequest = 51,
        ClientInGame = 52,

        LoadReadyStatus = 53,

        LoadAllReady = 54,
        DialogueReady = 60,
        DialogueAdvance = 61,
        DialogueReadyStatus = 62,
        FirstBuildingPlaced = 70,
        BuildingPlaceIntent = 71,
        BuildingPlaced = 72,
        BuildingRemoved = 73,
        ResourcesSnapshot = 74,

        FoodSnapshot = 75,

        FoodBanIntent = 76,
        CursorUpdate = 80,
        PipOrderIntent = 81,
        PipOrderApplied = 82,
        PipAppearanceSnapshot = 83,

        ResearchIntent = 84,

        ResearchSnapshot = 85,

        WorldBuildingsSnapshot = 86,

        WorldStateRequest = 87,

        SaveTransferRequest = 90,

        SaveTransferBegin = 91,

        SaveTransferChunk = 92,

        SaveTransferEnd = 93,

        SaveTransferAck = 94,

        MechanicsSnapshot = 95,

        ScalesSnapshot = 96,

        ResearchCheatUnlockAll = 101,

        MorningStateRequest = 104,

        HardSyncBegin = 109,

        HardSyncEnd = 110,

        HardSyncAck = 111,

        TerrainChanged = 112,

        TerrainSnapshot = 113,

        EventCommit = 119,

        EventPhaseReady = 120,

        EventPhaseAck = 121,

        EventInput = 122,
    }

    public static class EventCommitPhase
    {
        public const byte Night = 1;
        public const byte Arrival = 2;
        public const byte Boon = 3;
        public const byte Force = 4;
        public const byte PreTurn = 5;
        public const byte Replace = 6;

        public const byte Targets = 7;

        public const byte Effect = 8;

        public const byte StageTape = 9;
    }

    public static class EventPhase
    {
        public const byte AfterCheckForNewEvents = 1;
        public const byte BeforePerformEvent = 2;

        public const byte BeforeStartExecution = 4;

        public const byte AfterStartExecution = 5;
    }

    public static class EventInputKind
    {
        public const byte RollAdvance = 1;
        public const byte BoonChoice = 2;
        public const byte EventChoicePath = 3;
    }

    public static class PipOrderFlags
    {
        public const int FromSideAction = 1;
        public const int Rollback = 2;

        public const int WantActivate = 4;

        public const int WantDeactivate = 8;

        public const int RemoveOne = 16;

        public const int ClearWorkers = 32;

        public const int BuildingActivated = 64;

        public const int WorkerRoster = 128;

        public const int WantExchange = 256;
    }

    public static class PipOrderTargetKind
    {
        public const int Building = 0;
        public const int Terrain = 1;
        public const int Creature = 2;
    }

    public struct PipOrderPayload
    {
        public int WorkAction;
        public int Param;
        public int Flags;
        public int TargetKind;
        public int TerrainI;
        public int TerrainJ;
        public int BuildingDefId;
        public int TargetCreatureUid;
        public int[] WorkerUids;

        public int ExchangeId;
    }

    public struct PipAppearanceEntry
    {
        public int Uid;
        public byte IsMale;
        public int ColorIndex;
        public int HairIndex;
        public int ChinIndex;
        public int RaceId;
        public int ClassId;
        public string Name;

        public int HomeTerrainI;
        public int HomeTerrainJ;
        public byte IsChild;
    }

    public struct PipAppearanceSnapshotPayload
    {
        public PipAppearanceEntry[] Entries;
    }

    public struct CursorUpdatePayload
    {

        public float X;

        public float Y;
    }

    public struct ScaleBalanceNet
    {
        public int ScaleId;
        public bool Enabled;
        public bool Visible;
        public bool Destroyed;
        public int FlowValue;
        public int SnowballValue;
        public int TemporaryValue;
        public int SeasonalValue;
        public int FailedEventsInARow;
    }

    public struct EventCommitPayload
    {
        public byte Phase;
        public int Day;

        public byte Flags;
        public int PredUid;
        public int EventDefId;
        public int Nature;
        public float NormalizedRoll;
        public int[] PipUids;
        public int[] CreatureUids;
        public int[] TerrainI;
        public int[] TerrainJ;
        public int[] BoonOptionIds;

        public int EffectValue;

        public int[] ResourceTypeIds;

        public int[] CreatureDefIds;

        public bool HasEvent { get { return (Flags & 1) != 0; } }
        public bool Fulfilled { get { return (Flags & 2) != 0; } }
        public bool HasRoll { get { return (Flags & 4) != 0; } }

        public static byte MakeFlags(bool hasEvent, bool fulfilled, bool hasRoll)
        {
            byte f = 0;
            if (hasEvent) f |= 1;
            if (fulfilled) f |= 2;
            if (hasRoll) f |= 4;
            return f;
        }
    }

    public struct DialogueReadyStatusPayload
    {
        public string StepId;
        public int ReadyCount;
        public int NeededCount;
    }

    public struct BuildingPlacementPayload
    {
        public int TerrainI;
        public int TerrainJ;
        public int BuildingDefId;

        public int Flags;

        public int[] BuilderUids;

        public int BuildingStage;
    }

    public struct BuildingRemovedPayload
    {
        public int TerrainI;
        public int TerrainJ;
    }

    public struct ResourceAmount
    {
        public int TypeId;
        public int Available;
    }

    public struct ResourcesSnapshotPayload
    {
        public ResourceAmount[] Amounts;
    }

    public struct FoodAssignEntry
    {
        public int CreatureUid;
        public int TypeId;
    }

    public static class FoodBanFlags
    {
        public const byte PipsDisabled = 1;
        public const byte CreaturesDisabled = 2;
    }

    public struct FoodBanEntry
    {
        public int TypeId;
        public byte Flags;
    }

    public struct FoodBanIntentPayload
    {
        public int TypeId;
        public byte Flags;
    }

    public struct FoodSnapshotPayload
    {

        public ResourceAmount[] Spending;
        public FoodAssignEntry[] Assignments;
        public FoodBanEntry[] Bans;
    }

    public static class ResearchIntentKind
    {
        public const int SetCurrent = 1;
        public const int SelectChoice = 2;
        public const int QueueAdd = 3;
        public const int QueueRemove = 4;
    }

    public struct ResearchIntentPayload
    {
        public int Kind;
        public int DefId;
        public int ContainerId;
    }

    public struct IntPair
    {
        public int Key;
        public int Value;
    }

    public struct ResearchSnapshotPayload
    {
        public int CurrentDefId;
        public int CurrentPoints;
        public int OverflowPoints;
        public byte HasStartedResearch;
        public byte AskForNewResearch;
        public int LatestCompletedDefId;
        public int[] KnownDefIds;
        public IntPair[] ChoiceDefIds;
        public IntPair[] ContainerPoints;
        public int[] QueueContainerIds;
        public string[] ChosenPaths;
    }

    public struct GameBootstrapPayload
    {
        public int Seed;
        public int Difficulty;
        public int ElderId;
        public int Day;
        public bool[] EnabledMods;

        public bool IsLoadGame;

        public int RunEpoch;
    }

    public struct WorldBuildingsSnapshotPayload
    {
        public BuildingPlacementPayload[] Buildings;
    }

    public struct TerrainTilePayload
    {
        public int TerrainI;
        public int TerrainJ;
        public int DefId;
        public int Height;
        public int Cap;
        public int PrevDefId;
        public int Explored;
        public int OuterDirection;
        public int[] AdditionalDefIds;
        public int[] SsDefs;
    }

    public struct TerrainSnapshotPayload
    {
        public TerrainTilePayload[] Tiles;
    }

    public struct StateDeltaPayload
    {
        public string Kind;
        public string Body;
    }

    public static class CoopProtocol
    {
        public const byte Version = 1;

        public const int BuildingFlagInstant = 1;

        public const int BuildingFlagWip = 2;
        private static readonly byte[] EmptyBytes = new byte[0];

        public static byte[] Pack(CoopMessageType type)
        {
            return Pack(type, null);
        }

        public static byte[] Pack(CoopMessageType type, byte[] payload)
        {
            if (payload == null)
                payload = EmptyBytes;

            byte[] packet = new byte[2 + payload.Length];
            packet[0] = Version;
            packet[1] = (byte)type;
            if (payload.Length > 0)
                Buffer.BlockCopy(payload, 0, packet, 2, payload.Length);
            return packet;
        }

        public static bool TryUnpack(byte[] packet, out CoopMessageType type, out byte[] payload)
        {
            type = 0;
            payload = EmptyBytes;
            if (packet == null || packet.Length < 2)
                return false;
            if (packet[0] != Version)
                return false;

            type = (CoopMessageType)packet[1];
            if (packet.Length == 2)
                return true;

            payload = new byte[packet.Length - 2];
            Buffer.BlockCopy(packet, 2, payload, 0, payload.Length);
            return true;
        }

        public static byte[] PackEventCommit(EventCommitPayload data)
        {
            MemoryStream ms = new MemoryStream();
            BinaryWriter bw = new BinaryWriter(ms);
            bw.Write(data.Phase);
            bw.Write(data.Day);
            bw.Write(data.Flags);
            bw.Write(data.PredUid);
            bw.Write(data.EventDefId);
            bw.Write(data.Nature);
            bw.Write(data.NormalizedRoll);
            WriteIntArray(bw, data.PipUids);
            WriteIntArray(bw, data.CreatureUids);
            WriteIntArray(bw, data.TerrainI);
            WriteIntArray(bw, data.TerrainJ);
            WriteIntArray(bw, data.BoonOptionIds);
            bw.Write(data.EffectValue);
            WriteIntArray(bw, data.ResourceTypeIds);
            WriteIntArray(bw, data.CreatureDefIds);
            bw.Flush();
            return ms.ToArray();
        }

        public static bool TryReadEventCommit(byte[] payload, out EventCommitPayload data)
        {
            data = default(EventCommitPayload);
            data.PipUids = new int[0];
            data.CreatureUids = new int[0];
            data.TerrainI = new int[0];
            data.TerrainJ = new int[0];
            data.BoonOptionIds = new int[0];
            data.ResourceTypeIds = new int[0];
            data.CreatureDefIds = new int[0];
            if (payload == null || payload.Length < 1)
                return false;
            try
            {
                BinaryReader br = new BinaryReader(new MemoryStream(payload));
                data.Phase = br.ReadByte();
                data.Day = br.ReadInt32();
                data.Flags = br.ReadByte();
                data.PredUid = br.ReadInt32();
                data.EventDefId = br.ReadInt32();
                data.Nature = br.ReadInt32();
                data.NormalizedRoll = br.ReadSingle();
                data.PipUids = ReadIntArray(br);
                data.CreatureUids = ReadIntArray(br);
                data.TerrainI = ReadIntArray(br);
                data.TerrainJ = ReadIntArray(br);
                data.BoonOptionIds = ReadIntArray(br);

                if (br.BaseStream.Position + 4 <= br.BaseStream.Length)
                {
                    data.EffectValue = br.ReadInt32();
                    if (br.BaseStream.Position < br.BaseStream.Length)
                        data.ResourceTypeIds = ReadIntArray(br);

                    if (br.BaseStream.Position < br.BaseStream.Length)
                        data.CreatureDefIds = ReadIntArray(br);
                }
                return true;
            }
            catch
            {
                data = default(EventCommitPayload);
                return false;
            }
        }

        public static byte[] PackEventPhase(byte phase)
        {
            return new byte[] { phase };
        }

        public static bool TryReadEventPhase(byte[] payload, out byte phase)
        {
            phase = 0;
            if (payload == null || payload.Length < 1)
                return false;
            phase = payload[0];
            return true;
        }

        public static byte[] PackEventInput(byte kind, int intA, int intB, float floatA, string path)
        {
            MemoryStream ms = new MemoryStream();
            BinaryWriter bw = new BinaryWriter(ms, Encoding.UTF8);
            bw.Write(kind);
            bw.Write(intA);
            bw.Write(intB);
            bw.Write(floatA);
            bw.Write(path ?? string.Empty);
            bw.Flush();
            return ms.ToArray();
        }

        public static bool TryReadEventInput(
            byte[] payload, out byte kind, out int intA, out int intB, out float floatA, out string path)
        {
            kind = 0;
            intA = 0;
            intB = 0;
            floatA = 0f;
            path = string.Empty;
            if (payload == null || payload.Length < 1)
                return false;
            try
            {
                BinaryReader br = new BinaryReader(new MemoryStream(payload), Encoding.UTF8);
                kind = br.ReadByte();
                intA = br.ReadInt32();
                intB = br.ReadInt32();
                floatA = br.ReadSingle();
                path = br.ReadString() ?? string.Empty;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void WriteIntArray(BinaryWriter bw, int[] arr)
        {
            if (arr == null)
            {
                bw.Write(0);
                return;
            }
            bw.Write(arr.Length);
            for (int i = 0; i < arr.Length; i++)
                bw.Write(arr[i]);
        }

        private static int[] ReadIntArray(BinaryReader br)
        {
            int n = br.ReadInt32();
            if (n <= 0)
                return new int[0];
            if (n > 512)
                n = 512;
            int[] arr = new int[n];
            for (int i = 0; i < n; i++)
                arr[i] = br.ReadInt32();
            return arr;
        }

        public static byte[] StringPayload(string text)
        {
            if (text == null)
                text = string.Empty;
            return Encoding.UTF8.GetBytes(text);
        }

        public static string ReadString(byte[] payload)
        {
            if (payload == null || payload.Length == 0)
                return string.Empty;
            return Encoding.UTF8.GetString(payload);
        }

        public static byte[] PackBootstrap(GameBootstrapPayload data)
        {
            MemoryStream ms = new MemoryStream();
            BinaryWriter bw = new BinaryWriter(ms, Encoding.UTF8);
            bw.Write(data.Seed);
            bw.Write(data.Difficulty);
            bw.Write(data.ElderId);
            bw.Write(data.Day);
            bool[] mods = data.EnabledMods ?? new bool[0];
            bw.Write(mods.Length);
            for (int i = 0; i < mods.Length; i++)
                bw.Write(mods[i]);

            bw.Write(data.IsLoadGame);

            bw.Write(data.RunEpoch);
            bw.Flush();
            return ms.ToArray();
        }

        public static bool TryReadBootstrap(byte[] payload, out GameBootstrapPayload data)
        {
            data = default(GameBootstrapPayload);
            try
            {
                MemoryStream ms = new MemoryStream(payload);
                BinaryReader br = new BinaryReader(ms, Encoding.UTF8);
                data.Seed = br.ReadInt32();
                data.Difficulty = br.ReadInt32();
                data.ElderId = br.ReadInt32();
                data.Day = br.ReadInt32();
                int n = br.ReadInt32();
                if (n < 0 || n > 64)
                    return false;
                data.EnabledMods = new bool[n];
                for (int i = 0; i < n; i++)
                    data.EnabledMods[i] = br.ReadBoolean();
                if (ms.Position < ms.Length)
                    data.IsLoadGame = br.ReadBoolean();
                if (ms.Position + sizeof(int) <= ms.Length)
                    data.RunEpoch = br.ReadInt32();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static byte[] PackWorldBuildingsSnapshot(WorldBuildingsSnapshotPayload data)
        {
            BuildingPlacementPayload[] buildings = data.Buildings ?? new BuildingPlacementPayload[0];
            MemoryStream ms = new MemoryStream();
            BinaryWriter bw = new BinaryWriter(ms, Encoding.UTF8);
            bw.Write(buildings.Length);
            for (int i = 0; i < buildings.Length; i++)
            {
                BuildingPlacementPayload b = buildings[i];
                int[] builders = b.BuilderUids ?? new int[0];
                bw.Write(b.TerrainI);
                bw.Write(b.TerrainJ);
                bw.Write(b.BuildingDefId);
                bw.Write(b.Flags);
                bw.Write(builders.Length);
                for (int j = 0; j < builders.Length; j++)
                    bw.Write(builders[j]);
                bw.Write(b.BuildingStage);
            }
            bw.Flush();
            return ms.ToArray();
        }

        public static bool TryReadWorldBuildingsSnapshot(byte[] payload, out WorldBuildingsSnapshotPayload data)
        {
            data = default(WorldBuildingsSnapshotPayload);
            data.Buildings = new BuildingPlacementPayload[0];
            try
            {
                MemoryStream ms = new MemoryStream(payload);
                BinaryReader br = new BinaryReader(ms, Encoding.UTF8);
                int n = br.ReadInt32();
                if (n < 0 || n > 4096)
                    return false;
                BuildingPlacementPayload[] buildings = new BuildingPlacementPayload[n];
                for (int i = 0; i < n; i++)
                {
                    BuildingPlacementPayload b = default(BuildingPlacementPayload);
                    b.TerrainI = br.ReadInt32();
                    b.TerrainJ = br.ReadInt32();
                    b.BuildingDefId = br.ReadInt32();
                    b.Flags = br.ReadInt32();
                    int bn = br.ReadInt32();
                    if (bn < 0 || bn > 64)
                        return false;
                    b.BuilderUids = new int[bn];
                    for (int j = 0; j < bn; j++)
                        b.BuilderUids[j] = br.ReadInt32();

                    if (ms.Position + 4 <= ms.Length)
                        b.BuildingStage = br.ReadInt32();
                    buildings[i] = b;
                }
                data.Buildings = buildings;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static byte[] PackStateDelta(string kind, string body)
        {
            if (kind == null) kind = string.Empty;
            if (body == null) body = string.Empty;
            MemoryStream ms = new MemoryStream();
            BinaryWriter bw = new BinaryWriter(ms, Encoding.UTF8);
            bw.Write(kind);
            bw.Write(body);
            bw.Flush();
            return ms.ToArray();
        }

        public static bool TryReadStateDelta(byte[] payload, out StateDeltaPayload data)
        {
            data = default(StateDeltaPayload);
            try
            {
                MemoryStream ms = new MemoryStream(payload);
                BinaryReader br = new BinaryReader(ms, Encoding.UTF8);
                data.Kind = br.ReadString();
                data.Body = br.ReadString();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static byte[] PackDialogueReadyStatus(string stepId, int readyCount, int neededCount)
        {
            if (stepId == null)
                stepId = string.Empty;
            MemoryStream ms = new MemoryStream();
            BinaryWriter bw = new BinaryWriter(ms, Encoding.UTF8);
            bw.Write(stepId);
            bw.Write(readyCount);
            bw.Write(neededCount);
            bw.Flush();
            return ms.ToArray();
        }

        public static bool TryReadDialogueReadyStatus(byte[] payload, out DialogueReadyStatusPayload data)
        {
            data = default(DialogueReadyStatusPayload);
            try
            {
                MemoryStream ms = new MemoryStream(payload);
                BinaryReader br = new BinaryReader(ms, Encoding.UTF8);
                data.StepId = br.ReadString();
                data.ReadyCount = br.ReadInt32();
                data.NeededCount = br.ReadInt32();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static byte[] PackLoadReadyStatus(int readyCount, int neededCount)
        {
            MemoryStream ms = new MemoryStream();
            BinaryWriter bw = new BinaryWriter(ms, Encoding.UTF8);
            bw.Write(readyCount);
            bw.Write(neededCount);
            bw.Flush();
            return ms.ToArray();
        }

        public static bool TryReadLoadReadyStatus(byte[] payload, out int readyCount, out int neededCount)
        {
            readyCount = 0;
            neededCount = 1;
            try
            {
                MemoryStream ms = new MemoryStream(payload);
                BinaryReader br = new BinaryReader(ms, Encoding.UTF8);
                readyCount = br.ReadInt32();
                neededCount = br.ReadInt32();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static byte[] PackSaveTransferBegin(int totalBytes, int chunkSize, int chunkCount, string saveKey)
        {
            if (saveKey == null)
                saveKey = string.Empty;
            MemoryStream ms = new MemoryStream();
            BinaryWriter bw = new BinaryWriter(ms, Encoding.UTF8);
            bw.Write(totalBytes);
            bw.Write(chunkSize);
            bw.Write(chunkCount);
            bw.Write(saveKey);
            bw.Flush();
            return ms.ToArray();
        }

        public static bool TryReadSaveTransferBegin(byte[] payload, out int totalBytes, out int chunkSize, out int chunkCount, out string saveKey)
        {
            totalBytes = 0;
            chunkSize = 0;
            chunkCount = 0;
            saveKey = string.Empty;
            try
            {
                MemoryStream ms = new MemoryStream(payload);
                BinaryReader br = new BinaryReader(ms, Encoding.UTF8);
                totalBytes = br.ReadInt32();
                chunkSize = br.ReadInt32();
                chunkCount = br.ReadInt32();
                saveKey = br.ReadString();
                return totalBytes >= 0 && chunkCount >= 0;
            }
            catch
            {
                return false;
            }
        }

        public static byte[] PackSaveTransferChunk(int index, byte[] data)
        {
            if (data == null)
                data = EmptyBytes;
            MemoryStream ms = new MemoryStream();
            BinaryWriter bw = new BinaryWriter(ms, Encoding.UTF8);
            bw.Write(index);
            bw.Write(data.Length);
            bw.Write(data);
            bw.Flush();
            return ms.ToArray();
        }

        public static bool TryReadSaveTransferChunk(byte[] payload, out int index, out byte[] data)
        {
            index = 0;
            data = EmptyBytes;
            try
            {
                MemoryStream ms = new MemoryStream(payload);
                BinaryReader br = new BinaryReader(ms, Encoding.UTF8);
                index = br.ReadInt32();
                int n = br.ReadInt32();
                if (n < 0 || n > 512 * 1024)
                    return false;
                data = br.ReadBytes(n);
                return data != null && data.Length == n;
            }
            catch
            {
                return false;
            }
        }

        public static byte[] PackSaveTransferEnd(bool ok, string message)
        {
            if (message == null)
                message = string.Empty;
            MemoryStream ms = new MemoryStream();
            BinaryWriter bw = new BinaryWriter(ms, Encoding.UTF8);
            bw.Write(ok);
            bw.Write(message);
            bw.Flush();
            return ms.ToArray();
        }

        public static bool TryReadSaveTransferEnd(byte[] payload, out bool ok, out string message)
        {
            ok = false;
            message = string.Empty;
            try
            {
                MemoryStream ms = new MemoryStream(payload);
                BinaryReader br = new BinaryReader(ms, Encoding.UTF8);
                ok = br.ReadBoolean();
                message = br.ReadString();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static byte[] PackBuildingPlacement(BuildingPlacementPayload data)
        {
            int[] builders = data.BuilderUids ?? new int[0];
            MemoryStream ms = new MemoryStream();
            BinaryWriter bw = new BinaryWriter(ms, Encoding.UTF8);
            bw.Write(data.TerrainI);
            bw.Write(data.TerrainJ);
            bw.Write(data.BuildingDefId);
            bw.Write(data.Flags);
            bw.Write(builders.Length);
            for (int i = 0; i < builders.Length; i++)
                bw.Write(builders[i]);

            bw.Write(data.BuildingStage);
            bw.Flush();
            return ms.ToArray();
        }

        public static bool TryReadBuildingPlacement(byte[] payload, out BuildingPlacementPayload data)
        {
            data = default(BuildingPlacementPayload);
            data.BuilderUids = new int[0];
            try
            {
                MemoryStream ms = new MemoryStream(payload);
                BinaryReader br = new BinaryReader(ms, Encoding.UTF8);
                data.TerrainI = br.ReadInt32();
                data.TerrainJ = br.ReadInt32();
                data.BuildingDefId = br.ReadInt32();

                if (ms.Position < ms.Length)
                    data.Flags = br.ReadInt32();

                if (ms.Position < ms.Length)
                {
                    int n = br.ReadInt32();
                    if (n < 0 || n > 64)
                        return false;
                    data.BuilderUids = new int[n];
                    for (int i = 0; i < n; i++)
                        data.BuilderUids[i] = br.ReadInt32();
                }

                if (ms.Position < ms.Length)
                    data.BuildingStage = br.ReadInt32();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static byte[] PackBuildingRemoved(BuildingRemovedPayload data)
        {
            MemoryStream ms = new MemoryStream();
            BinaryWriter bw = new BinaryWriter(ms, Encoding.UTF8);
            bw.Write(data.TerrainI);
            bw.Write(data.TerrainJ);
            bw.Flush();
            return ms.ToArray();
        }

        public static bool TryReadBuildingRemoved(byte[] payload, out BuildingRemovedPayload data)
        {
            data = default(BuildingRemovedPayload);
            try
            {
                MemoryStream ms = new MemoryStream(payload);
                BinaryReader br = new BinaryReader(ms, Encoding.UTF8);
                data.TerrainI = br.ReadInt32();
                data.TerrainJ = br.ReadInt32();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static byte[] PackResourcesSnapshot(ResourcesSnapshotPayload data)
        {
            ResourceAmount[] amounts = data.Amounts ?? new ResourceAmount[0];
            MemoryStream ms = new MemoryStream();
            BinaryWriter bw = new BinaryWriter(ms, Encoding.UTF8);
            bw.Write(amounts.Length);
            for (int i = 0; i < amounts.Length; i++)
            {
                bw.Write(amounts[i].TypeId);
                bw.Write(amounts[i].Available);
            }
            bw.Flush();
            return ms.ToArray();
        }

        public static bool TryReadResourcesSnapshot(byte[] payload, out ResourcesSnapshotPayload data)
        {
            data = default(ResourcesSnapshotPayload);
            try
            {
                MemoryStream ms = new MemoryStream(payload);
                BinaryReader br = new BinaryReader(ms, Encoding.UTF8);
                int n = br.ReadInt32();
                if (n < 0 || n > 2048)
                    return false;
                data.Amounts = new ResourceAmount[n];
                for (int i = 0; i < n; i++)
                {
                    data.Amounts[i].TypeId = br.ReadInt32();
                    data.Amounts[i].Available = br.ReadInt32();
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static byte[] PackFoodSnapshot(FoodSnapshotPayload data)
        {
            ResourceAmount[] spending = data.Spending ?? new ResourceAmount[0];
            FoodAssignEntry[] assigns = data.Assignments ?? new FoodAssignEntry[0];
            FoodBanEntry[] bans = data.Bans ?? new FoodBanEntry[0];
            MemoryStream ms = new MemoryStream();
            BinaryWriter bw = new BinaryWriter(ms, Encoding.UTF8);
            bw.Write(spending.Length);
            for (int i = 0; i < spending.Length; i++)
            {
                bw.Write(spending[i].TypeId);
                bw.Write(spending[i].Available);
            }
            bw.Write(assigns.Length);
            for (int i = 0; i < assigns.Length; i++)
            {
                bw.Write(assigns[i].CreatureUid);
                bw.Write(assigns[i].TypeId);
            }
            bw.Write(bans.Length);
            for (int i = 0; i < bans.Length; i++)
            {
                bw.Write(bans[i].TypeId);
                bw.Write(bans[i].Flags);
            }
            bw.Flush();
            return ms.ToArray();
        }

        public static bool TryReadFoodSnapshot(byte[] payload, out FoodSnapshotPayload data)
        {
            data = default(FoodSnapshotPayload);
            try
            {
                MemoryStream ms = new MemoryStream(payload);
                BinaryReader br = new BinaryReader(ms, Encoding.UTF8);
                int nSpend = br.ReadInt32();
                if (nSpend < 0 || nSpend > 2048)
                    return false;
                data.Spending = new ResourceAmount[nSpend];
                for (int i = 0; i < nSpend; i++)
                {
                    data.Spending[i].TypeId = br.ReadInt32();
                    data.Spending[i].Available = br.ReadInt32();
                }
                int nAssign = br.ReadInt32();
                if (nAssign < 0 || nAssign > 4096)
                    return false;
                data.Assignments = new FoodAssignEntry[nAssign];
                for (int i = 0; i < nAssign; i++)
                {
                    data.Assignments[i].CreatureUid = br.ReadInt32();
                    data.Assignments[i].TypeId = br.ReadInt32();
                }
                if (ms.Position < ms.Length)
                {
                    int nBan = br.ReadInt32();
                    if (nBan < 0 || nBan > 2048)
                        return false;
                    data.Bans = new FoodBanEntry[nBan];
                    for (int i = 0; i < nBan; i++)
                    {
                        data.Bans[i].TypeId = br.ReadInt32();
                        data.Bans[i].Flags = br.ReadByte();
                    }
                }
                else
                {
                    data.Bans = new FoodBanEntry[0];
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static byte[] PackFoodBanIntent(FoodBanIntentPayload data)
        {
            MemoryStream ms = new MemoryStream(8);
            BinaryWriter bw = new BinaryWriter(ms);
            bw.Write(data.TypeId);
            bw.Write(data.Flags);
            bw.Flush();
            return ms.ToArray();
        }

        public static bool TryReadFoodBanIntent(byte[] payload, out FoodBanIntentPayload data)
        {
            data = default(FoodBanIntentPayload);
            try
            {
                if (payload == null || payload.Length < 5)
                    return false;
                MemoryStream ms = new MemoryStream(payload);
                BinaryReader br = new BinaryReader(ms);
                data.TypeId = br.ReadInt32();
                data.Flags = br.ReadByte();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static byte[] PackResearchIntent(ResearchIntentPayload data)
        {
            MemoryStream ms = new MemoryStream(12);
            BinaryWriter bw = new BinaryWriter(ms);
            bw.Write(data.Kind);
            bw.Write(data.DefId);
            bw.Write(data.ContainerId);
            bw.Flush();
            return ms.ToArray();
        }

        public static bool TryReadResearchIntent(byte[] payload, out ResearchIntentPayload data)
        {
            data = default(ResearchIntentPayload);
            try
            {
                if (payload == null || payload.Length < 12)
                    return false;
                MemoryStream ms = new MemoryStream(payload);
                BinaryReader br = new BinaryReader(ms);
                data.Kind = br.ReadInt32();
                data.DefId = br.ReadInt32();
                data.ContainerId = br.ReadInt32();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static byte[] PackResearchSnapshot(ResearchSnapshotPayload data)
        {
            int[] known = data.KnownDefIds ?? new int[0];
            IntPair[] choices = data.ChoiceDefIds ?? new IntPair[0];
            IntPair[] points = data.ContainerPoints ?? new IntPair[0];
            int[] queue = data.QueueContainerIds ?? new int[0];
            string[] paths = data.ChosenPaths ?? new string[0];
            MemoryStream ms = new MemoryStream();
            BinaryWriter bw = new BinaryWriter(ms, Encoding.UTF8);
            bw.Write(data.CurrentDefId);
            bw.Write(data.CurrentPoints);
            bw.Write(data.OverflowPoints);
            bw.Write(data.HasStartedResearch);
            bw.Write(data.AskForNewResearch);
            bw.Write(data.LatestCompletedDefId);
            bw.Write(known.Length);
            for (int i = 0; i < known.Length; i++)
                bw.Write(known[i]);
            bw.Write(choices.Length);
            for (int i = 0; i < choices.Length; i++)
            {
                bw.Write(choices[i].Key);
                bw.Write(choices[i].Value);
            }
            bw.Write(points.Length);
            for (int i = 0; i < points.Length; i++)
            {
                bw.Write(points[i].Key);
                bw.Write(points[i].Value);
            }
            bw.Write(queue.Length);
            for (int i = 0; i < queue.Length; i++)
                bw.Write(queue[i]);
            bw.Write(paths.Length);
            for (int i = 0; i < paths.Length; i++)
                bw.Write(paths[i] ?? string.Empty);
            bw.Flush();
            return ms.ToArray();
        }

        public static bool TryReadResearchSnapshot(byte[] payload, out ResearchSnapshotPayload data)
        {
            data = default(ResearchSnapshotPayload);
            try
            {
                MemoryStream ms = new MemoryStream(payload);
                BinaryReader br = new BinaryReader(ms, Encoding.UTF8);
                data.CurrentDefId = br.ReadInt32();
                data.CurrentPoints = br.ReadInt32();
                data.OverflowPoints = br.ReadInt32();
                data.HasStartedResearch = br.ReadByte();
                data.AskForNewResearch = br.ReadByte();
                data.LatestCompletedDefId = br.ReadInt32();
                int nKnown = br.ReadInt32();
                if (nKnown < 0 || nKnown > 4096)
                    return false;
                data.KnownDefIds = new int[nKnown];
                for (int i = 0; i < nKnown; i++)
                    data.KnownDefIds[i] = br.ReadInt32();
                int nChoices = br.ReadInt32();
                if (nChoices < 0 || nChoices > 4096)
                    return false;
                data.ChoiceDefIds = new IntPair[nChoices];
                for (int i = 0; i < nChoices; i++)
                {
                    data.ChoiceDefIds[i].Key = br.ReadInt32();
                    data.ChoiceDefIds[i].Value = br.ReadInt32();
                }
                int nPoints = br.ReadInt32();
                if (nPoints < 0 || nPoints > 4096)
                    return false;
                data.ContainerPoints = new IntPair[nPoints];
                for (int i = 0; i < nPoints; i++)
                {
                    data.ContainerPoints[i].Key = br.ReadInt32();
                    data.ContainerPoints[i].Value = br.ReadInt32();
                }
                int nQueue = br.ReadInt32();
                if (nQueue < 0 || nQueue > 512)
                    return false;
                data.QueueContainerIds = new int[nQueue];
                for (int i = 0; i < nQueue; i++)
                    data.QueueContainerIds[i] = br.ReadInt32();
                int nPaths = br.ReadInt32();
                if (nPaths < 0 || nPaths > 64)
                    return false;
                data.ChosenPaths = new string[nPaths];
                for (int i = 0; i < nPaths; i++)
                    data.ChosenPaths[i] = br.ReadString();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static byte[] PackMechanicsSnapshot(int[] unlockedIds, int progressionCounter)
        {
            if (unlockedIds == null)
                unlockedIds = new int[0];
            MemoryStream ms = new MemoryStream();
            BinaryWriter bw = new BinaryWriter(ms, Encoding.UTF8);
            bw.Write(unlockedIds.Length);
            for (int i = 0; i < unlockedIds.Length; i++)
                bw.Write(unlockedIds[i]);

            bw.Write(progressionCounter);
            bw.Flush();
            return ms.ToArray();
        }

        public static bool TryReadMechanicsSnapshot(byte[] payload, out int[] unlockedIds, out int progressionCounter)
        {
            unlockedIds = new int[0];
            progressionCounter = -1;
            try
            {
                MemoryStream ms = new MemoryStream(payload);
                BinaryReader br = new BinaryReader(ms, Encoding.UTF8);
                int n = br.ReadInt32();
                if (n < 0 || n > 4096)
                    return false;
                unlockedIds = new int[n];
                for (int i = 0; i < n; i++)
                    unlockedIds[i] = br.ReadInt32();
                if (ms.Position + sizeof(int) <= ms.Length)
                    progressionCounter = br.ReadInt32();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static byte[] PackScalesSnapshot(int scenarioIndex, ScaleBalanceNet[] balances)
        {
            if (balances == null)
                balances = new ScaleBalanceNet[0];
            MemoryStream ms = new MemoryStream();
            BinaryWriter bw = new BinaryWriter(ms, Encoding.UTF8);
            bw.Write(scenarioIndex);
            bw.Write(balances.Length);
            for (int i = 0; i < balances.Length; i++)
            {
                ScaleBalanceNet b = balances[i];
                bw.Write(b.ScaleId);
                bw.Write(b.Enabled);
                bw.Write(b.Visible);
                bw.Write(b.Destroyed);
                bw.Write(b.FlowValue);
                bw.Write(b.SnowballValue);
                bw.Write(b.TemporaryValue);
                bw.Write(b.SeasonalValue);
                bw.Write(b.FailedEventsInARow);
            }
            bw.Flush();
            return ms.ToArray();
        }

        public static bool TryReadScalesSnapshot(byte[] payload, out int scenarioIndex, out ScaleBalanceNet[] balances)
        {
            scenarioIndex = 0;
            balances = new ScaleBalanceNet[0];
            try
            {
                MemoryStream ms = new MemoryStream(payload);
                BinaryReader br = new BinaryReader(ms, Encoding.UTF8);
                scenarioIndex = br.ReadInt32();
                int n = br.ReadInt32();
                if (n < 0 || n > 128)
                    return false;
                balances = new ScaleBalanceNet[n];
                for (int i = 0; i < n; i++)
                {
                    balances[i].ScaleId = br.ReadInt32();
                    balances[i].Enabled = br.ReadBoolean();
                    balances[i].Visible = br.ReadBoolean();
                    balances[i].Destroyed = br.ReadBoolean();
                    balances[i].FlowValue = br.ReadInt32();
                    balances[i].SnowballValue = br.ReadInt32();
                    balances[i].TemporaryValue = br.ReadInt32();
                    balances[i].SeasonalValue = br.ReadInt32();
                    balances[i].FailedEventsInARow = br.ReadInt32();
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static byte[] PackCursorUpdate(CursorUpdatePayload data)
        {
            MemoryStream ms = new MemoryStream(8);
            BinaryWriter bw = new BinaryWriter(ms);
            bw.Write(data.X);
            bw.Write(data.Y);
            bw.Flush();
            return ms.ToArray();
        }

        public static bool TryReadCursorUpdate(byte[] payload, out CursorUpdatePayload data)
        {
            data = default(CursorUpdatePayload);
            try
            {
                if (payload == null || payload.Length < 8)
                    return false;
                MemoryStream ms = new MemoryStream(payload);
                BinaryReader br = new BinaryReader(ms);
                data.X = br.ReadSingle();
                data.Y = br.ReadSingle();
                if (float.IsNaN(data.X) || float.IsNaN(data.Y) ||
                    float.IsInfinity(data.X) || float.IsInfinity(data.Y))
                    return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static byte[] PackPipOrder(PipOrderPayload data)
        {
            int[] workers = data.WorkerUids ?? new int[0];
            MemoryStream ms = new MemoryStream();
            BinaryWriter bw = new BinaryWriter(ms);
            bw.Write(data.WorkAction);
            bw.Write(data.Param);
            bw.Write(data.Flags);
            bw.Write(data.TargetKind);
            bw.Write(data.TerrainI);
            bw.Write(data.TerrainJ);
            bw.Write(data.BuildingDefId);
            bw.Write(data.TargetCreatureUid);
            bw.Write(workers.Length);
            for (int i = 0; i < workers.Length; i++)
                bw.Write(workers[i]);
            bw.Write(data.ExchangeId);
            bw.Flush();
            return ms.ToArray();
        }

        public static byte[] PackMorningRoster(string morningId, PipOrderPayload[] entries)
        {
            entries = entries ?? new PipOrderPayload[0];
            MemoryStream ms = new MemoryStream();
            BinaryWriter bw = new BinaryWriter(ms, Encoding.UTF8);
            bw.Write(morningId ?? string.Empty);
            bw.Write(entries.Length);
            for (int i = 0; i < entries.Length; i++)
            {
                byte[] one = PackPipOrder(entries[i]);
                bw.Write(one.Length);
                bw.Write(one);
            }
            bw.Flush();
            return ms.ToArray();
        }

        public static bool TryReadMorningRoster(byte[] payload, out string morningId, out PipOrderPayload[] entries)
        {
            morningId = string.Empty;
            entries = new PipOrderPayload[0];
            try
            {
                if (payload == null || payload.Length < 5)
                    return false;
                MemoryStream ms = new MemoryStream(payload);
                BinaryReader br = new BinaryReader(ms, Encoding.UTF8);
                morningId = br.ReadString();
                int n = br.ReadInt32();
                if (n < 0 || n > 512)
                    return false;
                entries = new PipOrderPayload[n];
                for (int i = 0; i < n; i++)
                {
                    int len = br.ReadInt32();
                    if (len < 0 || len > 4096 || ms.Position + len > ms.Length)
                        return false;
                    byte[] one = br.ReadBytes(len);
                    PipOrderPayload e;
                    if (!TryReadPipOrder(one, out e))
                        return false;
                    entries[i] = e;
                }
                return true;
            }
            catch
            {
                morningId = string.Empty;
                entries = new PipOrderPayload[0];
                return false;
            }
        }

        public static bool TryReadPipOrder(byte[] payload, out PipOrderPayload data)
        {
            data = default(PipOrderPayload);
            data.WorkerUids = new int[0];
            data.ExchangeId = -1;
            try
            {
                if (payload == null || payload.Length < 36)
                    return false;
                MemoryStream ms = new MemoryStream(payload);
                BinaryReader br = new BinaryReader(ms);
                data.WorkAction = br.ReadInt32();
                data.Param = br.ReadInt32();
                data.Flags = br.ReadInt32();
                data.TargetKind = br.ReadInt32();
                data.TerrainI = br.ReadInt32();
                data.TerrainJ = br.ReadInt32();
                data.BuildingDefId = br.ReadInt32();
                data.TargetCreatureUid = br.ReadInt32();
                int n = br.ReadInt32();
                if (n < 0 || n > 64)
                    return false;
                data.WorkerUids = new int[n];
                for (int i = 0; i < n; i++)
                    data.WorkerUids[i] = br.ReadInt32();

                if (ms.Position + 4 <= ms.Length)
                    data.ExchangeId = br.ReadInt32();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static byte[] PackPipAppearanceSnapshot(PipAppearanceSnapshotPayload data)
        {
            PipAppearanceEntry[] entries = data.Entries ?? new PipAppearanceEntry[0];
            MemoryStream ms = new MemoryStream();
            BinaryWriter bw = new BinaryWriter(ms, Encoding.UTF8);
            bw.Write(entries.Length);
            for (int i = 0; i < entries.Length; i++)
            {
                PipAppearanceEntry e = entries[i];
                bw.Write(e.Uid);
                bw.Write(e.IsMale);
                bw.Write(e.ColorIndex);
                bw.Write(e.HairIndex);
                bw.Write(e.ChinIndex);
                bw.Write(e.RaceId);
                bw.Write(e.ClassId);
                bw.Write(e.Name ?? string.Empty);
                bw.Write(e.HomeTerrainI);
                bw.Write(e.HomeTerrainJ);
                bw.Write(e.IsChild);
            }
            bw.Flush();
            return ms.ToArray();
        }

        public static bool TryReadPipAppearanceSnapshot(byte[] payload, out PipAppearanceSnapshotPayload data)
        {
            data = default(PipAppearanceSnapshotPayload);
            data.Entries = new PipAppearanceEntry[0];
            try
            {
                if (payload == null || payload.Length < 4)
                    return false;
                MemoryStream ms = new MemoryStream(payload);
                BinaryReader br = new BinaryReader(ms, Encoding.UTF8);
                int n = br.ReadInt32();
                if (n < 0 || n > 512)
                    return false;
                data.Entries = new PipAppearanceEntry[n];
                for (int i = 0; i < n; i++)
                {
                    PipAppearanceEntry e = default(PipAppearanceEntry);
                    e.Uid = br.ReadInt32();
                    e.IsMale = br.ReadByte();
                    e.ColorIndex = br.ReadInt32();
                    e.HairIndex = br.ReadInt32();
                    e.ChinIndex = br.ReadInt32();
                    e.RaceId = br.ReadInt32();
                    e.ClassId = br.ReadInt32();
                    e.Name = br.ReadString();
                    e.HomeTerrainI = -1;
                    e.HomeTerrainJ = -1;
                    e.IsChild = 0;
                    if (ms.Position + 9 <= ms.Length)
                    {
                        e.HomeTerrainI = br.ReadInt32();
                        e.HomeTerrainJ = br.ReadInt32();
                        e.IsChild = br.ReadByte();
                    }
                    data.Entries[i] = e;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static byte[] PackTerrainTile(TerrainTilePayload data)
        {
            MemoryStream ms = new MemoryStream(48);
            BinaryWriter bw = new BinaryWriter(ms);
            WriteTerrainTile(bw, data);
            bw.Flush();
            return ms.ToArray();
        }

        public static bool TryReadTerrainTile(byte[] payload, out TerrainTilePayload data)
        {
            data = default(TerrainTilePayload);
            try
            {
                if (payload == null || payload.Length < 28)
                    return false;
                MemoryStream ms = new MemoryStream(payload);
                BinaryReader br = new BinaryReader(ms);
                return TryReadTerrainTile(br, out data);
            }
            catch
            {
                return false;
            }
        }

        public static byte[] PackTerrainSnapshot(TerrainSnapshotPayload data)
        {
            TerrainTilePayload[] tiles = data.Tiles ?? new TerrainTilePayload[0];
            MemoryStream ms = new MemoryStream(32 + tiles.Length * 40);
            BinaryWriter bw = new BinaryWriter(ms);
            bw.Write(tiles.Length);
            for (int i = 0; i < tiles.Length; i++)
                WriteTerrainTile(bw, tiles[i]);
            bw.Flush();
            return ms.ToArray();
        }

        public static bool TryReadTerrainSnapshot(byte[] payload, out TerrainSnapshotPayload data)
        {
            data = default(TerrainSnapshotPayload);
            data.Tiles = new TerrainTilePayload[0];
            try
            {
                if (payload == null || payload.Length < 4)
                    return false;
                MemoryStream ms = new MemoryStream(payload);
                BinaryReader br = new BinaryReader(ms);
                int n = br.ReadInt32();
                if (n < 0 || n > 20000)
                    return false;
                TerrainTilePayload[] tiles = new TerrainTilePayload[n];
                for (int i = 0; i < n; i++)
                {
                    TerrainTilePayload tile;
                    if (!TryReadTerrainTile(br, out tile))
                        return false;
                    tiles[i] = tile;
                }
                data.Tiles = tiles;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void WriteTerrainTile(BinaryWriter bw, TerrainTilePayload data)
        {
            int[] add = data.AdditionalDefIds ?? new int[0];
            int[] ss = data.SsDefs ?? new int[0];
            bw.Write(data.TerrainI);
            bw.Write(data.TerrainJ);
            bw.Write(data.DefId);
            bw.Write(data.Height);
            bw.Write(data.Cap);
            bw.Write(data.PrevDefId);
            bw.Write(data.Explored);
            bw.Write(data.OuterDirection);
            bw.Write(add.Length);
            for (int i = 0; i < add.Length; i++)
                bw.Write(add[i]);
            bw.Write(ss.Length);
            for (int i = 0; i < ss.Length; i++)
                bw.Write(ss[i]);
        }

        private static bool TryReadTerrainTile(BinaryReader br, out TerrainTilePayload data)
        {
            data = default(TerrainTilePayload);
            data.TerrainI = br.ReadInt32();
            data.TerrainJ = br.ReadInt32();
            data.DefId = br.ReadInt32();
            data.Height = br.ReadInt32();
            data.Cap = br.ReadInt32();
            data.PrevDefId = br.ReadInt32();
            data.Explored = br.ReadInt32();
            data.OuterDirection = br.ReadInt32();
            int nAdd = br.ReadInt32();
            if (nAdd < 0 || nAdd > 64)
                return false;
            data.AdditionalDefIds = new int[nAdd];
            for (int i = 0; i < nAdd; i++)
                data.AdditionalDefIds[i] = br.ReadInt32();
            int nSs = br.ReadInt32();
            if (nSs < 0 || nSs > 64)
                return false;
            data.SsDefs = new int[nSs];
            for (int i = 0; i < nSs; i++)
                data.SsDefs[i] = br.ReadInt32();
            return true;
        }

    }
}

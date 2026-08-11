using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Newtonsoft.Json;
using Pulsar4X.Colonies;
using Pulsar4X.Datablobs;
using Pulsar4X.Engine;
using Pulsar4X.Names;
using Pulsar4X.Engine.Orders;
using Pulsar4X.Extensions;
using Pulsar4X.Factions;
using Pulsar4X.Fleets;
using Pulsar4X.Ships;
using Stringify = Pulsar4X.Api.Stringify;


namespace Pulsar4X.Storage;

public class CargoTransferOrder : EntityCommand
{
    public enum Conditionals
    {
        TakeAvailibleAtOrder,
        WaitTillFull,
        WailTillEmpty,
        TakeAvailible

    }
    [JsonProperty]
    public Conditionals Condition { get; private set; } = Conditionals.TakeAvailibleAtOrder;
    [JsonProperty]
    public bool IsPrimaryEntity { get; private set; }

    public override ActionLaneTypes ActionLanes => ActionLaneTypes.Movement | ActionLaneTypes.InteractWithExternalEntity;

    public override bool IsBlocking => true;

    public override string Name { get; } = "Cargo Transfer";

    public override string Details
    {
        get
        {
            try
            {
                if (TransferData == null)
                    return "Cargo Transfer.";

                string otherEntity;
                if (_entityCommanding == TransferData.PrimaryEntity)
                    otherEntity = TransferData.SecondaryEntity.GetName(RequestingFactionGuid);
                else
                    otherEntity = TransferData.PrimaryEntity.GetName(RequestingFactionGuid);
                string detailStr = "With " + otherEntity + ".";
                if (!IsRunning)
                    detailStr += " Waiting to start";
                else
                {
                    detailStr += " Transfering, " + Stringify.Quantity(AmountLeftToXfer(), "#.#") + " remaining.";
                }
                return detailStr;
            }
            catch
            {
                return "Cargo Transfer.";
            }
        }
    }

    Entity _entityCommanding = Entity.InvalidEntity;

    internal override Entity EntityCommanding { get { return _entityCommanding; } }

    internal CargoTransferDataDB TransferData { get; }

    private bool _aborted;

    private CargoTransferOrder(CargoTransferDataDB transferData)
    {
        TransferData = transferData;
    }

    /// <summary>
    /// Cancel a transfer: restore escrowed cargo, drop CargoTransferDB, and remove both
    /// partner orders that share this <see cref="TransferData"/>.
    /// </summary>
    internal void Abort()
    {
        if (_aborted || TransferData == null)
            return;
        _aborted = true;

        bool stillEscrowed = TransferData.PrimaryStorageDB.EscroItems.Contains(TransferData)
                             || TransferData.SecondaryStorageDB.EscroItems.Contains(TransferData);

        if (stillEscrowed)
        {
            foreach (var (item, count, _) in TransferData.EscroHeldInPrimary.ToList())
            {
                if (count > 0)
                    TransferData.PrimaryStorageDB.AddCargoByUnit(item, count);
            }
            TransferData.EscroHeldInPrimary.Clear();

            foreach (var (item, count, _) in TransferData.EscroHeldInSecondary.ToList())
            {
                if (count > 0)
                    TransferData.SecondaryStorageDB.AddCargoByUnit(item, count);
            }
            TransferData.EscroHeldInSecondary.Clear();

            TransferData.PrimaryStorageDB.EscroItems.Remove(TransferData);
            TransferData.SecondaryStorageDB.EscroItems.Remove(TransferData);
        }

        if (TransferData.PrimaryEntity.HasDataBlob<CargoTransferDB>())
            TransferData.PrimaryEntity.RemoveDataBlob<CargoTransferDB>();
        if (TransferData.SecondaryEntity.HasDataBlob<CargoTransferDB>())
            TransferData.SecondaryEntity.RemoveDataBlob<CargoTransferDB>();

        RemoveMatchingOrders(TransferData.PrimaryEntity);
        RemoveMatchingOrders(TransferData.SecondaryEntity);
        _isFinished = true;
        IsRunning = false;
    }

    private void RemoveMatchingOrders(Entity entity)
    {
        if (!entity.TryGetDataBlob<OrderableDB>(out var orderable))
            return;

        orderable.ActionList.RemoveAll(cmd =>
            cmd is CargoTransferOrder other
            && ReferenceEquals(other.TransferData, TransferData));
    }

    public static bool CreateCommands(int faction, Entity primaryEntity, Entity secondaryEntity, List<(ICargoable item, long amount)> itemsToMove, OrderSource source = OrderSource.Issued)
    {
        CargoTransferDataDB cargoData = new(primaryEntity, secondaryEntity, itemsToMove);
        var cmd1 = new CargoTransferOrder(cargoData)
        {
            RequestingFactionGuid = faction,
            EntityCommandingGuid = primaryEntity.Id,
            CreatedDate = primaryEntity.AttachedManager.ManagerSubpulses.StarSysDateTime,
            IsPrimaryEntity = true,
            Source = source,
        };
        bool primaryAccepted = OrderEnqueue.Enqueue(primaryEntity.AttachedManager.Game, cmd1);

        var cmd2 = new CargoTransferOrder(cargoData)
        {
            RequestingFactionGuid = faction,
            EntityCommandingGuid = secondaryEntity.Id,
            CreatedDate = primaryEntity.AttachedManager.ManagerSubpulses.StarSysDateTime,
            IsPrimaryEntity = false,
            Source = source,
        };
        return OrderEnqueue.Enqueue(secondaryEntity.AttachedManager.Game, cmd2) && primaryAccepted;
    }

    /// <summary>
    /// Single item conditional order.
    /// Assumes transfer from secondary to primary
    /// </summary>
    /// <param name="faction"></param>
    /// <param name="primaryEntity"></param>
    /// <param name="secondaryEntity"></param>
    /// <param name="item"></param>
    /// <param name="condition"></param>
    public static void CreateCommands(int faction, Entity primaryEntity, Entity secondaryEntity, ICargoable item, Conditionals condition, OrderSource source = OrderSource.Issued)
    {
        long amount = 0;
        if (condition == Conditionals.WaitTillFull)
        {
            // Tank capacity only — orphan EscroItems must not shrink WaitTillFull to 0.
            amount = CargoMath.GetFreeUnitSpace(primaryEntity.GetDataBlob<CargoStorageDB>(), item, includeEscro: false);
        }

        List<(ICargoable item, long amount)> itemList = new List<(ICargoable item, long amount)>();
        itemList.Add((item, amount));
        CargoTransferDataDB cargoData = new(primaryEntity, secondaryEntity, itemList);

        var cmd1 = new CargoTransferOrder(cargoData)
        {
            RequestingFactionGuid = faction,
            EntityCommandingGuid = primaryEntity.Id,
            CreatedDate = primaryEntity.AttachedManager.ManagerSubpulses.StarSysDateTime,
            IsPrimaryEntity = true,
            Condition = condition,
            Source = source,
        };
        OrderEnqueue.Enqueue(primaryEntity.AttachedManager.Game, cmd1);

        var cmd2 = new CargoTransferOrder(cargoData)
        {
            RequestingFactionGuid = faction,
            EntityCommandingGuid = secondaryEntity.Id,
            CreatedDate = primaryEntity.AttachedManager.ManagerSubpulses.StarSysDateTime,
            IsPrimaryEntity = false,
            Condition = condition,
            Source = source,
        };
        OrderEnqueue.Enqueue(secondaryEntity.AttachedManager.Game, cmd2);
    }

    /// <returns>True if at least one of the fleet's ships was issued a lasting refuel transfer.</returns>
    public static bool CreateRefuelFleetCommand(Entity cargoFromEntity, Entity fleet, OrderSource source = OrderSource.Issued)
    {
        if (!cargoFromEntity.TryGetDataBlob<CargoStorageDB>(out var colonyStorage))
            return false;

        EnsureMinimumTransferCapability(colonyStorage);
        ReleaseOrphanEscrow(colonyStorage);

        var fleetOwner = fleet.GetFactionOwner;
        var cargoLibrary = fleetOwner.GetDataBlob<FactionInfoDB>().Data.CargoGoods;
        bool anyIssued = false;
        if (!fleet.TryGetDataBlob<FleetDB>(out var fleetDB))
            return false;

        var ships = fleetDB.Children.Where(c =>
            !c.HasDataBlob<FleetDB>() && c.HasDataBlob<CargoStorageDB>());

        int skippedAway = 0, skippedBusy = 0, skippedNoFuel = 0, skippedNoStore = 0, skippedFull = 0, skippedEmptyColony = 0, skippedEx = 0, skippedVanished = 0;

        foreach (var ship in ships)
        {
            try
            {
                if (!ship.TryGetDataBlob<CargoStorageDB>(out var shipStorage))
                    continue;

                ReleaseOrphanEscrow(shipStorage);

                // Never start a WaitTillFull transfer while the hull is elsewhere
                // (e.g. SensorSat at Earth made IsFleetAtColony true, Surveyor still at Mercury).
                if (cargoFromEntity.HasDataBlob<ColonyInfoDB>()
                    && !FleetOrderCleanup.IsShipAtColony(ship, cargoFromEntity))
                {
                    skippedAway++;
                    continue;
                }

                EnsureMinimumTransferCapability(shipStorage);

                // Already refueling — don't stack another WaitTillFull transfer.
                if (ship.TryGetDataBlob<OrderableDB>(out var shipOrders)
                    && shipOrders.ActionList.OfType<CargoTransferOrder>().Any())
                {
                    skippedBusy++;
                    continue;
                }

                var fuelInfo = ship.GetFuelInfo(cargoLibrary);
                ICargoable? fuel = fuelInfo.Item1;

                if (fuel == null
                    && ship.TryGetDataBlob<Movement.WarpAbilityDB>(out var warp)
                    && !string.IsNullOrEmpty(warp.EnergyType))
                {
                    fuel = cargoLibrary.GetAny(warp.EnergyType);
                }

                if (fuel == null)
                {
                    skippedNoFuel++;
                    continue;
                }

                // Colony (or ship) missing this cargo type store → escrow ctor used to KeyNotFound crash.
                if (!colonyStorage.TypeStores.ContainsKey(fuel.CargoTypeID)
                    || !shipStorage.TypeStores.ContainsKey(fuel.CargoTypeID))
                {
                    skippedNoStore++;
                    continue;
                }

                // Ignore escrow for "has tank room" — orphan EscroItems from aborted
                // transfers used to report free=0 while GetFuelPercent still showed ~50%.
                long free = CargoMath.GetFreeUnitSpace(shipStorage, fuel, includeEscro: false);
                if (free <= 0)
                {
                    skippedFull++;
                    continue;
                }

                long colonyUnits = CargoMath.GetUnitsStored(colonyStorage, fuel, includeEscro: false);
                if (colonyUnits <= 0)
                {
                    skippedEmptyColony++;
                    continue;
                }

                // Clear lingering warps so Movement lane is free for CargoTransfer.
                FleetOrderCleanup.AbortShipMovementOrdersOnEntity(ship);

                CreateCommands(fleet.FactionOwnerID, ship, cargoFromEntity, fuel, Conditionals.WaitTillFull, source);

                // CreateCommands used to always count as success even when colony escrow
                // was 0 (instant WaitTillFull finish) or Enqueue rejected the orders.
                bool lasting = ship.HasDataBlob<CargoTransferDB>()
                    || (ship.TryGetDataBlob<OrderableDB>(out var afterOrders)
                        && afterOrders.ActionList.OfType<CargoTransferOrder>().Any());
                if (lasting)
                    anyIssued = true;
                else
                    skippedVanished++;
            }
            catch (Exception ex)
            {
                skippedEx++;
                System.Diagnostics.Debug.WriteLine($"CreateRefuelFleetCommand ship {ship.Id}: {ex.Message}");
            }
        }

        if (!anyIssued)
        {
            Pulsar4X.Api.DebugTraceLog.Warn("Refuel",
                $"CreateRefuelFleetCommand fleet#{fleet.Id} issued=0 " +
                $"(away={skippedAway} busy={skippedBusy} noFuel={skippedNoFuel} " +
                $"noStore={skippedNoStore} full={skippedFull} emptyColony={skippedEmptyColony} " +
                $"vanished={skippedVanished} ex={skippedEx})",
                fleet.IsValid ? fleet.StarSysDateTime : null);
        }

        return anyIssued;
    }

    /// <summary>
    /// Dead transfers can leave EscroItems pinning cargo with no live CargoTransferDB/order.
    /// That makes GetUnitsStored(includeEscro:false) look empty while fuel is locked away.
    /// </summary>
    internal static void ReleaseOrphanEscrow(CargoStorageDB storage)
    {
        if (storage?.EscroItems == null || storage.EscroItems.Count == 0)
            return;

        foreach (var data in storage.EscroItems.ToList())
        {
            if (data == null)
                continue;

            bool live = IsTransferLive(data);
            if (live)
                continue;

            foreach (var (item, count, _) in data.EscroHeldInPrimary.ToList())
            {
                if (count > 0)
                    data.PrimaryStorageDB.AddCargoByUnit(item, count);
            }
            data.EscroHeldInPrimary.Clear();

            foreach (var (item, count, _) in data.EscroHeldInSecondary.ToList())
            {
                if (count > 0)
                    data.SecondaryStorageDB.AddCargoByUnit(item, count);
            }
            data.EscroHeldInSecondary.Clear();

            data.PrimaryStorageDB?.EscroItems.Remove(data);
            data.SecondaryStorageDB?.EscroItems.Remove(data);
        }
    }

    private static bool IsTransferLive(CargoTransferDataDB data)
    {
        if (data.PrimaryEntity is { IsValid: true } primary
            && (primary.HasDataBlob<CargoTransferDB>()
                || (primary.TryGetDataBlob<OrderableDB>(out var pOrders)
                    && pOrders.ActionList.OfType<CargoTransferOrder>()
                        .Any(o => ReferenceEquals(o.TransferData, data)))))
            return true;

        if (data.SecondaryEntity is { IsValid: true } secondary
            && (secondary.HasDataBlob<CargoTransferDB>()
                || (secondary.TryGetDataBlob<OrderableDB>(out var sOrders)
                    && sOrders.ActionList.OfType<CargoTransferOrder>()
                        .Any(o => ReferenceEquals(o.TransferData, data)))))
            return true;

        return false;
    }

    /// <summary>
    /// Saved games / broken installation templates can leave TransferRate at 0 forever.
    /// Refuel still issues orders; without a floor nothing ever moves.
    /// </summary>
    internal static void EnsureMinimumTransferCapability(CargoStorageDB storage)
    {
        if (storage.TransferRate <= 0)
            storage.TransferRate = StorageSpaceProcessor.FallbackTransferRate_kgs;
        if (storage.TransferRangeDv_mps <= 0)
            storage.TransferRangeDv_mps = StorageSpaceProcessor.FallbackTransferRange_mps;
    }


    /// <summary>
    /// Validates and actions the command.
    /// may eventualy need to return a responce instead of void.
    /// This creates a CargoTranferDB from the command, which does all the work.
    /// the command is to create and enqueue a CargoTransferDB.
    /// </summary>
    internal override void Execute(DateTime atDateTime)
    {
        // Live entity refs are not serialized; recover from TransferData after save/load.
        if (!_entityCommanding.IsValid)
        {
            if (TransferData?.PrimaryEntity?.Id == EntityCommandingGuid)
                _entityCommanding = TransferData.PrimaryEntity;
            else if (TransferData?.SecondaryEntity?.Id == EntityCommandingGuid)
                _entityCommanding = TransferData.SecondaryEntity;
        }

        if (!_entityCommanding.IsValid)
            return;

        if (TransferData is null)
            return;

        if (!IsRunning)
        {
            CargoTransferDB transferDB = new CargoTransferDB(TransferData);
            transferDB.ParentStorageDB = _entityCommanding.GetDataBlob<CargoStorageDB>();
            _entityCommanding.SetDataBlob(transferDB);
            IsRunning = true;
        }
    }

    internal override void BindCommandingEntity(Entity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        _entityCommanding = entity;
        base.BindCommandingEntity(entity);
    }

    internal override bool IsValidCommand(Game game)
    {
        if (CommandHelpers.IsCommandValid(game.GlobalManager, RequestingFactionGuid, EntityCommandingGuid, out var factionEntity, out _entityCommanding))
        {
            return true;
        }
        return false;
    }

    internal override bool IsFinished()
    {
        if (!IsRunning)
            return _isFinished = false;

        switch (Condition)
        {
            case Conditionals.TakeAvailibleAtOrder:
                {
                    if (AmountLeftToXfer() > 0)
                        _isFinished = false;
                    else
                        _isFinished = true;
                    break;
                }
            case Conditionals.WaitTillFull:
                {
                    if (AmountLeftToXfer() > 0)
                        _isFinished = false;
                    else //if we've transfered everything from the inital order, check if we can fit more
                    {
                        for (int index = 0; index < TransferData.OrderedToTransfer.Count; index++)
                        {
                            (ICargoable item, long amount) tup = TransferData.OrderedToTransfer[index];
                            var amount = CargoMath.GetFreeUnitSpace(TransferData.PrimaryStorageDB, tup.item);
                            TransferData.UpdateEscro(tup.item, amount);
                        }
                        if (AmountLeftToXfer() > 0)
                            _isFinished = false;
                        else
                            _isFinished = true;
                    }
                    break;
                }
            case Conditionals.WailTillEmpty:
                throw new NotImplementedException();
            case Conditionals.TakeAvailible:
                throw new NotImplementedException();
            default:
                throw new ArgumentOutOfRangeException();
        }
        if (_isFinished)
        {
            //TransferData.PrimaryStorageDB.EscroItems.Remove(TransferData);
            //TransferData.SecondaryStorageDB.EscroItems.Remove(TransferData);
            _entityCommanding.GetDataBlob<CargoStorageDB>().EscroItems.Remove(TransferData);
            _entityCommanding.RemoveDataBlob<CargoTransferDB>();
        }
        return _isFinished;
    }

    long AmountLeftToXfer()
    {
        long amount = 0;
        foreach (var tup in TransferData.EscroHeldInPrimary)
        {
            amount += tup.count;
        }
        foreach (var tup in TransferData.EscroHeldInSecondary)
        {
            amount += tup.count;
        }
        return amount;
    }

    public override EntityCommand Clone()
    {
        throw new NotImplementedException();
    }

}
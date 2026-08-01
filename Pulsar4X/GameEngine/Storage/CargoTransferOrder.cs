using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Newtonsoft.Json;
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
    public Conditionals Condition {get; private set;} = Conditionals.TakeAvailibleAtOrder;
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

    Entity _entityCommanding;

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

    public static bool CreateCommands(int faction, Entity primaryEntity, Entity secondaryEntity, List<(ICargoable item, long amount)> itemsToMove )
    {
        CargoTransferDataDB cargoData = new(primaryEntity, secondaryEntity, itemsToMove);
        var cmd1 = new CargoTransferOrder(cargoData)
        {
            RequestingFactionGuid = faction,
            EntityCommandingGuid = primaryEntity.Id,
            CreatedDate = primaryEntity.Manager.ManagerSubpulses.StarSysDateTime,
            IsPrimaryEntity = true,
        };
        bool primaryAccepted = primaryEntity.Manager.Game.OrderHandler.HandleOrder(cmd1);

        var cmd2 = new CargoTransferOrder(cargoData)
        {
            RequestingFactionGuid = faction,
            EntityCommandingGuid = secondaryEntity.Id,
            CreatedDate = primaryEntity.Manager.ManagerSubpulses.StarSysDateTime,
            IsPrimaryEntity = false
        };
        return secondaryEntity.Manager.Game.OrderHandler.HandleOrder(cmd2) && primaryAccepted;
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
    public static void CreateCommands(int faction, Entity primaryEntity, Entity secondaryEntity, ICargoable item,  Conditionals condition )
    {
        long amount = 0;
        if (condition == Conditionals.WaitTillFull)
        {
            amount = CargoMath.GetFreeUnitSpace(primaryEntity.GetDataBlob<CargoStorageDB>(), item);
        }

        List<(ICargoable item, long amount)> itemList = new List<(ICargoable item, long amount)>();
        itemList.Add((item, amount));
        CargoTransferDataDB cargoData = new(primaryEntity, secondaryEntity, itemList);

        var cmd1 = new CargoTransferOrder(cargoData)
        {
            RequestingFactionGuid = faction,
            EntityCommandingGuid = primaryEntity.Id,
            CreatedDate = primaryEntity.Manager.ManagerSubpulses.StarSysDateTime,
            IsPrimaryEntity = true,
            Condition = condition
        };
        primaryEntity.Manager.Game.OrderHandler.HandleOrder(cmd1);

        var cmd2 = new CargoTransferOrder(cargoData)
        {
            RequestingFactionGuid = faction,
            EntityCommandingGuid = secondaryEntity.Id,
            CreatedDate = primaryEntity.Manager.ManagerSubpulses.StarSysDateTime,
            IsPrimaryEntity = false,
            Condition = condition
        };
        secondaryEntity.Manager.Game.OrderHandler.HandleOrder(cmd2);
    }

    /// <returns>True if at least one of the fleet's ships was issued a refuel transfer.</returns>
    public static bool CreateRefuelFleetCommand(Entity cargoFromEntity, Entity fleet)
    {
        if (!cargoFromEntity.TryGetDataBlob<CargoStorageDB>(out var colonyStorage))
            return false;

        EnsureMinimumTransferCapability(colonyStorage);

        var fleetOwner = fleet.GetFactionOwner;
        var cargoLibrary = fleetOwner.GetDataBlob<FactionInfoDB>().Data.CargoGoods;
        bool anyIssued = false;
        if (!fleet.TryGetDataBlob<FleetDB>(out var fleetDB))
            return false;

        var ships = fleetDB.Children.Where(c =>
            !c.HasDataBlob<FleetDB>() && c.HasDataBlob<CargoStorageDB>());

        foreach (var ship in ships)
        {
            try
            {
                if (!ship.TryGetDataBlob<CargoStorageDB>(out var shipStorage))
                    continue;

                EnsureMinimumTransferCapability(shipStorage);

                // Already refueling — don't stack another WaitTillFull transfer.
                if (ship.TryGetDataBlob<OrderableDB>(out var shipOrders)
                    && shipOrders.ActionList.OfType<CargoTransferOrder>().Any())
                    continue;

                var fuelInfo = ship.GetFuelInfo(cargoLibrary);
                ICargoable? fuel = fuelInfo.Item1;

                if (fuel == null
                    && ship.TryGetDataBlob<Movement.WarpAbilityDB>(out var warp)
                    && !string.IsNullOrEmpty(warp.EnergyType))
                {
                    fuel = cargoLibrary.GetAny(warp.EnergyType);
                }

                if (fuel == null)
                    continue;

                // Colony (or ship) missing this cargo type store → escrow ctor used to KeyNotFound crash.
                if (!colonyStorage.TypeStores.ContainsKey(fuel.CargoTypeID)
                    || !shipStorage.TypeStores.ContainsKey(fuel.CargoTypeID))
                    continue;

                long free = CargoMath.GetFreeUnitSpace(shipStorage, fuel);
                if (free <= 0)
                    continue;

                CreateCommands(fleet.FactionOwnerID, ship, cargoFromEntity, fuel, Conditionals.WaitTillFull);
                anyIssued = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CreateRefuelFleetCommand ship {ship.Id}: {ex.Message}");
            }
        }

        return anyIssued;
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
        if (_entityCommanding == null || !_entityCommanding.IsValid)
        {
            if (TransferData?.PrimaryEntity?.Id == EntityCommandingGuid)
                _entityCommanding = TransferData.PrimaryEntity;
            else if (TransferData?.SecondaryEntity?.Id == EntityCommandingGuid)
                _entityCommanding = TransferData.SecondaryEntity;
        }

        if (_entityCommanding == null || !_entityCommanding.IsValid)
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
        if(!IsRunning)
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
                break;
            case Conditionals.TakeAvailible:
                throw new NotImplementedException();
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
        if(_isFinished)
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